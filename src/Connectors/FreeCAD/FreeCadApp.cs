using Mediator.Core.Cad;

namespace Mediator.Connectors.FreeCAD;

public sealed class FreeCadApp(CadLocator? locator = null, string? overrideFolder = null) : ScriptCadApp(overrideFolder)
{
    /// <summary>Where it is installed (see <see cref="CadLocator"/>).</summary>
    public static readonly CadProgramSpec Program = new("freecad", "FreeCAD", "MEDIATOR_FREECAD", ["FreeCADCmd", "freecadcmd"],
        [@"FreeCAD *\bin\FreeCADCmd.exe", @"FreeCAD\bin\FreeCADCmd.exe"], ["FreeCAD"], [@"bin\FreeCADCmd.exe", "FreeCADCmd.exe"]);

    private readonly CadLocator _locator = locator ?? CadLocator.Default;
    public override string Prefix => "fc";
    public override string DisplayName => "FreeCAD";
    public override bool HasLiveSession => false;
    protected override string ScriptName => "freecad.py";
    public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
    {
        CadCapability.Session, CadCapability.Documents, CadCapability.Export, CadCapability.Inspect, CadCapability.Edit,
        CadCapability.Sketch, CadCapability.Feature, CadCapability.Simulate, CadCapability.Script,
    };
    public override IReadOnlyList<string> ModelExtensions { get; } = [".fcstd"];
    public override IReadOnlyList<CadExportFormat> ExportFormats { get; } =
    [
        new("step", ".step", true, false), new("iges", ".igs", true, false), new("brep", ".brep", true, false),
        new("stl", ".stl", true, false), new("obj", ".obj", true, false), new("3mf", ".3mf", true, false), new("fcstd", ".FCStd", true, false),
    ];
    public override IReadOnlyList<string> MaterialExamples => ["Steel-Generic", "Aluminum-6061-T6", "ABS-Generic", "PLA-Generic"];
    protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
    {
        [CadCapability.Edit] = ["set_dimension", "set_global", "add_global", "suppress", "unsuppress", "rename_feature", "delete_feature", "set_property", "delete_property", "set_material", "rebuild"],
        [CadCapability.Feature] = ["extrude", "cut", "revolve", "sweep", "loft", "fillet", "chamfer", "shell", "hole", "pattern_linear", "pattern_circular", "mirror", "plane", "sketch", "boolean", "delete"],
        [CadCapability.Simulate] = ["material", "fixed", "force", "pressure", "mesh", "solve"],
    };

    protected override CadScriptRunner? Runner(out string? why)
    {
        why = null;
        if (_locator.Find(FreeCadApp.Program) is { } exe) return new CadScriptRunner(exe, [], [], ".py");
        why = "FreeCAD (FreeCADCmd) is not installed on this PC (or set MEDIATOR_FREECAD to FreeCADCmd.exe).";
        return null;
    }

    public override CadScriptInfo Script => new("Python (FreeCAD)",
        "The script runs in FreeCADCmd (headless FreeCAD, Python 3). In scope: App/FreeCAD, Part, doc (the document at path, opened), args, " +
        "result({...}) to return values (print output is returned too), save() to save doc. Import other modules as needed (PartDesign, Sketcher, " +
        "TechDraw, ObjectsFem, Path for CAM, Mesh). FreeCAD is Z-up; lengths in mm. There is no GUI: no views or colours.");
}
