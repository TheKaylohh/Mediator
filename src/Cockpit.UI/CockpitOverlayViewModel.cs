using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Cockpit.Core.Bus;
using Cockpit.Core.Context;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;

namespace Cockpit.UI;

/// <summary>
/// The overlay: a JetBrains Toolbox–style panel opened with Ctrl+Shift+Space. A prompt box at the top routes
/// to an action (or filters the action list when it starts with "/"); context chips come from the window that
/// was in front when the hotkey was pressed; tabs show Actions, Jobs and Activity; approvals take over the
/// panel until answered; the red kill switch ends every running job.
/// It is also the runtime's <see cref="IApprovalPrompt"/>: every pending approval is a TaskCompletionSource that
/// one of the card's buttons resolves.
/// </summary>
public sealed partial class CockpitOverlayViewModel : ObservableObject, IApprovalPrompt
{
    public const int MaxActivity = 300;
    public const string ClaudeProvider = "claude-code";
    public const string GeminiProvider = "gemini-cli";

    private readonly ICockpitRuntime _runtime;
    private readonly IUiDispatcher _ui;
    private readonly List<ApprovalViewModel> _approvalQueue = new();
    private string _promptText = "";
    private bool _isVisible;
    private int _selectedTab;
    private string _statusText = "Ready. Type what you want, or / to search actions.";
    private string? _billingBanner;
    private bool _useTerminal;
    private bool _keepSubscription;
    private string _routeText = "";
    private int _routeVersion;
    private readonly Dictionary<string, AiProvider> _jobProviders = new(StringComparer.Ordinal);
    private ApprovalViewModel? _pendingApproval;
    private ActionTileViewModel? _suggestedAction;

    public CockpitOverlayViewModel(ICockpitRuntime runtime, IUiDispatcher ui)
    {
        _runtime = runtime;
        _ui = ui;

        Actions = new ObservableCollection<ActionTileViewModel>(DefaultActions());
        FilteredActions = new ObservableCollection<ActionTileViewModel>(Actions);
        Providers.Add(new ProviderStatusViewModel(ClaudeProvider, "Claude Pro"));
        Providers.Add(new ProviderStatusViewModel(GeminiProvider, "Google AI Pro"));

        ToggleCommand = new RelayCommand(() => { if (IsVisible) Hide(); else Show(); });
        HideCommand = new RelayCommand(Hide);
        SubmitPromptCommand = new RelayCommand(SubmitPrompt, () => PromptText.Trim().Length > 0);
        RunActionCommand = new RelayCommand(p => { if (p is ActionTileViewModel tile) _ = RunActionAsync(tile, PromptText); }, p => p is ActionTileViewModel { IsEnabled: true });
        KillAllCommand = new RelayCommand(KillAll, () => Jobs.Any(j => j.IsActive));
        DismissBannerCommand = new RelayCommand(() => BillingBanner = null);

        _runtime.JobChanged += job => _ui.Post(() => OnJobChanged(job));
        _runtime.ToolCallActivity += e => _ui.Post(() =>
        {
            AddActivity(new ActivityItemViewModel(DateTimeOffset.UtcNow, e.JobId, e.Tool, ToolPhase.Describe(e), e.Detail ?? "", ToolPhase.IsProblem(e), ToolPhase.IsUncertain(e)));
            if (Jobs.FirstOrDefault(j => j.JobId == e.JobId) is { } item)
            {
                if (ToolPhase.IsUncertain(e)) item.UncertainCalls++;
                else if (ToolPhase.Resolves(e)) item.UncertainCalls--;
            }
        });
        _runtime.StreamEvent += (jobId, e) => _ui.Post(() => OnStreamEvent(jobId, e));
        _runtime.BillingAlarm += (jobId, source) => _ui.Post(() =>
            BillingBanner = $"Job {jobId} authenticated with {source} — that bills per token, not your subscription. It was stopped unless you opted in.");
        _runtime.Notice += (_, message) => _ui.Post(() => StatusText = message);
        UpdateSuggestion();
    }

    // ── Bindable state ───────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ActionTileViewModel> Actions { get; }
    public ObservableCollection<ActionTileViewModel> FilteredActions { get; }
    public ObservableCollection<ContextChipViewModel> ContextChips { get; } = new();
    public ObservableCollection<JobItemViewModel> Jobs { get; } = new();
    public ObservableCollection<ActivityItemViewModel> Activity { get; } = new();
    public ObservableCollection<ProviderStatusViewModel> Providers { get; } = new();

