using System.Collections.ObjectModel;
using System.Globalization;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.UI;

/// <summary>A row in the Jobs tab (Toolbox's "installed tools" list, for running work).</summary>
public sealed class JobItemViewModel : ObservableObject
{
    private JobState _state;
    private string _detail = "";
    private DateTimeOffset? _endedAt;
    private string _provider;
    private string _routing = "";
    private string _outcome = "";
    private int _uncertain;

    public JobItemViewModel(RunningJob job, Action<JobItemViewModel> kill)
    {
        JobId = job.JobId;
        Title = job.Spec.Title;
        Kind = job.Spec.Kind;
        Launch = job.Spec.Launch.ToString();
        StartedAt = job.StartedAt;
        WorkspaceFolder = job.Workspace.Root;
        _provider = job.Provider is { } p ? ProviderRouter.DisplayName(p) : "no CLI";
        _state = job.State;
        KillCommand = new RelayCommand(() => kill(this), () => IsActive);
    }

    public string JobId { get; }
    public string Title { get; }
    public string Kind { get; }
    public string Launch { get; }
    public string WorkspaceFolder { get; }
    /// <summary>The subscription the job is on now (changes if it switched after a usage limit).</summary>
    public string Provider { get => _provider; private set => SetProperty(ref _provider, value); }
    /// <summary>Original provider, every switch or refusal and why ("before launch: …; after start: …").</summary>
    public string RoutingText { get => _routing; private set => SetProperty(ref _routing, value); }
    /// <summary>How the CLI run ended: "rate limited", "not signed in", "ended without a result"…</summary>
    public string OutcomeText { get => _outcome; private set => SetProperty(ref _outcome, value); }
    /// <summary>Calls whose outcome is unknown (timed out or cancelled while inside an app). Never shown as stopped.</summary>
    public int UncertainCalls
    {
        get => _uncertain;
        internal set
        {
            if (!SetProperty(ref _uncertain, Math.Max(0, value))) return;
            OnPropertyChanged(nameof(StateText));
        }
    }
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

    /// <summary>
    /// Never implies that work stopped when it may not have: a killed job revokes access to new calls, but a call
    /// already inside an application (a SolidWorks rebuild, say) can still finish, and the text says so.
    /// </summary>
    public string StateText => (State, UncertainCalls) switch
    {
        (JobState.Killed, > 0) => $"killed: no new calls; {UncertainCalls} call(s) may still finish in the app",
        (JobState.Killed, _) => "killed: access revoked",
        (_, > 0) when !IsActive => $"{State.ToString().ToLowerInvariant()}; {UncertainCalls} call(s) with unknown outcome",
        _ => State.ToString().ToLowerInvariant(),
    };
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
        if (job.Provider is { } p) Provider = ProviderRouter.DisplayName(p);
        RoutingText = job.RoutingNotice is null && job.RoutingHistory.Count <= 1 ? "" : string.Join("; ", job.RoutingHistory);
        OutcomeText = job.Outcome switch
        {
            null => "",
            RunOutcome.Succeeded => "completed",
            RunOutcome.ResultError => "the model or a tool reported an error",
            RunOutcome.RateLimited => "usage limit reached",
            RunOutcome.AuthFailed => "the CLI is not signed in",
            RunOutcome.NoResult => "the CLI ended without a result",
            RunOutcome.ResultThenAbnormalExit => "result reported, then the CLI exited abnormally",
            _ => job.Outcome.ToString()!,
        };
        State = job.State;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ElapsedText));
    }

    internal void Tick() => OnPropertyChanged(nameof(ElapsedText));
}

/// <summary>A line in the Activity tab, from the bus's ToolCallActivity.</summary>
/// <param name="Phase">Display text ("waiting for you", "tool error").</param>
/// <param name="IsProblem">The bus outcome was an error, exception or timeout (shown in red).</param>
/// <param name="IsUncertain">The outcome is unknown: the call may still complete (shown in amber, never as failed or stopped).</param>
public sealed record ActivityItemViewModel(DateTimeOffset At, string JobId, string Tool, string Phase, string Detail, bool IsProblem = false, bool IsUncertain = false)
{
    /// <summary>live (done) | bad (failed) | warn (waiting or uncertain) | "": drives the status glyph.</summary>
    public string Kind => IsProblem ? "bad" : IsUncertain || Phase.StartsWith("waiting", StringComparison.Ordinal) ? "warn" : Phase.Contains("done", StringComparison.Ordinal) ? "live" : "";
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

public enum ContextKind { Folder, File, App, Attachment, Image }

/// <summary>
/// A context chip under the prompt: Explorer folder, selected files and foreground app (click to include or exclude),
/// or (V7) something the user dropped, pasted or picked, which has an × to remove it.
/// </summary>
public sealed class ContextChipViewModel : ObservableObject
{
    private bool _isIncluded = true;
    private string? _sharedWith;

    public ContextChipViewModel(ContextKind kind, string label, string value)
    {
        Kind = kind;
        Label = label;
        Value = value;
        ToggleCommand = new RelayCommand(() => IsIncluded = !IsIncluded);
    }

    public ContextChipViewModel(Mediator.Core.Attachments.Attachment attachment)
        : this(attachment.Kind == Mediator.Core.Attachments.AttachmentKind.Image ? ContextKind.Image : ContextKind.Attachment, attachment.Name, attachment.Path)
    {
        Attachment = attachment;
    }

