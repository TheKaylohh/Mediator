using System.Diagnostics;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Connectors.Blender;

/// <summary>Runs Blender in background mode (`blender -b ...`).</summary>
public interface IBlenderRunner
{
    bool IsAvailable { get; }
    string Describe { get; }
    Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

public sealed class BlenderRunner(string? executable, IProcessRunner runner) : IBlenderRunner
{
    public static BlenderRunner Detect(IProcessRunner? runner = null) =>
        new(Environment.GetEnvironmentVariable("MEDIATOR_BLENDER") is { Length: > 0 } b ? b : Locate(), runner ?? DefaultProcessRunner.Instance);

    /// <summary>
    /// `blender` on PATH, else the newest install found in Program Files, %LOCALAPPDATA%\Programs, the BlenderFoundation
    /// registry key, Apps &amp; features or Steam (see <see cref="DesktopAppLocator"/>).
    /// </summary>
    public static string? Locate() => ComponentLocator.FindDesktopApp(DesktopApp.Blender)?.Executable;

    public bool IsAvailable => executable is not null;
    public string Describe => executable ?? "Blender not found (install it or set MEDIATOR_BLENDER)";

    public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable ?? throw new InvalidOperationException(Describe));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return runner.RunAsync(psi, timeout, ct);
    }
}
