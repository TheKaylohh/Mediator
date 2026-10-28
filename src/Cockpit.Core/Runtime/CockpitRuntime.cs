using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cockpit.Core.Attachments;
using Cockpit.Core.Bus;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Files;
using Cockpit.Core.Jobs;
using Cockpit.Core.Platform;
using Cockpit.Core.Blender;
using Cockpit.Core.CadCode;
using Cockpit.Core.Resolve;
using Cockpit.Core.Swx;
using Cockpit.Core.Tools;
using Cockpit.Core.Tutor;
using Cockpit.Protocol;

namespace Cockpit.Core.Runtime;

/// <summary>Where the Cockpit keeps its state. One folder, so backing up or wiping it is one step.</summary>
public sealed record CockpitPaths(string DataDirectory)
{
    public string AuditDatabase => Path.Combine(DataDirectory, "audit.db");
    public string JournalDatabase => Path.Combine(DataDirectory, "journal.db");
    public string UsageDatabase => Path.Combine(DataDirectory, "usage.db");
    public string OperationsDatabase => Path.Combine(DataDirectory, "operations.db");
    public string WorkspacesDirectory => Path.Combine(DataDirectory, "jobs");
    /// <summary>Snapshots of files and pasted images the user attached (V7). Pruned after 7 days.</summary>
    public string AttachmentsDirectory => Path.Combine(DataDirectory, "attachments");
    /// <summary>Overlay settings (theme, hotkey, first-run) (V7).</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    /// <summary>Custom MCP servers the user registered in the Connectors tab (V7).</summary>
    public string CustomMcpFile => Path.Combine(DataDirectory, "cockpit.mcp.json");

    /// <summary>%LOCALAPPDATA%\AiCockpit on Windows; $XDG_DATA_HOME/ai-cockpit or ~/.local/share/ai-cockpit elsewhere.</summary>
    public static CockpitPaths Default()
    {
        if (Environment.GetEnvironmentVariable("COCKPIT_DATA_DIR") is { Length: > 0 } custom) return new(Path.GetFullPath(custom));
        if (OperatingSystem.IsWindows())
            return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiCockpit"));
        string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return new(Path.Combine(xdg, "ai-cockpit"));
    }
}

public enum LaunchMode
{
    /// <summary>No CLI is started: the job's grant and workspace exist and an MCP client (CLI command, test) connects itself.</summary>
    None,
    /// <summary>`claude -p` or `agy` with stream-json, monitored for usage and rate limits.</summary>
    Headless,
    /// <summary>The CLI in a Windows Terminal tab, the billing-safe fallback; the user drives it. Watched through <see cref="InteractiveSession"/>.</summary>
    Interactive,
}

public enum JobState { Starting, Running, Succeeded, Failed, Killed, Ended }

public sealed record JobSpec
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string Prompt { get; init; } = "";
    public required IReadOnlyList<string> Tools { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    /// <summary>Folders the job may only read and list (e.g. where the files a prompt names live).</summary>
    public IReadOnlyList<string> ReadRoots { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public LaunchMode Launch { get; init; } = LaunchMode.Headless;
    public int MaxTurns { get; init; } = 40;
    public string? Model { get; init; }
    /// <summary>V10: reasoning effort (low|medium|high|xhigh|max) passed as --effort; null = the CLI's default.</summary>
    public string? Effort { get; init; }
    /// <summary>The CLI <see cref="Model"/> and <see cref="Effort"/> were chosen for; on another CLI (a switch) they are re-selected.</summary>
    public AiProvider? ModelFor { get; init; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(2);
    public bool AllowMeteredBilling { get; init; }
    public BusTransport Transport { get; init; } = BusTransport.NamedPipe;
    public IReadOnlyDictionary<string, ApprovalMode>? ToolModes { get; init; }
    /// <summary>Optional fixed ID (tests); otherwise "&lt;kind&gt;-&lt;utc time&gt;-&lt;random&gt;".</summary>
    public string? JobId { get; init; }
    /// <summary>Drives the default subscription (see <see cref="ProviderRouter"/>).</summary>
    public TaskCategory Category { get; init; } = TaskCategory.Code;
    /// <summary>Use this subscription first (the user picked it); null = the category's default.</summary>
    public AiProvider? Provider { get; init; }
    /// <summary>Refuse to start rather than switch subscriptions when the chosen one is at its limit.</summary>
    public bool NeverDowngrade { get; init; }
    /// <summary>
    /// What the user dropped, pasted or picked (V7). Each one's folder becomes a read-only root of the job; the job
    /// reads them with cockpit_read_attachment. Never widens write access.
    /// </summary>
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];
    /// <summary>
    /// V9: continue this Claude Code session (`claude --resume &lt;id&gt;`) instead of starting a new one. Must be a safe
    /// token (<see cref="SessionIds.IsSafe"/>); pins the job to Claude Code and never switches subscriptions. Built by
    /// <see cref="JobSpecFactory.Continue"/> from a <see cref="ContinuableSession"/>.
    /// </summary>
    public string? ContinueSessionId { get; init; }
    /// <summary>V10: the user's own words (model choice and cross-CLI context use them, not the full instructions).</summary>
    public string? UserText { get; init; }
    /// <summary>V10: which subscription goes first when <see cref="Provider"/> is null.</summary>
    public RoutingPolicy RoutingPolicy { get; init; } = RoutingPolicy.AutoBest;
}

/// <summary>A started job. Thread-safe: the state changes on the runtime's threads, observers read snapshots.</summary>
public sealed class RunningJob
{
    private readonly TaskCompletionSource<JobState> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state = (int)JobState.Starting;

    internal RunningJob(JobSpec spec, JobGrant grant, JobWorkspace workspace)
    {
        Spec = spec;
        Grant = grant;
        Workspace = workspace;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string JobId => Grant.JobId;
    public JobSpec Spec { get; }
    public JobGrant Grant { get; }
    public JobWorkspace Workspace { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public JobState State => (JobState)Volatile.Read(ref _state);
    public Task<JobState> Completion => _done.Task;
    public RunResultEvent? LastResult { get; internal set; }
    public string? Detail { get; internal set; }
    /// <summary>The subscription the job runs on (null for LaunchMode.None).</summary>
    public AiProvider? Provider { get; internal set; }
    /// <summary>Why the job is not on its default subscription, when it was switched.</summary>
    public string? RoutingNotice { get; internal set; }
    /// <summary>The subscription the job was routed to first (differs from <see cref="Provider"/> after a switch).</summary>
    public AiProvider? OriginalProvider { get; internal set; }
    /// <summary>How the last headless run ended (null while running, or for interactive jobs).</summary>
    public RunOutcome? Outcome { get; internal set; }
    /// <summary>V9: the CLI's session id from its stream (init or result event), for a later `--resume`.</summary>
    public string? SessionId { get; internal set; }
    /// <summary>V10: the model the current run uses (the job's, or <see cref="ProviderRouter.SelectModel"/>'s choice).</summary>
    public string? Model { get; internal set; }
    /// <summary>V10: the reasoning effort of the current run (null = the CLI's default).</summary>
    public string? Effort { get; internal set; }
    /// <summary>Every routing decision for this job, oldest first (also in the audit as cockpit/route).</summary>
    public IReadOnlyList<string> RoutingHistory => _routing;
    internal readonly List<string> _routing = new();
    internal bool SwitchedMidJob { get; set; }
    internal Process? Process { get; set; }
    internal ChildProcessJob ProcessGroup { get; } = new();
    internal InteractiveSession? Interactive { get; set; }
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    internal DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
    internal void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    internal void SetRunning() => Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Starting);

    /// <summary>
    /// First terminal state wins (a kill racing a normal exit stays a kill). <paramref name="beforeCompletion"/> runs
    /// once the final state is set but before <see cref="Completion"/> completes, so whatever it records is visible to
    /// anyone awaiting the job.
    /// </summary>
    internal bool TryFinish(JobState state, string? detail, Action<RunningJob>? beforeCompletion = null)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _state);
            if (current >= (int)JobState.Succeeded) return false;
        }
        while (Interlocked.CompareExchange(ref _state, (int)state, current) != current);
        EndedAt = DateTimeOffset.UtcNow;
        Detail ??= detail;
        beforeCompletion?.Invoke(this);
        _done.TrySetResult(state);
        return true;
    }
}

