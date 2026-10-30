using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.Core.Cad;

/// <summary>
/// V18: the connector for one CAD program driven through <see cref="ICadApp"/> (FreeCAD, AutoCAD, Inventor, NX, Creo, CATIA).
/// Its tools are the shared CAD vocabulary under the program's prefix; a chat reaches it by the program's name, its file
/// types, or the program being in front.
/// </summary>
/// <param name="create">Makes the program's <see cref="ICadApp"/>; gets the folder where a patched script overrides the built-in one.</param>
/// <param name="spec">Where the program is installed (for the Connectors tab and the Software Library).</param>
/// <param name="order">Routing order (20-29; SolidWorks is 10).</param>
/// <param name="keywords">Names and file types that mean this program ("freecad", "fcstd").</param>
/// <param name="process">Its process name when in front ("freecad"), or null.</param>
public abstract class CadAppConnector(Func<string?, ICadApp> create, CadProgramSpec spec, int order, string[] keywords, string? process) : IMediatorConnector
{
    /// <summary>The CAD-file tools (the CAD files connector's) every CAD chat also gets, when that connector is installed.</summary>
    public static readonly string[] CadFileTools = ["cad_inspect_file", "cad_convert_mesh"];

    private readonly ICadApp _app = create(null);
    private string[]? _tools;

    public string Id => spec.Id;
    public string DisplayName => spec.Name;
    public string Category => "CAD";
    public virtual string Summary => $"Open, inspect, change and export {spec.Name} documents; anything else through its own API in a script you approve.";
    public CadProgramSpec Spec => spec;

    /// <summary>A new <see cref="ICadApp"/> for the program (a patched script in <paramref name="overrideFolder"/> wins).</summary>
    public ICadApp CreateApp(string? overrideFolder) => create(overrideFolder);

    /// <summary>The tool prefix (fc, acad, inv, nx, creo, catia).</summary>
    public string Prefix => _app.Prefix;

    public IReadOnlyList<string> ChatTools => _tools ??= [.. new CadAppToolProvider(_app).ToolNames, .. CadFileTools];

    public bool OwnsTool(string tool) => tool.StartsWith(Prefix + "_", StringComparison.Ordinal);

    public virtual ConnectorInstance Start(ConnectorContext context) =>
        new([new CadAppToolProvider(create(context.DataFolder(Id)))]);

    public IReadOnlyList<ConnectorRoute> Routes =>
        [new(order, TaskCategory.Cad, ChatTools, r => r.AnyWord(keywords) || (process is not null && r.AppIs(process)))];

    public IReadOnlyList<ConnectorProgram> Programs =>
        [new(Id, DisplayName, "\uE7F8", Summary, "CAD", Capabilities(_app), process)];

    /// <summary>"session, documents, export (STEP, IGES), inspect, edit, sketch, features, scripts".</summary>
    public static string Capabilities(ICadApp app) => string.Join(", ", app.Capabilities.Order().Select(c => c switch
    {
        CadCapability.Export when app.ExportFormats.Count > 0 => "export (" + string.Join(", ", app.ExportFormats.Select(f => f.Name.ToUpperInvariant())) + ")",
        CadCapability.Feature => "features",
        CadCapability.Script => "scripts in its own API",
        _ => c.ToString().ToLowerInvariant(),
    }));

    public AppStatus ProbeProgram(string programId, ProgramProbe probe)
    {
        if (probe.Forced(Id, out var forced))
            return forced is null ? new AppStatus(Id, DisplayName, false, "not installed") : new AppStatus(Id, DisplayName, true, forced, forced);
        string? exe = CadLocator.Default.Find(spec);
        if (exe is null && !CadLocator.Default.ComRegistered(spec))
            return new AppStatus(Id, DisplayName, false, !probe.WindowsApps && spec.PathCommands.Length == 0
                ? "Windows only" : $"not installed (or set {spec.OverrideVariable} to its executable)");
        string running = process is not null && probe.Running(process) is { } pid ? $"running (PID {pid})" : "not running";
        return new AppStatus(Id, DisplayName, true, $"{running}; {exe ?? "COM registered"}", exe);
    }
}
