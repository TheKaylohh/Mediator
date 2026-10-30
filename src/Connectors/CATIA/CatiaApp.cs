using Mediator.Core.Cad;

namespace Mediator.Connectors.CATIA;

public sealed class CatiaApp(CadLocator? locator = null, string? overrideFolder = null) : ComScriptCadApp(CatiaApp.Program, locator, overrideFolder)
{
    /// <summary>Where it is installed (see <see cref="CadLocator"/>).</summary>
    public static readonly CadProgramSpec Program = new("catia", "CATIA V5", "MEDIATOR_CATIA", [],
        [@"Dassault Systemes\B*\win_b64\code\bin\CNEXT.exe"], ["CATIA P3 V5", "CATIA V5"], [@"win_b64\code\bin\CNEXT.exe"], "CATIA.Application");

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
