using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Cockpit.Core.Attachments;
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
    private readonly ISettingsStore _settingsStore;
    private CockpitSettings _settings;
    private string? _fault;
    private ReactorState _reactorState;
    private bool _isDragOver;

    public const int MaxAttachments = 20;

    public CockpitOverlayViewModel(ICockpitRuntime runtime, IUiDispatcher ui, ISettingsStore? settings = null)
    {
        _runtime = runtime;
        _ui = ui;
        _settingsStore = settings ?? new MemorySettingsStore();
        _settings = _settingsStore.Load();

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
        CycleThemeCommand = new RelayCommand(CycleTheme);
        AcknowledgeFaultCommand = new RelayCommand(() => SetFault(null), () => _fault is not null);
        AttachCommand = new RelayCommand(() => BrowseForAttachmentsRequested?.Invoke());
        ClearContinuationCommand = new RelayCommand(() => ClearContinuation("Starting fresh: the next prompt opens a new session."), () => Continuation is not null);

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
        {
            BillingBanner = $"Job {jobId} authenticated with {source} — that bills per token, not your subscription. It was stopped unless you opted in.";
            SetFault($"billing: {source}");
        });
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

    /// <summary>0 Actions, 1 Jobs, 2 Activity, 3 Connectors.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value) && value == ConnectorsTab && Connectors is { IsBusy: false } c) _ = c.RefreshAsync();
        }
    }
    public const int ConnectorsTab = 3;

    /// <summary>The Connectors tab (V7). Set by the host after construction; null in hosts without one.</summary>
    public ConnectorsViewModel? Connectors { get; set; }
    /// <summary>The first-run card (V7). Set by the host after construction.</summary>
    public OnboardingViewModel? Onboarding { get; set; }
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
            UpdateReactor();
        }
    }
    public bool HasPendingApproval => PendingApproval is not null;
    public string QueuedApprovalsText => _approvalQueue.Count > 1 ? $"+{_approvalQueue.Count - 1} more waiting" : "";

    public ActionTileViewModel? SuggestedAction
    {
        get => _suggestedAction;
        private set { if (SetProperty(ref _suggestedAction, value)) OnPropertyChanged(nameof(SuggestionText)); }
    }
    public string SuggestionText => IsContinuing
        ? $"Enter ↵  Continue: {Continuation!.Session.Spec.Title}  ·  Claude Pro"
        : SuggestedAction is { } a ? $"Enter ↵  {a.Title}  ·  {a.Provider}" : "";

    public int ActiveJobCount => Jobs.Count(j => j.IsActive);
    public string JobsTabHeader => ActiveJobCount > 0 ? $"Jobs ({ActiveJobCount})" : "Jobs";

    public RelayCommand ToggleCommand { get; }
    public RelayCommand HideCommand { get; }
    public RelayCommand SubmitPromptCommand { get; }
    public RelayCommand RunActionCommand { get; }
    public RelayCommand KillAllCommand { get; }
    public RelayCommand DismissBannerCommand { get; }
    public RelayCommand CycleThemeCommand { get; }
    /// <summary>Clicking the orb in the Error state clears it.</summary>
    public RelayCommand AcknowledgeFaultCommand { get; }
    /// <summary>The [ + Attach ] button: the view opens a file picker and calls <see cref="AttachPaths"/>.</summary>
    public RelayCommand AttachCommand { get; }

    // ── Theme (V7) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The look of the overlay; saved in settings.json. The view swaps its theme dictionary on <see cref="ThemeChanged"/>.</summary>
    public CockpitTheme CurrentTheme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value || !Enum.IsDefined(value)) return;
            UpdateSettings(_settings with { Theme = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeDisplayName));
            ThemeChanged?.Invoke(value);
        }
    }

    public string ThemeDisplayName => ThemeName(CurrentTheme);
    public static string ThemeName(CockpitTheme t) => t switch
    {
        CockpitTheme.HoloHud => "Holo HUD",
        CockpitTheme.ProDark => "Pro Dark",
        CockpitTheme.CleanLight => "Clean Light",
        _ => t.ToString(),
    };
    public static IReadOnlyList<CockpitTheme> Themes { get; } = Enum.GetValues<CockpitTheme>();

    public event Action<CockpitTheme>? ThemeChanged;

    /// <summary>The header button: Holo HUD → Pro Dark → Clean Light → Holo HUD.</summary>
    public void CycleTheme() => CurrentTheme = (CockpitTheme)(((int)CurrentTheme + 1) % Themes.Count);

    public CockpitSettings Settings => _settings;

    /// <summary>Saves settings (theme, hotkey, first run). A save failure is reported, never thrown at the UI.</summary>
    public void UpdateSettings(CockpitSettings settings)
    {
        _settings = settings;
        try { _settingsStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = "Settings could not be saved: " + ex.Message; }
    }

    // ── Reactor orb (V7) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AwaitingApproval wins (the user must act), then Active (any job running), then Error (the last failure,
    /// usage limit or billing alarm, until acknowledged or a new job starts), else Idle.
    /// </summary>
    public ReactorState ReactorState
    {
        get => _reactorState;
        private set
        {
            if (!SetProperty(ref _reactorState, value)) return;
            OnPropertyChanged(nameof(ReactorCaption));
        }
    }

    public string ReactorCaption => ReactorState switch
    {
        ReactorState.AwaitingApproval => "AWAITING APPROVAL",
        ReactorState.Active => ActiveJobCount == 1 ? "ACTIVE · 1 JOB" : $"ACTIVE · {ActiveJobCount} JOBS",
        ReactorState.Error => "FAULT",
        _ => "STANDBY",
    };

    /// <summary>Tooltip: what the orb is showing and why.</summary>
    public string ReactorDetail => ReactorState switch
    {
        ReactorState.AwaitingApproval => "A job is waiting for your approval.",
        ReactorState.Active => $"{ActiveJobCount} job(s) running.",
        ReactorState.Error => $"{_fault} — click to clear.",
        _ => "Ready.",
    };

    public string? Fault => _fault;

    private void SetFault(string? fault)
    {
        _fault = fault;
        OnPropertyChanged(nameof(Fault));
        AcknowledgeFaultCommand.Refresh();
        UpdateReactor();
    }

    private void UpdateReactor()
    {
        ReactorState = HasPendingApproval ? ReactorState.AwaitingApproval
            : ActiveJobCount > 0 ? ReactorState.Active
            : _fault is not null ? ReactorState.Error
            : ReactorState.Idle;
        OnPropertyChanged(nameof(ReactorCaption));
        OnPropertyChanged(nameof(ReactorDetail));
    }

    // ── Attachments (V7): drag-drop, Ctrl+V, [ + Attach ] ─────────────────────────────────────────────

    /// <summary>The view sets this while files are dragged over the window (shows the drop overlay).</summary>
    public bool IsDragOver { get => _isDragOver; set => SetProperty(ref _isDragOver, value); }

    public event Action? BrowseForAttachmentsRequested;

    public IEnumerable<ContextChipViewModel> AttachmentChips => ContextChips.Where(c => c.Attachment is not null);

    /// <summary>
    /// Dropped, pasted or picked paths. Each is checked and staged by the Core (files are copied, folders granted
    /// read-only; links, devices and system folders refused), becomes a removable chip, and is given read-only to
    /// the next job, and to the running job when exactly one is running. Returns how many were attached.
    /// </summary>
    public async Task<int> AttachPathsAsync(IEnumerable<string> paths)
    {
        int added = 0;
        var problems = new List<string>();
        var notes = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (AttachmentChips.Count() >= MaxAttachments) { problems.Add($"at most {MaxAttachments} attachments"); break; }
            if (AttachmentChips.Any(c => string.Equals(c.Attachment!.Source, Path.GetFullPath(path), StringComparison.Ordinal))) continue;
            try
            {
                var a = _runtime.StageAttachment(path);
                if (await AddAttachmentChipAsync(a).ConfigureAwait(true) is { } note) notes.Add(note);
                added++;
            }
            catch (Exception ex) when (ex is AttachmentException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add(ex.Message);
            }
        }
        StatusText = problems.Count > 0
            ? (added > 0 ? $"Attached {added}; " : "") + "not attached: " + string.Join("; ", problems.Take(3))
            : added > 0 ? $"Attached {added} item(s), read-only." + (notes.Count > 0 ? " " + notes[0] : "") : StatusText;
        return added;
    }

    /// <summary>Synchronous wrapper for the view's drop handler.</summary>
    public void AttachPaths(IEnumerable<string> paths) => _ = AttachPathsAsync(paths.ToList());

    /// <summary>A screenshot from the clipboard (PNG bytes, encoded by the view). Becomes an image chip a vision model can read.</summary>
    public async Task<bool> PasteImageAsync(byte[] png)
    {
        if (AttachmentChips.Count() >= MaxAttachments) { StatusText = $"At most {MaxAttachments} attachments."; return false; }
        try
        {
            var a = _runtime.StagePastedImage(png);
            string? note = await AddAttachmentChipAsync(a).ConfigureAwait(true);
            StatusText = $"Pasted image attached ({png.Length / 1024} KiB)." + (note is null ? "" : " " + note);
            return true;
        }
        catch (Exception ex) when (ex is AttachmentException or IOException or UnauthorizedAccessException)
        {
            StatusText = "Image not attached: " + ex.Message;
            return false;
        }
    }

    public void PasteImage(byte[] png) => _ = PasteImageAsync(png);

    /// <summary>Adds the chip and offers it to the one running job. Returns a note for the status line, or null.</summary>
    private async Task<string?> AddAttachmentChipAsync(Attachment a)
    {
        var chip = new ContextChipViewModel(a);
        chip.RemoveCommand = new RelayCommand(() => RemoveAttachment(chip));
        ContextChips.Add(chip);
        OnPropertyChanged(nameof(AttachmentChips));
        var active = Jobs.Where(j => j.IsActive).ToList();
        if (active.Count != 1) return null;
        if (await _runtime.AttachToJobAsync(active[0].JobId, a).ConfigureAwait(true))
        {
            chip.SharedWith = active[0].Title;
            return $"Also shared with {active[0].Title}.";
        }
        return $"{active[0].Title} cannot take new attachments (it runs headless); it goes to the next job.";
    }

    /// <summary>The chip's ×. A snapshot nobody received is deleted; one a job received stays until pruned (the job may be reading it).</summary>
    public void RemoveAttachment(ContextChipViewModel chip)
    {
        if (chip.Attachment is null || !ContextChips.Remove(chip)) return;
        if (chip.SharedWith is null) _runtime.DiscardAttachment(chip.Attachment);
        OnPropertyChanged(nameof(AttachmentChips));
    }

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

    private bool _isPinned;
    private int _modalDepth;

    /// <summary>
    /// Pinned: the panel stays up when another window is clicked, so files can be dragged in from Explorer.
    /// Esc and the hide button still hide it.
    /// </summary>
    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }

    /// <summary>The view calls this around its own dialogs (file picker), which deactivate the panel without meaning "hide".</summary>
    public IDisposable BeginModal()
    {
        _modalDepth++;
        return new ModalScope(this);
    }

    private sealed class ModalScope(CockpitOverlayViewModel vm) : IDisposable
    {
        private bool _done;
        public void Dispose() { if (!_done) { _done = true; vm._modalDepth--; } }
    }

    /// <summary>Another window was activated: hide unless pinned, a dialog of ours is open, or an approval is pending.</summary>
    public void OnDeactivated()
    {
        if (IsPinned || _modalDepth > 0) return;
        Hide();
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
        // V9: opening the panel on another folder means a new task there, not a follow-up.
        if (Continuation is { } chip && ContextChips.FirstOrDefault(c => c.Kind == ContextKind.Folder) is { } here && !SameFolder(here.Value, chip.Folder))
            ClearContinuation();
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
        OnPropertyChanged(nameof(IsContinuing));
        OnPropertyChanged(nameof(SuggestionText));
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
        if (IsContinuing)
        {
            _ = ContinueSessionAsync(PromptText.Trim());
            return;
        }
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
        var attached = AttachmentChips.Where(c => c.IsIncluded).ToList();
        JobSpec spec = SpecFor(tile.Id, folder, prompt, launch) with { NeverDowngrade = KeepSubscription, Attachments = attached.Select(c => c.Attachment!).ToList() };
        return await StartAndShowAsync(spec, attached, $"Started: {spec.Title}").ConfigureAwait(true);
    }

    private async Task<RunningJob?> StartAndShowAsync(JobSpec spec, IReadOnlyList<ContextChipViewModel> attached, string started)
    {
        try
        {
            var job = await _runtime.StartJobAsync(spec).ConfigureAwait(true);
            _ui.Post(() =>
            {
                // The job owns these now: the chips go, the staged copies stay for it to read.
                foreach (var c in attached) ContextChips.Remove(c);
                OnPropertyChanged(nameof(AttachmentChips));
                OnJobChanged(job);
                StatusText = started;
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

    // ── V9: session continuation (`claude --resume`) ─────────────────────────────────────────────────

    private ContinuationViewModel? _continuation;

    /// <summary>The "↩ Continue session" chip, when the last headless Claude run can be continued; null otherwise.</summary>
    public ContinuationViewModel? Continuation
    {
        get => _continuation;
        private set
        {
            if (_continuation is { } old) old.PropertyChanged -= OnContinuationChanged;
            if (!SetProperty(ref _continuation, value)) return;
            if (value is not null) value.PropertyChanged += OnContinuationChanged;
            OnPropertyChanged(nameof(HasContinuation));
            ClearContinuationCommand.Refresh();
            UpdateSuggestion();
        }
    }

    public bool HasContinuation => Continuation is not null;

    /// <summary>The next Enter continues the session: the chip is selected and the prompt is not a "/" command.</summary>
    public bool IsContinuing => Continuation is { IsSelected: true } && !PromptText.TrimStart().StartsWith('/');

    public RelayCommand ClearContinuationCommand { get; }

    private void OnContinuationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContinuationViewModel.IsSelected)) UpdateSuggestion();
    }

    public void ClearContinuation(string? status = null)
    {
        if (Continuation is null) return;
        Continuation = null;
        if (status is not null) StatusText = status;
    }

    private static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Sends <paramref name="prompt"/> as a follow-up in the chip's session: same folder, tools and attachments (plus any
    /// new ones), `claude -p --resume &lt;id&gt;`, never on another subscription. A different folder named in the prompt
    /// starts fresh instead (and removes the chip). The chip goes while the follow-up runs and comes back with its
    /// session when it finishes.
    /// </summary>
    public async Task<RunningJob?> ContinueSessionAsync(string prompt)
    {
        if (Continuation is not { } chip) return null;
        if (ResolveFolder(prompt) is { } folder && !SameFolder(folder, chip.Folder))
        {
            ClearContinuation($"Different folder: starting a fresh session for {ShortName(folder)}.");
            return Route(PromptText) is { } tile ? await RunActionAsync(tile, PromptBody(PromptText)).ConfigureAwait(true) : null;
        }
        var attached = AttachmentChips.Where(c => c.IsIncluded).ToList();
        var spec = JobSpecFactory.Continue(chip.Session, prompt, UseTerminal ? LaunchMode.Interactive : LaunchMode.Headless, attached.Select(c => c.Attachment!).ToList());
        var job = await StartAndShowAsync(spec, attached, $"Continuing session {chip.Session.ShortId}: {chip.Session.Spec.Title}").ConfigureAwait(true);
        if (job is not null) _ui.Post(() => { if (ReferenceEquals(Continuation, chip)) Continuation = null; });
        return job;
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
            if (item.IsActive) SetFault(null); // a new job clears the last fault
        }
        item.Update(job);
        if (!item.IsActive && ContinuableSession.From(job) is { } session && (Continuation is null || Continuation.Session.EndedAt <= session.EndedAt))
            Continuation = new ContinuationViewModel(session);
        if (!item.IsActive)
        {
            // Any approval still showing for an ended job can no longer matter.
            foreach (var card in _approvalQueue.Where(c => c.JobId == job.JobId).ToList()) card.Cancel();
            if (job.State == JobState.Failed) StatusText = $"{job.Spec.Title} failed: {job.Detail}";
            if (job.State == JobState.Failed || job.Outcome is RunOutcome.AuthFailed or RunOutcome.RateLimited)
                SetFault(job.Outcome switch
                {
                    RunOutcome.RateLimited => $"{job.Spec.Title}: usage limit reached",
                    RunOutcome.AuthFailed => $"{job.Spec.Title}: the CLI is not signed in",
                    _ => $"{job.Spec.Title} failed: {job.Detail}",
                });
        }
        OnPropertyChanged(nameof(ActiveJobCount));
        OnPropertyChanged(nameof(JobsTabHeader));
        KillAllCommand.Refresh();
        UpdateReactor();
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
                SetFault($"{pill.Name}: {pill.Status}");
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
