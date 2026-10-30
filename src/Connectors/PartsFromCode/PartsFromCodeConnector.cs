using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Isolation;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Connectors.PartsFromCode;

/// <summary>
/// V18 connector: parametric parts as build123d code (built-in templates, or the model's own code run in a sandbox),
/// exported as STEP and checked against size envelopes. SolidWorks imports the STEP files when it is installed.
/// </summary>
public sealed class PartsFromCodeConnector : IMediatorConnector
{
    public string Id => "parts-from-code";
    public string DisplayName => "Parts from code";
    public string Category => "CAD";
    public string Summary => "Hubs, discs, springs, arms and custom parts as build123d code, exported as STEP.";

    /// <summary>Python with build123d. Null: the runtime's Python. Set a <see cref="WslPythonRunner"/> to keep generated code inside WSL.</summary>
    public IPythonRunner? Python { get; init; }
    /// <summary>
    /// The sandbox for model-written code. Null = detected: bubblewrap on Linux; on Windows none unless configured
    /// (a <see cref="WslBubblewrapSandbox"/>). Without one, custom code is refused, never run on the host.
    /// </summary>
    public ISandboxRunner? Sandbox { get; init; }
    /// <summary>The interpreter inside the sandbox. Null = probed from the host Python on Linux.</summary>
    public SandboxPython? SandboxPython { get; init; }
    /// <summary>Where sandbox runs are staged. Null = the workspaces folder. Must be inside the WSL exchange folder for the WSL sandbox.</summary>
    public string? StagingDirectory { get; init; }

    public static readonly string[] Tools = [CodeCadToolProvider.GenerateTool, CodeCadToolProvider.CustomTool];
    public IReadOnlyList<string> ChatTools => Tools;

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
        [new(31, TaskCategory.Cad, Tools, r => r.AnyWord(["parametric", "build123d", "3d print", "generate part", "make part"]))];

    public ConnectorInstance Start(ConnectorContext context)
    {
        var python = Python ?? context.Python;
        var sandbox = Sandbox ?? (OperatingSystem.IsLinux()
            ? new BubblewrapSandbox(context.ProcessRunner)
            : new UnavailableSandbox("no sandbox is configured on Windows: set up the restricted WSL distro with bubblewrap (see docs/v6/ISOLATION.md)"));
        var sandboxPython = SandboxPython ?? (sandbox is BubblewrapSandbox && python is LocalPythonRunner local ? SandboxPython.Probe(local.Executable) : null);
        return new([new CodeCadToolProvider(python, sandbox, sandboxPython, StagingDirectory ?? context.Paths.WorkspacesDirectory)]);
    }
}
