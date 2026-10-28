using Cockpit.Core.Diagnostics;
using Cockpit.Core.Swx;

namespace Cockpit.Core.Runtime;

/// <summary>
/// A desktop app a tool drives (SolidWorks). The tool asks for it to be ready before its first call, so the user never has
/// to open the app before writing the prompt; <see cref="WarmUp"/> starts it early, while the model is still thinking.
/// </summary>
public interface IDesktopAppSession
{
    /// <summary>Starts the app if it is not running and waits until it accepts automation. Never starts a second copy.</summary>
    Task EnsureReadyAsync(CancellationToken ct);

    /// <summary>Starts the app in the background if needed; returns at once. Failures are reported by the next <see cref="EnsureReadyAsync"/>.</summary>
    void WarmUp();
}

/// <summary>For tests, `--simulate` and machines without the app: nothing to start.</summary>
public sealed class NoDesktopAppSession : IDesktopAppSession
{
    public static NoDesktopAppSession Instance { get; } = new();
    private NoDesktopAppSession() { }
    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
    public void WarmUp() { }
}

/// <summary>
/// The user's SolidWorks: one copy, started on demand through <see cref="SolidWorksLauncher"/> (in-process lock plus a
/// session-wide mutex). A warm-up already in progress is joined rather than repeated.
/// </summary>
public sealed class SolidWorksSession(TimeSpan startTimeout, ICockpitLog? log = null) : IDesktopAppSession
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
