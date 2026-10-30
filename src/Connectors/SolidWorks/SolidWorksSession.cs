using Mediator.Core.Diagnostics;
using Mediator.Core.Runtime;

namespace Mediator.Connectors.SolidWorks;

/// <summary>
/// The user's SolidWorks: one copy, started on demand through <see cref="SolidWorksLauncher"/> (in-process lock plus a
/// session-wide mutex). A warm-up already in progress is joined rather than repeated.
/// </summary>
public sealed class SolidWorksSession(TimeSpan startTimeout, IMediatorLog? log = null) : IDesktopAppSession
{
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private Task? _starting;

    public Task EnsureReadyAsync(CancellationToken ct)
    {
        Task start;
        lock (_gate)
        {
            // Join a start in progress; after a failure or once done, the next caller checks again (cheap when running).
            if (_starting is not { IsCompleted: false }) _starting = Task.Run(Start, CancellationToken.None);
            start = _starting;
        }
        return start.WaitAsync(ct);
    }

    public void WarmUp()
    {
        if (!OperatingSystem.IsWindows() || SolidWorksLauncher.IsRunning()) return;
        _ = EnsureReadyAsync(CancellationToken.None).ContinueWith(
            t => log?.Info("SolidWorks warm-up failed: " + t.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void Start()
    {
        if (SolidWorksLauncher.EnsureRunning(startTimeout)) log?.Info("Started SolidWorks for a job.");
    }
}
