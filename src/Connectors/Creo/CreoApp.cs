using Mediator.Core.Cad;

namespace Mediator.Connectors.Creo;

public sealed class CreoApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(CreoApp.Program, locator, overrideFolder)
{
    /// <summary>Where it is installed (see <see cref="CadLocator"/>).</summary>
    public static readonly CadProgramSpec Program = new("creo", "PTC Creo", "MEDIATOR_CREO", [],
        [@"PTC\Creo *\Parametric\bin\parametric.exe"], ["PTC Creo"], [@"Parametric\bin\parametric.exe"], "pfcls.pfcAsyncConnection");

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
