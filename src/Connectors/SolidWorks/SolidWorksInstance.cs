using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;

namespace Mediator.Connectors.SolidWorks;

/// <summary>The SolidWorks connector in one runtime: its worker, the SolidWorks it starts, and the open-document link.</summary>
public sealed class SolidWorksInstance : ConnectorInstance
{
    private readonly SwxWorkerClient? _ownedWorker;
    private readonly Func<CancellationToken, Task<string?>>? _activeDocument;
    private readonly ConnectorContext _context;

    /// <summary>Where the assembly tools run (the worker, or a stand-in).</summary>
    public ISwxBackend Backend { get; }
    /// <summary>V10: the SolidWorks the CAD tools drive, started on demand (one copy); a no-op with a simulated backend.</summary>
    public IDesktopAppSession Session { get; }

    internal SolidWorksInstance(SolidWorksConnector connector, ConnectorContext context) : this(Build(connector, context, out var owned, out var session, out var active), owned, session, active, context)
    {
    }

    private SolidWorksInstance((ISwxBackend Backend, List<Core.Bus.IToolProvider> Providers) built, SwxWorkerClient? owned, IDesktopAppSession session,
        Func<CancellationToken, Task<string?>>? active, ConnectorContext context) : base(built.Providers)
    {
        Backend = built.Backend;
        _ownedWorker = owned;
        Session = session;
        _activeDocument = active;
        _context = context;
    }

    private static (ISwxBackend, List<Core.Bus.IToolProvider>) Build(SolidWorksConnector c, ConnectorContext context, out SwxWorkerClient? owned,
        out IDesktopAppSession session, out Func<CancellationToken, Task<string?>>? active)
    {
        owned = null;
        ISwxBackend backend;
        if (c.Backend is { } b) backend = b;
        else if (context.Simulate) backend = new SimulatedSwxBackend();
        else
        {
            var worker = c.Worker ?? LocateWorker();
            owned = new SwxWorkerClient(new SwxWorkerOptions(worker?.FileName ?? ComponentLocator.SwxWorker, worker?.PrefixArguments ?? []), context.ProcessGroup, context.Log);
            backend = owned;
        }
        // The real SolidWorks only with the real worker on Windows; a simulated backend (tests, --simulate) starts nothing.
        bool real = owned is not null && OperatingSystem.IsWindows();
        session = c.Session ?? (real ? new SolidWorksSession(SolidWorksSession.DefaultStartTimeout, context.Log) : NoDesktopAppSession.Instance);
        active = c.ActiveDocument ?? (real
            ? ct => SolidWorksLauncher.IsRunning() ? Task.Run(() => SwxParametric.ActivePath(TimeSpan.FromSeconds(20)), ct) : Task.FromResult<string?>(null)
            : null);
        return (backend,
        [
            new SwxToolProvider(backend, context.Jobs, session),
            new SwxPartToolProvider(session),
            new Core.Cad.CadAppToolProvider(new SolidWorksCadApp(session) { ScriptOverrideFolder = context.DataFolder("solidworks") }),
        ]);
    }

    /// <summary>
    /// mediator-swx-worker: MEDIATOR_SWX_WORKER (mediator-cli --worker), else in this connector's folder (shipped there), next
    /// to the app, or in the repository build.
    /// </summary>
    internal static ComponentCommand? LocateWorker() =>
        Environment.GetEnvironmentVariable(WorkerVariable) is { Length: > 0 } forced ? ComponentLocator.Find(ComponentLocator.SwxWorker, forced)
        : ComponentLocator.Find(ComponentLocator.SwxWorker, baseDirectory: Path.GetDirectoryName(typeof(SolidWorksInstance).Assembly.Location))
          ?? ComponentLocator.Find(ComponentLocator.SwxWorker);

    /// <summary>Set by mediator-cli --worker.</summary>
    public const string WorkerVariable = "MEDIATOR_SWX_WORKER";

    public override async Task<JobSpec> PrepareJobAsync(JobSpec spec, CancellationToken ct)
    {
        spec = await WithOpenDocumentAsync(spec, ct).ConfigureAwait(false);
        // A job that will drive SolidWorks starts it now, so it is loading while the model reads and plans. V14.1: only when
        // this request asks for SolidWorks (an action tile, or CAD words in what the user typed). A follow-up in a chat that
        // once used CAD keeps the tools, and SolidWorks starts on the first SolidWorks tool call if the model needs it.
        if (SolidWorksConnector.WarmsSolidWorks(spec)) Session.WarmUp();
        return spec;
    }

    /// <summary>What a plain chat about the model on screen should do first.</summary>
    internal const string ChatHint = " For \"this part\", \"the open model\" or what is on screen in SolidWorks, start with swx_session and swx_inspect_model (sections selection for what they selected); look at it with swx_capture_views before judging a shape.";

    /// <summary>
    /// V16.1: "the open part", "this assembly": a SolidWorks job works on the document active in SolidWorks, so that document's
    /// folder joins the job's folders (as a folder selected in File Explorer does) and the model is told which file it is.
    /// Without it a chat with no folder could read the open part but not change or export it. Protected folders are never added;
    /// every change still shows its approval card.
    /// </summary>
    private async Task<JobSpec> WithOpenDocumentAsync(JobSpec spec, CancellationToken ct)
    {
        if (spec.Category != TaskCategory.Cad || !spec.Tools.Any(SolidWorksCadApp.ToolNames.Contains)) return spec;
        if (spec.Kind == "assistant" && !spec.Prompt.Contains("swx_session and swx_inspect_model", StringComparison.Ordinal))
        {
            // The user's words follow "\n\nUser: "; the hint joins the instructions before them.
            int at = spec.Prompt.IndexOf("\n", StringComparison.Ordinal);
            spec = spec with { Prompt = at < 0 ? spec.Prompt + ChatHint : spec.Prompt[..at] + ChatHint + spec.Prompt[at..] };
        }
        if (_activeDocument is null) return spec;
        string? path;
        try { path = await _activeDocument(ct).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            _context.Log.Warn("The document open in SolidWorks could not be read: " + ex.Message);
            return spec;
        }
        if (path is null) return spec;
        string folder;
        try { folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return spec; }
        if (folder.Length == 0 || !Directory.Exists(folder)) return spec;
        bool covered = spec.Roots.Any(r => PathSyntax.Current.IsSameOrInside(folder, Path.GetFullPath(r)));
        if (!covered && _context.PathPolicy.CheckJobRoot(folder) is not null) return spec;
        string note = $"\n\nOpen in SolidWorks right now: {path}. Its folder is shared with you; a swx_ tool without a path works on this document.";
        return spec with { Roots = covered ? spec.Roots : [.. spec.Roots, folder], Prompt = spec.Prompt + note };
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ownedWorker is not null) await _ownedWorker.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