public sealed record RuntimeOptions
{
    public CockpitPaths Paths { get; init; } = CockpitPaths.Default();
    public BusOptions Bus { get; init; } = new();
    /// <summary>Null: located with <see cref="ComponentLocator"/>.</summary>
    public ComponentCommand? Bridge { get; init; }
    /// <summary>Null: `claude` resolved on PATH when a job needs it.</summary>
    public string? ClaudeExecutable { get; init; }
    /// <summary>Null: the real out-of-process worker. Tests and `--simulate` pass a <see cref="SimulatedSwxBackend"/>.</summary>
    public ISwxBackend? SwxBackend { get; init; }
    public ComponentCommand? SwxWorker { get; init; }
    public ProtectedPathPolicy? PathPolicy { get; init; }
    /// <summary>Starts SolidWorks for CAD tools. Null: the real one on Windows with the real worker, else nothing.</summary>
    public IDesktopAppSession? SolidWorksSession { get; init; }
    public TimeSpan BridgeTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public ICockpitLog Log { get; init; } = NullLog.Instance;
    /// <summary>Null: Antigravity CLI (`agy`) located on PATH or in %LOCALAPPDATA%gybin when a job needs it.</summary>
    public string? GeminiExecutable { get; init; }
    /// <summary>Tests: do not touch the user's ~/.gemini/antigravity-cli/settings.json.</summary>
    public bool SkipAgySettings { get; init; }
    /// <summary>How CLI processes are started (tests substitute a fake).</summary>
    public ICliLauncher Launcher { get; init; } = DefaultCliLauncher.Instance;
    /// <summary>Interactive sessions: how often the sentinel files are checked.</summary>
    public TimeSpan InteractivePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Interactive sessions: how long the terminal may take to write interactive.pid before the job fails.</summary>
    public TimeSpan InteractiveStartupGrace { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Interactive sessions: no tool call for this long ends the job (the grant is revoked; the tab stays open).</summary>
    public TimeSpan InteractiveIdleTimeout { get; init; } = TimeSpan.FromHours(2);

    // Helper programs for the tool providers. Defaults are detected when the options are created; tests pass fakes.
    public IProcessRunner ProcessRunner { get; init; } = DefaultProcessRunner.Instance;
    public IMediaProbe MediaProbe { get; init; } = CompositeMediaProbe.Detect();
    /// <summary>Python for PDF extraction and the Resolve script (local: they need your Windows files and apps).</summary>
    public IPythonRunner Python { get; init; } = LocalPythonRunner.Detect();
    /// <summary>Python for build123d. Null = <see cref="Python"/>. Set a <see cref="WslPythonRunner"/> to keep generated code inside WSL.</summary>
    public IPythonRunner? CadPython { get; init; }
    /// <summary>
    /// The sandbox for model-written code. Null = detected: bubblewrap on Linux; on Windows none unless configured
    /// (a <see cref="Isolation.WslBubblewrapSandbox"/>). Without one, custom code is refused, never run on the host.
    /// </summary>
    public Isolation.ISandboxRunner? CadSandbox { get; init; }
    /// <summary>The interpreter inside the sandbox. Null = probed from the host Python on Linux.</summary>
    public CadCode.SandboxPython? CadSandboxPython { get; init; }
    /// <summary>Where sandbox runs are staged. Null = the workspaces folder. Must be inside the WSL exchange folder for the WSL sandbox.</summary>
    public string? SandboxStagingDirectory { get; init; }
    public IBlenderRunner Blender { get; init; } = BlenderRunner.Detect();
    public string? Ffmpeg { get; init; } = ToolLocator.Ffmpeg();
}

/// <summary>Starts CLI processes. The default really starts them; tests record the start info and run a stand-in.</summary>
public interface ICliLauncher
{
    /// <summary>Starts a headless CLI with redirected stdio; the runtime writes the prompt and reads the stream.</summary>
    Process StartHeadless(ProcessStartInfo psi);
    /// <summary>Opens a terminal tab. The returned launcher process (wt.exe) is meaningless: it exits at once.</summary>
    void StartInteractive(ProcessStartInfo psi, JobWorkspace workspace);
}

public sealed class DefaultCliLauncher : ICliLauncher
{
    public static readonly DefaultCliLauncher Instance = new();

    public Process StartHeadless(ProcessStartInfo psi) => Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");

    public void StartInteractive(ProcessStartInfo psi, JobWorkspace workspace)
    {
        using var launcher = Process.Start(psi) ?? throw new InvalidOperationException("Could not open a terminal.");
    }
}

/// <summary>What the overlay and the CLI host need from the Core. Kept small so ViewModels can be tested with a fake.</summary>
public interface ICockpitRuntime
{
    event Action<ToolCallEvent>? ToolCallActivity;
    event Action<RunningJob>? JobChanged;
    event Action<string, CockpitStreamEvent>? StreamEvent;
    event Action<string, string>? BillingAlarm;
    /// <summary>(jobId, message): routing switched subscriptions, an interactive session ended, and similar news for the status line.</summary>
    event Action<string, string>? Notice;

    /// <summary>V10: the working folder for general chat jobs (no file roots). Default: a temp folder.</summary>
    string ChatDirectory => Path.Combine(Path.GetTempPath(), "AiCockpit-chat");

    /// <summary>Attach the overlay or console prompt here. With nothing attached every approval is denied.</summary>
    SwappableApprovalPrompt Approvals { get; }
    IReadOnlyList<RunningJob> Jobs { get; }
    Task<RunningJob> StartJobAsync(JobSpec spec, CancellationToken ct = default);
    /// <summary>The kill switch: revokes the grant (in-flight calls end, leases drop) and terminates the job's processes.</summary>
    bool KillJob(string jobId, string reason);
    Task EndJobAsync(string jobId);
    /// <summary>When a provider ("claude-code", "gemini-cli") hit a usage limit, the reported reset time; null when available.</summary>
    Task<DateTimeOffset?> GetCooldownAsync(string provider);
    /// <summary>Which subscription a job of this category would run on right now (for the overlay's pills). Throws <see cref="ProviderUnavailableException"/>.</summary>
    Task<RouteDecision> RouteAsync(TaskCategory category, AiProvider? provider = null, bool neverDowngrade = false);

    /// <summary>Stages a dropped or picked file (snapshot) or folder (read-only in place). Throws <see cref="AttachmentException"/>.</summary>
    Attachment StageAttachment(string path);
    /// <summary>Stages a pasted screenshot (PNG bytes). Throws <see cref="AttachmentException"/>.</summary>
    Attachment StagePastedImage(byte[] png);
    /// <summary>Forgets a staged attachment that was removed before use (deletes its snapshot).</summary>
    void DiscardAttachment(Attachment attachment);
    /// <summary>Gives a running job read-only access to an attachment. Audited as "cockpit/attach". False when the job is not running or cannot read attachments.</summary>
    Task<bool> AttachToJobAsync(string jobId, Attachment attachment);
    /// <summary>V10: the models each installed CLI offers (the prompt bar's picker). Default: none detected.</summary>
    Task<IReadOnlyList<CliModels>> DetectModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CliModels>>([]);

    /// <summary>
    /// V10: lets a running job read and write <paramref name="folder"/> (created if missing). Null when granted, else why not
    /// (a protected place, or the job is not running). Audited.
    /// </summary>
    Task<string?> GrantFolderAsync(string jobId, string folder) => Task.FromResult<string?>("not supported");
}

/// <summary>
/// The Core's composition root: bus, job registry, leases, gate, audit, file journal, usage ledger and tool
/// providers, plus job lifecycle (workspace, CLI launch, stream monitoring, kill). Hosted by Cockpit.CliHost
/// and by Cockpit.Wpf; both are thin shells around this class.
/// </summary>
public sealed class CockpitRuntime : ICockpitRuntime, IAsyncDisposable
{
    private readonly RuntimeOptions _options;
    private readonly ConcurrentDictionary<string, RunningJob> _jobs = new(StringComparer.Ordinal);
    private readonly ChildProcessJob _workerGroup = new();
    private readonly SwxWorkerClient? _ownedWorker;

    public event Action<ToolCallEvent>? ToolCallActivity;
    public event Action<RunningJob>? JobChanged;
    public event Action<string, CockpitStreamEvent>? StreamEvent;
    public event Action<string, string>? BillingAlarm;
    public event Action<string, string>? Notice;

