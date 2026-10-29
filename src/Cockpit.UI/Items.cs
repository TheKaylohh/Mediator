using System.Globalization;
using Cockpit.Core.Bus;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;

namespace Cockpit.UI;

/// <summary>A row in the Jobs tab (Toolbox's "installed tools" list, for running work).</summary>
public sealed class JobItemViewModel : ObservableObject
{
    private JobState _state;
    private string _detail = "";
    private DateTimeOffset? _endedAt;

    public JobItemViewModel(RunningJob job, Action<JobItemViewModel> kill)
    {
        JobId = job.JobId;
        Title = job.Spec.Title;
        Kind = job.Spec.Kind;
        Launch = job.Spec.Launch.ToString();
        StartedAt = job.StartedAt;
        WorkspaceFolder = job.Workspace.Root;
        Provider = job.Provider is { } p ? ProviderRouter.DisplayName(p) : "no CLI";
        _state = job.State;
        KillCommand = new RelayCommand(() => kill(this), () => IsActive);
    }

    public string JobId { get; }
    public string Title { get; }
    public string Kind { get; }
    public string Launch { get; }
    public string WorkspaceFolder { get; }
    public string Provider { get; }
    public DateTimeOffset StartedAt { get; }
    public RelayCommand KillCommand { get; }

    public JobState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsActive));
            KillCommand.Refresh();
        }
    }

    public string StateText => State.ToString().ToLowerInvariant();
    public bool IsActive => State is JobState.Starting or JobState.Running;
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public string ElapsedText
    {
        get
        {
            var span = (_endedAt ?? DateTimeOffset.UtcNow) - StartedAt;
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }

    internal void Update(RunningJob job)
    {
        _endedAt = job.EndedAt;
        Detail = job.Detail ?? "";
        State = job.State;
        OnPropertyChanged(nameof(ElapsedText));
    }

    internal void Tick() => OnPropertyChanged(nameof(ElapsedText));
}

/// <summary>A line in the Activity tab, from the bus's ToolCallActivity.</summary>
/// <param name="Phase">Display text ("waiting for you", "tool error").</param>
/// <param name="IsProblem">The bus outcome was an error, exception or timeout (shown in red).</param>
public sealed record ActivityItemViewModel(DateTimeOffset At, string JobId, string Tool, string Phase, string Detail, bool IsProblem = false)
{
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

public enum ContextKind { Folder, File, App }

/// <summary>A context chip under the prompt (Explorer folder, selected files, foreground app). Click to include or exclude.</summary>
public sealed class ContextChipViewModel : ObservableObject
{
    private bool _isIncluded = true;

    public ContextChipViewModel(ContextKind kind, string label, string value)
    {
        Kind = kind;
        Label = label;
        Value = value;
        ToggleCommand = new RelayCommand(() => IsIncluded = !IsIncluded);
    }

    public ContextKind Kind { get; }
    public string Label { get; }
    public string Value { get; }
    public string KindGlyph => Kind switch { ContextKind.Folder => "", ContextKind.File => "", _ => "" }; // Segoe Fluent Icons
    public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }
    public RelayCommand ToggleCommand { get; }
}

/// <summary>
/// A tile in the Actions tab, like a tool row in JetBrains Toolbox: icon, name, one line of description, the
/// subscription it would run on right now (from the router, so it changes when one is at its limit), and a Run button.
/// </summary>
public sealed class ActionTileViewModel : ObservableObject
{
    private bool _isEnabled = true;
    private string _disabledReason = "";
    private string _provider;

    public ActionTileViewModel(string id, string glyph, string title, string description, TaskCategory category, string[] keywords,
        bool needsFolder, IReadOnlyList<string> tools)
    {
        Id = id;
        Glyph = glyph;
        Title = title;
        Description = description;
        Category = category;
        Keywords = keywords;
        NeedsFolder = needsFolder;
        Tools = tools;
        _provider = ProviderRouter.DisplayName(ProviderRouter.Defaults(category).Primary);
    }

    public string Id { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string Description { get; }
    public TaskCategory Category { get; }
    public IReadOnlyList<string> Keywords { get; }
    public bool NeedsFolder { get; }
    /// <summary>The Cockpit tools a job started from this tile may use (its grant's allow-list).</summary>
    public IReadOnlyList<string> Tools { get; }
    public string ToolsText => Tools.Count == 0 ? "the CLI's own tools" : string.Join(", ", Tools);
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string DisabledReason { get => _disabledReason; set => SetProperty(ref _disabledReason, value); }
    /// <summary>"Claude Pro (Claude Code)", "Google AI Pro (Gemini CLI)", or why neither is available.</summary>
    public string Provider { get => _provider; set => SetProperty(ref _provider, value); }

    /// <summary>Toolbox-style search: every word of the query must prefix-match the title, a keyword or the provider.</summary>
    public bool Matches(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return true;
        var haystack = Title.Split(' ').Concat(Keywords).Concat(Provider.Split(' ')).Append(Category.ToString()).ToList();
        return words.All(w => haystack.Any(h => h.StartsWith(w, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Header pill per subscription: Claude Pro, Google AI Pro. Status from the usage ledger (estimates).</summary>
public sealed class ProviderStatusViewModel : ObservableObject
{
    private string _status = "ready";
    private bool _isCoolingDown;
    private bool _isActive;

    public ProviderStatusViewModel(string id, string name) { Id = id; Name = name; }

    public string Id { get; }
    public string Name { get; }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsCoolingDown { get => _isCoolingDown; set => SetProperty(ref _isCoolingDown, value); }
    /// <summary>The subscription the suggested action would run on (highlighted in the header).</summary>
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

internal static class ToolPhase
{
    public static string Describe(ToolCallEvent e) => e.Phase switch
    {
        "received" => "received",
        "awaiting_approval" => "waiting for you",
        "running" => "running",
        "ok" => "done",
        _ => e.Phase.Replace('_', ' '),
    };

    public static bool IsProblem(ToolCallEvent e) => e.Phase is "tool_error" or "exception" or "timeout";
}
