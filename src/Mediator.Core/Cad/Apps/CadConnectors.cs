namespace Mediator.Core.Cad.Apps;

/// <summary>
/// V17: the CAD programs beyond SolidWorks, each driven by one script (Cad/Scripts). What each one can do as structured tools
/// is listed in its capabilities and ops; everything else is its run_script tool in the program's own API language.
/// </summary>
public static class CadPrograms
{
    public static readonly CadProgramSpec FreeCad = new("freecad", "FreeCAD", "MEDIATOR_FREECAD", ["FreeCADCmd", "freecadcmd"],
        [@"FreeCAD *\bin\FreeCADCmd.exe", @"FreeCAD\bin\FreeCADCmd.exe"], ["FreeCAD"], [@"bin\FreeCADCmd.exe", "FreeCADCmd.exe"]);
    public static readonly CadProgramSpec AutoCad = new("autocad", "AutoCAD", "MEDIATOR_AUTOCAD", [],
        [@"Autodesk\AutoCAD *\acad.exe"], ["AutoCAD 20", "AutoCAD LT"], ["acad.exe"], "AutoCAD.Application");
    public static readonly CadProgramSpec Inventor = new("inventor", "Autodesk Inventor", "MEDIATOR_INVENTOR", [],
        [@"Autodesk\Inventor *\Bin\Inventor.exe"], ["Autodesk Inventor Professional", "Autodesk Inventor 20"], [@"Bin\Inventor.exe"], "Inventor.Application");
    public static readonly CadProgramSpec Nx = new("nx", "Siemens NX", "MEDIATOR_NX", ["run_journal"],
        [@"Siemens\NX*\NXBIN\run_journal.exe", @"Siemens\NX *\NXBIN\run_journal.exe"], ["Siemens NX"], [@"NXBIN\run_journal.exe"]);
    public static readonly CadProgramSpec Creo = new("creo", "PTC Creo", "MEDIATOR_CREO", [],
        [@"PTC\Creo *\Parametric\bin\parametric.exe"], ["PTC Creo"], [@"Parametric\bin\parametric.exe"], "pfcls.pfcAsyncConnection");
    public static readonly CadProgramSpec Catia = new("catia", "CATIA V5", "MEDIATOR_CATIA", [],
        [@"Dassault Systemes\B*\win_b64\code\bin\CNEXT.exe"], ["CATIA P3 V5", "CATIA V5"], [@"win_b64\code\bin\CNEXT.exe"], "CATIA.Application");

    public static readonly IReadOnlyList<CadProgramSpec> All = [FreeCad, AutoCad, Inventor, Nx, Creo, Catia];
}

/// <summary>
/// A connector whose script runs in Windows PowerShell 5.1 and talks to a running desktop program through COM
/// (GetActiveObject, or a new instance when none runs). Available when the program is installed or its COM class is registered.
/// </summary>
public abstract class ComScriptCadApp(CadProgramSpec spec, CadLocator? locator, string? overrideFolder) : ScriptCadApp(overrideFolder)
{
    protected CadLocator Locator { get; } = locator ?? CadLocator.Default;
    public CadProgramSpec Spec { get; } = spec;
    public override string DisplayName => Spec.Name;

    protected override CadScriptRunner? Runner(out string? why)
    {
        why = null;
        if (!OperatingSystem.IsWindows()) { why = $"{Spec.Name} automation needs Windows."; return null; }
        if (Locator.Find(Spec) is null && !Locator.ComRegistered(Spec))
        {
            why = $"{Spec.Name} is not installed on this PC (or set {Spec.OverrideVariable} to its executable).";
            return null;
        }
        if (Locator.WindowsPowerShell() is not { } ps) { why = "Windows PowerShell 5.1 (powershell.exe) was not found."; return null; }
        return new CadScriptRunner(ps, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], [], ".ps1", Utf8Bom: true);
    }

    public override CadScriptInfo Script => new("Windows PowerShell (COM)",
        $"The script runs in Windows PowerShell 5.1. In scope: $app (the running {Spec.Name}, {Spec.ProgId} COM object), $doc (the document at path, " +
        "opened if needed, else the active one), $MediatorArgs (the call's arguments), Write-Result @{ ... } (the hashtable you return; otherwise " +
        $"the script's output is returned). Use the {Spec.Name} COM API exactly as from VBA. Throw to report an error.");
}

