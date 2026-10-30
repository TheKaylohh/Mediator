using Mediator.Core.Cad;

namespace Mediator.Connectors.NX;

public sealed class NxApp(CadLocator? locator = null, string? overrideFolder = null) : ScriptCadApp(overrideFolder)
{
    /// <summary>Where it is installed (see <see cref="CadLocator"/>).</summary>
    public static readonly CadProgramSpec Program = new("nx", "Siemens NX", "MEDIATOR_NX", ["run_journal"],
        [@"Siemens\NX*\NXBIN\run_journal.exe", @"Siemens\NX *\NXBIN\run_journal.exe"], ["Siemens NX"], [@"NXBIN\run_journal.exe"]);

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
        if (_locator.Find(NxApp.Program) is { } exe) return new CadScriptRunner(exe, [], [], ".py");
        why = "Siemens NX (run_journal.exe) is not installed on this PC (or set MEDIATOR_NX to run_journal.exe).";
        return null;
    }

    public override CadScriptInfo Script => new("Python (NX Open journal)",
        "The script runs as an NX Open Python journal in batch (run_journal), on the part at path. In scope: NXOpen, session (NXOpen.Session.GetSession()), " +
        "part (the part at path, opened as the work part), args, result({...}) to return values, save() to save the part. Lengths in the part's units.");
}