    public ContextKind Kind { get; }
    public string Label { get; }
    public string Value { get; }
    /// <summary>Set for dropped, pasted or picked items: staged by the Core, granted read-only to the job.</summary>
    public Mediator.Core.Attachments.Attachment? Attachment { get; }
    public bool IsRemovable => Attachment is not null;
    public string KindGlyph => Kind switch
    {
        ContextKind.Folder => "\uE8B7",
        ContextKind.File => "\uE8A5",
        ContextKind.Attachment => Attachment?.Kind == Mediator.Core.Attachments.AttachmentKind.Folder ? "\uE8B7" : "\uE723",
        ContextKind.Image => "\uEB9F",
        _ => "\uE737",
    }; // Segoe Fluent Icons
    public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }
    /// <summary>The running job this attachment was also given to (read-only), or null.</summary>
    public string? SharedWith
    {
        get => _sharedWith;
        internal set { if (SetProperty(ref _sharedWith, value)) OnPropertyChanged(nameof(ToolTipText)); }
    }
    public string ToolTipText => Attachment is null ? Value
        : $"{Attachment.Source}\nRead-only for the job{(SharedWith is null ? "" : $"; also shared with {SharedWith}")}";
    public RelayCommand ToggleCommand { get; }
    /// <summary>Set by the overlay for removable chips.</summary>
    public RelayCommand? RemoveCommand { get; internal set; }
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
    /// <summary>Set for user-made actions: the saved instructions.</summary>
    public string? Instructions { get; init; }
    public bool IsCustom => Instructions is not null;
    public string Glyph { get; }
    public string Title { get; }
    public string Description { get; }
    public TaskCategory Category { get; }
    public IReadOnlyList<string> Keywords { get; }
    public bool NeedsFolder { get; }
    /// <summary>The Mediator tools a job started from this tile may use (its grant's allow-list).</summary>
    public IReadOnlyList<string> Tools { get; }
    public string ToolsText => Tools.Count == 0 ? "the CLI's own tools" : string.Join(", ", Tools);
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string DisabledReason { get => _disabledReason; set => SetProperty(ref _disabledReason, value); }
    /// <summary>"Claude Pro (Claude Code)", "Google AI Pro (Antigravity CLI)", or why neither is available.</summary>
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
        "tool_error" => "failed",
        "outcome_unknown" => "outcome unknown: may still complete",
        "replayed" => "already done: returned the earlier result, not run again",
        "duplicate_refused" => "not run again: an identical call is in progress or uncertain",
        "late_ok" => "finished late: done",
        "late_tool_error" => "finished late: failed",
        "late_exception" => "finished late with an error",
        _ => e.Phase.Replace('_', ' '),
    };

    public static bool IsProblem(ToolCallEvent e) => e.Phase is "tool_error" or "exception" or "late_tool_error" or "late_exception";
    public static bool IsUncertain(ToolCallEvent e) => e.Phase is "outcome_unknown";
    public static bool Resolves(ToolCallEvent e) => e.Phase.StartsWith("late_", StringComparison.Ordinal);
}

/// <summary>
/// V9: the "↩ Continue session (940df0a2)" chip. Shown when a headless Claude run finished with a session id;
/// selected by default so the next prompt in the same folder is a follow-up (`claude --resume`). Clicking it
/// deselects it (start fresh this time); × removes it. Switching folders removes it too.
/// </summary>
public sealed class ContinuationViewModel : ObservableObject
{
    private bool _isSelected = true;

    public ContinuationViewModel(ContinuableSession session) => Session = session;

    public ContinuableSession Session { get; }
    public string Folder => Session.WorkingDirectory;
    public string Text => $"\u21A9 Continue session ({Session.ShortId})";
    public string ToolTipText => $"Follow-up to \u201C{Session.Spec.Title}\u201D in {Folder} on Claude Pro (claude --resume {Session.SessionId}). Click to start fresh instead; \u00D7 removes it.";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// V10: one turn of the in-app conversation: the user's prompt, which CLI and model answered, what it is doing
/// (tool steps), and the full answer (never truncated; the view shows it in a selectable read-only text box).
/// </summary>
public sealed class ChatTurnViewModel(string jobId, string userPrompt, string badgeText) : ObservableObject
{
    private string _badgeText = badgeText;
    private string _thinkingSummary = "Thinking…";
    private string _responseText = "";
    private bool _isRunning = true;
    private bool _isError;

    public string JobId { get; } = jobId;
    public string UserPrompt { get; } = userPrompt;
    public string BadgeText { get => _badgeText; set => SetProperty(ref _badgeText, value); }
    public string ThinkingSummary { get => _thinkingSummary; set => SetProperty(ref _thinkingSummary, value); }
    public string ResponseText { get => _responseText; set => SetProperty(ref _responseText, value); }
    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }
    public bool IsError { get => _isError; set => SetProperty(ref _isError, value); }
    public ObservableCollection<string> Steps { get; } = new();
}

/// <summary>V10: one entry of the prompt bar's model picker. Auto: no provider or model (the routing rules decide).</summary>
public sealed record ModelOption(string Label, Mediator.Core.Jobs.AiProvider? Provider, string? Model)
{
    /// <summary>How the picked CLI is stored in settings.json.</summary>
    public const string ClaudeKey = "claude", GeminiKey = "gemini";
    public static readonly ModelOption Auto = new("Auto model", null, null);
    public string? ProviderKey => Provider switch { Mediator.Core.Jobs.AiProvider.ClaudeCode => ClaudeKey, Mediator.Core.Jobs.AiProvider.GeminiCli => GeminiKey, _ => null };
    public override string ToString() => Label;
}
