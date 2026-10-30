using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mediator.Core.Runtime;

namespace Mediator.UI.Workspace;

/// <summary>
/// One mode of the right sidebar. All six live in the same panel; each keeps its own state (filters, selection, expanded
/// sections) while another is shown, and <see cref="Refresh"/> brings its data up to date when it is shown.
/// </summary>
public abstract class RightModeViewModel : ObservableObject
{
    private bool _isActive;

    protected RightModeViewModel(WorkspaceViewModel workspace, RightMode mode, string title, string glyph, string description)
    {
        Workspace = workspace;
        Mode = mode;
        Title = title;
        Glyph = glyph;
        Description = description;
        SelectCommand = new RelayCommand(() => workspace.SelectMode(mode));
    }

    protected WorkspaceViewModel Workspace { get; }
    protected MediatorOverlayViewModel Main => Workspace.Main;
    public RightMode Mode { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string Description { get; }
    public RelayCommand SelectCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set { if (SetProperty(ref _isActive, value) && value) Refresh(); }
    }

    public virtual void Refresh() { }
}

/// <summary>A label and a value in a compact card ("Provider · model", "Chat · Claude Code · sonnet").</summary>
public sealed record InfoRow(string Label, string Value, bool IsWarning = false);

/// <summary>A tool call in the Cockpit's Tools card.</summary>
public sealed record ToolCallRow(string TimeText, string Tool, string Phase, string Detail, bool IsProblem);

// ── 6.1 Cockpit ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>The live execution environment of the open chat: where it runs, what it can reach, what it did.</summary>
public sealed class CockpitModeViewModel(WorkspaceViewModel workspace)
    : RightModeViewModel(workspace, RightMode.Cockpit, "Cockpit", "\uE7B8", "Environment, context, connectors, tools, outputs, jobs")
{
    public ObservableCollection<InfoRow> Environment { get; } = new();
    public ObservableCollection<InfoRow> Context { get; } = new();
    public ObservableCollection<InfoRow> Connectors { get; } = new();
    public ObservableCollection<ToolCallRow> Tools { get; } = new();
    public ObservableCollection<JobRow> BackgroundTasks { get; } = new();
    /// <summary>Files this chat produced (shared with the Artifacts mode).</summary>
    public ObservableCollection<ArtifactItem> Outputs => Workspace.Artifacts.Items;
    public bool HasTools => Tools.Count > 0;
    public bool HasBackgroundTasks => BackgroundTasks.Count > 0;
    /// <summary>Not tracked yet: Git working-tree changes and per-file diffs.</summary>
    public string ChangesNote { get; } = "File changes and diffs are not tracked yet. Files the AI wrote are listed under Outputs.";

    public override void Refresh()
    {
        var last = Main.Conversation.LastOrDefault();
        Fill(Environment,
        [
            new("Project", Workspace.ProjectText),
            new("Folders", Main.ChatFolders.Count == 0 ? "none shared" : string.Join("; ", Main.ChatFolders)),
            new("Save to", Main.SaveFolder ?? "folder selected in Explorer"),
            new("Provider · model", last?.BadgeText is { Length: > 0 } b ? b : "Auto (routing picks per prompt)"),
            new("Routing", Main.RoutingPolicyText),
            new("Session", Main.Continuation is { } c ? c.Session.SessionId : "new (no session to continue)"),
            new("Host", $"{System.Environment.MachineName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}"),
            new("Mediator", MediatorVersion.Display),
        ]);
        Fill(Context,
        [
            new("Conversation", $"{Main.Conversation.Count} turn(s), carried to every follow-up"),
            new("Attachments", Main.AttachmentChips.Count(c => c.IsIncluded) is var n and > 0 ? $"{n} included" : "none"),
            new("Context window", "not reported by the CLIs per turn yet", IsWarning: false),
            new("Status", Workspace.SessionText),
        ]);
        var connectors = new List<InfoRow>();
        if (Main.Connectors is { } con)
        {
            connectors.AddRange(con.Clis.Select(c => new InfoRow(c.Name, c.StatusText, c.NeedsAttention)));
            connectors.AddRange(con.Apps.Select(a => new InfoRow(a.Name, a.StatusText)));
            connectors.AddRange(con.McpServers.Select(m => new InfoRow(m.Name + " (MCP)", $"{m.StatusText} · {m.ToolsText}", !m.IsHealthy)));
        }
        if (connectors.Count == 0) connectors.Add(new InfoRow("Connectors", "checked when the quick panel's Connectors tab opens"));
        Fill(Connectors, connectors);
        string? job = Main.Jobs.FirstOrDefault()?.JobId;
        Fill(Tools, Main.Activity.Where(a => job is null || a.JobId == job).Take(40)
            .Select(a => new ToolCallRow(a.TimeText, a.Tool, a.Phase, a.Detail, a.IsProblem)));
        Fill(BackgroundTasks, Workspace.JobRows.Where(r => r.Job.IsActive));
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(HasBackgroundTasks));
        Workspace.Artifacts.Refresh();
    }

    internal static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}

