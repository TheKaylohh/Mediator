
namespace Mediator.Core.Runtime;

/// <summary>
/// A desktop app a tool drives (SolidWorks, Chrome). The tool asks for it to be ready before its first call, so the user never has
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