    public string PromptText
    {
        get => _promptText;
        set
        {
            if (!SetProperty(ref _promptText, value ?? "")) return;
            ApplyFilter();
            UpdateSuggestion();
            SubmitPromptCommand.Refresh();
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set { if (SetProperty(ref _isVisible, value) && value) FocusPromptRequested?.Invoke(); }
    }

    /// <summary>0 Actions, 1 Jobs, 2 Activity.</summary>
    public int SelectedTab { get => _selectedTab; set => SetProperty(ref _selectedTab, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string? BillingBanner
    {
        get => _billingBanner;
        private set { if (SetProperty(ref _billingBanner, value)) OnPropertyChanged(nameof(HasBillingBanner)); }
    }
    public bool HasBillingBanner => BillingBanner is not null;

    /// <summary>Run model jobs as an interactive Claude Code terminal (the billing-safe fallback) instead of headless.</summary>
    public bool UseTerminal { get => _useTerminal; set => SetProperty(ref _useTerminal, value); }

    /// <summary>Never switch subscriptions: refuse instead of falling back when the routed one is at its limit.</summary>
    public bool KeepSubscription
    {
        get => _keepSubscription;
        set { if (SetProperty(ref _keepSubscription, value)) _ = RefreshRouteAsync(); }
    }

    /// <summary>Which subscription Enter would use, or why none can ("Google AI Pro (Gemini CLI) — Claude Pro is at its limit until 15:00").</summary>
    public string RouteText { get => _routeText; private set => SetProperty(ref _routeText, value); }

    public ApprovalViewModel? PendingApproval
    {
        get => _pendingApproval;
        private set
        {
            if (!SetProperty(ref _pendingApproval, value)) return;
            OnPropertyChanged(nameof(HasPendingApproval));
            OnPropertyChanged(nameof(QueuedApprovalsText));
        }
    }
    public bool HasPendingApproval => PendingApproval is not null;
    public string QueuedApprovalsText => _approvalQueue.Count > 1 ? $"+{_approvalQueue.Count - 1} more waiting" : "";

    public ActionTileViewModel? SuggestedAction
    {
        get => _suggestedAction;
        private set { if (SetProperty(ref _suggestedAction, value)) OnPropertyChanged(nameof(SuggestionText)); }
    }
    public string SuggestionText => SuggestedAction is { } a ? $"Enter ↵  {a.Title}  ·  {a.Provider}" : "";

    public int ActiveJobCount => Jobs.Count(j => j.IsActive);
    public string JobsTabHeader => ActiveJobCount > 0 ? $"Jobs ({ActiveJobCount})" : "Jobs";

    public RelayCommand ToggleCommand { get; }
    public RelayCommand HideCommand { get; }
    public RelayCommand SubmitPromptCommand { get; }
    public RelayCommand RunActionCommand { get; }
    public RelayCommand KillAllCommand { get; }
    public RelayCommand DismissBannerCommand { get; }

    /// <summary>The view focuses and selects the prompt box.</summary>
    public event Action? FocusPromptRequested;
    /// <summary>An approval arrived: the view shows itself and flashes the tray/taskbar.</summary>
    public event Action? AttentionRequested;

    // ── Show / hide ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Hotkey handler. The snapshot is taken by the view before it activates, while the user's window is still in front.</summary>
    public void OnHotkey(ContextSnapshot? snapshot)
    {
        if (IsVisible && !HasPendingApproval) { Hide(); return; }
        SetContext(snapshot);
        Show();
    }

    public void Show()
    {
        IsVisible = true;
        _ = RefreshProvidersAsync();
    }

    /// <summary>Deactivation or Esc. An open approval keeps the panel up: it must be answered, denied or time out.</summary>
    public void Hide()
    {
        if (HasPendingApproval) return;
        IsVisible = false;
    }

    public void SetContext(ContextSnapshot? snapshot)
    {
        ContextChips.Clear();
        if (snapshot is null) return;
        if (snapshot.ExplorerFolder is { Length: > 0 } folder)
            ContextChips.Add(new ContextChipViewModel(ContextKind.Folder, ShortName(folder), folder));
        foreach (var path in snapshot.SelectedPaths.Take(8))
            ContextChips.Add(new ContextChipViewModel(Directory.Exists(path) ? ContextKind.Folder : ContextKind.File, ShortName(path), path));
        if (snapshot.SelectedPaths.Count > 8)
            StatusText = $"{snapshot.SelectedPaths.Count} items selected; the first 8 are shown as context.";
        if (!snapshot.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && snapshot.ProcessName.Length > 0)
            ContextChips.Add(new ContextChipViewModel(ContextKind.App, snapshot.ProcessName, snapshot.WindowTitle) { IsIncluded = false });
        if (snapshot.AmbiguousExplorerTab)
            StatusText = "Several Explorer tabs are open; check the folder chip is the one you meant.";
        UpdateSuggestion();
    }

    private static string ShortName(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } n ? n : path;

    // ── Prompt routing ───────────────────────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        string q = PromptText.StartsWith('/') ? PromptText[1..] : "";
        FilteredActions.Clear();
        foreach (var a in Actions.Where(a => a.Matches(q))) FilteredActions.Add(a);
    }

    private void UpdateSuggestion()
    {
        SuggestedAction = Route(PromptText);
        _ = RefreshRouteAsync();
    }

    /// <summary>
    /// Zero-token routing. A category prefix (/cad /code /files /tutor /resolve /blender) limits the choice to that
    /// category's actions; other "/query" text searches the list; free text picks by keywords; anything else opens
    /// Claude Code in a terminal in the context folder.
    /// </summary>
    public ActionTileViewModel? Route(string prompt)
    {
        string text = prompt.Trim();
        if (text.Length == 0) return null;
        IEnumerable<ActionTileViewModel> candidates = Actions.Where(a => a.IsEnabled);
        if (ProviderRouter.ParsePrefix(text) is { } prefixed)
        {
            candidates = candidates.Where(a => a.Category == prefixed.Category).ToList();
            text = prefixed.Remainder;
            if (text.Length == 0) return candidates.FirstOrDefault();
        }
        else if (text.StartsWith('/')) return FilteredActions.FirstOrDefault(a => a.IsEnabled);
        var lower = text.ToLowerInvariant();
        var scored = candidates.Select(a => (Tile: a, Score: a.Keywords.Count(k => lower.Contains(k, StringComparison.Ordinal)))).ToList();
        var best = scored.Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Tile).FirstOrDefault();
        return best ?? (ProviderRouter.ParsePrefix(prompt) is not null ? scored.FirstOrDefault().Tile : Actions.FirstOrDefault(a => a.Id == "claude-terminal"));
    }

    /// <summary>The prompt without a leading category prefix or search query.</summary>
    public static string PromptBody(string prompt)
    {
        string t = prompt.Trim();
        if (ProviderRouter.ParsePrefix(t) is { } p) return p.Remainder;
        return t.StartsWith('/') ? "" : t;
    }

    private void SubmitPrompt()
    {
        var tile = Route(PromptText);
        if (tile is null) return;
        _ = RunActionAsync(tile, PromptBody(PromptText));
    }

    /// <summary>
    /// Asks the runtime which subscription the suggested action would use now, and lights that pill. Shows the
    /// failover ("Claude Pro is at its limit until 15:00; using Google AI Pro instead") before anything starts.
    /// </summary>
    public async Task RefreshRouteAsync()
    {
        var tile = SuggestedAction;
        int version = Interlocked.Increment(ref _routeVersion);
        if (tile is null || tile.Id == "claude-terminal")
        {
            _ui.Post(() => { foreach (var p in Providers) p.IsActive = tile is not null && p.Id == ClaudeProvider; RouteText = ""; });
            return;
        }
        RouteDecision? decision = null;
        string? error = null;
        try { decision = await _runtime.RouteAsync(tile.Category, null, KeepSubscription).ConfigureAwait(true); }
        catch (ProviderUnavailableException ex) { error = ex.Message; }
        _ui.Post(() =>
        {
            if (version != Volatile.Read(ref _routeVersion)) return; // a newer prompt already asked
            foreach (var p in Providers) p.IsActive = decision is not null && p.Id == ProviderRouter.Id(decision.Provider);
            tile.Provider = decision is null ? "no subscription available" : ProviderRouter.DisplayName(decision.Provider);
            RouteText = error ?? decision?.Notice ?? "";
            OnPropertyChanged(nameof(SuggestionText));
        });
    }

    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\|/)[^""<>|?*\r\n]+")]
    private static partial Regex PathInText();