// ── 6.2 Artifacts ────────────────────────────────────────────────────────────────────────────────────

/// <summary>A file the conversation produced or used: an answer named it and it exists, or it was attached.</summary>
public sealed class ArtifactItem : ObservableObject
{
    public ArtifactItem(string path, string source, WorkspaceViewModel workspace)
    {
        Path = path;
        Source = source;
        Name = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        try
        {
            var info = new FileInfo(path);
            SizeText = info.Exists ? FormatSize(info.Length) : "missing";
            CreatedText = info.Exists ? info.LastWriteTime.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { SizeText = "?"; CreatedText = ""; }
        OpenCommand = new RelayCommand(() => workspace.Host.OpenPath(path));
        RevealCommand = new RelayCommand(() => workspace.Host.OpenPath(System.IO.Path.GetDirectoryName(path) ?? path));
        SelectCommand = new RelayCommand(() => workspace.Artifacts.Selected = this);
    }

    public string Path { get; }
    public string Name { get; }
    public string Extension { get; }
    /// <summary>"answer" (a file an answer named) or "attachment".</summary>
    public string Source { get; }
    public string SizeText { get; }
    public string CreatedText { get; }
    public string Kind => ArtifactsModeViewModel.KindOf(Extension);
    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand SelectCommand { get; }

    private static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => (b / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (b / 1024.0 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}

/// <summary>Files of this conversation, by kind, with the selected one's details and actions.</summary>
public sealed partial class ArtifactsModeViewModel(WorkspaceViewModel workspace)
    : RightModeViewModel(workspace, RightMode.Artifacts, "Artifacts", "\uE8A5", "Files this chat produced or used")
{
    private ArtifactItem? _selected;
    private string _kindFilter = "All";

    public ObservableCollection<ArtifactItem> Items { get; } = new();
    public IReadOnlyList<string> Kinds { get; } = ["All", "PDF", "Image", "Office", "CAD", "Code & text", "Other"];
    public bool HasItems => Items.Count > 0;

    public string KindFilter { get => _kindFilter; set { if (SetProperty(ref _kindFilter, value ?? "All")) Refresh(); } }

    public ArtifactItem? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => Selected is not null;
    public string ViewerNote { get; } = "In-app viewing (PDF pages, sheets, slides, images, code) comes in a later version; Open uses the file's own program.";

    /// <summary>"PDF", "Image", "Office", "CAD", "Code & text" or "Other".</summary>
    public static string KindOf(string extension) => extension.ToLowerInvariant() switch
    {
        "pdf" => "PDF",
        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" => "Image",
        "docx" or "xlsx" or "pptx" or "doc" or "xls" or "ppt" or "csv" => "Office",
        "sldprt" or "sldasm" or "slddrw" or "step" or "stp" or "stl" or "3mf" or "obj" or "iges" or "igs" or "dxf" or "dwg" or "x_t" => "CAD",
        "md" or "txt" or "json" or "cs" or "py" or "js" or "ts" or "html" or "xml" or "yaml" or "yml" => "Code & text",
        _ => "Other",
    };

    /// <summary>An absolute path to a file with an extension, as answers write them (C:\Work\bracket.SLDPRT, /home/me/plate.step).</summary>
    [GeneratedRegex(@"(?:[A-Za-z]:\\|(?<![\w:/.])/)[^\r\n""'<>|*?`]*?\.[A-Za-z0-9_]{1,6}\b")]
    private static partial Regex FilePath();

    /// <summary>The existing files the answers of this conversation name, newest first, plus its attachments.</summary>
    public static IEnumerable<(string Path, string Source)> Collect(MediatorOverlayViewModel main)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var turn in main.Conversation.Reverse())
            foreach (Match m in FilePath().Matches(turn.ResponseText))
            {
                string p = m.Value.TrimEnd('.', ',', ';', ')');
                if (seen.Add(p) && SafeExists(p)) yield return (p, "answer");
            }
        foreach (var chip in main.AttachmentChips)
            if (chip.Attachment?.Path is { } staged && seen.Add(staged) && SafeExists(staged)) yield return (staged, "attachment");
    }

    private static bool SafeExists(string p)
    {
        try { return File.Exists(p); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public override void Refresh()
    {
        string? keep = Selected?.Path;
        var items = Collect(Main).Take(200).Select(x => new ArtifactItem(x.Path, x.Source, Workspace))
            .Where(i => KindFilter == "All" || i.Kind == KindFilter).ToList();
        CockpitModeViewModel.Fill(Items, items);
        Selected = items.FirstOrDefault(i => i.Path == keep) ?? items.FirstOrDefault();
        OnPropertyChanged(nameof(HasItems));
    }
}

// ── 6.3 Access ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>A folder this chat's jobs may read and write.</summary>
public sealed record GrantRow(string Resource, string Scope, string Expires, RelayCommand RevokeCommand);

/// <summary>Permissions: what this chat may touch, what waits for the user's decision, and past decisions.</summary>
public sealed class AccessModeViewModel(WorkspaceViewModel workspace)
    : RightModeViewModel(workspace, RightMode.Access, "Access", "\uE72E", "Permissions, pending requests, decisions")
{
    public ObservableCollection<GrantRow> Grants { get; } = new();
    public ObservableCollection<ActivityItemViewModel> History { get; } = new();
    public ApprovalViewModel? Pending => Main.PendingApproval;
    public bool HasPending => Main.HasPendingApproval;
    public string PendingText => Main.HasPendingApproval ? Main.QueuedApprovalsText : "No request is waiting for you.";
    public string RulesNote { get; } = "Read-only tools run on their own; anything that writes asks here first, and destructive tools always ask. " +
        "A job can only use the folders below and the tools of its kind; it can never approve its own requests.";

    public bool AutoApproveAll { get => Main.AutoApproveAll; set { Main.AutoApproveAll = value; OnPropertyChanged(); } }

    public override void Refresh()
    {
        CockpitModeViewModel.Fill(Grants, Main.ChatFolders.Select(f => new GrantRow(f, "Files · read and write · this chat's jobs", "until removed",
            new RelayCommand(() => { if (Main.RemoveChatFolderCommand.CanExecute(f)) Main.RemoveChatFolderCommand.Execute(f); Refresh(); }))));
        if (Main.SaveFolder is { } save)
            Grants.Add(new GrantRow(save, "Files · new jobs save here (Save to)", "until cleared", new RelayCommand(() => { Main.ClearSaveFolderCommand.Execute(null); Refresh(); })));
        CockpitModeViewModel.Fill(History, Main.Activity.Where(IsDecision).Take(100));
        foreach (var n in new[] { nameof(Pending), nameof(HasPending), nameof(PendingText), nameof(AutoApproveAll) }) OnPropertyChanged(n);
    }

    internal static bool IsDecision(ActivityItemViewModel a) =>
        a.Phase.Contains("approv", StringComparison.OrdinalIgnoreCase) || a.Phase.Contains("denied", StringComparison.OrdinalIgnoreCase)
        || a.Phase.Contains("waiting", StringComparison.OrdinalIgnoreCase) || a.Phase.Contains("refused", StringComparison.OrdinalIgnoreCase)
        || a.Tool.StartsWith("mediator/", StringComparison.Ordinal);
}

// ── 6.4 Activity ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>The timeline of tool calls, decisions and messages, searchable and filterable, exportable without secrets.</summary>
public sealed class ActivityModeViewModel(WorkspaceViewModel workspace)
    : RightModeViewModel(workspace, RightMode.Activity, "Activity", "\uE81C", "Timeline of tools, jobs and decisions")
{
    private string _query = "", _category = "All";
    private string? _jobFilter;

    public IReadOnlyList<string> Categories { get; } = ["All", "Tools", "Problems", "Decisions", "Messages"];
    public ObservableCollection<ActivityItemViewModel> Items { get; } = new();
    public bool HasItems => Items.Count > 0;
    public string Query { get => _query; set { if (SetProperty(ref _query, value ?? "")) Refresh(); } }
    public string Category { get => _category; set { if (SetProperty(ref _category, value ?? "All")) Refresh(); } }

    /// <summary>One job's events only (a job's Inspect), or null for all.</summary>
    public string? JobFilter
    {
        get => _jobFilter;
        set { if (SetProperty(ref _jobFilter, value)) { OnPropertyChanged(nameof(HasJobFilter)); OnPropertyChanged(nameof(JobFilterText)); Refresh(); } }
    }
    public bool HasJobFilter => JobFilter is not null;
    public string JobFilterText => JobFilter is null ? "" : "Job " + JobFilter;
    public RelayCommand ClearJobFilterCommand => new(() => JobFilter = null);
    public RelayCommand ExportCommand => new(Export);
    private string _exportText = "";
    public string ExportText { get => _exportText; private set => SetProperty(ref _exportText, value); }

    public override void Refresh()
    {
        string q = _query.Trim();
        CockpitModeViewModel.Fill(Items, Main.Activity.Where(a =>
                (JobFilter is null || a.JobId == JobFilter)
                && (q.Length == 0 || a.Tool.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Detail.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Phase.Contains(q, StringComparison.OrdinalIgnoreCase))
                && Category switch
                {
                    "Tools" => a.Phase != "says",
                    "Problems" => a.IsProblem || a.IsUncertain,
                    "Decisions" => AccessModeViewModel.IsDecision(a),
                    "Messages" => a.Phase == "says",
                    _ => true,
                })
            .Take(500));
        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>The shown events as text with secrets removed, written to Documents\Mediator and opened.</summary>
    private void Export()
    {
        var sb = new StringBuilder();
        foreach (var a in Items.Reverse())
            sb.AppendLine(CultureInfo.InvariantCulture, $"{a.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}\t{a.JobId}\t{a.Tool}\t{a.Phase}\t{a.Detail.ReplaceLineEndings(" ")}");
        try
        {
            string dir = JobSpecFactory.MediatorDocumentsFolder();
            string file = Path.Combine(dir, $"activity-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, Mediator.Core.Security.Redactor.Redact(sb.ToString()));
            ExportText = "Saved " + file;
            Workspace.Host.OpenPath(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ExportText = "Could not save the log: " + ex.Message; }
    }
}

// ── 6.5 Knowledge ────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What the AI knows for this chat. Mediator keeps no long-term memory or document index yet; this shows what exists
/// (the conversation carried to each turn, shared folders, attachments) and exactly what was read for the latest answer.
/// </summary>
public sealed class KnowledgeModeViewModel(WorkspaceViewModel workspace)
    : RightModeViewModel(workspace, RightMode.Knowledge, "Knowledge", "\uE82D", "Memory, project context, what was retrieved")
{
    public ObservableCollection<InfoRow> Sources { get; } = new();
    public ObservableCollection<ActivityItemViewModel> Retrieved { get; } = new();
    public bool HasRetrieved => Retrieved.Count > 0;
    public string MemoryNote { get; } = "No long-term memory, project instructions or document index yet: each chat knows its own conversation, " +
        "the folders and files shared with it, and what its tools read. Those are listed here.";

    /// <summary>Tools that read something for the answer (files, attachments, mail, pages, models).</summary>
    internal static bool IsRetrieval(ActivityItemViewModel a) =>
        a.Tool.Contains("read", StringComparison.OrdinalIgnoreCase) || a.Tool.Contains("inspect", StringComparison.OrdinalIgnoreCase)
        || a.Tool.Contains("search", StringComparison.OrdinalIgnoreCase) || a.Tool.Contains("list", StringComparison.OrdinalIgnoreCase)
        || a.Tool.EndsWith("_cards", StringComparison.Ordinal) || a.Tool.EndsWith("_session", StringComparison.Ordinal);

    public override void Refresh()
    {
        CockpitModeViewModel.Fill(Sources,
        [
            new("Conversation", $"{Main.Conversation.Count} turn(s) carried to every follow-up"),
            new("Shared folders", Main.ChatFolders.Count == 0 ? "none" : string.Join("; ", Main.ChatFolders)),
            new("Attachments", !Main.AttachmentChips.Any() ? "none" : string.Join("; ", Main.AttachmentChips.Select(c => c.Label))),
            new("Saved chats", $"{Main.SavedChats.Count} (searchable from the left sidebar)"),
        ]);
        string? job = Main.Conversation.LastOrDefault()?.JobId;
        CockpitModeViewModel.Fill(Retrieved, Main.Activity.Where(a => a.JobId == job && IsRetrieval(a)).Take(60));
        OnPropertyChanged(nameof(HasRetrieved));
    }
}

// ── 6.6 Software Library ─────────────────────────────────────────────────────────────────────────────

/// <summary>A desktop program Mediator knows, with what a chat about it gets.</summary>
public sealed class SoftwareItem : ObservableObject
{
    private bool _isExpanded, _isPinned;

    public SoftwareItem(string id, string name, string category, string glyph, string status, bool available, string detail, string? executable,
        string capabilities, string? process, string? promptPrefix, WorkspaceViewModel workspace)
    {
        Id = id; Name = name; Category = category; Glyph = glyph; StatusText = status; IsAvailable = available; Detail = detail;
        Capabilities = capabilities;
        _isPinned = workspace.PinnedSoftware.Contains(id);
        RunCommand = new RelayCommand(() => { if (executable is not null) workspace.Host.Launch(executable); }, () => executable is not null);
        NewChatCommand = new RelayCommand(() =>
        {
            workspace.Main.StartAppChat(name, process, promptPrefix, $"{name} ({status}; {detail}). Mediator can: {capabilities}.");
            workspace.Host.ActivateComposer();
        });
        DetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        PinCommand = new RelayCommand(() => { IsPinned = !IsPinned; workspace.Software.SavePins(); });
        ConfigureCommand = new RelayCommand(() => workspace.Host.OpenOverlayTab(MediatorOverlayViewModel.ConnectorsTab));
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string Glyph { get; }
    public string StatusText { get; }
    public bool IsAvailable { get; }
    public string Detail { get; }
    public string Capabilities { get; }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }
    public RelayCommand RunCommand { get; }
    public RelayCommand NewChatCommand { get; }
    public RelayCommand DetailsCommand { get; }
    public RelayCommand PinCommand { get; }
    public RelayCommand ConfigureCommand { get; }
}

public sealed record SoftwareGroup(string Name, IReadOnlyList<SoftwareItem> Items);

/// <summary>Connected desktop programs by category; "New chat" opens a chat that already knows the program.</summary>
public sealed class SoftwareModeViewModel(WorkspaceViewModel workspace)
    : RightModeViewModel(workspace, RightMode.Software, "Software", "\uE74C", "Desktop programs Mediator drives")
{
    public static IReadOnlyList<string> CategoryOrder { get; } = ["Pinned", "Productivity", "CAD", "Simulation", "Development", "Creative", "Other"];
    public ObservableCollection<SoftwareGroup> Groups { get; } = new();
    private readonly List<SoftwareItem> _items = [];

    /// <summary>
    /// Category, process name (routes the chat's tools), prompt prefix and what Mediator can do, per program id. V18: a
    /// connector's programs describe themselves; the CLIs and Chrome are built in.
    /// </summary>
    public static (string Category, string? Process, string? Prefix, string Capabilities) Describe(string id) => Mediator.Core.Connectors.ConnectorCatalog.Program(id) is { } p
        ? (p.Category, p.Process, p.PromptPrefix, p.Capabilities) : id switch
    {
        "chrome" => ("Other", null, "/chrome ", "read and act in your Chrome tabs"),
        "claude" or "claude-code" or "gemini" or "antigravity" => ("Development", null, "/code ", "coding jobs in a folder"),
        _ => ("Other", null, null, "open it from a chat"),
    };

    public override void Refresh()
    {
        _items.Clear();
        if (Main.Connectors is { } c)
        {
            foreach (var a in c.Apps)
            {
                var (category, process, prefix, caps) = Describe(a.Id);
                _items.Add(new SoftwareItem(a.Id, a.Name, category, a.Glyph, a.StatusText, a.IsAvailable, a.Detail, a.IsAvailable ? a.IconPath : null,
                    caps, process, prefix, Workspace));
            }
            foreach (var cli in c.Clis)
            {
                var (category, process, prefix, caps) = Describe(cli.Id);
                _items.Add(new SoftwareItem(cli.Id, cli.Name, category, cli.Glyph, cli.StatusText, cli.IsInstalled, $"{cli.VersionText} · {cli.AuthText}", null,
                    caps, process, prefix, Workspace));
            }
        }
        Regroup();
    }

    private void Regroup()
    {
        var groups = _items.GroupBy(i => i.IsPinned ? "Pinned" : i.Category)
            .OrderBy(g => CategoryOrder.ToList().IndexOf(g.Key) is var k && k < 0 ? 99 : k)
            .Select(g => new SoftwareGroup(g.Key, g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()));
        CockpitModeViewModel.Fill(Groups, groups);
        OnPropertyChanged(nameof(HasItems));
    }

    public bool HasItems => _items.Count > 0;
    public string EmptyNote { get; } = "Programs are detected when the quick panel's Connectors tab is checked (Configure).";

    internal void SavePins()
    {
        Workspace.SetPinnedSoftware(_items.Where(i => i.IsPinned).Select(i => i.Id));
        Regroup();
    }
}
