using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.UI.Workspace;

/// <summary>
/// V16.2: the full-screen workspace window. Three regions around the overlay's conversation (<see cref="Main"/>): the top bar
/// (identity, project, search and command palette, the six right-sidebar mode buttons, notifications, profile menu), the
/// left sidebar (new chat, search, projects, active jobs, pinned and recent chats) and the right sidebar, whose six modes
/// (<see cref="Modes"/>) share one panel and keep their state when switched. Sidebar sizes and visibility are saved and
/// fitted to the window by <see cref="WorkspaceLayout"/>, so the conversation always keeps its share of the width.
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject
{
    private readonly Func<WorkspacePrefs> _load;
    private readonly Action<WorkspacePrefs> _save;
    private WorkspacePrefs _prefs;
    private double _available = 1600; // until the window reports its width
    private WorkspaceFit _fit;

    /// <param name="load">The saved preferences.</param>
    /// <param name="save">Persists changed preferences (settings.json).</param>
    /// <param name="host">What the window or the app does for the workspace (open a file, start a program, ...).</param>
    public WorkspaceViewModel(MediatorOverlayViewModel main, Func<WorkspacePrefs> load, Action<WorkspacePrefs> save, IWorkspaceHost? host = null)
    {
        Main = main;
        _load = load;
        _save = save;
        Host = host ?? NullWorkspaceHost.Instance;
        _prefs = load();
        _fit = WorkspaceLayout.Fit(_available, _prefs);

        Cockpit = new CockpitModeViewModel(this);
        Artifacts = new ArtifactsModeViewModel(this);
        Access = new AccessModeViewModel(this);
        Activity = new ActivityModeViewModel(this);
        Knowledge = new KnowledgeModeViewModel(this);
        Software = new SoftwareModeViewModel(this);
        Modes = [Cockpit, Artifacts, Access, Activity, Knowledge, Software];
        foreach (var m in Modes) m.IsActive = m.Mode == _prefs.Mode;

        ToggleLeftCommand = new RelayCommand(() => SetSidebar(left: true, show: !IsLeftShown));
        ToggleRightCommand = new RelayCommand(() => SetSidebar(left: false, show: !IsRightShown));
        NewChatCommand = new RelayCommand(NewChat);
        OpenPaletteCommand = new RelayCommand(() => { PaletteQuery = ""; IsPaletteOpen = true; RefreshPalette(); });
        ClosePaletteCommand = new RelayCommand(() => IsPaletteOpen = false);
        RunFirstPaletteItemCommand = new RelayCommand(() => { if (PaletteResults.FirstOrDefault() is { } first) first.RunCommand.Execute(null); });
        ToggleProfileCommand = new RelayCommand(() => IsProfileOpen = !IsProfileOpen);
        CloseOverlaysCommand = new RelayCommand(() => { IsPaletteOpen = false; IsProfileOpen = false; IsShortcutsOpen = false; IsAboutOpen = false; });
        OpenNotificationsCommand = new RelayCommand(() => SelectMode(Main.HasPendingApproval ? RightMode.Access : RightMode.Activity, toggle: false));
        ProfileItems =
        [
            Item("Usage & Analytics", "", "Plan meters and Mediator's own runs", () => Host.OpenOverlayTab(MediatorOverlayViewModel.UsageTab)),
            Item("Providers", "", "Claude Code, Antigravity: sign-in, limits", () => Host.OpenOverlayTab(MediatorOverlayViewModel.ConnectorsTab)),
            Item("Connections (MCP servers)", "", "Desktop apps and custom MCP servers", () => Host.OpenOverlayTab(MediatorOverlayViewModel.ConnectorsTab)),
            Item("Keyboard Shortcuts", "", "Every shortcut of this window", () => IsShortcutsOpen = true),
            Item("Appearance", "", "Switch theme (Holo HUD, Pro Dark, Clean Light)", () => Main.CycleTheme()),
            Item("Settings", "", "Hotkey, routing, start with Windows", () => Host.OpenOverlayTab(0)),
            Item("Logs & Diagnostics", "", "Open the data folder (logs, audit, chats)", () => Host.OpenDataFolder()),
            Item("About", "", "Version and build", () => IsAboutOpen = true),
        ];

        Main.SavedChats.CollectionChanged += (_, _) => RefreshChats();
        Main.Jobs.CollectionChanged += OnJobsChanged;
        Main.Conversation.CollectionChanged += (_, _) => RefreshHeader();
        Main.ChatFolders.CollectionChanged += (_, _) => { RefreshProjects(); RefreshHeader(); };
        Main.PropertyChanged += OnMainChanged;
        RefreshChats();
        OnJobsChanged(null, null);
        RefreshProjects();
    }

    public MediatorOverlayViewModel Main { get; }
    public IWorkspaceHost Host { get; }
    public string VersionText => "Mediator " + MediatorVersion.Display;

    private PaletteItem Item(string title, string glyph, string hint, Action run) =>
        new(title, glyph, hint, new RelayCommand(() => { IsProfileOpen = false; IsPaletteOpen = false; run(); }));

    // ── Layout ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window's usable width (device-independent pixels); set on every resize. Re-fits the sidebars.</summary>
    public double AvailableWidth
    {
        get => _available;
        set { if (double.IsFinite(value) && value > 0 && Math.Abs(value - _available) > 0.5) { _available = value; Refit(); } }
    }

    public bool IsLeftShown => _fit.LeftShown;
    public bool IsRightShown => _fit.RightShown;
    public double LeftMaxWidth => WorkspaceLayout.MaxLeft(_available, _fit);
    public double RightMaxWidth => WorkspaceLayout.MaxRight(_available, _fit);
    public double CenterMinWidth => _fit.CenterMinWidth;
    public string LayoutText => WorkspaceLayout.Describe(_fit);

    /// <summary>The left sidebar's width; the splitter writes it (clamped so the conversation keeps its share).</summary>
    public double LeftWidth
    {
        get => _fit.LeftShown ? _fit.LeftWidth : 0;
        set { if (_fit.LeftShown && double.IsFinite(value) && value > 0) Update(_prefs with { LeftWidth = Math.Clamp(value, WorkspaceLayout.LeftMin, LeftMaxWidth) }); }
    }

    public double RightWidth
    {
        get => _fit.RightShown ? _fit.RightWidth : 0;
        set { if (_fit.RightShown && double.IsFinite(value) && value > 0) Update(_prefs with { RightWidth = Math.Clamp(value, WorkspaceLayout.RightMin, RightMaxWidth) }); }
    }

    private void SetSidebar(bool left, bool show)
    {
        var next = left ? _prefs with { LeftVisible = show } : _prefs with { RightVisible = show };
        // Shown on purpose on a narrow window: the other sidebar makes room (its own choice is kept for wider windows).
        if (show && !WorkspaceLayout.Fit(_available, next).Shown(left))
            Main.NotifyStatus("The window is too narrow for both sidebars and the conversation; the other sidebar is hidden until it is wider.");
        Update(next);
    }

    private void Update(WorkspacePrefs prefs)
    {
        if (prefs == _prefs) { Refit(); return; }
        _prefs = prefs;
        _save(prefs);
        Refit();
    }

    private void Refit()
    {
        _fit = WorkspaceLayout.Fit(_available, _prefs);
        foreach (var name in new[] { nameof(IsLeftShown), nameof(IsRightShown), nameof(LeftWidth), nameof(RightWidth), nameof(LeftMaxWidth),
                     nameof(RightMaxWidth), nameof(CenterMinWidth), nameof(LayoutText) })
            OnPropertyChanged(name);
    }

    public RelayCommand ToggleLeftCommand { get; }
    public RelayCommand ToggleRightCommand { get; }

    // ── Right sidebar modes ──────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<RightModeViewModel> Modes { get; }
    public CockpitModeViewModel Cockpit { get; }
    public ArtifactsModeViewModel Artifacts { get; }
    public AccessModeViewModel Access { get; }
    public ActivityModeViewModel Activity { get; }
    public KnowledgeModeViewModel Knowledge { get; }
    public SoftwareModeViewModel Software { get; }

    public RightMode ActiveMode => _prefs.Mode;
    public string ActiveModeTitle => Modes.First(m => m.Mode == _prefs.Mode).Title;

    /// <summary>A mode button: shows that mode (opening the sidebar); pressed again while it shows, it hides the sidebar.</summary>
    public void SelectMode(RightMode mode, bool toggle = true)
    {
        bool hide = toggle && mode == _prefs.Mode && IsRightShown;
        // The mode buttons are toggle buttons bound two-way: whatever a click did to one, exactly the chosen mode is active after.
        foreach (var m in Modes) m.IsActive = m.Mode == mode;
        if (hide) { SetSidebar(left: false, show: false); return; }
        Update(_prefs with { Mode = mode, RightVisible = true });
        Modes.First(m => m.Mode == mode).Refresh();
        OnPropertyChanged(nameof(ActiveMode));
        OnPropertyChanged(nameof(ActiveModeTitle));
    }

    internal IReadOnlyList<string> PinnedSoftware => _prefs.PinnedSoftware;
    internal void SetPinnedSoftware(IEnumerable<string> ids) => Update(_prefs with { PinnedSoftware = ids.Distinct().ToList() });

    /// <summary>V18: where the full-screen app opens (its last place), or null for centred and maximized.</summary>
    public WindowBounds? WindowPlacement => _prefs.Window;

    /// <summary>V18: the window reports where it is when it closes, so it reopens there.</summary>
    public void SaveWindowPlacement(WindowBounds bounds)
    {
        if (bounds.Width >= WindowBounds.MinWidth && bounds.Height >= WindowBounds.MinHeight && bounds != _prefs.Window) Update(_prefs with { Window = bounds });
    }

    // ── Left sidebar ─────────────────────────────────────────────────────────────────────────────────

    public RelayCommand NewChatCommand { get; }

    private void NewChat()
    {
        if (Main.ClearConversationCommand.CanExecute(null)) Main.ClearConversationCommand.Execute(null);
        Host.ActivateComposer();
    }

    /// <summary>Searches chat titles, prompts and answers (the sidebar lists only matches).</summary>
    public string SearchText { get => Main.ChatSearch; set { Main.ChatSearch = value; OnPropertyChanged(); } }

    public ObservableCollection<ChatListItem> PinnedChats { get; } = new();
    public ObservableCollection<ChatListItem> RecentChats { get; } = new();
    public bool HasPinnedChats => PinnedChats.Count > 0;

    private void RefreshChats()
    {
        PinnedChats.Clear();
        RecentChats.Clear();
        foreach (var c in Main.SavedChats) (c.Pinned ? PinnedChats : RecentChats).Add(new ChatListItem(c, Main));
        OnPropertyChanged(nameof(HasPinnedChats));
        RefreshProjects();
    }

    /// <summary>Projects: the folders chats were allowed to work in, with their chats.</summary>
    public ObservableCollection<ProjectItem> Projects { get; } = new();
    public bool HasProjects => Projects.Count > 0;

    private void RefreshProjects()
    {
        var folders = Main.SavedChats.SelectMany(c => (c.Folders ?? []).Select(f => (Folder: f, Chat: (SavedChat?)c)))
            .Concat(Main.ChatFolders.Select(f => (Folder: f, Chat: (SavedChat?)null)))
            .Where(x => x.Folder.Length > 0)
            .GroupBy(x => Path.TrimEndingDirectorySeparator(x.Folder), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectItem(g.Key, g.Select(x => x.Chat).OfType<SavedChat>().DistinctBy(c => c.Id).Count(), this))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        Projects.Clear();
        foreach (var p in folders) Projects.Add(p);
        OnPropertyChanged(nameof(HasProjects));
    }

    /// <summary>Every job of this session, newest first, with its state as the workspace names it.</summary>
    public ObservableCollection<JobRow> JobRows { get; } = new();
    public int ActiveJobCount => JobRows.Count(j => j.Job.IsActive);
    public string JobsHeader => ActiveJobCount > 0 ? $"Active jobs ({ActiveJobCount})" : "Jobs";

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        foreach (var row in JobRows) row.Detach();
        JobRows.Clear();
        foreach (var job in Main.Jobs) JobRows.Add(new JobRow(job, this));
        OnPropertyChanged(nameof(ActiveJobCount));
        OnPropertyChanged(nameof(JobsHeader));
        Cockpit.Refresh();
    }

    internal void JobStateChanged()
    {
        OnPropertyChanged(nameof(ActiveJobCount));
        OnPropertyChanged(nameof(JobsHeader));
    }

    /// <summary>Shows one job's tool calls in the Activity mode.</summary>
    internal void Inspect(JobRow row)
    {
        Activity.JobFilter = row.Job.JobId;
        SelectMode(RightMode.Activity, toggle: false);
    }

    // ── Conversation header ─────────────────────────────────────────────────────────────────────────

    public string ProjectText => Main.ChatFolders.FirstOrDefault() is { } f ? Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) is { Length: > 0 } n ? n : f
        : Main.SaveFolder is { } s ? Path.GetFileName(Path.TrimEndingDirectorySeparator(s)) : "No project";
    public string ProjectToolTip => string.Join("\n", Main.ChatFolders.DefaultIfEmpty(Main.SaveFolder ?? "No folder is shared with this chat. Use Allow folder or Save to."));
    /// <summary>"Chat · Claude Code · sonnet": the route, CLI and model of the latest answer.</summary>
    public string ProviderText => Main.Conversation.LastOrDefault()?.BadgeText is { Length: > 0 } b ? b : "Auto routing";
    public string SessionText => Main.IsChatBusy ? "Working…" : Main.Continuation is { } c ? $"Session {c.Session.ShortId} · can continue" : Main.HasConversation ? "Idle" : "New chat";

    private void RefreshHeader()
    {
        foreach (var n in new[] { nameof(ProjectText), nameof(ProjectToolTip), nameof(ProviderText), nameof(SessionText) }) OnPropertyChanged(n);
    }

    // ── Notifications ────────────────────────────────────────────────────────────────────────────────

    public int NotificationCount => Main.PendingApprovalCount + JobRows.Count(j => j.IsFailedUnseen);
    public bool HasNotifications => NotificationCount > 0;
    public string NotificationText => NotificationCount == 0 ? "Nothing needs you" :
        string.Join(" · ", new[] { Main.PendingApprovalCount > 0 ? $"{Main.PendingApprovalCount} approval(s) waiting" : null,
            JobRows.Count(j => j.IsFailedUnseen) is > 0 and var f ? $"{f} failed job(s)" : null }.OfType<string>());
    public RelayCommand OpenNotificationsCommand { get; }

    internal void RefreshNotifications()
    {
        OnPropertyChanged(nameof(NotificationCount));
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(NotificationText));
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediatorOverlayViewModel.ChatSearch): OnPropertyChanged(nameof(SearchText)); break;
            case nameof(MediatorOverlayViewModel.PendingApproval) or nameof(MediatorOverlayViewModel.PendingApprovalCount):
                RefreshNotifications();
                Access.Refresh();
                break;
            case nameof(MediatorOverlayViewModel.IsChatBusy) or nameof(MediatorOverlayViewModel.Continuation) or nameof(MediatorOverlayViewModel.HasConversation):
                RefreshHeader();
                break;
        }
    }

    // ── Command palette and profile menu ────────────────────────────────────────────────────────────

    private bool _isPaletteOpen, _isProfileOpen, _isShortcutsOpen, _isAboutOpen;
    private string _paletteQuery = "";

    public bool IsPaletteOpen { get => _isPaletteOpen; set => SetProperty(ref _isPaletteOpen, value); }
    public bool IsProfileOpen { get => _isProfileOpen; set => SetProperty(ref _isProfileOpen, value); }
    public bool IsShortcutsOpen { get => _isShortcutsOpen; set => SetProperty(ref _isShortcutsOpen, value); }
    public bool IsAboutOpen { get => _isAboutOpen; set => SetProperty(ref _isAboutOpen, value); }

    public string PaletteQuery
    {
        get => _paletteQuery;
        set { if (SetProperty(ref _paletteQuery, value ?? "")) RefreshPalette(); }
    }

    public ObservableCollection<PaletteItem> PaletteResults { get; } = new();
    public IReadOnlyList<PaletteItem> ProfileItems { get; }
    public RelayCommand OpenPaletteCommand { get; }
    public RelayCommand ClosePaletteCommand { get; }
    public RelayCommand RunFirstPaletteItemCommand { get; }
    public RelayCommand ToggleProfileCommand { get; }
    public RelayCommand CloseOverlaysCommand { get; }

    public IReadOnlyList<string> Shortcuts { get; } =
    [
        "Ctrl+N  New chat", "Ctrl+K  Search chats", "Ctrl+P  Command palette", "Ctrl+B  Show or hide the left sidebar",
        "Ctrl+Alt+B  Show or hide the right sidebar", "Ctrl+1 … Ctrl+6  Cockpit, Artifacts, Access, Activity, Knowledge, Software",
        "Enter  Send · Shift+Enter  New line · Esc  Stop", "Ctrl+Shift+C  Copy the last answer", "Ctrl+Shift+Space  The quick panel (any app)",
    ];

    /// <summary>Commands, modes and chats matching the query (all commands when it is empty).</summary>
    private void RefreshPalette()
    {
        var all = new List<PaletteItem>
        {
            Item("New chat", "", "Ctrl+N", NewChat),
            Item("Toggle left sidebar", "", "Ctrl+B", () => ToggleLeftCommand.Execute(null)),
            Item("Toggle right sidebar", "", "Ctrl+Alt+B", () => ToggleRightCommand.Execute(null)),
        };
        all.AddRange(Modes.Select((m, i) => Item("Open " + m.Title, m.Glyph, $"Ctrl+{i + 1} · {m.Description}", () => SelectMode(m.Mode, toggle: false))));
        all.AddRange(ProfileItems.Select(p => Item(p.Title, p.Glyph, p.Hint, () => p.RunCommand.Execute(null))));
        all.AddRange(Main.SavedChats.Take(200).Select(c => Item("Open chat: " + c.DisplayTitle, "", c.Updated.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture),
            () => { if (Main.OpenSavedChatCommand.CanExecute(c)) Main.OpenSavedChatCommand.Execute(c); })));
        string q = _paletteQuery.Trim();
        PaletteResults.Clear();
        foreach (var item in all.Where(i => q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(30))
            PaletteResults.Add(item);
    }

    /// <summary>Reloads the preferences (another window changed settings.json).</summary>
    public void Reload()
    {
        _prefs = _load();
        foreach (var m in Modes) m.IsActive = m.Mode == _prefs.Mode;
        Refit();
    }
}

