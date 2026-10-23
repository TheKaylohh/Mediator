using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    /// <summary>`claude -p` or `gemini` with stream-json, monitored for usage and rate limits.</summary>
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
    public required string WorkingDirectory { get; init; }
    public LaunchMode Launch { get; init; } = LaunchMode.Headless;
    public int MaxTurns { get; init; } = 40;
    public string? Model { get; init; }
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
    internal Process? Process { get; set; }
    internal ChildProcessJob ProcessGroup { get; } = new();
    internal InteractiveSession? Interactive { get; set; }
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    internal DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
    internal void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    internal void SetRunning() => Interlocked.CompareExchange(ref _state, (int)JobState.Running, (int)JobState.Starting);

    /// <summary>First terminal state wins (a kill racing a normal exit stays a kill).</summary>
    internal bool TryFinish(JobState state, string? detail)
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
    public TimeSpan BridgeTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public ICockpitLog Log { get; init; } = NullLog.Instance;
    /// <summary>Null: `gemini` resolved on PATH when a job needs it.</summary>
    public string? GeminiExecutable { get; init; }
    /// <summary>Launch Gemini with --skip-trust so folder trust cannot hide the job's MCP config (needs a recent Gemini CLI; see doctor).</summary>
    public bool GeminiSkipTrust { get; init; }
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
    public CockpitPaths Paths => _options.Paths;

    public IReadOnlyList<RunningJob> Jobs => _jobs.Values.OrderBy(j => j.StartedAt).ToList();

    /// <summary>Durable record of side-effecting tool calls (dedupe, outcome-unknown, restart reconciliation).</summary>
    public OperationStore Operations { get; }

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
        Tools.Register(new SwxToolProvider(Swx, Registry));
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

    public static string NewJobId(string kind) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}");

    public async Task<RunningJob> StartJobAsync(JobSpec spec, CancellationToken ct = default)
    {
        var bridge = Bridge ?? throw new InvalidOperationException(
            "cockpit-mcp-bridge was not found next to the Cockpit or in the repository build output. Build src/Cockpit.Bridge first.");
        RouteDecision? route = spec.Launch == LaunchMode.None ? null
            : await RouteAsync(spec.Category, spec.Provider, spec.NeverDowngrade).ConfigureAwait(false); // throws before anything is issued
        string jobId = spec.JobId ?? NewJobId(spec.Kind);
        var grant = Registry.Issue(jobId, spec.Tools.Append(OperationsToolProvider.StatusTool).Distinct(StringComparer.Ordinal), spec.Roots, spec.Lifetime, spec.Transport, spec.ToolModes, allowMeteredBilling: spec.AllowMeteredBilling);
        RunningJob job;
        try
        {
            var ws = JobWorkspaceFactory.Create(Paths.WorkspacesDirectory, grant, bridge, Bus.EndpointFor(grant), _options.BridgeTimeout);
            job = new RunningJob(spec, grant, ws) { Provider = route?.Provider, RoutingNotice = route?.Notice };
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

    public async Task<RouteDecision> RouteAsync(TaskCategory category, AiProvider? provider = null, bool neverDowngrade = false)
    {
        var cooldowns = new Dictionary<AiProvider, DateTimeOffset?>();
        foreach (var p in Enum.GetValues<AiProvider>())
            cooldowns[p] = await Usage.GetCooldownAsync(ProviderRouter.Id(p)).ConfigureAwait(false);
        return ProviderRouter.Route(category, provider, neverDowngrade, p => cooldowns[p], p => ExecutableFor(p) is not null);
    }

    private string? ExecutableFor(AiProvider provider) => provider == AiProvider.ClaudeCode
        ? _options.ClaudeExecutable ?? CliLaunchBuilder.ResolveOnPath("claude")
        : _options.GeminiExecutable ?? CliLaunchBuilder.ResolveOnPath("gemini");

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
            job.ProcessGroup, "cockpit-cli", ct);
    }

    private string RequireExecutable(AiProvider provider) => ExecutableFor(provider) ?? throw new ProviderUnavailableException(provider == AiProvider.ClaudeCode
        ? "Claude Code (claude) is not on PATH. Install it and sign in with your Pro account first (`claude`, then /login)."
        : "Gemini CLI (gemini) is not on PATH. Install it (npm install -g @google/gemini-cli) and sign in with your Google account (`gemini`, choose Login with Google).");

    private ProcessStartInfo HeadlessStartInfo(RunningJob job)
    {
        var spec = job.Spec;
        return job.Provider == AiProvider.GeminiCli
            ? new GeminiLaunchBuilder(RequireExecutable(AiProvider.GeminiCli), _options.GeminiSkipTrust).Headless(job.Workspace, spec.Model, spec.AllowMeteredBilling)
            : new CliLaunchBuilder(RequireExecutable(AiProvider.ClaudeCode), _options.BridgeTimeout + TimeSpan.FromMinutes(1))
                .ClaudeHeadless(job.Workspace, spec.WorkingDirectory, spec.MaxTurns, spec.Model, spec.AllowMeteredBilling);
    }

    private async Task LaunchHeadlessAsync(RunningJob job, CancellationToken ct)
    {
        var provider = job.Provider ?? AiProvider.ClaudeCode;
        string cli = provider == AiProvider.ClaudeCode ? "claude" : "gemini";
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

        await process.StandardInput.WriteAsync(job.Spec.Prompt.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();

        var monitor = CliRunMonitor.For(provider, job.JobId, Usage);
        monitor.EventReceived += (id, e) =>
        {
            job.Touch();
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
                bool ok = process.ExitCode == 0 && monitor.LastResult is { IsError: false };
                string detail = monitor.LastResult?.RateLimit?.Message ?? monitor.LastResult?.ResultText
                    ?? $"{cli} exited with code {process.ExitCode}. {string.Join(" | ", stderrTail)}";
                Finish(job, ok ? JobState.Succeeded : JobState.Failed, detail);
            }
            catch (Exception ex)
            {
                Finish(job, JobState.Failed, ex.Message);
            }
        }, CancellationToken.None);
    }

    private void LaunchInteractive(RunningJob job)
    {
        var spec = job.Spec;
        var psi = job.Provider == AiProvider.GeminiCli
            ? new GeminiLaunchBuilder(RequireExecutable(AiProvider.GeminiCli), _options.GeminiSkipTrust).Interactive(job.Workspace, spec.Prompt, spec.AllowMeteredBilling)
            : new CliLaunchBuilder(RequireExecutable(AiProvider.ClaudeCode), _options.BridgeTimeout + TimeSpan.FromMinutes(1))
                .ClaudeInteractive(job.Workspace, spec.WorkingDirectory, spec.Prompt, spec.AllowMeteredBilling);
        job.Interactive = new InteractiveSession(job.Workspace.Root);
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
        if (job.TryFinish(state, detail)) Changed(job);
        Registry.Revoke(job.JobId);
    }

    private void OnRevoked(string jobId)
    {
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
}

