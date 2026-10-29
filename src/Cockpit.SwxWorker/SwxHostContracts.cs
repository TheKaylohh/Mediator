using Cockpit.Protocol.Swx;

namespace Cockpit.SwxWorker;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────
//  What Program.cs needs from a host, independent of the SolidWorks interop assemblies (V8). SwxComHost implements
//  it when the interop is available at build time; SwxUnavailableHost when it is not (SOLIDWORKS_NOT_INSTALLED).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────

public interface ISwxHost : IDisposable
{
    /// <summary>Raised when a COM call wedged the STA thread; the process must exit (code 3).</summary>
    event Action<string>? Wedged;
    Task<string> HealthAsync(CancellationToken ct);
    Task<PartCardBatch> ExtractPartCardsAsync(IReadOnlyList<string> paths, TimeSpan runTimeout, CancellationToken ct);
    Task<StepImportBatch> ImportStepAsync(IReadOnlyList<string> paths, string outputFolder, TimeSpan runTimeout, CancellationToken ct);
    Task<BuildResult> BuildAssemblyAsync(ResolvedAssemblyPlan plan, TimeSpan runTimeout, CancellationToken ct);
    Task ShutdownAsync(TimeSpan timeout);
}

public sealed class SwxException : Exception
{
    public string Code { get; }
    public SwxException(string code, string message) : base(message) => Code = code;
}

public sealed record SwxHostOptions
{
    public TimeSpan AttachWait { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long the message filter keeps retrying a busy SolidWorks before giving up on a call.</summary>
    public TimeSpan BusyRetryWindow { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxCylinderFaces { get; init; } = 8;
    public int MaxPlanarFaces { get; init; } = 6;
    /// <summary>Run COM work on an STA thread with the OLE message filter. Always true in production; offline tests on Linux set it false.</summary>
    public bool UseStaApartment { get; init; } = true;
    /// <summary>How long a call may block before the host starts looking for a SolidWorks modal dialog.</summary>
    public TimeSpan DialogProbeDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan DialogProbeInterval { get; init; } = TimeSpan.FromSeconds(1);
}