    /// <summary>A folder named in the prompt wins over the Explorer chip; then the first included folder chip.</summary>
    public string? ResolveFolder(string prompt)
    {
        foreach (Match m in PathInText().Matches(prompt))
        {
            string candidate = m.Value.TrimEnd('.', ',', ';', ' ');
            for (string? c = candidate; !string.IsNullOrEmpty(c); c = c.Contains(' ', StringComparison.Ordinal) ? c[..c.LastIndexOf(' ')] : null)
                if (Directory.Exists(c)) return Path.GetFullPath(c);
        }
        return ContextChips.FirstOrDefault(c => c.IsIncluded && c.Kind == ContextKind.Folder)?.Value
            ?? ContextChips.Where(c => c.IsIncluded && c.Kind == ContextKind.File).Select(c => Path.GetDirectoryName(c.Value)).FirstOrDefault(d => d is not null);
    }

    public async Task<RunningJob?> RunActionAsync(ActionTileViewModel tile, string prompt)
    {
        if (!tile.IsEnabled) { StatusText = tile.DisabledReason; return null; }
        string? folder = ResolveFolder(prompt);
        if (tile.NeedsFolder && folder is null)
        {
            StatusText = "Which folder? Select it in File Explorer before pressing the hotkey, or put its path in the prompt.";
            return null;
        }
        var launch = UseTerminal ? LaunchMode.Interactive : LaunchMode.Headless;
        JobSpec spec = SpecFor(tile.Id, folder, prompt, launch) with { NeverDowngrade = KeepSubscription };
        try
        {
            var job = await _runtime.StartJobAsync(spec).ConfigureAwait(true);
            _ui.Post(() =>
            {
                OnJobChanged(job);
                StatusText = $"Started: {spec.Title}";
                PromptText = "";
                SelectedTab = 1;
            });
            return job;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _ui.Post(() => StatusText = ex.Message);
            return null;
        }
    }