/// <summary>The job recipes behind the overlay's action tiles and the CLI commands.</summary>
public static class JobSpecFactory
{
    public static readonly string[] SwxTools = [SwxToolProvider.PartCardsTool, SwxToolProvider.BuildTool];
    public static readonly string[] CadTools = [CodeCadToolProvider.GenerateTool, CodeCadToolProvider.CustomTool, SwxToolProvider.ImportStepTool, SwxToolProvider.PartCardsTool, SwxToolProvider.BuildTool];
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
            Tools = CadTools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = 40,
            Prompt = $$"""
                Design parametric parts for: {{request}}
                Output folder: {{full}}
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
    public static JobSpec Tutor(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "tutor", Category = TaskCategory.Tutor, Title = "Linear algebra tutor",
            Tools = TutorTools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = 40,
            Lifetime = TimeSpan.FromHours(4),
            Prompt = $$"""
                Tutor the user: {{request}}
                The textbook PDF is in {{full}}. Use tutor_extract_pdf_chapter on the pages of the chapter they name (ask for page
                numbers if unclear), then teach from that text: one idea at a time, a worked example, then a question for them.
                Before you state any matrix result or grade an answer, call tutor_verify_matrix and use its exact result.
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
            Tools = SwxTools,
            Roots = roots,
            WorkingDirectory = full,
            Launch = launch,
            MaxTurns = 30,
            Prompt = $$"""
                Create a new SolidWorks assembly from the part files in this folder: {{full}}
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