internal static class WorkspaceFitExtensions
{
    public static bool Shown(this WorkspaceFit fit, bool left) => left ? fit.LeftShown : fit.RightShown;
}

/// <summary>What the workspace asks of its window or the app.</summary>
public interface IWorkspaceHost
{
    void ActivateComposer();
    /// <summary>Shows the quick panel on one of its tabs (Usage, Connectors, ...).</summary>
    void OpenOverlayTab(int tab);
    void OpenDataFolder();
    /// <summary>Opens a file or folder in its default program.</summary>
    void OpenPath(string path);
    /// <summary>Starts a desktop program.</summary>
    void Launch(string executable);
}

public sealed class NullWorkspaceHost : IWorkspaceHost
{
    public static readonly NullWorkspaceHost Instance = new();
    public void ActivateComposer() { }
    public void OpenOverlayTab(int tab) { }
    public void OpenDataFolder() { }
    public void OpenPath(string path) { }
    public void Launch(string executable) { }
}

/// <summary>A command in the palette or the profile menu.</summary>
public sealed record PaletteItem(string Title, string Glyph, string Hint, RelayCommand RunCommand);

/// <summary>A saved chat in the left sidebar, with its own actions.</summary>
public sealed class ChatListItem(SavedChat chat, MediatorOverlayViewModel main)
{
    public SavedChat Chat { get; } = chat;
    public string Title => Chat.DisplayTitle;
    public string UpdatedText => Chat.Updated.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
    public string ProjectText => Chat.Folders is { Count: > 0 } f ? Path.GetFileName(Path.TrimEndingDirectorySeparator(f[0])) : "";
    public bool IsPinned => Chat.Pinned;
    public RelayCommand OpenCommand { get; } = new(() => main.OpenSavedChatCommand.Execute(chat));
    public RelayCommand PinCommand { get; } = new(() => main.PinSavedChatCommand.Execute(chat));
    public RelayCommand DeleteCommand { get; } = new(() => main.DeleteSavedChatCommand.Execute(chat));
}

