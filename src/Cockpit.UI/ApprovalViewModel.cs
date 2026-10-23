using System.Collections.ObjectModel;
using System.Globalization;
using Cockpit.Core.Bus;
using Cockpit.Core.Runtime;

namespace Cockpit.UI;

/// <summary>One row of a selectable preview (a mate, a file move). Unticking it narrows the approval.</summary>
public sealed class ApprovalRowViewModel : ObservableObject
{
    private bool _isChecked = true;

    public ApprovalRowViewModel(string key, string primary, string? detail, double? confidence, bool selectable)
    {
        Key = key;
        Primary = primary;
        Detail = detail ?? "";
        Confidence = confidence;
        IsSelectable = selectable;
    }

    public string Key { get; }
    public string Primary { get; }
    public string Detail { get; }
    public double? Confidence { get; }
    public string ConfidenceText => Confidence is double c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "";
    /// <summary>Below 0.5: highlighted so low-confidence mates get a second look.</summary>
    public bool IsLowConfidence => Confidence is < 0.5;
    public bool IsSelectable { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (IsSelectable && SetProperty(ref _isChecked, value)) CheckedChanged?.Invoke(); }
    }

    internal event Action? CheckedChanged;
}

/// <summary>
/// The approval card: one pending <see cref="ApprovalRequest"/> answered from the overlay. Completing it resolves
/// the gate's TaskCompletionSource exactly once; later clicks are ignored.
/// </summary>
public sealed class ApprovalViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ApprovalResponse> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApprovalViewModel(ApprovalRequest request)
    {
        Request = request;
        var (title, summary, selectable, rows) = ConsoleApprovalPrompt.ReadPreview(request.Preview);
        Title = title ?? request.Summary;
        Summary = summary ?? (title is null ? "" : request.Summary);
        IsSelectable = selectable && rows.Count > 0;
        foreach (var r in rows)
        {
            var row = new ApprovalRowViewModel(r.Key, r.Primary, r.Detail, r.Confidence, IsSelectable);
            row.CheckedChanged += OnRowChanged;
            Rows.Add(row);
        }
        ApproveSelectedCommand = new RelayCommand(ApproveSelected, () => !IsCompleted && (!IsSelectable || CheckedCount > 0));
        ApproveAllCommand = new RelayCommand(() => Complete(ApprovalResponse.ApproveAll), () => !IsCompleted);
        DenyCommand = new RelayCommand(() => Complete(ApprovalResponse.Deny), () => !IsCompleted);
        KillJobCommand = new RelayCommand(() => Complete(new ApprovalResponse(false, KillJob: true)), () => !IsCompleted);
    }

    public ApprovalRequest Request { get; }
    public string JobId => Request.JobId;
    public string Tool => Request.Tool;
    public string Risk => Request.Risk.ToString();
    public bool IsDestructive => Request.Risk == ToolRisk.Destructive;
    public string Title { get; }
    public string Summary { get; }
    public bool IsSelectable { get; }
    public ObservableCollection<ApprovalRowViewModel> Rows { get; } = new();
    public bool HasRows => Rows.Count > 0;

    public int CheckedCount => Rows.Count(r => r.IsChecked);
    public string SelectionText => IsSelectable ? $"{CheckedCount} of {Rows.Count} selected" : Rows.Count == 0 ? "" : $"{Rows.Count} item(s)";

    public RelayCommand ApproveSelectedCommand { get; }
    public RelayCommand ApproveAllCommand { get; }
    public RelayCommand DenyCommand { get; }
    public RelayCommand KillJobCommand { get; }

    public bool IsCompleted => _answer.Task.IsCompleted;
    public Task<ApprovalResponse> Answer => _answer.Task;

    /// <summary>Raised once, when answered or cancelled (the overlay then shows the next queued approval).</summary>
    public event Action<ApprovalViewModel>? Completed;

    private void OnRowChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(SelectionText));
        ApproveSelectedCommand.Refresh();
    }

    private void ApproveSelected()
    {
        if (!IsSelectable || CheckedCount == Rows.Count) { Complete(ApprovalResponse.ApproveAll); return; }
        var keys = Rows.Where(r => r.IsChecked).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        Complete(new ApprovalResponse(true, keys));
    }

    internal void Complete(ApprovalResponse response)
    {
        if (!_answer.TrySetResult(response)) return;
        Finish();
    }

    /// <summary>The gate's timeout or the job's end: the card disappears and the call is treated as not approved.</summary>
    internal void Cancel()
    {
        if (!_answer.TrySetCanceled()) return;
        Finish();
    }

    private void Finish()
    {
        OnPropertyChanged(nameof(IsCompleted));
        ApproveSelectedCommand.Refresh();
        ApproveAllCommand.Refresh();
        DenyCommand.Refresh();
        KillJobCommand.Refresh();
        Completed?.Invoke(this);
    }
}
