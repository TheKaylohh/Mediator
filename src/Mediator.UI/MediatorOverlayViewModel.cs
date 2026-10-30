using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using Mediator.Core.Attachments;
using Mediator.Core.Bus;
using Mediator.Core.Context;
using Mediator.Core.Files;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.UI;

/// <summary>
/// The overlay: a JetBrains Toolbox–style panel opened with Ctrl+Shift+Space. A prompt box at the top routes
/// to an action (or filters the action list when it starts with "/"); context chips come from the window that
/// was in front when the hotkey was pressed; tabs show Actions, Jobs and Activity; approvals take over the
/// panel until answered; the red kill switch ends every running job.
/// It is also the runtime's <see cref="IApprovalPrompt"/>: every pending approval is a TaskCompletionSource that
/// one of the card's buttons resolves.
/// </summary>
public sealed partial class MediatorOverlayViewModel : ObservableObject, IApprovalPrompt
{
    public const int MaxActivity = 300;
    public const string ClaudeProvider = "claude-code";
    public const string GeminiProvider = "gemini-cli";

    private readonly IMediatorRuntime _runtime;
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
    private MediatorSettings _settings;
    private string? _fault;
    private ReactorState _reactorState;
    private bool _isDragOver;

    public const int MaxAttachments = 20;