    public SwappableApprovalPrompt Approvals { get; } = new();
    public JobRegistry Registry { get; }
    public LeaseManager Leases { get; } = new();
    public PermissionGate Gate { get; }
    public ToolRegistry Tools { get; } = new();
    public AuditLog Audit { get; }
    public FileJournalService Journal { get; }
    public UsageLedger Usage { get; }
    public McpBusServer Bus { get; }
    public ComponentCommand? Bridge { get; }
    public ISwxBackend Swx { get; }
    /// <summary>V10: the SolidWorks the CAD tools drive, started on demand (one copy); a no-op with a simulated backend.</summary>
    public IDesktopAppSession SolidWorks { get; }
    public CockpitPaths Paths => _options.Paths;

    public IReadOnlyList<RunningJob> Jobs => _jobs.Values.OrderBy(j => j.StartedAt).ToList();

    /// <summary>Durable record of side-effecting tool calls (dedupe, outcome-unknown, restart reconciliation).</summary>
    public OperationStore Operations { get; }

    /// <summary>Interactive jobs' tasks, served over the bus instead of a command line.</summary>
    internal JobBriefToolProvider Briefs { get; } = new();

    /// <summary>Attachments (V7): staged under the data folder, read by jobs through cockpit_read_attachment.</summary>
    public AttachmentStore AttachmentStore { get; }
    internal AttachmentToolProvider Attachments { get; } = new();

    /// <summary>Custom MCP servers from the Connectors tab (V7): every tool Destructive, proxied through the gate.</summary>
    public Connectors.CustomMcpService CustomMcp { get; }

    /// <summary>What restart reconciliation found when this runtime started.</summary>
    public (int NotRun, int Settled, int NeedsUser) Recovery { get; private set; }

    private CockpitRuntime(RuntimeOptions options, AuditLog audit, FileJournalService journal, UsageLedger usage, OperationStore operations)
    {
        Operations = operations;
        _options = options;
        Audit = audit;
        Journal = journal;
        Usage = usage;
        Registry = new JobRegistry();
        Gate = new PermissionGate(Approvals);
        Gate.KillRequested += jobId => KillJob(jobId, "kill chosen in an approval");
        Bridge = options.Bridge ?? ComponentLocator.Find(ComponentLocator.Bridge);

        if (options.SwxBackend is { } backend)
        {
            Swx = backend;
        }
        else
        {
            var worker = options.SwxWorker ?? ComponentLocator.Find(ComponentLocator.SwxWorker);
            _ownedWorker = new SwxWorkerClient(
                new SwxWorkerOptions(worker?.FileName ?? ComponentLocator.SwxWorker, worker?.PrefixArguments ?? []), _workerGroup, options.Log);
            Swx = _ownedWorker;
        }

        Tools.Register(new FilesToolProvider(journal));
        // The real SolidWorks only with the real worker on Windows; a simulated backend (tests, --simulate) starts nothing.
        SolidWorks = options.SolidWorksSession ?? (options.SwxBackend is null && OperatingSystem.IsWindows()
            ? new SolidWorksSession(SolidWorksSession.DefaultStartTimeout, options.Log)
            : NoDesktopAppSession.Instance);
        Tools.Register(new SwxToolProvider(Swx, Registry, SolidWorks));
        Tools.Register(new SwxPartToolProvider(SolidWorks));
        Tools.Register(new ResolveToolProvider(options.MediaProbe, options.Python, Registry));
        Tools.Register(new BlenderToolProvider(options.Blender, options.ProcessRunner, options.Ffmpeg));
        var cadPython = options.CadPython ?? options.Python;
        var sandbox = options.CadSandbox ?? (OperatingSystem.IsLinux()
            ? new Isolation.BubblewrapSandbox(options.ProcessRunner)
            : new Isolation.UnavailableSandbox("no sandbox is configured on Windows: set up the restricted WSL distro with bubblewrap (see docs/v6/ISOLATION.md)"));
        var sandboxPython = options.CadSandboxPython ?? (sandbox is Isolation.BubblewrapSandbox && cadPython is LocalPythonRunner local
            ? CadCode.SandboxPython.Probe(local.Executable) : null);
        Tools.Register(new CodeCadToolProvider(cadPython, sandbox, sandboxPython, options.SandboxStagingDirectory ?? options.Paths.WorkspacesDirectory));
        Tools.Register(new TutorToolProvider(options.Python, options.Paths.WorkspacesDirectory));
        Tools.Register(new OperationsToolProvider(operations));
        Tools.Register(Briefs);
        Tools.Register(Attachments);
        AttachmentStore = new AttachmentStore(options.Paths.AttachmentsDirectory);
        try { AttachmentStore.Prune(); } catch (IOException ex) { options.Log.Warn("Old attachments could not be pruned: " + ex.Message); }
        CustomMcp = new Connectors.CustomMcpService(options.Paths.CustomMcpFile, Tools, _workerGroup);
        if (CustomMcp.LoadError is { } mcpError) options.Log.Warn($"{options.Paths.CustomMcpFile} was not loaded ({mcpError}); no custom MCP tools are available.");
        Bus = new McpBusServer(options.Bus, Registry, Tools, Gate, Leases, audit, options.Log, operations);
        Bus.ToolCallActivity += e =>
        {
            if (_jobs.TryGetValue(e.JobId, out var active)) active.Touch();
            ToolCallActivity?.Invoke(e);
        };
        Registry.JobRevoked += OnRevoked;
    }

    public static async Task<CockpitRuntime> CreateAsync(RuntimeOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.Paths.DataDirectory);
        Directory.CreateDirectory(options.Paths.WorkspacesDirectory);
        Cockpit.Core.Security.Redactor.RegisterEnvironmentSecrets(GeminiLaunchBuilder.VariablesToScrub(false));
        var audit = await AuditLog.OpenAsync(options.Paths.AuditDatabase, ct).ConfigureAwait(false);
        var journal = await FileJournalService.OpenAsync(options.Paths.JournalDatabase, options.PathPolicy ?? ProtectedPathPolicy.CreateDefault(), ct).ConfigureAwait(false);
        var usage = await UsageLedger.OpenAsync(options.Paths.UsageDatabase, ct: ct).ConfigureAwait(false);
        var operations = await OperationStore.OpenAsync(options.Paths.OperationsDatabase, ct).ConfigureAwait(false);
        var runtime = new CockpitRuntime(options, audit, journal, usage, operations);
        // Before any bridge can connect: settle what was mid-flight when the previous Core stopped.
        runtime.Recovery = await operations.ReconcileAfterRestartAsync(op =>
            runtime.Tools.TryGet(op.Tool, out var provider, out _) && provider is IOperationReconciler r
                ? r.ReconcileAsync(op) : Task.FromResult<(OperationState, string)?>(null)).ConfigureAwait(false);
        if (runtime.Recovery.NeedsUser > 0)
            options.Log.Warn($"{runtime.Recovery.NeedsUser} operation(s) from before the restart need a check: cockpit operations");
        runtime.Bus.Start();
        return runtime;
    }

    /// <summary>V10: the models each installed CLI offers, for the prompt bar's model picker.</summary>
    public async Task<IReadOnlyList<CliModels>> DetectModelsAsync(CancellationToken ct = default)
    {
        var list = new List<CliModels>();
        if (ExecutableFor(AiProvider.ClaudeCode) is not null) list.Add(new CliModels(AiProvider.ClaudeCode, "Claude Code", ModelCatalog.ClaudeModels));
        if (ExecutableFor(AiProvider.GeminiCli) is { } agy) list.Add(new CliModels(AiProvider.GeminiCli, "Antigravity CLI", await ModelCatalog.AgyModelsAsync(agy, ct).ConfigureAwait(false)));
        return list;
    }

