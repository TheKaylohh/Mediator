using System.Text.Json.Nodes;
using Mediator.Core.Cad;
using Mediator.Core.Runtime;

namespace Mediator.Core.Swx;

/// <summary>
/// V16: SolidWorks as an <see cref="ICadApp"/>: the swx_session, swx_document_ops, swx_export_neutral, swx_inspect_model,
/// swx_edit_model, swx_create_drawing and swx_capture_views tools (<see cref="CadAppToolProvider"/>), driven through
/// <see cref="SwxParametric"/> (late-bound COM on an STA thread per call; Windows only).
/// </summary>
public sealed class SolidWorksCadApp(IDesktopAppSession? session = null) : ICadApp
{
    public const string ToolPrefix = "swx";
    public static readonly string SessionTool = CadVerb.ToolName(ToolPrefix, CadCapability.Session);
    public static readonly string DocumentTool = CadVerb.ToolName(ToolPrefix, CadCapability.Documents);
    public static readonly string ExportTool = CadVerb.ToolName(ToolPrefix, CadCapability.Export);
    public static readonly string InspectTool = CadVerb.ToolName(ToolPrefix, CadCapability.Inspect);
    public static readonly string EditTool = CadVerb.ToolName(ToolPrefix, CadCapability.Edit);
    public static readonly string DrawingTool = CadVerb.ToolName(ToolPrefix, CadCapability.Drawing);
    public static readonly string ViewsTool = CadVerb.ToolName(ToolPrefix, CadCapability.Views);
    public static readonly string SketchTool = CadVerb.ToolName(ToolPrefix, CadCapability.Sketch);
    public static readonly string FeatureTool = CadVerb.ToolName(ToolPrefix, CadCapability.Feature);
    public static readonly string AssemblyTool = CadVerb.ToolName(ToolPrefix, CadCapability.Assembly);
    public static readonly string AnnotateTool = CadVerb.ToolName(ToolPrefix, CadCapability.Annotate);
    public static readonly string ScriptTool = CadVerb.ToolName(ToolPrefix, CadCapability.Script);
    public static readonly string[] ToolNames = [SessionTool, DocumentTool, ExportTool, InspectTool, EditTool, DrawingTool, ViewsTool,
        SketchTool, FeatureTool, AssemblyTool, AnnotateTool, ScriptTool];

    private readonly IDesktopAppSession _session = session ?? NoDesktopAppSession.Instance;

    public string Prefix => ToolPrefix;
    public string DisplayName => "SolidWorks";
    public string Lease => SwxToolProvider.Lease;
    /// <summary>Everything but simulation (SolidWorks Simulation is driven through run_script).</summary>
    public IReadOnlySet<CadCapability> Capabilities { get; } = Enum.GetValues<CadCapability>().Where(c => c != CadCapability.Simulate).ToHashSet();

    private static readonly Dictionary<CadCapability, string[]> SupportedOps = new()
    {
        [CadCapability.Edit] = ["set_dimension", "set_global", "add_global", "suppress", "unsuppress", "rename_feature", "delete_feature", "set_property", "delete_property",
            "set_material", "set_color", "activate_configuration", "add_configuration", "suppress_component", "unsuppress_component", "undo", "rebuild"],
        [CadCapability.Feature] = ["extrude", "cut", "revolve", "sweep", "loft", "fillet", "chamfer", "shell", "hole", "pattern_linear", "pattern_circular",
            "mirror", "plane", "sketch", "curve3d", "thicken", "knit", "delete"],
        [CadCapability.Assembly] = ["insert", "mate", "fix", "float", "move", "replace", "delete", "pattern_linear"],
        [CadCapability.Annotate] = ["projected_view", "section_view", "detail_view", "model_dimensions", "note", "bom_table", "balloons"],
    };

    public IReadOnlyList<string> Ops(CadCapability capability) => SupportedOps.TryGetValue(capability, out var ops) ? ops : [];

    /// <summary>Where a patched solidworks.ps1 may live (&lt;data&gt;\cad-scripts); null = the embedded one only.</summary>
    public string? ScriptOverrideFolder { get; init; }