    public MediatorOverlayViewModel(IMediatorRuntime runtime, IUiDispatcher ui, ISettingsStore? settings = null)
    {
        _runtime = runtime;
        _ui = ui;
        _settingsStore = settings ?? new MemorySettingsStore();
        _settings = _settingsStore.Load();

        Actions = new ObservableCollection<ActionTileViewModel>(DefaultActions().Concat((_settings.CustomActions ?? []).Select(CustomTile)));
        NewActionCommand = new RelayCommand(() => IsEditingAction = !IsEditingAction);
        SaveActionCommand = new RelayCommand(SaveNewAction);
        DeleteActionCommand = new RelayCommand(p => DeleteAction(p as ActionTileViewModel), p => p is ActionTileViewModel { IsCustom: true });
        FilteredActions = new ObservableCollection<ActionTileViewModel>(Actions);
        Providers.Add(new ProviderStatusViewModel(ClaudeProvider, "Claude Pro"));
        Providers.Add(new ProviderStatusViewModel(GeminiProvider, "Google AI Pro"));

        ToggleCommand = new RelayCommand(() => { if (IsVisible) Hide(); else Show(); });
        HideCommand = new RelayCommand(Hide);
        OpenFullViewCommand = new RelayCommand(() => { Hide(); OpenFullViewRequested?.Invoke(); });
        SubmitPromptCommand = new RelayCommand(SubmitPrompt, () => PromptText.Trim().Length > 0 && !IsChatBusy);
        StopCommand = new RelayCommand(StopChat, () => IsChatBusy);
        EffortOptions = new ObservableCollection<string>(["Auto", .. ModelCatalog.Efforts]);
        ModelOptions.Add(ModelOption.Auto);
        RunActionCommand = new RelayCommand(p => { if (p is ActionTileViewModel tile) _ = RunActionAsync(tile, PromptText); }, p => p is ActionTileViewModel { IsEnabled: true });
        KillAllCommand = new RelayCommand(KillAll, () => Jobs.Any(j => j.IsActive));
        DismissBannerCommand = new RelayCommand(() => BillingBanner = null);
        CycleThemeCommand = new RelayCommand(CycleTheme);
        ToggleLightDarkCommand = new RelayCommand(ToggleLightDark);
        AcknowledgeFaultCommand = new RelayCommand(() => SetFault(null), () => _fault is not null);
        AttachCommand = new RelayCommand(() => BrowseForAttachmentsRequested?.Invoke());
        ChooseSaveFolderCommand = new RelayCommand(() => BrowseForSaveFolderRequested?.Invoke());
        ClearSaveFolderCommand = new RelayCommand(() => SetSaveFolder(null), () => SaveFolder is not null);
        ClearConversationCommand = new RelayCommand(() =>
        {
            Conversation.Clear(); _turns.Clear(); _chatId = null; _lastSession = null;
            _chatTitle = null; _chatPinned = false; OnPropertyChanged(nameof(ChatTitle)); OnPropertyChanged(nameof(IsChatPinned));
            ChatFolders.Clear(); ClearFolderRequest(); _softwareContext = null;
            ClearContinuation("New chat."); RefreshBusy();
        });
        OpenSavedChatCommand = new RelayCommand(p => OpenSavedChat(p as SavedChat), p => p is SavedChat);
        DeleteSavedChatCommand = new RelayCommand(p => { if (p is SavedChat c) { _chats?.Delete(c.Id); if (c.Id == _chatId) _chatId = null; RefreshSavedChats(); } }, p => p is SavedChat);
        PinSavedChatCommand = new RelayCommand(p => { if (p is SavedChat c) SetPinned(c, !c.Pinned); }, p => p is SavedChat);
        TogglePinCommand = new RelayCommand(() => IsChatPinned = !IsChatPinned, () => Conversation.Count > 0);
        RetryCommand = new RelayCommand(Retry, () => !IsChatBusy && Conversation.Count > 0);
        EditTurnCommand = new RelayCommand(p => { if (p is ChatTurnViewModel t) EditTurn(t); }, p => p is ChatTurnViewModel);
        CycleRoutingPolicyCommand = new RelayCommand(() => RoutingPolicy = (RoutingPolicy)(((int)RoutingPolicy + 1) % 3));
        Conversation.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasConversation)); ClearConversationCommand.Refresh(); RetryCommand.Refresh(); TogglePinCommand.Refresh(); if (_chatTitle is null) OnPropertyChanged(nameof(ChatTitle)); };
        InitFolderCommands();
        ClearContinuationCommand = new RelayCommand(() => ClearContinuation("Starting fresh: the next prompt opens a new session."), () => Continuation is not null);

        _runtime.JobChanged += job => _ui.Post(() => OnJobChanged(job));
        _runtime.ToolCallActivity += e => _ui.Post(() =>
        {
            AddActivity(new ActivityItemViewModel(DateTimeOffset.UtcNow, e.JobId, e.Tool, ToolPhase.Describe(e), e.Detail ?? "", ToolPhase.IsProblem(e), ToolPhase.IsUncertain(e)));
            OnToolRefusedFolder(e);
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
        _ = LoadModelOptionsAsync();
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

    /// <summary>0 Actions, 1 Jobs, 2 Activity, 3 Connectors, 4 Usage.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            if (value == ConnectorsTab && Connectors is { IsBusy: false } c) _ = c.RefreshAsync();
            if (value == UsageTab && Usage is { IsBusy: false } u) _ = u.RefreshAsync();
        }
    }
    public const int ConnectorsTab = 3;
    public const int UsageTab = 4;

    /// <summary>The Usage tab (V14): 5-hour and weekly use per subscription. Set by the host; null in hosts without one.</summary>
    public UsageViewModel? Usage { get; set; }

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

    /// <summary>Which subscription Enter would use, or why none can ("Google AI Pro (Antigravity CLI) — Claude Pro is at its limit until 15:00").</summary>
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
    public MediatorTheme CurrentTheme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value || !Enum.IsDefined(value)) return;
            UpdateSettings(_settings with { Theme = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeDisplayName));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(LightDarkGlyph));
            OnPropertyChanged(nameof(LightDarkText));
            ThemeChanged?.Invoke(value);
        }
    }

    public string ThemeDisplayName => ThemeName(CurrentTheme);
    public static string ThemeName(MediatorTheme t) => t switch
    {
        MediatorTheme.Dark => "Dark",
        MediatorTheme.Light => "Light",
        MediatorTheme.HoloHud => "Holo HUD (classic)",
        MediatorTheme.ProDark => "Pro Dark (classic)",
        MediatorTheme.CleanLight => "Clean Light (classic)",
        _ => t.ToString(),
    };
    public static IReadOnlyList<MediatorTheme> Themes { get; } = Enum.GetValues<MediatorTheme>();

    public event Action<MediatorTheme>? ThemeChanged;

    /// <summary>Cycles every theme, the classics included: Dark → Light → Holo HUD → Pro Dark → Clean Light → Dark.</summary>
    public void CycleTheme() => CurrentTheme = (MediatorTheme)(((int)CurrentTheme + 1) % Themes.Count);

    /// <summary>V18: the light theme (or the Clean Light classic) is on.</summary>
    public bool IsLightTheme => ThemeSources.IsLight(CurrentTheme);

    /// <summary>V18: the sun/moon button in both windows: a light theme goes to Dark, anything else to Light.</summary>
    public void ToggleLightDark() => CurrentTheme = IsLightTheme ? MediatorTheme.Dark : MediatorTheme.Light;

    public RelayCommand ToggleLightDarkCommand { get; }

    /// <summary>The toggle's glyph: a moon in a light theme (go dark), a sun in a dark one (go light).</summary>
    public string LightDarkGlyph => IsLightTheme ? "\uE708" : "\uE706";

    /// <summary>The toggle's tooltip.</summary>
    public string LightDarkText => IsLightTheme ? "Switch to dark mode" : "Switch to light mode";

    public MediatorSettings Settings => _settings;

    // ── V16.2: the workspace window ──────────────────────────────────────────────────────────────────

    /// <summary>The full-screen workspace (three regions, six right-sidebar modes) around this conversation.</summary>
    public Workspace.WorkspaceViewModel Workspace => _workspace ??= new Workspace.WorkspaceViewModel(this, () => _settings.Workspace,
        prefs => UpdateSettings(_settings with { Workspace = prefs }), WorkspaceHost);
    private Workspace.WorkspaceViewModel? _workspace;

    /// <summary>Set by the host before the workspace is first used (opening files, starting programs, the quick panel's tabs).</summary>
    public Workspace.IWorkspaceHost? WorkspaceHost { get; set; }

    /// <summary>Approvals waiting for the user, the one shown included.</summary>
    public int PendingApprovalCount => _approvalQueue.Count;

    public void NotifyStatus(string text) => StatusText = text;

    private string? _softwareContext;

    /// <summary>
    /// V16.2: a new chat about one desktop program (Software Library). Its tools are routed as if the program were in front
    /// (<paramref name="process"/>), a prefix picks the tool set for Office or Chrome, and the program's identity, status and what
    /// Mediator can do with it are given to the model with every prompt of the chat.
    /// </summary>
    public void StartAppChat(string name, string? process, string? promptPrefix, string context)
    {
        if (ClearConversationCommand.CanExecute(null)) ClearConversationCommand.Execute(null);
        foreach (var chip in ContextChips.Where(c => c.Kind == ContextKind.App).ToList()) ContextChips.Remove(chip);
        _activeApp = process;
        if (process is not null) ContextChips.Add(new ContextChipViewModel(ContextKind.App, name, context));
        _softwareContext = context;
        PromptText = promptPrefix ?? "";
        StatusText = $"New chat about {name}.";
    }

    /// <summary>Saves settings (theme, hotkey, first run). A save failure is reported, never thrown at the UI.</summary>
    public void UpdateSettings(MediatorSettings settings)
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

    /// <summary>
    /// Shows the quick panel, unless the full-screen app is open: V18, the two are never on screen together, so the hotkey,
    /// the tray icon and an approval bring the full-screen app forward instead.
    /// </summary>
    public void Show()
    {
        if (IsWorkspaceOpen)
        {
            IsVisible = false;
            WorkspaceActivationRequested?.Invoke();
            return;
        }
        IsVisible = true;
        _ = RefreshProvidersAsync();
    }

    private bool _isWorkspaceOpen;

    /// <summary>
    /// V18: the full-screen app is open (shown, even minimized). Set by its window. Opening it hides the quick panel; while it is
    /// open the quick panel does not show (see <see cref="Show"/>).
    /// </summary>
    public bool IsWorkspaceOpen
    {
        get => _isWorkspaceOpen;
        set
        {
            if (!SetProperty(ref _isWorkspaceOpen, value)) return;
            if (value) IsVisible = false;
        }
    }

    /// <summary>V18: bring the full-screen app forward (restore it if minimized).</summary>
    public event Action? WorkspaceActivationRequested;
    /// <summary>V18: close the full-screen app (it hides; Mediator keeps running in the tray).</summary>
    public event Action? WorkspaceCloseRequested;

    /// <summary>
    /// V18: the full-screen app asked for one of the quick panel's tabs (settings, usage, connectors): it closes, and the quick
    /// panel opens on that tab. "Open full view" there brings the full-screen app back.
    /// </summary>
    public void ShowPanelTab(int tab)
    {
        SelectedTab = tab;
        WorkspaceCloseRequested?.Invoke();
        IsWorkspaceOpen = false; // the window reports it too; a host without one must not keep the panel hidden
        Show();
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

    private sealed class ModalScope(MediatorOverlayViewModel vm) : IDisposable
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
        _activeApp = snapshot?.ProcessName;
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

    // ── User-made actions ([ + New action ]) ──────────────────────────────────────────────────────────

    private bool _isEditingAction, _newActionNeedsFolder;
    private string _newActionTitle = "", _newActionDescription = "", _newActionInstructions = "";
    public bool IsEditingAction { get => _isEditingAction; set => SetProperty(ref _isEditingAction, value); }
    public string NewActionTitle { get => _newActionTitle; set => SetProperty(ref _newActionTitle, value ?? ""); }
    public string NewActionDescription { get => _newActionDescription; set => SetProperty(ref _newActionDescription, value ?? ""); }
    public string NewActionInstructions { get => _newActionInstructions; set => SetProperty(ref _newActionInstructions, value ?? ""); }
    public bool NewActionNeedsFolder { get => _newActionNeedsFolder; set => SetProperty(ref _newActionNeedsFolder, value); }
    public RelayCommand NewActionCommand { get; }
    public RelayCommand SaveActionCommand { get; }
    public RelayCommand DeleteActionCommand { get; }

    private static ActionTileViewModel CustomTile(CustomActionDef d) =>
        new(d.Id, "", d.Title, d.Description.Length > 0 ? d.Description : d.Instructions, TaskCategory.Chat,
            d.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries), d.NeedsFolder, []) { Instructions = d.Instructions };

    /// <summary>A user-made action: an assistant job whose prompt starts with the saved instructions; tools inferred from both.</summary>
    public static JobSpec CustomSpec(ActionTileViewModel tile, string? folder, string prompt, LaunchMode launch)
    {
        var (tools, category) = JobSpecFactory.InferToolsAndCategory(tile.Instructions + " " + prompt, null);
        var spec = JobSpecFactory.Assistant(folder, prompt.Length > 0 ? prompt : tile.Title, launch, tools, category);
        return spec with { Title = tile.Title, Prompt = $"Saved action \"{tile.Title}\". Follow these instructions: {tile.Instructions}\n\n{spec.Prompt}" };
    }

    private void SaveNewAction()
    {
        string title = NewActionTitle.Trim(), instructions = NewActionInstructions.Trim();
        if (title.Length == 0 || instructions.Length == 0) { StatusText = "Give the new action a name and instructions."; return; }
        var def = new CustomActionDef("custom-" + Guid.NewGuid().ToString("N")[..8], title, NewActionDescription.Trim(), instructions, NewActionNeedsFolder);
        UpdateSettings(_settings with { CustomActions = [.. _settings.CustomActions ?? [], def] });
        Actions.Add(CustomTile(def));
        ApplyFilter();
        NewActionTitle = NewActionDescription = NewActionInstructions = "";
        NewActionNeedsFolder = false;
        IsEditingAction = false;
        StatusText = $"Saved action: {title}";
    }

    private void DeleteAction(ActionTileViewModel? tile)
    {
        if (tile is not { IsCustom: true }) return;
        UpdateSettings(_settings with { CustomActions = (_settings.CustomActions ?? []).Where(d => d.Id != tile.Id).ToList() });
        Actions.Remove(tile);
        ApplyFilter();
        StatusText = $"Deleted action: {tile.Title}";
    }

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

    // -- V10: one prompt at a time per chat, Stop, and the model / effort picker --

    private const string ChatBusyMessage = "This chat is still working on your last prompt. Wait for it, or press Stop.";
    /// <summary>V16.1: what a terminal job's chat card says while it runs.</summary>
    public const string TerminalNote = "Running in its own terminal window (PowerShell): the answer and any questions appear there, not here. " +
        "Close that window, or press Stop in Jobs, to end it.";
    private const string StopReason = "stopped by the user";
    private bool _sending;

    private void SetSending(bool value)
    {
        _sending = value;
        RefreshBusy();
    }

    /// <summary>True while a prompt of this chat is starting or running: Send is off and Stop is on.</summary>
    public bool IsChatBusy => _sending || Conversation.Any(t => t.IsRunning);
    public RelayCommand StopCommand { get; }

    private void RefreshBusy()
    {
        OnPropertyChanged(nameof(IsChatBusy));
        SubmitPromptCommand.Refresh();
        StopCommand.Refresh();
        RetryCommand.Refresh();
    }

    /// <summary>Stops the running prompt of this chat: its CLI is killed and its grant revoked.</summary>
    public void StopChat()
    {
        var running = Conversation.Where(t => t.IsRunning).ToList();
        foreach (var t in running)
        {
            _runtime.KillJob(t.JobId, StopReason);
            t.IsRunning = false;
            t.ThinkingSummary = "Stopped";
            if (t.ResponseText.Length == 0) t.ResponseText = "Stopped.";
        }
        StatusText = running.Count == 0 ? "Nothing running in this chat." : "Stopped.";
        RefreshBusy();
    }

    /// <summary>Auto, then every model of every installed CLI.</summary>
    public ObservableCollection<ModelOption> ModelOptions { get; } = new();
    /// <summary>Auto, low, medium, high, xhigh, max.</summary>
    public ObservableCollection<string> EffortOptions { get; }

    private ModelOption _selectedModel = ModelOption.Auto;
    public ModelOption SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetProperty(ref _selectedModel, value ?? ModelOption.Auto)) return;
            UpdateSettings(_settings with { ModelProvider = _selectedModel.ProviderKey, ModelId = _selectedModel.Model });
        }
    }

    public string SelectedEffort
    {
        get => _settings.Effort ?? "Auto";
        set
        {
            string? e = ModelCatalog.IsEffort(value) ? value : null;
            if (e == _settings.Effort) return;
            UpdateSettings(_settings with { Effort = e });
            OnPropertyChanged();
        }
    }

    /// <summary>Fills the picker from the installed CLIs and selects the saved choice.</summary>
    public async Task LoadModelOptionsAsync()
    {
        IReadOnlyList<CliModels> found;
        try { found = await _runtime.DetectModelsAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { found = []; }
        _ui.Post(() =>
        {
            foreach (var cli in found)
                foreach (var m in cli.Models)
                    if (!ModelOptions.Any(o => o.Provider == cli.Provider && o.Model == m)) ModelOptions.Add(new ModelOption(cli.Cli + " · " + m, cli.Provider, m));
            _selectedModel = ModelOptions.FirstOrDefault(o => o.ProviderKey == _settings.ModelProvider && o.Model == _settings.ModelId) ?? ModelOption.Auto;
            OnPropertyChanged(nameof(SelectedModel));
        });
    }

    /// <summary>The picked model and effort on a job. A model fixes the CLI; Auto leaves both to the routing rules.</summary>
    public JobSpec ApplyModelPick(JobSpec spec)
    {
        if (SelectedModel.Provider is { } p) spec = spec with { Provider = p, Model = SelectedModel.Model, ModelFor = p };
        if (_settings.Effort is { } e) spec = spec with { Effort = e };
        return spec;
    }

    private void SubmitPrompt()
    {
        if (IsChatBusy) { StatusText = ChatBusyMessage; return; }
        if (IsContinuing)
        {
            _ = ContinueSessionAsync(PromptText.Trim());
            return;
        }
        string text = PromptText.Trim();
        AiProvider? provider = null;
        if (ProviderRouter.ParseProviderPrefix(text) is { } chosen) { provider = chosen.Provider; text = chosen.Remainder; }
        if (text.Length == 0) return;
        bool terminal = UseTerminal || text.StartsWith("/terminal", StringComparison.OrdinalIgnoreCase) || text.Contains("claude-terminal", StringComparison.OrdinalIgnoreCase);
        if (text.StartsWith("/terminal", StringComparison.OrdinalIgnoreCase)) text = text[9..].Trim();
        // "/search …" and "/ask …" force a web search or a plain answer: no action tile, no folder, no local tools.
        if (!terminal && ProviderRouter.ParsePrefix(text) is { Category: TaskCategory.Search or TaskCategory.Chat or TaskCategory.Office } ask)
        {
            // V15: "/office …" and "/outlook …" have no tile: an assistant job with the Office tools.
            if (ask.Remainder.Length > 0) _ = RunAssistantAsync(ask.Remainder, provider, ask.Category == TaskCategory.Office ? Mediator.Core.Connectors.ConnectorCatalog.Find("office")?.ChatTools ?? [] : [], ask.Category);
            return;
        }
        var tile = Route(text);
        if (tile is null) return;
        if (terminal) { _ = RunActionAsync(tile, PromptBody(text), provider); return; }

        // V10: never a terminal unless asked. A specific action with its folder runs as before (streamed into the
        // conversation); anything else (general questions, no folder, several kinds of tools) is a headless assistant job.
        string body = PromptBody(text);
        var (tools, category) = JobSpecFactory.InferToolsAndCategory(body, _activeApp);
        string? folder = ResolveFolder(body);
        // V14.1: a CAD tile runs only for a CAD request. Tile keywords match inside words ("material" has "mate", "github"
        // has "hub", "step" and "create" are everywhere in code talk), and a CAD tile starts SolidWorks.
        bool specific = category != TaskCategory.Browser && tile.Id != "claude-terminal" && (!tile.NeedsFolder || folder is not null) && tools.All(t => tile.Tools.Contains(t))
            && (tile.Category != TaskCategory.Cad || category == TaskCategory.Cad);
        if (specific) _ = RunActionAsync(tile, body, provider);
        else _ = RunAssistantAsync(body, provider, tools, category);
    }

    // ── V10: in-app conversation ─────────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, ChatTurnViewModel> _turns = new(StringComparer.Ordinal);
    private string? _activeApp;

    /// <summary>Every headless run as a chat turn: prompt, CLI · model, steps, full answer.</summary>
    public ObservableCollection<ChatTurnViewModel> Conversation { get; } = new();
    public bool HasConversation => Conversation.Count > 0;

    /// <summary>Opens the full-screen chat window (same conversation, prompt and attachments as the overlay).</summary>
    public RelayCommand OpenFullViewCommand { get; }

    // ── Saved chats: prompts, CLI answers and the resumable session, one JSON file per chat ─────────

    private ChatStore? _chats;
    private string? _chatId;                 // the open chat's file; null until its first answer is saved
    private ContinuableSession? _lastSession; // the open chat's latest resumable session (kept even if the chip is dismissed)

    /// <summary>Where chats are saved; null (tests) turns persistence off.</summary>
    public ChatStore? Chats
    {
        get => _chats;
        set
        {
            _chats = value;
            RefreshSavedChats();
            // Start where you left off: the most recent chat is reopened (and resumable) when the app starts.
            if (Conversation.Count == 0 && SavedChats.Count > 0) OpenSavedChat(SavedChats[0], quiet: true);
        }
    }
    public ObservableCollection<SavedChat> SavedChats { get; } = new();
    public RelayCommand OpenSavedChatCommand { get; }
    public RelayCommand DeleteSavedChatCommand { get; }

    private void RefreshSavedChats()
    {
        SavedChats.Clear();
        if (_chats is null) return;
        string q = _chatSearch.Trim();
        foreach (var c in _chats.List().Where(c => c.Matches(q))) SavedChats.Add(c);
    }

    // ── V10: chat-app features (search, rename, star, retry, edit, recall), as in the Claude app ─────

    private string _chatSearch = "";
    private string? _chatTitle;   // typed title of the open chat (null: the first prompt)
    private bool _chatPinned;

    /// <summary>Filters the sidebar by title, prompts and answers.</summary>
    public string ChatSearch
    {
        get => _chatSearch;
        set { if (SetProperty(ref _chatSearch, value ?? "")) RefreshSavedChats(); }
    }

    /// <summary>The open chat's title; typing a new one renames it (saved at once when the chat exists).</summary>
    public string ChatTitle
    {
        get => _chatTitle ?? DefaultTitle();
        set
        {
            string v = (value ?? "").Trim();
            string? custom = v.Length == 0 || v == DefaultTitle() ? null : v;
            if (custom == _chatTitle) return;
            _chatTitle = custom;
            OnPropertyChanged();
            if (_chatId is not null) SaveChat();
        }
    }

    /// <summary>Starred chats are listed first.</summary>
    public bool IsChatPinned
    {
        get => _chatPinned;
        set
        {
            if (!SetProperty(ref _chatPinned, value)) return;
            if (_chatId is not null) SaveChat();
        }
    }

    public RelayCommand PinSavedChatCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    /// <summary>Runs the last prompt again (its answer is replaced).</summary>
    public RelayCommand RetryCommand { get; }
    /// <summary>Puts a turn's prompt back in the composer to change and resend.</summary>
    public RelayCommand EditTurnCommand { get; }

    private string DefaultTitle()
    {
        string first = Conversation.Count > 0 ? Conversation[0].UserPrompt.ReplaceLineEndings(" ").Trim() : "New chat";
        return first.Length > 60 ? first[..60] + "…" : first;
    }

    private void SetPinned(SavedChat chat, bool pinned)
    {
        if (chat.Id == _chatId) { IsChatPinned = pinned; return; }
        try { _chats?.Save(chat with { Pinned = pinned }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = "Could not save the chat: " + ex.Message; return; }
        RefreshSavedChats();
    }

    private void Retry()
    {
        if (IsChatBusy || Conversation.Count == 0) return;
        var last = Conversation[^1];
        Conversation.RemoveAt(Conversation.Count - 1);
        _turns.Remove(last.JobId);
        PromptText = last.UserPrompt;
        SubmitPrompt();
    }

    private void EditTurn(ChatTurnViewModel turn)
    {
        PromptText = turn.UserPrompt;
        FocusPromptRequested?.Invoke();
    }

    /// <summary>Up arrow in an empty composer: the last prompt, to change and resend. False when there is none.</summary>
    public bool RecallLastPrompt()
    {
        if (PromptText.Length > 0 || Conversation.Count == 0) return false;
        PromptText = Conversation[^1].UserPrompt;
        return true;
    }

    /// <summary>The newest answer (for Ctrl+Shift+C).</summary>
    public string? LastResponse => Conversation.LastOrDefault(t => t.ResponseText.Length > 0)?.ResponseText;


    private void SaveChat()
    {
        if (_chats is null || Conversation.Count == 0) return;
        _chatId ??= ChatStore.NewId();
        var turns = Conversation.Select(t => new SavedTurn(t.JobId, t.UserPrompt, t.BadgeText, t.ResponseText, t.Steps.ToList(), t.IsError)).ToList();
        var resume = (Continuation?.Session ?? _lastSession) is { } s ? SavedResume.From(s) : null;
        try { _chats.Save(new SavedChat(_chatId, DefaultTitle(), DateTimeOffset.Now, turns, resume, _chatPinned, _chatTitle, ChatFolders.ToList())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = "Could not save the chat: " + ex.Message; return; }
        RefreshSavedChats();
    }

    /// <summary>Earlier turns as plain text (the tail, under 4000 chars) for CLIs that cannot resume natively.</summary>
    private static string History(IEnumerable<SavedTurn> turns)
    {
        string all = string.Join("\n\n", turns.Select(t => "User: " + t.Prompt + "\nAssistant: " + t.Response));
        return all.Length <= 3900 ? all : "…" + all[^3900..];
    }

    private void OpenSavedChat(SavedChat? chat, bool quiet = false)
    {
        if (chat is null) return;
        Conversation.Clear();
        _turns.Clear();
        foreach (var t in chat.Turns)
        {
            var turn = new ChatTurnViewModel(t.JobId, t.Prompt, t.Badge) { ResponseText = t.Response, IsError = t.IsError, IsRunning = false };
            foreach (var step in t.Steps) turn.Steps.Add(step);
            Conversation.Add(turn);
        }
        _chatId = chat.Id;
        _chatTitle = chat.CustomTitle;
        _chatPinned = chat.Pinned;
        ChatFolders.Clear();
        foreach (var f in chat.Folders ?? []) ChatFolders.Add(f);
        ClearFolderRequest();
        OnPropertyChanged(nameof(ChatTitle));
        OnPropertyChanged(nameof(IsChatPinned));
        try { _lastSession = chat.Resume?.ToSession(chat.Updated, History(chat.Turns)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _lastSession = null; }
        Continuation = _lastSession is null ? null : new ContinuationViewModel(_lastSession);
        if (quiet) return;
        StatusText = _lastSession is null ? $"Opened \"{chat.Title}\" (read-only: no session to resume)." : $"Opened \"{chat.Title}\": your next prompt continues it.";
        SelectedTab = 0;
    }
    public event Action? OpenFullViewRequested;
    public RelayCommand ClearConversationCommand { get; }
    public RelayCommand CycleRoutingPolicyCommand { get; }

    /// <summary>Which subscription goes first when none is picked; saved in settings.json.</summary>
    public RoutingPolicy RoutingPolicy
    {
        get => _settings.RoutingPolicy;
        set
        {
            if (_settings.RoutingPolicy == value) return;
            UpdateSettings(_settings with { RoutingPolicy = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoutingPolicyText));
        }
    }

    public string RoutingPolicyText => RoutingPolicyName(RoutingPolicy);
    public static string RoutingPolicyName(RoutingPolicy p) => p switch
    {
        RoutingPolicy.ConserveClaude => "Quota Saver (Antigravity)",
        RoutingPolicy.PreferClaude => "Claude First",
        _ => "Auto Best",
    };

    /// <summary>A headless "just answer" job: the folder from the prompt or context if any (else no file access), inferred tools.</summary>
    public Task<RunningJob?> RunAssistantAsync(string prompt, AiProvider? provider = null, IReadOnlyList<string>? tools = null, TaskCategory? category = null)
    {
        var inferred = tools is null || category is null ? JobSpecFactory.InferToolsAndCategory(prompt, _activeApp) : (tools, category.Value);
        var attached = AttachmentChips.Where(c => c.IsIncluded).ToList();
        var spec = JobSpecFactory.Assistant(ResolveFolder(prompt), prompt, LaunchMode.Headless, inferred.Item1, inferred.Item2, _runtime.ChatDirectory)
            with { Provider = provider, NeverDowngrade = KeepSubscription, RoutingPolicy = RoutingPolicy, Attachments = attached.Select(c => c.Attachment!).ToList() };
        return StartAndShowAsync(spec, attached, $"Asking: {spec.Title}", stayOnActions: true);
    }

    private ChatTurnViewModel? TurnFor(string jobId) => _turns.GetValueOrDefault(jobId);

    /// <summary>"Web search · Claude Code · haiku": the route the prompt took, the CLI and the model.</summary>
    private static string Badge(RunningJob job) =>
        ProviderRouter.CategoryName(job.Spec.Category) + " · " + (job.Provider is { } p ? (p == AiProvider.ClaudeCode ? "Claude Code" : "Antigravity CLI") : "…") + " · " + (job.Model ?? job.Spec.Model ?? "default") + ((job.Effort ?? job.Spec.Effort) is { } e ? " · " + e : "");

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


    /// <summary>A folder named in the prompt wins over the Explorer chip; then the first included folder chip.</summary>
    // ── Save to…: the folder every job saves into, until cleared ─────────────────────────────────────────

    public RelayCommand ChooseSaveFolderCommand { get; }
    public RelayCommand ClearSaveFolderCommand { get; }
    /// <summary>The view opens a folder picker and calls <see cref="SetSaveFolder"/>.</summary>
    public event Action? BrowseForSaveFolderRequested;
    public string? SaveFolder => _settings.SaveFolder is { } f && Directory.Exists(f) ? f : null;
    public bool HasSaveFolder => SaveFolder is not null;
    public string SaveFolderText => SaveFolder is { } f ? "Saving to " + f : "";

    public void SetSaveFolder(string? folder)
    {
        UpdateSettings(_settings with { SaveFolder = folder is null ? null : Path.GetFullPath(folder) });
        OnPropertyChanged(nameof(SaveFolder));
        OnPropertyChanged(nameof(HasSaveFolder));
        OnPropertyChanged(nameof(SaveFolderText));
        ClearSaveFolderCommand.Refresh();
        StatusText = folder is null ? "Save folder cleared: jobs use the folder selected in Explorer." : $"Jobs now save to {folder}";
        if (SaveFolder is { } granted) _ = GrantRunningAsync(granted);
    }

    /// <summary>Save to while a turn runs: that job may write there at once (later turns get it as a root).</summary>
    private async Task GrantRunningAsync(string folder)
    {
        foreach (var turn in Conversation.Where(t => t.IsRunning).ToList())
            if (await _runtime.GrantFolderAsync(turn.JobId, folder).ConfigureAwait(true) is { } why) StatusText = $"Save to {folder}: {why}";
    }

    /// <summary>The job's folder: a path typed in the prompt, else the Save to… folder, else the Explorer selection.</summary>
    public string? ResolveFolder(string prompt)
    {
        if (PromptPaths.TargetFolder(prompt) is { } named) return named;
        return SaveFolder
            ?? ContextChips.FirstOrDefault(c => c.IsIncluded && c.Kind == ContextKind.Folder)?.Value
            ?? ContextChips.Where(c => c.IsIncluded && c.Kind == ContextKind.File).Select(c => Path.GetDirectoryName(c.Value)).FirstOrDefault(d => d is not null)
            // A follow-up ("now create the parts and assemble them") works where the conversation already said: the newest
            // folder an earlier message named, then the chat's allowed folders.
            ?? Conversation.Reverse().Select(t => PromptPaths.TargetFolder(t.UserPrompt)).FirstOrDefault(f => f is not null)
            ?? ChatFolders.LastOrDefault();
    }

    public async Task<RunningJob?> RunActionAsync(ActionTileViewModel tile, string prompt, AiProvider? provider = null)
    {
        if (!tile.IsEnabled) { StatusText = tile.DisabledReason; return null; }
        string? folder = ResolveFolder(prompt);
        if (tile.NeedsFolder && folder is null)
        {
            StatusText = "Which folder? Click Save to… and pick one, select it in File Explorer before pressing the hotkey, or put its path in the prompt.";
            return null;
        }
        var launch = UseTerminal ? LaunchMode.Interactive : LaunchMode.Headless;
        var attached = AttachmentChips.Where(c => c.IsIncluded).ToList();
        JobSpec spec = (tile.IsCustom ? CustomSpec(tile, folder, prompt, launch) : SpecFor(tile.Id, folder, prompt, launch)) with { NeverDowngrade = KeepSubscription, Attachments = attached.Select(c => c.Attachment!).ToList(), RoutingPolicy = RoutingPolicy };
        if (provider is not null) spec = spec with { Provider = provider };
        if (prompt.Length > 0 && spec.UserText is null) spec = spec with { UserText = prompt };
        return await StartAndShowAsync(spec, attached, $"Started: {spec.Title}").ConfigureAwait(true);
    }

    // ── V10: folder permissions. A chat keeps the folders the user allowed (Allow folder, a job's folder request, Save to);
    //    every job the chat starts, new or follow-up, may read and write them, and a running job gets them at once. ─────

    /// <summary>Folders allowed for this chat (saved with it).</summary>
    public ObservableCollection<string> ChatFolders { get; } = new();
    private string? _folderRequest;

    /// <summary>A folder a job was refused (it is outside its allowed folders); the view offers Allow / Dismiss.</summary>
    public string? FolderRequest
    {
        get => _folderRequest;
        private set
        {
            if (!SetProperty(ref _folderRequest, value)) return;
            OnPropertyChanged(nameof(HasFolderRequest));
            OnPropertyChanged(nameof(FolderRequestText));
        }
    }
    public bool HasFolderRequest => _folderRequest is not null;
    public string FolderRequestText => _folderRequest is { } f ? $"The job asked to use {f}, which it is not allowed to." : "";

    public RelayCommand AllowFolderRequestCommand { get; private set; } = null!;
    public RelayCommand DismissFolderRequestCommand { get; private set; } = null!;
    /// <summary>Opens a folder picker; the picked folder is allowed for this chat.</summary>
    public RelayCommand AllowFolderCommand { get; private set; } = null!;
    public RelayCommand RemoveChatFolderCommand { get; private set; } = null!;
    public event Action? BrowseForAllowFolderRequested;

    private void InitFolderCommands()
    {
        AllowFolderRequestCommand = new RelayCommand(() => { if (_folderRequest is { } f) _ = AllowFolderAsync(f); }, () => HasFolderRequest);
        DismissFolderRequestCommand = new RelayCommand(ClearFolderRequest, () => HasFolderRequest);
        AllowFolderCommand = new RelayCommand(() => BrowseForAllowFolderRequested?.Invoke());
        RemoveChatFolderCommand = new RelayCommand(p => { if (p is string f && ChatFolders.Remove(f)) { SaveChat(); StatusText = $"{f} is no longer allowed for new turns of this chat."; } }, p => p is string);
    }

    private void ClearFolderRequest()
    {
        FolderRequest = null;
        AllowFolderRequestCommand?.Refresh();
        DismissFolderRequestCommand?.Refresh();
    }

    /// <summary>"[path:outside_roots] C:\…\F40: outside this job's allowed folders…" in a failed tool call: the folder to offer.</summary>
    public static string? RefusedFolder(string? detail)
    {
        const string Marker = "[path:outside_roots] ";
        int at = detail?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
        if (at < 0) return null;
        int start = at + Marker.Length, end = detail!.IndexOf(": outside", start, StringComparison.Ordinal);
        if (end <= start) return null;
        string path = detail[start..end].Trim();
        // A file (has an extension and is not a folder): allow the folder it would be in.
        return Path.HasExtension(path) && !Directory.Exists(path) ? Path.GetDirectoryName(path) : path;
    }

    private void OnToolRefusedFolder(ToolCallEvent e)
    {
        if (RefusedFolder(e.Detail) is not { } folder || ChatFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        FolderRequest = folder;
        AllowFolderRequestCommand.Refresh();
        DismissFolderRequestCommand.Refresh();
    }

    /// <summary>
    /// Allows <paramref name="folder"/> for this chat: protected places are refused, a missing folder is created when a job
    /// starts, and running turns get it immediately. Returns false (with a status message) when it cannot be allowed.
    /// </summary>
    public async Task<bool> AllowFolderAsync(string folder)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { StatusText = "Not a folder path: " + ex.Message; return false; }
        if (ProtectedPathPolicy.CreateDefault().CheckJobRoot(full) is { } why) { StatusText = $"{full} cannot be allowed: {why}"; return false; }
        if (!ChatFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) ChatFolders.Add(full);
        if (string.Equals(_folderRequest, full, StringComparison.OrdinalIgnoreCase)) ClearFolderRequest();
        int live = 0;
        foreach (var turn in Conversation.Where(t => t.IsRunning).ToList())
            if (await _runtime.GrantFolderAsync(turn.JobId, full).ConfigureAwait(true) is null) live++;
        if (_chatId is not null) SaveChat();
        StatusText = live > 0 ? $"Allowed {full}: the running job can use it now, and so can later turns."
                              : $"Allowed {full} for this chat: reply (e.g. \"go ahead\") and the job can use it.";
        return true;
    }

    /// <summary>The chat's folders and the Save to folder join every job's roots; a Save to folder is where outputs go.</summary>
    /// <summary>The chat's finished turns (oldest first), so a follow-up keeps the whole conversation's instructions.</summary>
    private IReadOnlyList<JobSpecFactory.ConversationTurn> ConversationHistory() =>
        Conversation.Where(t => !t.IsRunning && !string.IsNullOrWhiteSpace(t.UserPrompt))
            .Select(t => new JobSpecFactory.ConversationTurn(t.UserPrompt, t.ResponseText, t.IsError)).ToList();

    private JobSpec WithAllowedFolders(JobSpec spec)
    {
        // Files and folders the user names in the message ("use C:\Parts\wheel.SLDPRT") may be read, not changed.
        var named = PromptPaths.ExistingPaths(spec.UserText ?? "");
        if (named.Count > 0) spec = spec with { ReadRoots = spec.ReadRoots.Concat(named).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
        var extra = ChatFolders.ToList();
        if (SaveFolder is { } save) extra.Add(save);
        var roots = spec.Roots.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == spec.Roots.Count) return spec;
        spec = spec with { Roots = roots };
        // A folder typed in this message wins over Save to for this message.
        if (SaveFolder is { } target && !spec.Prompt.Contains(target, StringComparison.OrdinalIgnoreCase) && PromptPaths.TargetFolder(spec.UserText ?? "") is null)
            spec = spec with { Prompt = spec.Prompt + $"\n\nSave every output file in {target} (the user picked it with Save to; it is allowed)." };
        return spec;
    }

    private async Task<RunningJob?> StartAndShowAsync(JobSpec spec, IReadOnlyList<ContextChipViewModel> attached, string started, bool stayOnActions = false)
    {
        spec = WithAllowedFolders(spec);
        if (_softwareContext is { } software && spec.Launch != LaunchMode.None) spec = spec with { Prompt = spec.Prompt + "\n\nThis chat is about: " + software };
        // Every new turn of a chat carries the conversation (natively resumed sessions already have it). Without this a
        // short follow-up after a failed turn ("now create the parts and assemble them") reached the model alone.
        if (spec.Launch == LaunchMode.Headless) spec = JobSpecFactory.WithConversation(spec, ConversationHistory());
        bool chat = spec.Launch == LaunchMode.Headless;
        if (chat && IsChatBusy) { StatusText = ChatBusyMessage; return null; }
        if (chat) SetSending(true);
        try
        {
            var job = await _runtime.StartJobAsync(ApplyModelPick(spec)).ConfigureAwait(true);
            _ui.Post(() =>
            {
                if (job.Spec.Launch is LaunchMode.Headless or LaunchMode.Interactive && !_turns.ContainsKey(job.JobId))
                {
                    var turn = new ChatTurnViewModel(job.JobId, spec.UserText ?? spec.Title, Badge(job));
                    // V16.1: a terminal job answers in its own PowerShell window; its card says so instead of staying empty.
                    // It does not keep the chat busy, so the chat can be used while it runs.
                    if (job.Spec.Launch == LaunchMode.Interactive) { turn.IsRunning = false; turn.ResponseText = TerminalNote; }
                    _turns[job.JobId] = turn;
                    Conversation.Add(turn);
                }
                // The job owns these now: the chips go, the staged copies stay for it to read.
                foreach (var c in attached) ContextChips.Remove(c);
                OnPropertyChanged(nameof(AttachmentChips));
                OnJobChanged(job);
                StatusText = started;
                PromptText = "";
                SelectedTab = stayOnActions ? 0 : 1;
            });
            return job;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _ui.Post(() => StatusText = ex.Message);
            return null;
        }
        finally
        {
            // Always unlock, whatever happened (posted after the turn is added, so a running turn keeps the chat busy).
            if (chat) _ui.Post(() => SetSending(false));
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
        var spec = JobSpecFactory.Continue(chip.Session, prompt, UseTerminal ? LaunchMode.Interactive : LaunchMode.Headless, attached.Select(c => c.Attachment!).ToList(), SelectedModel.Provider, ConversationHistory()) with { RoutingPolicy = RoutingPolicy };
        var job = await StartAndShowAsync(spec, attached, $"Continuing session {chip.Session.ShortId}: {chip.Session.Spec.Title}").ConfigureAwait(true);
        if (job is not null) _ui.Post(() => { if (ReferenceEquals(Continuation, chip)) Continuation = null; });
        return job;
    }

    /// <summary>The job recipe behind each tile (built in, or from the connector that offers it).</summary>
    public static JobSpec SpecFor(string actionId, string? folder, string prompt, LaunchMode launch) =>
        JobSpecFactory.Recipe(actionId, new Mediator.Core.Connectors.RecipeRequest(folder, prompt, launch))
        ?? throw new InvalidOperationException($"No job recipe for action '{actionId}' (is its connector installed?).");

    // ── Approvals (IApprovalPrompt) ──────────────────────────────────────────────────────────────────

    private bool _autoApproveAll;

    /// <summary>
    /// Approve every tool request without asking (this session only: it is off again at the next start). Each request is
    /// still logged in the chat turn of its job and audited by the bus. Turning it on also approves the cards waiting now.
    /// </summary>
    public bool AutoApproveAll
    {
        get => _autoApproveAll;
        set
        {
            if (!SetProperty(ref _autoApproveAll, value)) return;
            StatusText = value ? "Auto-approve is ON: every tool request runs without asking (logged in the chat)." : "Auto-approve is off: tool requests ask again.";
            if (value) foreach (var card in _approvalQueue.ToList()) card.ApproveAllCommand.Execute(null);
        }
    }

    /// <summary>Writes an approval decision into the chat turn of its job (shown with the steps).</summary>
    private void LogApproval(ApprovalRequest request, string decision)
    {
        string tool = request.Tool.StartsWith("mcp__mediator__", StringComparison.Ordinal) ? request.Tool["mcp__mediator__".Length..] : request.Tool;
        string args = request.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object ? request.Arguments.GetRawText() : "";
        if (args.Length > 160) args = args[..160] + "…";
        string line = $"{decision}: {tool} ({request.Risk}) {args}".TrimEnd();
        if (TurnFor(request.JobId) is { } turn) turn.Steps.Add(line);
        else StatusText = line;
    }

    public async Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (AutoApproveAll)
        {
            _ui.Post(() => LogApproval(request, "✔ Auto-approved"));
            return ApprovalResponse.ApproveAll;
        }
        var card = new ApprovalViewModel(request);
        card.Completed += c => _ui.Post(() => OnApprovalCompleted(c));
        using var registration = ct.Register(() => _ui.Post(card.Cancel));
        _ui.Post(() =>
        {
            if (card.IsCompleted) return; // cancelled before it was shown
            _approvalQueue.Add(card);
            OnPropertyChanged(nameof(PendingApprovalCount));
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
        if (card.Answer.IsCompletedSuccessfully)
        {
            var answer = card.Answer.Result;
            LogApproval(card.Request, answer.KillJob ? "✖ Job killed at" : !answer.Approved ? "✖ Denied"
                : AutoApproveAll ? "✔ Auto-approved" : answer.ApprovedRows is { } rows ? $"✔ Approved {rows.Count} of {card.Rows.Count}" : "✔ Approved");
        }
        else LogApproval(card.Request, "✖ Not answered (cancelled or timed out)");
        _approvalQueue.Remove(card);
        OnPropertyChanged(nameof(PendingApprovalCount));
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
        if (TurnFor(job.JobId) is { } turn)
        {
            turn.BadgeText = Badge(job);
            if (!item.IsActive)
            {
                turn.IsRunning = false;
                if (job.Spec.Launch == LaunchMode.Interactive && turn.ResponseText == TerminalNote)
                    turn.ResponseText = $"The terminal job ended ({job.State}){(job.Detail is { Length: > 0 } d ? ": " + d : ".")} Its conversation is in that PowerShell window.";
                else if (job.State != JobState.Succeeded && turn.ResponseText.Length == 0) { turn.IsError = true; turn.ResponseText = job.Detail ?? job.State.ToString(); }
            }
        }
        if (!item.IsActive && job.Spec.Kind != Mediator.Core.Browser.BrowserPanelChats.Kind // V14.1: the side panel keeps its own chats
            && ContinuableSession.From(job) is { } session && (Continuation is null || Continuation.Session.EndedAt <= session.EndedAt))
            Continuation = new ContinuationViewModel(_lastSession = session);
        if (!item.IsActive && TurnFor(job.JobId) is not null) SaveChat();
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
        RefreshBusy();
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

    private readonly Dictionary<string, (string? Tool, int Chars)> _progress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _progressKind = new(StringComparer.Ordinal);

    private static string ShortTool(string name) =>
        name.StartsWith("mcp__mediator__", StringComparison.Ordinal) ? name["mcp__mediator__".Length..] : Mediator.Core.Browser.ClaudeInChrome.ShortName(name);

    private void OnStreamEvent(string jobId, MediatorStreamEvent e)
    {
        switch (e)
        {
            case AssistantTextEvent t:
                if (TurnFor(jobId) is { } turn) // V10: the full text, never truncated
                {
                    turn.ResponseText = turn.ResponseText.Length == 0 ? t.Text : turn.ResponseText + "\n\n" + t.Text;
                    turn.ThinkingSummary = "Writing…";
                }
                string text = t.Text.ReplaceLineEndings(" ");
                string who = _jobProviders.GetValueOrDefault(jobId) == AiProvider.GeminiCli ? "agy" : "claude";
                AddActivity(new ActivityItemViewModel(DateTimeOffset.UtcNow, jobId, who, "says", text.Length > 240 ? text[..240] + "…" : text));
                break;
            case ProgressEvent p when TurnFor(jobId) is { } liveTurn:
                var (liveTool, liveChars) = _progress.GetValueOrDefault(jobId);
                if (p.Tool is not null || p.Kind != _progressKind.GetValueOrDefault(jobId)) { liveTool = p.Tool; liveChars = 0; }
                liveChars += p.Chars;
                _progress[jobId] = (liveTool, liveChars);
                _progressKind[jobId] = p.Kind;
                liveTurn.ThinkingSummary = p.Kind switch
                {
                    "tool" => $"Preparing {ShortTool(liveTool ?? "a tool")}… {liveChars:N0} characters",
                    "thinking" => $"Thinking… {liveChars:N0} characters",
                    _ => $"Writing… {liveChars:N0} characters",
                };
                break;
            case ToolUseEvent tu when TurnFor(jobId) is { } stepTurn:
                string tool = ShortTool(tu.Name);
                stepTurn.Steps.Add("\u2699 " + tool);
                stepTurn.ThinkingSummary = $"Using {tool} ({stepTurn.Steps.Count} step{(stepTurn.Steps.Count == 1 ? "" : "s")})";
                break;
            case RunResultEvent { RateLimit: null } done when TurnFor(jobId) is { } doneTurn:
                if (doneTurn.ResponseText.Length == 0) doneTurn.ResponseText = done.ResultText ?? "";
                doneTurn.IsError = done.IsError;
                doneTurn.IsRunning = false;
                _progress.Remove(jobId);
                _progressKind.Remove(jobId);
                RefreshBusy();
                doneTurn.ThinkingSummary = doneTurn.Steps.Count == 0 ? "Done" : $"Done · {doneTurn.Steps.Count} step(s)";
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

    /// <summary>
    /// Glyphs are Segoe Fluent Icons code points (Windows 11; Segoe MDL2 Assets on Windows 10). V18: Organize and Claude Code
    /// are built in; every other tile comes from an installed connector, in each recipe's order.
    /// </summary>
    public static IEnumerable<ActionTileViewModel> DefaultActions() =>
        Mediator.Core.Connectors.ConnectorCatalog.Recipes
            .Select(r => (r.Order, Tile: new ActionTileViewModel(r.Id, r.Glyph, r.Title, r.Description, r.Category, [.. r.Keywords], r.NeedsFolder, r.Tools)))
            .Append((30, new ActionTileViewModel("organize", "\uE8B7", "Organize a folder", "Plan moves from your description, review the batch, run it. Every move can be rolled back.",
                TaskCategory.Files, ["organi", "sort", "tidy", "clean up", "declutter", "move files", "folder"], needsFolder: true, JobSpecFactory.FileTools)))
            .Append((100, new ActionTileViewModel("claude-terminal", "\uE756", "Claude Code in a terminal", "Open Claude Code in this folder with your prompt. You drive it; billing stays on your Pro login.",
                TaskCategory.Code, ["code", "app", "poc", "finish", "fix", "terminal", "repo", "bug"], needsFolder: false, [])))
            .OrderBy(t => t.Item1).Select(t => t.Item2).ToList();

}