/// <summary>A project in the left sidebar: a folder chats worked in.</summary>
public sealed class ProjectItem
{
    public ProjectItem(string folder, int chats, WorkspaceViewModel workspace)
    {
        Folder = folder;
        Name = Path.GetFileName(folder) is { Length: > 0 } n ? n : folder;
        ChatCount = chats;
        OpenFolderCommand = new RelayCommand(() => workspace.Host.OpenPath(folder));
        SearchChatsCommand = new RelayCommand(() => workspace.SearchText = Name);
    }

    public string Folder { get; }
    public string Name { get; }
    public int ChatCount { get; }
    public string Detail => ChatCount == 1 ? "1 chat" : $"{ChatCount} chats";
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand SearchChatsCommand { get; }
}

/// <summary>A job in the left sidebar: state as the workspace names it, elapsed time, and actions.</summary>
public sealed class JobRow : ObservableObject
{
    private readonly WorkspaceViewModel _workspace;
    private bool _seen;

    public JobRow(JobItemViewModel job, WorkspaceViewModel workspace)
    {
        Job = job;
        _workspace = workspace;
        InspectCommand = new RelayCommand(() => { _seen = true; workspace.Inspect(this); workspace.RefreshNotifications(); });
        OpenFolderCommand = new RelayCommand(() => workspace.Host.OpenPath(job.WorkspaceFolder));
        job.PropertyChanged += OnJobChanged;
    }

    public JobItemViewModel Job { get; }
    public RelayCommand InspectCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Running, Awaiting approval, Queued, Completed, Failed or Cancelled.</summary>
    public string StateLabel => Job.State switch
    {
        JobState.Starting => "Queued",
        JobState.Running when _workspace.Main.PendingApproval?.JobId == Job.JobId => "Awaiting approval",
        JobState.Running => "Running",
        JobState.Succeeded or JobState.Ended => "Completed",
        JobState.Failed => "Failed",
        JobState.Killed => "Cancelled",
        _ => Job.StateText,
    };

    internal bool IsFailedUnseen => Job.State == JobState.Failed && !_seen;

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobItemViewModel.State) or nameof(JobItemViewModel.StateText))
        {
            OnPropertyChanged(nameof(StateLabel));
            _workspace.JobStateChanged();
            _workspace.RefreshNotifications();
        }
    }

    internal void Detach() => Job.PropertyChanged -= OnJobChanged;
}
