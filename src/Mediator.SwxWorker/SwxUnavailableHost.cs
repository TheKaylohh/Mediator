using Mediator.Protocol.Swx;

namespace Mediator.SwxWorker;

/// <summary>
/// The host used when mediator-swx-worker was built without the SolidWorks interop assemblies (V8: a machine without
/// SolidWorks, where Directory.Build.targets defines SOLIDWORKS_NOT_INSTALLED instead of failing the build). Every
/// method answers with one structured error, so the Core and the model get a clear reason instead of a crash or a
/// missing executable. Always compiled, so it is also tested in-process.
/// </summary>
public sealed class SwxUnavailableHost : ISwxHost
{
    /// <summary>The error code every call returns (SwxToolProvider tells the model not to retry it).</summary>
    public const string Code = "solidworks_not_installed";

    public const string Message =
        "SolidWorks assemblies not installed on this machine: this mediator-swx-worker was built without the SolidWorks " +
        "interop (SolidWorks.Interop.sldworks.dll was not found at build time). Coding, tutoring, Blender and Resolve jobs " +
        "are unaffected. To use SolidWorks jobs, install SolidWorks and rebuild with scripts\\publish-release.ps1 " +
        "(it finds <SOLIDWORKS folder>\\api\\redist in the registry, or pass -SolidWorksInteropDir).";

    // Never raised: nothing here can wedge.
    public event Action<string>? Wedged { add { } remove { } }

    private static Task<T> Unavailable<T>() => Task.FromException<T>(new SwxException(Code, Message));

    public Task<string> HealthAsync(CancellationToken ct) => Unavailable<string>();
    public Task<PartCardBatch> ExtractPartCardsAsync(IReadOnlyList<string> paths, TimeSpan runTimeout, CancellationToken ct) => Unavailable<PartCardBatch>();
    public Task<StepImportBatch> ImportStepAsync(IReadOnlyList<string> paths, string outputFolder, TimeSpan runTimeout, CancellationToken ct) => Unavailable<StepImportBatch>();
    public Task<BuildResult> BuildAssemblyAsync(ResolvedAssemblyPlan plan, TimeSpan runTimeout, CancellationToken ct) => Unavailable<BuildResult>();
    public Task ShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>Chooses the host the build supports. The only place that knows about SOLIDWORKS_NOT_INSTALLED.</summary>
public static class SwxHostFactory
{
    /// <summary>True when this worker was compiled against SolidWorks interop assemblies (real or the verify stub).</summary>
    public static bool HasSolidWorksInterop { get; } =
#if SOLIDWORKS_NOT_INSTALLED
        false;
#else
        true;
#endif

    public static ISwxHost Create(SwxHostOptions options)
    {
#if SOLIDWORKS_NOT_INSTALLED
        _ = options;
        return new SwxUnavailableHost();
#else
        return new SwxComHost(options);
#endif
    }
}
