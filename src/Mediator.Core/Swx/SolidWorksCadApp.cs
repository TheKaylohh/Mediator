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
    public static readonly string[] ToolNames = [SessionTool, DocumentTool, ExportTool, InspectTool, EditTool, DrawingTool, ViewsTool];

    private readonly IDesktopAppSession _session = session ?? NoDesktopAppSession.Instance;

    public string Prefix => ToolPrefix;
    public string DisplayName => "SolidWorks";
    public string Lease => SwxToolProvider.Lease;
    public IReadOnlySet<CadCapability> Capabilities { get; } = Enum.GetValues<CadCapability>().ToHashSet();
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

    public Task<JsonObject> CaptureViewsAsync(string? path, IReadOnlyList<string> views, string outputFolder, CancellationToken ct) =>
        Sta(() => SwxParametric.CaptureViews(path, views, outputFolder, TimeSpan.FromMinutes(3)), ct);
}