    /// <summary>The job recipe behind each tile.</summary>
    public static JobSpec SpecFor(string actionId, string? folder, string prompt, LaunchMode launch) => actionId switch
    {
        "assemble" => JobSpecFactory.Assemble(folder!, launch),
        "codecad" => JobSpecFactory.CodeCad(folder!, prompt.Length > 0 ? prompt : "the parts described in this folder's notes", launch),
        "organize" => JobSpecFactory.Organize(folder!, prompt.Length > 0 ? prompt : "group by file type into Documents, Pictures, Archives, Installers", launch),
        "resolve" => JobSpecFactory.ResolveEdit(folder!, prompt.Length > 0 ? prompt : "a short rough cut in a sensible order", launch),
        "blender" => JobSpecFactory.BlenderScene(folder!, prompt.Length > 0 ? prompt : "a car driving through a city at night", launch),
        "tutor" => JobSpecFactory.Tutor(folder!, prompt.Length > 0 ? prompt : "the chapter I am studying", launch),
        "claude-terminal" => JobSpecFactory.ClaudeCodeSession(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), prompt),
        _ => throw new InvalidOperationException($"No job recipe for action '{actionId}'."),
    };

    // ── Approvals (IApprovalPrompt) ──────────────────────────────────────────────────────────────────

    public async Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        var card = new ApprovalViewModel(request);
        card.Completed += c => _ui.Post(() => OnApprovalCompleted(c));
        using var registration = ct.Register(() => _ui.Post(card.Cancel));
        _ui.Post(() =>
        {
            if (card.IsCompleted) return; // cancelled before it was shown
            _approvalQueue.Add(card);
            PendingApproval ??= card;
            OnPropertyChanged(nameof(QueuedApprovalsText));
            IsVisible = true;
            AttentionRequested?.Invoke();
        });
        try
        {
            return await card.Answer.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private void OnApprovalCompleted(ApprovalViewModel card)
    {
        _approvalQueue.Remove(card);
        if (ReferenceEquals(PendingApproval, card)) PendingApproval = _approvalQueue.FirstOrDefault();
        OnPropertyChanged(nameof(QueuedApprovalsText));
    }

    // ── Jobs, kill switch, activity ──────────────────────────────────────────────────────────────────

    private void OnJobChanged(RunningJob job)
    {
        var item = Jobs.FirstOrDefault(j => j.JobId == job.JobId);
        if (job.Provider is { } provider) _jobProviders[job.JobId] = provider;
        if (item is null)
        {
            item = new JobItemViewModel(job, j => _runtime.KillJob(j.JobId, "killed from the overlay"));
            Jobs.Insert(0, item);
        }
        item.Update(job);
        if (!item.IsActive)
        {
            // Any approval still showing for an ended job can no longer matter.
            foreach (var card in _approvalQueue.Where(c => c.JobId == job.JobId).ToList()) card.Cancel();
            if (job.State == JobState.Failed) StatusText = $"{job.Spec.Title} failed: {job.Detail}";
        }
        OnPropertyChanged(nameof(ActiveJobCount));
        OnPropertyChanged(nameof(JobsTabHeader));
        KillAllCommand.Refresh();
    }

    /// <summary>The red button: kills every active job. Returns at once; processes die and grants are revoked synchronously.</summary>
    public void KillAll()
    {
        var active = Jobs.Where(j => j.IsActive).Select(j => j.JobId).ToList();
        foreach (var id in active) _runtime.KillJob(id, "kill switch");
        StatusText = active.Count == 0 ? "Nothing running." : $"Killed {active.Count} job(s).";
    }

    /// <summary>Called by the view's 1 s timer while visible.</summary>
    public void Tick()
    {
        foreach (var j in Jobs.Where(j => j.IsActive)) j.Tick();
    }

    private void AddActivity(ActivityItemViewModel item)
    {
        Activity.Insert(0, item);
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
    }

    private void OnStreamEvent(string jobId, CockpitStreamEvent e)
    {
        switch (e)
        {
            case AssistantTextEvent t:
                string text = t.Text.ReplaceLineEndings(" ");
                string who = _jobProviders.GetValueOrDefault(jobId) == AiProvider.GeminiCli ? "gemini" : "claude";
                AddActivity(new ActivityItemViewModel(DateTimeOffset.UtcNow, jobId, who, "says", text.Length > 240 ? text[..240] + "…" : text));
                break;
            case RunResultEvent { RateLimit: { } limit }:
                string limited = ProviderRouter.Id(_jobProviders.GetValueOrDefault(jobId, AiProvider.ClaudeCode));
                var pill = Providers.First(p => p.Id == limited);
                pill.IsCoolingDown = true;
                pill.Status = limit.ResetsAt is { } at ? "limit until " + at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "rate limited";
                break;
        }
    }

    public async Task RefreshProvidersAsync()
    {
        foreach (var pill in Providers)
        {
            DateTimeOffset? until = await _runtime.GetCooldownAsync(pill.Id).ConfigureAwait(true);
            _ui.Post(() =>
            {
                pill.IsCoolingDown = until is not null;
                pill.Status = until is { } u ? "limit until " + u.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "ready";
            });
        }
        await RefreshRouteAsync().ConfigureAwait(true);
    }

    // ── The action catalogue ─────────────────────────────────────────────────────────────────────────

    /// <summary>Glyphs are Segoe Fluent Icons code points (Windows 11; Segoe MDL2 Assets on Windows 10). Every action is live in V5.</summary>
    public static IEnumerable<ActionTileViewModel> DefaultActions() =>
    [
        new("assemble", "\uE7B8", "Assemble SolidWorks parts", "Read the parts in a folder, propose mates, review them, build a new .SLDASM.",
            TaskCategory.Cad, ["assembl", "solidworks", "sldprt", "mate"], needsFolder: true, JobSpecFactory.SwxTools),
        new("codecad", "\uE8B9", "Design parametric parts", "Hubs, discs, springs, arms as build123d code → STEP → SolidWorks parts, checked against size envelopes.",
            TaskCategory.Cad, ["design", "parametric", "generate part", "make part", "model a", "step", "build123d", "suspension", "hub", "brake", "spring", "bracket"], needsFolder: true, JobSpecFactory.CadTools),
        new("organize", "\uE8B7", "Organize a folder", "Plan moves from your description, review the batch, run it. Every move can be rolled back.",
            TaskCategory.Files, ["organi", "sort", "tidy", "clean up", "declutter", "move files", "folder"], needsFolder: true, JobSpecFactory.FileTools),
        new("resolve", "\uE714", "Rough cut for DaVinci Resolve", "Probe the clips, plan the cut, review it, export FCPXML + EDL to import in one step.",
            TaskCategory.Resolve, ["davinci", "resolve", "video", "clips", "edit my", "timeline", "rough cut", "footage"], needsFolder: true, JobSpecFactory.ResolveTools),
        new("blender", "\uF158", "Blender car scene", "A car driving through a procedural city with a chase camera; renders a preview.",
            TaskCategory.Blender, ["blender", "scene", "3d", "render", "animat", "car driving"], needsFolder: true, JobSpecFactory.BlenderTools),
        new("tutor", "\uE82D", "Linear algebra tutor", "Learn a textbook chapter from its PDF; every matrix result is checked exactly.",
            TaskCategory.Tutor, ["tutor", "teach", "learn", "explain", "chapter", "study", "linear algebra", "matrix", "eigen", "determinant"], needsFolder: true, JobSpecFactory.TutorTools),
        new("claude-terminal", "\uE756", "Claude Code in a terminal", "Open Claude Code in this folder with your prompt. You drive it; billing stays on your Pro login.",
            TaskCategory.Code, ["code", "app", "poc", "finish", "fix", "terminal", "repo", "bug"], needsFolder: false, []),
    ];
}
