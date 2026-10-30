using Mediator.Core.Cad;

namespace Mediator.Connectors.AutoCAD;

public sealed class AutoCadApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(AutoCadApp.Program, locator, overrideFolder)
{
    /// <summary>Where it is installed (see <see cref="CadLocator"/>).</summary>
    public static readonly CadProgramSpec Program = new("autocad", "AutoCAD", "MEDIATOR_AUTOCAD", [],
        [@"Autodesk\AutoCAD *\acad.exe"], ["AutoCAD 20", "AutoCAD LT"], ["acad.exe"], "AutoCAD.Application");

    public override string Prefix => "acad";
    protected override string ScriptName => "autocad.ps1";
    public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
    {
        CadCapability.Session, CadCapability.Documents, CadCapability.Export, CadCapability.Inspect, CadCapability.Edit,
        CadCapability.Views, CadCapability.Sketch, CadCapability.Annotate, CadCapability.Script,
    };
    public override IReadOnlyList<string> ModelExtensions { get; } = [".dwg", ".dxf"];
    public override IReadOnlyList<CadExportFormat> ExportFormats { get; } =
        [new("pdf", ".pdf", true, false), new("dxf", ".dxf", true, false), new("png", ".png", true, false), new("dwg", ".dwg", true, false)];
    protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
    {
        [CadCapability.Edit] = ["set_layer", "transform", "delete_entity", "set_attribute", "set_property", "delete_property", "run_command", "undo", "rebuild"],
        [CadCapability.Annotate] = ["dimension", "note", "leader", "table", "insert_block", "hatch", "layer"],
    };
}