    /// <summary>
    /// V10: a folder the user named in the prompt may not exist yet ("output to a new folder"): it is created so the
    /// job can write there. System locations (Windows, Program Files, AppData) are refused before anything is issued.
    /// </summary>
    private void PrepareRoots(JobSpec spec)
    {
        var policy = _options.PathPolicy ?? ProtectedPathPolicy.CreateDefault();
        foreach (var root in spec.Roots)
        {
            string full = Path.GetFullPath(root);
            if (policy.CheckJobRoot(full) is { } why) throw new ArgumentException($"The job cannot work in {full}: {why}. Pick another folder.", nameof(spec));
            if (Directory.Exists(full)) continue; // missing levels are created (Documents\CAD\F40 when only Documents exists)
            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException($"Could not create {full}: {ex.Message}", nameof(spec), ex);
            }
            RaiseNotice(spec.JobId ?? spec.Kind, $"Created the folder you named: {full}");
        }
    }

    public static string NewJobId(string kind) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}");

    public async Task<RunningJob> StartJobAsync(JobSpec spec, CancellationToken ct = default)
    {
        var bridge = Bridge ?? throw new InvalidOperationException(
            "cockpit-mcp-bridge was not found next to the Cockpit or in the repository build output. Build src/Cockpit.Bridge first.");
        string jobId = spec.JobId ?? NewJobId(spec.Kind);
        if (spec.ContinueSessionId is { } resume)
        {
            // V9: the id goes on a command line (and through claude.cmd, cmd.exe): only a plain token is accepted.
            if (!SessionIds.IsSafe(resume)) throw new ArgumentException("The session to continue has an invalid id; start a fresh job instead.", nameof(spec));
            if (spec.Provider == AiProvider.GeminiCli) throw new ArgumentException("Only Claude Code sessions can be continued.", nameof(spec));
            spec = spec with { Provider = AiProvider.ClaudeCode, NeverDowngrade = true };
        }
        RouteDecision? route = null;
        if (spec.Launch != LaunchMode.None)
        {
            try
            {
                route = await RouteAsync(spec.Category, spec.Provider, spec.NeverDowngrade, spec.RoutingPolicy).ConfigureAwait(false); // throws before anything is issued
            }
            catch (ProviderUnavailableException ex)
            {
                await AuditRouteAsync(jobId, spec.NeverDowngrade ? FailoverDecision.RefusedNeverDowngrade : FailoverDecision.RefusedBothUnavailable,
                    spec.Provider ?? ProviderRouter.Defaults(spec.Category).Primary, null, ex.Message).ConfigureAwait(false);
                if (spec.ContinueSessionId is not null)
                    throw new ProviderUnavailableException(ex.Message + " A Claude Code session can only continue on Claude Code; clear the continue-session chip to start fresh.");
                throw;
            }
            await AuditRouteAsync(jobId, route.Downgraded ? FailoverDecision.SwitchedBeforeLaunch : FailoverDecision.NotNeeded,
                route.Original, route.Provider, route.Reason).ConfigureAwait(false);
        }
        PrepareRoots(spec);
        // A job that will drive SolidWorks starts it now, so it is loading while the model reads and plans.
        if (spec.Tools.Any(t => SwxToolProvider.ToolNames.Contains(t) || SwxPartToolProvider.ToolNames.Contains(t)))
            SolidWorks.WarmUp();
        // The user's own words always reach the model, whatever template the action used (Assemble had no slot for them).
        spec = spec with { Prompt = JobSpecFactory.WithUserText(spec) };
        if (spec.Attachments.Count > 0) spec = spec with { Prompt = spec.Prompt + AttachmentPromptSuffix(spec.Attachments) };
        bool brief = spec.Launch == LaunchMode.Interactive && spec.Prompt.Length > 0;
        // Attachment tools (read-only, and they only see what the user attached to this job) are granted when the job
        // starts with attachments, or runs interactively, where the user can drop more while talking to the model.
        // A headless run cannot take new input, and every other job keeps exactly its action's tool list.
        var builtIns = new List<string> { OperationsToolProvider.StatusTool };
        if (spec.Attachments.Count > 0 || spec.Launch == LaunchMode.Interactive) builtIns.AddRange(AttachmentToolProvider.Names);
        // Enabled custom MCP tools (V7). They are Destructive: every call still needs the user's approval.
        builtIns.AddRange(CustomMcp.EnabledToolNames);
        if (brief) builtIns.Add(JobBriefToolProvider.BriefTool);
        var grant = Registry.Issue(jobId, spec.Tools.Concat(builtIns).Distinct(StringComparer.Ordinal), spec.Roots, spec.Lifetime, spec.Transport, spec.ToolModes,
            allowMeteredBilling: spec.AllowMeteredBilling, readOnlyRoots: spec.Attachments.Select(a => a.GrantedRoot).Concat(ReadOnlyExtras(spec)));
        foreach (var a in spec.Attachments) Attachments.Add(jobId, a);
        if (brief) Briefs.Set(jobId, spec.Prompt);
        RunningJob job;
        try
        {
            var ws = JobWorkspaceFactory.Create(Paths.WorkspacesDirectory, grant, bridge, Bus.EndpointFor(grant), _options.BridgeTimeout);
            job = new RunningJob(spec, grant, ws) { Provider = route?.Provider, RoutingNotice = route?.Notice, OriginalProvider = route?.Original };
            if (route is not null)
                job._routing.Add(route.Downgraded ? $"before launch: {route.Notice}" : $"before launch: {ProviderRouter.DisplayName(route.Provider)}");
            _jobs[jobId] = job;
        }
        catch
        {
            Registry.Revoke(jobId);
            throw;
        }

        try
        {
            switch (spec.Launch)
            {
                case LaunchMode.Headless: await LaunchHeadlessAsync(job, ct).ConfigureAwait(false); break;
                case LaunchMode.Interactive: LaunchInteractive(job); break;
            }
        }
        catch (Exception ex)
        {
            Finish(job, JobState.Failed, ex.Message);
            throw;
        }
        job.SetRunning();
        Changed(job);
        if (route?.Notice is { } notice) RaiseNotice(jobId, notice);
        return job;
    }

    internal static string AttachmentPromptSuffix(IReadOnlyList<Attachment> attachments) =>
        "\n\nThe user attached " + (attachments.Count == 1 ? "1 item" : $"{attachments.Count} items") + " (read-only; read them with the "
        + AttachmentToolProvider.ReadTool + " tool):\n"
        + string.Join("\n", attachments.Select(a => $"- {a.Id}: {a.Kind.ToString().ToLowerInvariant()} {a.Name}"));

    public Attachment StageAttachment(string path) => AttachmentStore.AddPath(path);
    public Attachment StagePastedImage(byte[] png) => AttachmentStore.AddPng(png);
    public void DiscardAttachment(Attachment attachment) => AttachmentStore.Discard(attachment);

    /// <summary>
    /// Adds a read-only root to a running job's grant and lists the attachment for it. Only read access is ever
    /// added, the grant's tools and approval rules are unchanged, and the change is audited. False when the job is
    /// not running, or was not granted the attachment tools (a headless job started without attachments).
    /// </summary>
    public async Task<bool> AttachToJobAsync(string jobId, Attachment attachment)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || job.State is not (JobState.Starting or JobState.Running) || !Registry.TryGet(jobId, out var grant) || grant is null || !Registry.IsCurrent(grant))
            return false;
        if (!grant.AllowedTools.Contains(AttachmentToolProvider.ReadTool)) return false;
        string root = grant.Paths.AddReadOnlyRoot(attachment.GrantedRoot);
        Attachments.Add(jobId, attachment);
        await Audit.RecordAsync(new AuditEntry(jobId, "cockpit/attach", attachment.Id, ToolRisk.ReadOnly,
            new JsonObject { ["kind"] = attachment.Kind.ToString(), ["name"] = attachment.Name, ["root"] = root, ["access"] = "read_only" }.ToJsonString(),
            "user", "ok", null, 0)).ConfigureAwait(false);
        RaiseNotice(jobId, $"Attached {attachment.Name} (read-only). The job reads it with {AttachmentToolProvider.ReadTool}.");
        return true;
    }

    public async Task<string?> GrantFolderAsync(string jobId, string folder)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ex.Message; }
        if ((_options.PathPolicy ?? ProtectedPathPolicy.CreateDefault()).CheckJobRoot(full) is { } why) return $"{full} cannot be allowed: {why}";
        if (!_jobs.TryGetValue(jobId, out var job) || job.State is not (JobState.Starting or JobState.Running)
            || !Registry.TryGet(jobId, out var grant) || grant is null || !Registry.IsCurrent(grant))
            return "the job is not running";
        string root;
        try
        {
            Directory.CreateDirectory(full);
            root = grant.Paths.AddRoot(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return $"could not allow {full}: {ex.Message}"; }
        await Audit.RecordAsync(new AuditEntry(jobId, "cockpit/grant-folder", root, ToolRisk.Write,
            new JsonObject { ["root"] = root, ["access"] = "read_write" }.ToJsonString(), "user", "ok", null, 0)).ConfigureAwait(false);
        RaiseNotice(jobId, $"Allowed {root} for this job (read and write).");
        return null;
    }

    public Task<RouteDecision> RouteAsync(TaskCategory category, AiProvider? provider = null, bool neverDowngrade = false) =>
        RouteAsync(category, provider, neverDowngrade, RoutingPolicy.AutoBest);

    /// <summary>V10: routing with a <see cref="RoutingPolicy"/> (Quota Saver puts Antigravity first, Claude First puts Claude first).</summary>
    public async Task<RouteDecision> RouteAsync(TaskCategory category, AiProvider? provider, bool neverDowngrade, RoutingPolicy policy)
    {
        var cooldowns = new Dictionary<AiProvider, DateTimeOffset?>();
        foreach (var p in Enum.GetValues<AiProvider>())
            cooldowns[p] = await Usage.GetCooldownAsync(ProviderRouter.Id(p)).ConfigureAwait(false);
        return ProviderRouter.Route(category, provider, neverDowngrade, p => cooldowns[p], p => ExecutableFor(p) is not null, policy);
    }

    /// <summary>
    /// Every routing decision is audited as tool "cockpit/route": the original provider, the provider attempted
    /// (if any), the decision and the reason. Grants, tools and approval policy never change with a switch.
    /// </summary>
    private Task AuditRouteAsync(string jobId, FailoverDecision decision, AiProvider original, AiProvider? attempted, string? reason) =>
        Audit.RecordAsync(new AuditEntry(jobId, "cockpit/route", "route", ToolRisk.ReadOnly,
            new JsonObject
            {
                ["original"] = ProviderRouter.Id(original),
                ["attempted"] = attempted is { } a ? ProviderRouter.Id(a) : null,
                ["decision"] = decision.ToString(),
            }.ToJsonString(),
            decision is FailoverDecision.NotNeeded or FailoverDecision.SwitchedBeforeLaunch or FailoverDecision.SwitchedAfterRateLimit ? "routed" : "refused",
            decision is FailoverDecision.NotNeeded or FailoverDecision.SwitchedBeforeLaunch or FailoverDecision.SwitchedAfterRateLimit ? "ok" : "not_run",
            reason, 0));

    private static readonly System.Text.RegularExpressions.Regex AuthText = new(
        @"invalid api key|please run /login|not logged in|log ?in required|authenticat|unauthori[sz]ed|\b401\b|auth method|credentials? (are|were|is) (missing|invalid|expired)|oauth",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Classifies how a headless run ended. Pure; unit-tested.</summary>
    public static RunOutcome Classify(int exitCode, RunResultEvent? result, string stderrTail)
    {
        if (result is null)
            return exitCode == GeminiLaunchBuilder.ExitNoAuth || AuthText.IsMatch(stderrTail) ? RunOutcome.AuthFailed : RunOutcome.NoResult;
        if (result.RateLimit is not null) return RunOutcome.RateLimited;
        if (result.IsError) return AuthText.IsMatch(result.ResultText ?? "") ? RunOutcome.AuthFailed : RunOutcome.ResultError;
        return exitCode == 0 ? RunOutcome.Succeeded : RunOutcome.ResultThenAbnormalExit;
    }

    /// <summary>
    /// True when no side-effecting call of this job can have taken effect: every recorded operation stopped before
    /// Executing (rejected, denied, cancelled). Anything that reached Executing, including failed calls, may have
    /// partly taken effect, so it blocks an automatic retry on another subscription.
    /// </summary>
    internal async Task<(bool None, int Count)> SideEffectsAsync(string jobId)
    {
        var ops = await Operations.ListAsync(jobId).ConfigureAwait(false);
        int possible = ops.Count(o => o.State is not (OperationState.Rejected or OperationState.Denied or OperationState.Cancelled
            or OperationState.Pending or OperationState.Validated or OperationState.Approved));
        return (possible == 0, possible);
    }

    private string? ExecutableFor(AiProvider provider) => provider == AiProvider.ClaudeCode
        ? _options.ClaudeExecutable ?? CliLaunchBuilder.ResolveOnPath("claude")
        : _options.GeminiExecutable ?? GeminiLaunchBuilder.Locate();

    private void RaiseNotice(string jobId, string message)
    {
        _options.Log.Info($"Job {jobId}: {message}");
        try { Notice?.Invoke(jobId, message); }
        catch (Exception ex) { _options.Log.Error("Notice handler failed", ex); }
    }

    /// <summary>Connects an MCP client to a job through the real bridge process, as Claude Code would.</summary>
    public Task<BridgeMcpClient> ConnectClientAsync(RunningJob job, CancellationToken ct = default)
    {
        var bridge = Bridge ?? throw new InvalidOperationException("cockpit-mcp-bridge not found.");
        return BridgeMcpClient.StartAsync(bridge, JobWorkspaceFactory.BridgeArguments(job.Grant, Bus.EndpointFor(job.Grant), _options.BridgeTimeout),
            job.ProcessGroup, "cockpit-cli", ct, JobWorkspaceFactory.BridgeEnvironment(job.Grant));
    }

    private string RequireExecutable(AiProvider provider) => ExecutableFor(provider) ?? throw new ProviderUnavailableException(provider == AiProvider.ClaudeCode
        ? "Claude Code (claude) is not on PATH. Install it and sign in with your Pro account first (`claude`, then /login)."
        : "Antigravity CLI (agy) is not installed. Install it (irm https://antigravity.google/cli/install.ps1 | iex) and sign in with your Google AI Pro account (`agy`).");

    private ProcessStartInfo AgyHeadless(RunningJob job) =>
        AllowCockpitTools(new GeminiLaunchBuilder(RequireExecutable(AiProvider.GeminiCli))).Headless(job.Workspace, job.Model, job.Spec.AllowMeteredBilling, job.Effort);

    /// <summary>agy soft-denies unapproved MCP tools in headless runs; make sure the user-level mcp(cockpit/*) rule exists.</summary>
    private GeminiLaunchBuilder AllowCockpitTools(GeminiLaunchBuilder builder)
    {
        if (!_options.SkipAgySettings && !GeminiLaunchBuilder.EnsureCockpitToolsAllowed())
            _options.Log.Info("Antigravity settings.json is not valid JSON; add \"" + GeminiLaunchBuilder.CockpitAllowRule + "\" to permissions.allow yourself.");
        return builder;
    }

    /// <summary>V10: a Chat or Search job with no folder, tools or attachments: the CLI gets no access to local files.</summary>
    internal static bool NoLocalAccess(JobSpec spec) =>
        JobSpecFactory.IsNoLocalAccess(spec.Category) && spec.Roots.Count == 0 && spec.Tools.Count == 0 && spec.Attachments.Count == 0;

    private ProcessStartInfo HeadlessStartInfo(RunningJob job)
    {
        var spec = job.Spec;
        var provider = job.Provider ?? AiProvider.ClaudeCode;
        // V10: a job without a model gets one per run, for the provider actually used (a mid-job switch re-selects it).
        bool chosenHere = spec.ModelFor is null || spec.ModelFor == provider;
        // The action's Cockpit tools; the built-ins every job gets (status, attachments) do not make a job a tool job.
        int tools = spec.Tools.Count;
        var (model, effort, note) = ProviderRouter.ResolveRun(provider, chosenHere ? spec.Model : null, chosenHere ? spec.Effort : null,
            spec.Category, spec.UserText ?? spec.Prompt, tools);
        job.Model = model;
        job.Effort = effort;
        if (note is not null) RaiseNotice(job.JobId, note);
        return provider == AiProvider.GeminiCli
            ? AgyHeadless(job)
            : new CliLaunchBuilder(RequireExecutable(AiProvider.ClaudeCode), _options.BridgeTimeout + TimeSpan.FromMinutes(1))
                .ClaudeHeadless(job.Workspace, spec.WorkingDirectory, spec.MaxTurns, job.Model, spec.AllowMeteredBilling, spec.ContinueSessionId,
                    webSearch: spec.Category == TaskCategory.Search, noLocalFiles: NoLocalAccess(spec), effort: job.Effort);
    }

    private async Task LaunchHeadlessAsync(RunningJob job, CancellationToken ct)
    {
        var provider = job.Provider ?? AiProvider.ClaudeCode;
        string cli = provider == AiProvider.ClaudeCode ? "claude" : "agy";
        var process = _options.Launcher.StartHeadless(HeadlessStartInfo(job));
        job.Process = process;
        job.ProcessGroup.Assign(process);

        var stderrTail = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrTail.Enqueue(e.Data);
            while (stderrTail.Count > 40) stderrTail.TryDequeue(out string? _);
        };
        process.BeginErrorReadLine();

        string stdin = provider == AiProvider.GeminiCli
            ? GeminiLaunchBuilder.StdinMessage(job.Spec.Prompt, cockpitTools: !NoLocalAccess(job.Spec), webSearch: job.Spec.Category == TaskCategory.Search)
            : job.Spec.Prompt;
        await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();

        var monitor = CliRunMonitor.For(provider, job.JobId, Usage);
        monitor.EventReceived += (id, e) =>
        {
            job.Touch();
            if (e.SessionId is { Length: > 0 } sid) job.SessionId = sid;
            StreamEvent?.Invoke(id, e);
        };
        monitor.BillingAlarm += (id, source) =>
        {
            _options.Log.Warn($"Job {id} is billed to an API key ({source}), not your subscription.");
            BillingAlarm?.Invoke(id, source);
            if (!job.Spec.AllowMeteredBilling) KillJob(id, $"run authenticated with {source}; metered billing was not opted in");
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await monitor.ObserveAsync(process.StandardOutput, CancellationToken.None).ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                job.LastResult = monitor.LastResult;
                string tail = string.Join(" | ", stderrTail);
                var outcome = Classify(process.ExitCode, monitor.LastResult, tail);
                job.Outcome = outcome;
                if (monitor.LastResult is null) await monitor.RecordMissingResultAsync($"{cli} exited with code {process.ExitCode} without a result").ConfigureAwait(false);
                if (job.State is not (JobState.Starting or JobState.Running)) return; // killed meanwhile: the kill stands
                if (outcome == RunOutcome.RateLimited && await TrySwitchAfterRateLimitAsync(job, provider).ConfigureAwait(false)) return;
                string detail = outcome switch
                {
                    RunOutcome.Succeeded => monitor.LastResult!.ResultText ?? "done",
                    RunOutcome.RateLimited => monitor.LastResult!.RateLimit!.Message,
                    RunOutcome.AuthFailed => $"{cli} is not signed in or its login expired: {monitor.LastResult?.ResultText ?? tail}. Sign in by running `{cli}` once in a terminal.",
                    RunOutcome.NoResult => $"{cli} exited with code {process.ExitCode} without a result. {tail}".TrimEnd(),
                    RunOutcome.ResultThenAbnormalExit => $"{cli} reported a result but then exited with code {process.ExitCode}: {monitor.LastResult!.ResultText}",
                    _ => monitor.LastResult?.ResultText ?? $"{cli} reported an error.",
                };
                Finish(job, outcome == RunOutcome.Succeeded ? JobState.Succeeded : JobState.Failed, detail);
            }
            catch (Exception ex)
            {
                Finish(job, JobState.Failed, ex.Message);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// A run hit a usage limit. Switch to the other subscription and run the same job again only if: the job allows
    /// switching (not NeverDowngrade), it has not switched mid-job before, the other CLI is installed and not cooling
    /// down, and no side-effecting call can have taken effect. The grant, tool allow-list, roots and approval policy
    /// are unchanged. Every outcome is audited and shown.
    /// </summary>
    private async Task<bool> TrySwitchAfterRateLimitAsync(RunningJob job, AiProvider failed)
    {
        var other = failed == AiProvider.ClaudeCode ? AiProvider.GeminiCli : AiProvider.ClaudeCode;
        string limit = job.LastResult?.RateLimit?.Message ?? "usage limit";
        async Task<bool> Refuse(FailoverDecision d, string why)
        {
            job._routing.Add($"after start: stayed on {ProviderRouter.DisplayName(failed)}: {why}");
            await AuditRouteAsync(job.JobId, d, failed, other, why).ConfigureAwait(false);
            RaiseNotice(job.JobId, $"{ProviderRouter.DisplayName(failed)} hit its limit; not switching: {why}");
            return false;
        }
        if (job.Spec.NeverDowngrade) return await Refuse(FailoverDecision.RefusedNeverDowngrade, "the job is set to never switch subscriptions").ConfigureAwait(false);
        if (job.SwitchedMidJob) return await Refuse(FailoverDecision.RefusedAlreadySwitched, "the job already switched once").ConfigureAwait(false);
        var (none, count) = await SideEffectsAsync(job.JobId).ConfigureAwait(false);
        if (!none) return await Refuse(FailoverDecision.RefusedSideEffects,
            $"{count} side-effecting call(s) may already have taken effect; re-running on another subscription could repeat or contradict them").ConfigureAwait(false);
        RouteDecision next;
        try { next = await RouteAsync(job.Spec.Category, other, neverDowngrade: true).ConfigureAwait(false); }
        catch (ProviderUnavailableException ex) { return await Refuse(FailoverDecision.RefusedBothUnavailable, ex.Message).ConfigureAwait(false); }

        string why = $"{ProviderRouter.DisplayName(failed)} hit a usage limit ({limit}) before any side effect";
        job.SwitchedMidJob = true;
        job.Provider = next.Provider;
        job.RoutingNotice = $"{why}; continuing on {ProviderRouter.DisplayName(next.Provider)}.";
        job._routing.Add("after start: " + job.RoutingNotice);
        job.Outcome = null;
        job.LastResult = null;
        await AuditRouteAsync(job.JobId, FailoverDecision.SwitchedAfterRateLimit, failed, next.Provider, why).ConfigureAwait(false);
        RaiseNotice(job.JobId, job.RoutingNotice);
        Changed(job);
        await LaunchHeadlessAsync(job, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private void LaunchInteractive(RunningJob job)
    {
        var spec = job.Spec;
        var session = new InteractiveSession(job.Workspace.Root);
        session.ClearStale();
        bool hasBrief = spec.Prompt.Length > 0;
        var psi = job.Provider == AiProvider.GeminiCli
            ? AllowCockpitTools(new GeminiLaunchBuilder(RequireExecutable(AiProvider.GeminiCli))).Interactive(job.Workspace, session, hasBrief, spec.AllowMeteredBilling)
            : new CliLaunchBuilder(RequireExecutable(AiProvider.ClaudeCode), _options.BridgeTimeout + TimeSpan.FromMinutes(1))
                .ClaudeInteractive(job.Workspace, spec.WorkingDirectory, session, hasBrief, spec.AllowMeteredBilling, spec.ContinueSessionId);
        job.Interactive = session;
        _options.Launcher.StartInteractive(psi, job.Workspace);
        // Never watch the launcher: wt.exe exits within milliseconds. The script's sentinel files tell us what happens.
        _ = Task.Run(() => WatchInteractiveAsync(job), CancellationToken.None);
    }

    /// <summary>
    /// Ends an interactive job when the CLI exits (interactive.done), when its terminal disappears without writing
    /// it (tab closed), when the terminal never starts, or after the idle timeout. Kill and End are handled elsewhere.
    /// </summary>
    private async Task WatchInteractiveAsync(RunningJob job)
    {
        var session = job.Interactive!;
        var started = DateTimeOffset.UtcNow;
        bool sawTerminal = false;
        try
        {
            while (job.State is JobState.Starting or JobState.Running)
            {
                if (session.TryReadDone(out int exit))
                {
                    RaiseNotice(job.JobId, $"Interactive session ended (exit code {exit}).");
                    Finish(job, exit == 0 ? JobState.Succeeded : JobState.Failed, $"the CLI exited with code {exit}");
                    return;
                }
                if (session.TryReadPid(out _, out _))
                {
                    using var terminal = session.FindLiveProcess();
                    if (terminal is not null) sawTerminal = true;
                    else if (!session.TryReadDone(out _))
                    {
                        RaiseNotice(job.JobId, "The terminal tab was closed; the job's access was revoked.");
                        Finish(job, JobState.Ended, "the terminal was closed");
                        return;
                    }
                }
                else if (!sawTerminal && DateTimeOffset.UtcNow - started > _options.InteractiveStartupGrace)
                {
                    Finish(job, JobState.Failed, "the terminal did not start (no interactive.pid). Is PowerShell allowed to run the launch script?");
                    return;
                }
                if (DateTimeOffset.UtcNow - job.LastActivity > _options.InteractiveIdleTimeout)
                {
                    RaiseNotice(job.JobId, "Interactive job ended after being idle; its terminal stays open without Cockpit tools.");
                    Finish(job, JobState.Ended, $"idle for {_options.InteractiveIdleTimeout.TotalMinutes:0} min");
                    return;
                }
                await Task.Delay(_options.InteractivePollInterval).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _options.Log.Error($"Interactive watcher for {job.JobId} failed", ex);
        }
    }

    public bool KillJob(string jobId, string reason)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        bool first = job.TryFinish(JobState.Killed, reason);
        Registry.Revoke(jobId); // closes its bus connections, cancels in-flight calls, releases leases
        job.ProcessGroup.TerminateAll();
        try { if (job.Process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (System.ComponentModel.Win32Exception ex) { _options.Log.Warn($"Kill of job {jobId}: {ex.Message}"); }
        // A terminal tab belongs to Windows Terminal, not to our job object: end the shell recorded in interactive.pid.
        if (job.Interactive?.KillTerminal() == true) _options.Log.Info($"Job {jobId}: terminal process tree ended.");
        if (first)
        {
            _options.Log.Warn($"Job {jobId} killed: {reason}");
            _ = Audit.RecordAsync(new AuditEntry(jobId, "cockpit/kill", "kill", ToolRisk.Destructive, "{}", "killed", "not_run", reason, 0));
            Changed(job);
        }
        return true;
    }

    public Task<DateTimeOffset?> GetCooldownAsync(string provider) => Usage.GetCooldownAsync(provider);

    public Task EndJobAsync(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job)) Finish(job, JobState.Ended, "ended by the user");
        return Task.CompletedTask;
    }

    private void Finish(RunningJob job, JobState state, string? detail)
    {
        if (job.TryFinish(state, detail, j => { if (ContinuableSession.From(j) is { } session) LastContinuableSession = session; }))
            Changed(job);
        Registry.Revoke(job.JobId);
    }

    /// <summary>V9: the most recent finished headless Claude run that a follow-up can continue (`--resume`), or null.</summary>
    public ContinuableSession? LastContinuableSession { get; private set; }

    /// <summary>V10: where general chat jobs run (no file roots).</summary>
    public string ChatDirectory => Path.Combine(Paths.DataDirectory, "chat");

    private void OnRevoked(string jobId)
    {
        Briefs.Remove(jobId);
        Attachments.Remove(jobId);
        // Expiry (bus sweep) or an external revoke ends the job too.
        if (_jobs.TryGetValue(jobId, out var job) && job.TryFinish(JobState.Ended, "grant revoked or expired"))
        {
            try { if (job.Process is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Changed(job);
        }
    }

    private void Changed(RunningJob job)
    {
        try { JobChanged?.Invoke(job); }
        catch (Exception ex) { _options.Log.Error("JobChanged handler failed", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var job in _jobs.Values.Where(j => j.State is JobState.Starting or JobState.Running))
            KillJob(job.JobId, "Cockpit shutting down");
        await Bus.DisposeAsync().ConfigureAwait(false);
        if (_ownedWorker is not null) await _ownedWorker.DisposeAsync().ConfigureAwait(false);
        _workerGroup.Dispose();
        foreach (var job in _jobs.Values) job.ProcessGroup.Dispose();
        await Usage.DisposeAsync().ConfigureAwait(false);
        await Journal.DisposeAsync().ConfigureAwait(false);
        await Operations.DisposeAsync().ConfigureAwait(false);
        await Audit.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The spec's read-only folders that exist, are not protected and are not already writable roots.</summary>
    private IEnumerable<string> ReadOnlyExtras(JobSpec spec)
    {
        var policy = _options.PathPolicy ?? ProtectedPathPolicy.CreateDefault();
        return spec.ReadRoots.Where(r => Directory.Exists(r) && policy.CheckJobRoot(r) is null
            && !spec.Roots.Contains(r, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The job recipes behind the overlay's action tiles and the CLI commands.</summary>
public static partial class JobSpecFactory
{
    /// <summary>
    /// Every CAD job gets the whole SolidWorks toolset: building parts (native or from code / STEP), reading part cards and
    /// building assemblies. Splitting them made "make the parts and assemble them" impossible in one job.
    /// </summary>
    public static readonly string[] CadTools =
    [
        SwxPartToolProvider.BuildTool, SwxPartToolProvider.SetTool,                // native parametric parts
        SwxPartToolProvider.SessionTool, SwxPartToolProvider.DocumentTool, SwxPartToolProvider.ExportTool, // open/save/close, STEP/STL/PDF
        CodeCadToolProvider.GenerateTool, CodeCadToolProvider.CustomTool, SwxToolProvider.ImportStepTool, // code → STEP → part
        SwxToolProvider.PartCardsTool, SwxToolProvider.BuildTool,                  // part cards → assembly
    ];

    /// <summary>
    /// Turns (model replies, one per tool call) a CAD job may take: several parts, their fixes and version iterations,
    /// part cards and the assembly all happen in one run. Claude Code stops the job at --max-turns.
    /// </summary>
    public const int CadMaxTurns = 60;
    public static readonly string[] ResolveTools = [ResolveToolProvider.ProbeTool, ResolveToolProvider.ExportTool, ResolveToolProvider.ApplyTool];
    public static readonly string[] BlenderTools = [BlenderToolProvider.BuildTool, BlenderToolProvider.RenderTool];
    public static readonly string[] TutorTools = [TutorToolProvider.ExtractTool, TutorToolProvider.VerifyTool];

    private static string J(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>Prompt 1: parametric parts as code → STEP → SolidWorks parts → assembly.</summary>
    public static JobSpec CodeCad(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "codecad", Category = TaskCategory.Cad, Title = $"Parametric parts in {Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}",
            Tools = CadTools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = CadMaxTurns,
            Prompt = $$"""
                Design parametric parts for: {{request}}
                Output folder: {{full}}
                A. A single part described by its dimensions or features (plates, holes, bosses, ribs, fillets), or anything where the user
                   wants design intent / to change dimensions later in SolidWorks: build it NATIVELY with swx_build_parametric_part
                   (outputFolder "{{J(full)}}"). Make every size a global (mm) and position features with expressions of those globals
                   (e.g. corner holes at "W/2-E"), so the part survives dimension changes. Check the returned report (no rebuild errors,
                   holes at the right edge distances, features centred). If the user asks to change dimensions, call swx_set_part_globals
                   and verify the report again. The part stays open in SolidWorks. Skip steps 1-3 below.
                   Organic or styled shapes (vehicles, figurines, handles, housings, anything to 3D print) are also case A. Build them
                   with curves, not boxes: draw sections with splines/outlines on offset planes (op plane + named sketches), loft them in
                   order (add guide sketches to control the silhouette; a surface loft with guides stands in for a boundary surface),
                   sweep a profile along a path for tubes and rims, revolve round parts, and use surface + knit + thicken for skins.
                   For symmetric shapes build one half and mirror it. Finish with fillets (edgesNear picks specific edges) and cuts or
                   extrudes for details. Keep ONE solid body with no thin walls (>= 1.5 mm). For a size limit set fitBoxMm and check
                   the reported bounding box against it ("4 cubic inch box": assume a 4 in = 101.6 mm cube unless they say volume, and
                   state the assumption). If a feature fails, read the log line, fix that feature and rebuild.
                   When the request names a real object (a car model, a product, a character), it must be recognisable as THAT object,
                   not a generic smooth shape. Work in a loop:
                   1. Reference sheet first (in your answer): the real overall size (length, width, height in mm; web search if you can,
                      else what you know) and 6-10 defining features with where they sit along the length (e.g. for a car: wheelbase and
                      overhangs, nose shape, windscreen and roof position, side intakes, engine cover, rear wing, lights, wheel size).
                   2. Build "<name>-v1" with referenceMm set to that size. Draw sections from the real proportions (positions as
                      fractions of the length), not from guesses; add every defining feature as its own feature (cut, extrude, loft).
                   3. Compare: look at the returned front, right, top and iso pictures (a car built along X shows its side profile in the front view) and the proportions check, feature by feature
                      against the sheet. Write the differences as a short list.
                   4. Rework and build "<name>-v2", "-v3"... (older versions are deleted automatically) until the proportions match and
                      every defining feature is visible in the pictures, at most 6 versions. Finish with the checklist: each feature, done
                      or approximated, and what still differs. Set exportStl true when the user wants to print it. Never assemble unrelated parts from the folder.
                   Several parts that go together (a mechanism, a kit, "make the parts and mate them"): one job does it all, one tool
                   call at a time. Build every part with swx_build_parametric_part (check each report), then call swx_part_cards on
                   the output folder, then swx_build_assembly with only the parts you built and mates read from the cards
                   (concentric for matching cylinders, coincident for opposed faces), largest part fixed.
                   A DXF/SVG drawing the user gives (logo, plate outline, gasket) becomes a profile shape "import" (path, width?) and
                   is extruded or cut; lock: true keeps its exact shape. A 3D path (pipe, handlebar, wire) is op curve3d, then sweep.
                   Existing SolidWorks files: swx_session shows what is open; swx_document_ops opens, saves or closes (never discard
                   the user's unsaved work unless they say so); swx_export_neutral writes STEP/STL/IGES/Parasolid (PDF for drawings).
                B. Otherwise (templated mechanical parts, assemblies):
                1. Choose built-in templates in cad_generate_parametric_step (wheel_hub, brake_disc, coil_spring, strut_tube, control_arm,
                   shaft, flange_bracket) with realistic millimetre parameters and an envelopeMm for each part. State any dimension you assume.
                   Use cad_generate_custom_step only if no template fits; the user reviews that code.
                2. Import the STEP files with swx_import_step (outputFolder "{{J(full)}}").
                3. Read them with swx_part_cards and, if the user asked for an assembly, call swx_build_assembly with mates you are confident in.
                Report every part with its bounding box, and which dimensions the user should check against real measurements.
                """,
        };
    }

    /// <summary>Prompt 2: a rough cut of the clips in a folder, as a Resolve-importable timeline.</summary>
    public static JobSpec ResolveEdit(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "resolve", Category = TaskCategory.Resolve, Title = $"Rough cut of {Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}",
            Tools = ResolveTools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = 25,
            Prompt = $$"""
                Edit the video clips in {{full}}: {{request}}
                1. Call resolve_probe_clips with {"folder": "{{J(full)}}"}.
                2. Plan a rough cut from file names, durations and the request: order, in/out points (seconds), dissolves where they help,
                   music or B-roll on track 2 with timelineSec. Give each cut a short reason. You cannot see the footage: say so, and keep
                   in/out points conservative.
                3. Call resolve_export_timeline. The user reviews every cut.
                4. Tell the user to import the .fcpxml in DaVinci Resolve (File > Import > Timeline). Only call resolve_apply_studio with
                   run=true if the user said they use Resolve Studio with external scripting enabled.
                """,
        };
    }

    /// <summary>Prompt 3: an animated car scene in Blender.</summary>
    public static JobSpec BlenderScene(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "blender", Category = TaskCategory.Blender, Title = "Blender car scene",
            Tools = BlenderTools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = 15,
            Prompt = $$"""
                Create an animated Blender scene: {{request}}
                Output folder: {{full}}
                1. Translate the request into blender_build_car_scene parameters (road shape as roadPoints in metres, speed, duration, fps,
                   camera offset, building count, style night or day, engine). Describe the look in your own words; do not copy any
                   game's or brand's assets, logos or names into the scene.
                2. Call blender_build_car_scene with outputFolder "{{J(full)}}".
                3. If it saved a .blend, call blender_render_preview for a short low-resolution preview.
                Report what was built and how to open it (Blender > File > Open).
                """,
        };
    }

    /// <summary>Prompt 5: tutoring from a textbook chapter, with exact arithmetic checks.</summary>
    public static JobSpec Tutor(string? folder, string request, LaunchMode launch)
    {
        string? full = folder is null ? null : Path.GetFullPath(folder);
        string work = full ?? Path.Combine(Path.GetTempPath(), "AiCockpit-chat");
        Directory.CreateDirectory(work);
        string source = full is null
            ? "If the user attached PDFs or documents, read them and answer from them."
            : $"Their documents (PDFs, notes) are in {full}; read them when the question is about them.";
        return new JobSpec
        {
            Kind = "tutor", Category = TaskCategory.Tutor, Title = "Ask & learn",
            Tools = TutorTools, Roots = full is null ? [] : [full], WorkingDirectory = work, Launch = launch, MaxTurns = 40,
            Lifetime = TimeSpan.FromHours(4),
            UserText = request,
            Prompt = $$"""
                Answer the user's question or teach them the topic they ask about, on any subject: {{request}}
                {{source}} For a PDF, use tutor_extract_pdf_chapter on the pages that matter (ask for page numbers if unclear).
                Be clear and direct. When teaching, go one idea at a time with a worked example, and offer a check question.
                Before you state any matrix or linear-algebra result, or grade an answer that contains one, call tutor_verify_matrix
                and use its exact result.
                """,
        };
    }

    public static readonly string[] FileTools =
        [FilesToolProvider.PlanRules, FilesToolProvider.PlanMoves, FilesToolProvider.Execute, FilesToolProvider.Rollback, FilesToolProvider.Status];

    public static string DefaultAssemblyPath(string folder) =>
        Path.Combine(Path.GetFullPath(folder), string.Create(CultureInfo.InvariantCulture, $"Cockpit-Assembly-{DateTime.Now:yyyyMMdd-HHmmss}.SLDASM"));

    public static JobSpec Assemble(string folder, LaunchMode launch, string? outputPath = null)
    {
        string full = Path.GetFullPath(folder);
        string output = Path.GetFullPath(outputPath ?? DefaultAssemblyPath(full));
        var roots = new List<string> { full };
        string outDir = Path.GetDirectoryName(output)!;
        // The user chose the output folder; it becomes a second root unless it already lies inside the first.
        if (!Cockpit.Core.Security.PathSyntax.Current.IsSameOrInside(outDir, full)) roots.Add(outDir);
        return new JobSpec
        {
            Kind = "assemble",
            Category = TaskCategory.Cad,
            Title = $"Assemble parts in {Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}",
            Tools = CadTools,
            Roots = roots,
            WorkingDirectory = full,
            Launch = launch,
            MaxTurns = CadMaxTurns,
            Prompt = $$"""
                Create a new SolidWorks assembly from the part files in this folder: {{full}}
                0. If the request needs parts that are not in the folder yet, build each one first with swx_build_parametric_part
                   (outputFolder "{{J(full)}}", one call per part, checking each report), then continue with all the parts.
                1. Call swx_part_cards with {"folder": "{{full.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}.
                2. From the cards (bounding boxes, cylinders, planes, mate references) work out how the parts fit together.
                   Prefer concentric mates between cylinders of equal radius and coincident mates between opposed planes.
                   Give every mate a confidence (0-1) and a one-line reason. Leave out mates you are unsure about.
                3. Call swx_build_assembly once with outputPath "{{output.Replace("\\", "\\\\", StringComparison.Ordinal)}}", the first
                   (largest) part fixed, and your mates. The user reviews and may untick mates before anything is built.
                4. Report which mates were built, which failed, and what the user should check in SolidWorks.
                Use only the cockpit tools. Do not try to edit the part files.
                """,
        };
    }

    public static JobSpec Organize(string folder, string criteria, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "organize",
            Category = TaskCategory.Files,
            Title = $"Organize {full}",
            Tools = FileTools,
            Roots = [full],
            WorkingDirectory = full,
            Launch = launch,
            MaxTurns = 25,
            Prompt = $"""
                Organize the files under {full} according to: {criteria}
                Plan with files_plan_by_rules (preferred: a few rules) or files_plan_moves, read the summary, then call
                files_execute_batch once. The user approves the batch; every move is journaled and can be rolled back.
                Never delete anything. Leave files you cannot classify where they are. Report what moved and the batchId.
                """,
        };
    }

    /// <summary>
    /// Claude Code in a terminal in <paramref name="folder"/>, started with the user's prompt. The cockpit server is
    /// attached with no tools (Claude Code's own tools and permission prompts apply); the job ends when the user ends it.
    /// </summary>
    public static JobSpec ClaudeCodeSession(string folder, string prompt)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "claude-code",
            Category = TaskCategory.Code,
            Provider = AiProvider.ClaudeCode,
            Title = prompt.Length == 0 ? $"Claude Code in {full}" : $"Claude Code: {(prompt.Length > 60 ? prompt[..60] + "…" : prompt)}",
            Tools = [],
            Roots = [full],
            WorkingDirectory = full,
            Launch = LaunchMode.Interactive,
            Lifetime = TimeSpan.FromHours(8),
            Prompt = prompt,
        };
    }

    /// <summary>Deterministic organize: the CLI itself calls the file tools through the bridge; no model involved.</summary>
    public static JobSpec OrganizeWithRules(string folder) => Organize(folder, "(rules file)", LaunchMode.None) with { Kind = "organize-rules", Prompt = "" };
}
