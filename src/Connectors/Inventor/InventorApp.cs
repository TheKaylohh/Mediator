using Mediator.Core.Cad;

namespace Mediator.Connectors.Inventor;

public sealed class InventorApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(InventorApp.Program, locator, overrideFolder)
{
    /// <summary>Where it is installed (see <see cref="CadLocator"/>).</summary>
    public static readonly CadProgramSpec Program = new("inventor", "Autodesk Inventor", "MEDIATOR_INVENTOR", [],
        [@"Autodesk\Inventor *\Bin\Inventor.exe"], ["Autodesk Inventor Professional", "Autodesk Inventor 20"], [@"Bin\Inventor.exe"], "Inventor.Application");

    public override string Prefix => "inv";
    protected override string ScriptName => "inventor.ps1";
    public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
    {
        CadCapability.Session, CadCapability.Documents, CadCapability.Export, CadCapability.Inspect, CadCapability.Edit, CadCapability.Views,
        CadCapability.Drawing, CadCapability.Sketch, CadCapability.Feature, CadCapability.Assembly, CadCapability.Script,
    };
    public override IReadOnlyList<string> ModelExtensions { get; } = [".ipt", ".iam"];
    public override string? DrawingExtension => ".idw";
    public override IReadOnlyList<CadExportFormat> ExportFormats { get; } =
    [
        new("step", ".stp", true, false), new("iges", ".igs", true, false), new("stl", ".stl", true, false), new("sat", ".sat", true, false),
        new("pdf", ".pdf", false, true), new("dwg", ".dwg", false, true), new("dxf", ".dxf", false, true),
    ];
    public override IReadOnlyList<string> MaterialExamples => ["Steel", "Aluminum 6061", "ABS Plastic", "Stainless Steel"];
    protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
    {
        [CadCapability.Edit] = ["set_dimension", "set_global", "add_global", "suppress", "unsuppress", "rename_feature", "delete_feature", "set_property", "delete_property", "set_material", "suppress_component", "unsuppress_component", "undo", "rebuild"],
        [CadCapability.Feature] = ["extrude", "cut", "revolve", "fillet", "chamfer", "shell", "pattern_circular", "delete"],
        [CadCapability.Assembly] = ["insert", "mate", "fix", "float", "move", "replace", "delete", "pattern_linear"],
    };
}