public sealed class FreeCadApp(CadLocator? locator = null, string? overrideFolder = null) : ScriptCadApp(overrideFolder)
{
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
        if (_locator.Find(CadPrograms.FreeCad) is { } exe) return new CadScriptRunner(exe, [], [], ".py");
        why = "FreeCAD (FreeCADCmd) is not installed on this PC (or set MEDIATOR_FREECAD to FreeCADCmd.exe).";
        return null;
    }

    public override CadScriptInfo Script => new("Python (FreeCAD)",
        "The script runs in FreeCADCmd (headless FreeCAD, Python 3). In scope: App/FreeCAD, Part, doc (the document at path, opened), args, " +
        "result({...}) to return values (print output is returned too), save() to save doc. Import other modules as needed (PartDesign, Sketcher, " +
        "TechDraw, ObjectsFem, Path for CAM, Mesh). FreeCAD is Z-up; lengths in mm. There is no GUI: no views or colours.");
}

public sealed class AutoCadApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(CadPrograms.AutoCad, locator, overrideFolder)
{
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

public sealed class InventorApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(CadPrograms.Inventor, locator, overrideFolder)
{
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

public sealed class CatiaApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(CadPrograms.Catia, locator, overrideFolder)
{
    public override string Prefix => "catia";
    protected override string ScriptName => "catia.ps1";
    public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
    {
        CadCapability.Session, CadCapability.Documents, CadCapability.Export, CadCapability.Inspect, CadCapability.Edit, CadCapability.Views, CadCapability.Script,
    };
    public override IReadOnlyList<string> ModelExtensions { get; } = [".catpart", ".catproduct"];
    public override string? DrawingExtension => ".catdrawing";
    public override IReadOnlyList<CadExportFormat> ExportFormats { get; } =
    [
        new("step", ".stp", true, false), new("iges", ".igs", true, false), new("stl", ".stl", true, false), new("3dxml", ".3dxml", true, false),
        new("pdf", ".pdf", false, true), new("dxf", ".dxf", false, true),
    ];
    protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
    {
        [CadCapability.Edit] = ["set_dimension", "set_global", "set_property", "rename_feature", "suppress", "unsuppress", "rebuild"],
    };
}

public sealed class CreoApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(CadPrograms.Creo, locator, overrideFolder)
{
    public override string Prefix => "creo";
    protected override string ScriptName => "creo.ps1";
    public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
    {
        CadCapability.Session, CadCapability.Documents, CadCapability.Inspect, CadCapability.Edit, CadCapability.Views, CadCapability.Script,
    };
    public override IReadOnlyList<string> ModelExtensions { get; } = [".prt", ".asm"];
    public override string? DrawingExtension => ".drw";
    /// <summary>Export goes through run_script for Creo (its export instructions differ between releases).</summary>
    public override IReadOnlyList<CadExportFormat> ExportFormats { get; } = [];
    protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
    {
        [CadCapability.Edit] = ["set_global", "rebuild"],
    };

    public override CadScriptInfo Script => new("Windows PowerShell (Creo VB API)",
        "The script runs in Windows PowerShell 5.1 against Creo's VB API (it must be registered: run vb_api_register.bat once, and set PRO_COMM_MSG_EXE). " +
        "In scope: $session (pfcSession of the running Creo), $model (the model at path, retrieved and shown, else the current one), $MediatorArgs, " +
        "Write-Result @{ ... }. Create VB API objects with New-Object -ComObject pfcls.<Class>. Throw to report an error.");
}

public sealed class NxApp(CadLocator? locator = null, string? overrideFolder = null) : ScriptCadApp(overrideFolder)
{
    private readonly CadLocator _locator = locator ?? CadLocator.Default;
    public override string Prefix => "nx";
    public override string DisplayName => "Siemens NX";
    public override bool HasLiveSession => false;
    protected override string ScriptName => "nx.py";
    public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
    {
        CadCapability.Session, CadCapability.Documents, CadCapability.Export, CadCapability.Inspect, CadCapability.Edit, CadCapability.Script,
    };
    public override IReadOnlyList<string> ModelExtensions { get; } = [".prt"];
    public override IReadOnlyList<CadExportFormat> ExportFormats { get; } =
        [new("step", ".stp", true, false), new("parasolid", ".x_t", true, false)];
    protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
    {
        [CadCapability.Edit] = ["set_global", "set_dimension", "add_global", "suppress", "unsuppress", "set_property", "rebuild"],
    };

    protected override CadScriptRunner? Runner(out string? why)
    {
        why = null;
        if (_locator.Find(CadPrograms.Nx) is { } exe) return new CadScriptRunner(exe, [], [], ".py");
        why = "Siemens NX (run_journal.exe) is not installed on this PC (or set MEDIATOR_NX to run_journal.exe).";
        return null;
    }

    public override CadScriptInfo Script => new("Python (NX Open journal)",
        "The script runs as an NX Open Python journal in batch (run_journal), on the part at path. In scope: NXOpen, session (NXOpen.Session.GetSession()), " +
        "part (the part at path, opened as the work part), args, result({...}) to return values, save() to save the part. Lengths in the part's units.");
}