    public CadScriptInfo? Script => new("Windows PowerShell (SolidWorks COM)",
        "The script runs in Windows PowerShell 5.1. In scope: $app (the running SldWorks.Application), $doc (the document at path, opened if needed, " +
        "else the active one), $MediatorArgs, Write-Result @{ ... } (the hashtable you return; otherwise the script's output is returned). Use the " +
        "SolidWorks API as from VBA (late bound): e.g. $doc.FeatureManager, $doc.Extension.SelectByID2(...), SolidWorks Simulation via " +
        "$app.GetAddInObject('SldWorks.Simulation').CosmosWorks, CAM via its add-in. Lengths in metres, angles in radians. Throw to report an error.");
    public IReadOnlyList<string> ModelExtensions { get; } = [".sldprt", ".sldasm"];
    public string? DrawingExtension => ".slddrw";
    public IReadOnlyList<CadExportFormat> ExportFormats => SwxParametric.ExportFormats;
    public IReadOnlyList<string> MaterialExamples { get; } = ["6061 Alloy", "AISI 304", "Plain Carbon Steel", "ABS", "Brass", "PC High Viscosity"];

    public string? Unavailable() => OperatingSystem.IsWindows() ? null : "SolidWorks needs Windows.";

    public Task EnsureReadyAsync(CancellationToken ct) => _session.EnsureReadyAsync(ct);

    /// <summary>Off the caller's thread: <see cref="SwxParametric"/> runs each call on its own STA thread and blocks until it ends.</summary>
    private static Task<T> Sta<T>(Func<T> work, CancellationToken ct) => Task.Run(work, ct);

    public Task<string?> ActiveDocumentPathAsync(CancellationToken ct) => Sta(() => SwxParametric.ActivePath(TimeSpan.FromMinutes(1)), ct);

    public Task<JsonObject> SessionAsync(CancellationToken ct) => Sta(() => SwxParametric.Session(TimeSpan.FromMinutes(3)), ct);

    public Task<JsonObject> DocumentAsync(CadDocumentRequest request, CancellationToken ct) => Sta(() => SwxParametric.Document(request, TimeSpan.FromMinutes(3)), ct);

    public Task<JsonObject> ExportAsync(IReadOnlyList<string> paths, IReadOnlyList<string> formats, string outputFolder, CancellationToken ct) =>
        Sta(() => SwxParametric.Export(paths, formats, outputFolder, TimeSpan.FromMinutes(7)), ct);

    public Task<JsonObject> InspectAsync(string? path, IReadOnlySet<string> sections, CancellationToken ct) =>
        Sta(() => SwxParametric.Inspect(path, sections, TimeSpan.FromMinutes(4.5)), ct);

    public Task<JsonObject> EditAsync(string? path, JsonArray ops, bool save, CancellationToken ct) =>
        Sta(() => SwxParametric.Edit(path, ops, save, TimeSpan.FromMinutes(5)), ct);

    public Task<JsonObject> CreateDrawingAsync(CadDrawingRequest request, CancellationToken ct) => Sta(() => SwxParametric.Drawing(request, TimeSpan.FromMinutes(7)), ct);

    public Task<JsonObject> SketchAsync(string? path, JsonObject sketch, bool save, CancellationToken ct) =>
        Sta(() => SwxParametric.SketchOn(path, sketch, save, TimeSpan.FromMinutes(4)), ct);

    public Task<JsonObject> RunOpsAsync(CadCapability capability, string? path, JsonArray ops, bool save, CancellationToken ct) => capability switch
    {
        CadCapability.Feature => Sta(() => SwxParametric.AddFeatures(path, ops, save, TimeSpan.FromMinutes(7)), ct),
        CadCapability.Assembly => Sta(() => SwxParametric.AssemblyOps(path, ops, save, TimeSpan.FromMinutes(7)), ct),
        CadCapability.Annotate => Sta(() => SwxParametric.Annotate(path, ops, save, TimeSpan.FromMinutes(5)), ct),
        _ => throw new CadAppException($"SolidWorks has no {CadVerb.For(capability)} tool here; use {ScriptTool}."),
    };

    public async Task<JsonObject> RunScriptAsync(string code, string? path, TimeSpan timeout, CancellationToken ct)
    {
        string ps = CadLocator.Default.WindowsPowerShell() ?? throw new CadAppException("Windows PowerShell 5.1 (powershell.exe) was not found.");
        var runner = new CadScriptRunner(ps, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], [], ".ps1", Utf8Bom: true);
        var output = await CadScriptHost.RunAsync(runner, ScriptCadApp.LoadEmbedded("solidworks.ps1", ScriptOverrideFolder),
            new JsonObject { ["verb"] = "script", ["path"] = path, ["code"] = code }, timeout, ct).ConfigureAwait(false);
        return CadScriptHost.ToReport(output, DisplayName, timeout);
    }

    public Task<JsonObject> CaptureViewsAsync(string? path, IReadOnlyList<string> views, string outputFolder, CancellationToken ct) =>
        Sta(() => SwxParametric.CaptureViews(path, views, outputFolder, TimeSpan.FromMinutes(3)), ct);
}
