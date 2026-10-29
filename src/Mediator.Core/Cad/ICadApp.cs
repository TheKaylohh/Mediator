using System.Text.Json.Nodes;

namespace Mediator.Core.Cad;

/// <summary>
/// What a desktop CAD program can do for a chat (V16). Each one becomes a tool named "&lt;prefix&gt;_&lt;verb&gt;"
/// (<see cref="CadVerb"/>), so SolidWorks gets swx_session, swx_inspect_model, ... and a later Inventor, Fusion or Revit
/// connector gets the same tools under its own prefix, with the same arguments, previews and path checks.
/// </summary>
public enum CadCapability { Session, Documents, Export, Inspect, Edit, Drawing, Views }

/// <summary>The tool verbs. The tool name is the app's prefix, '_', and the verb.</summary>
public static class CadVerb
{
    public const string Session = "session";
    public const string Documents = "document_ops";
    public const string Export = "export_neutral";
    public const string Inspect = "inspect_model";
    public const string Edit = "edit_model";
    public const string Drawing = "create_drawing";
    public const string Views = "capture_views";

    public static string For(CadCapability c) => c switch
    {
        CadCapability.Session => Session,
        CadCapability.Documents => Documents,
        CadCapability.Export => Export,
        CadCapability.Inspect => Inspect,
        CadCapability.Edit => Edit,
        CadCapability.Drawing => Drawing,
        CadCapability.Views => Views,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static string ToolName(string prefix, CadCapability c) => prefix + "_" + For(c);
}

/// <summary>A CAD call that could not be done. The message is for the model and the user.</summary>
public sealed class CadAppException(string message) : Exception(message);

/// <summary>An export format: its name in the tool, the file extension, and whether it is for drawings, models or both.</summary>
public sealed record CadExportFormat(string Name, string Extension, bool ForModels, bool ForDrawings);

/// <summary>open | save | save_as | close one document. Path null = the active document.</summary>
public sealed record CadDocumentRequest(string Action, string? Path, bool Save, bool Discard, string? TargetPath);

/// <summary>A new drawing of <paramref name="ModelPath"/> (null = the active part or assembly), saved as <paramref name="OutputPath"/>.</summary>
public sealed record CadDrawingRequest(string? ModelPath, string OutputPath, string Sheet, bool FirstAngle, bool Isometric, bool Dimensions, bool ExportPdf);

/// <summary>
/// One CAD program. Implementations run the work on their own threads (COM, an add-in socket, ...) and return JSON
/// reports; they throw <see cref="CadAppException"/>, <see cref="ArgumentException"/>, <see cref="InvalidOperationException"/>
/// or <see cref="TimeoutException"/> with a readable message. Paths reaching them are already authorized by
/// <see cref="CadAppToolProvider"/>.
/// </summary>
public interface ICadApp
{
    /// <summary>Tool name prefix, e.g. "swx". Lower case letters and digits.</summary>
    string Prefix { get; }
    /// <summary>"SolidWorks", "Autodesk Inventor", ...</summary>
    string DisplayName { get; }
    /// <summary>The bus lease every tool of this app holds, so two jobs never drive the program at once.</summary>
    string Lease { get; }
    IReadOnlySet<CadCapability> Capabilities { get; }
    /// <summary>Native document extensions (lower case, with the dot), parts and assemblies first, e.g. .sldprt.</summary>
    IReadOnlyList<string> ModelExtensions { get; }
    /// <summary>The native drawing extension (lower case), or null when the app has no drawings.</summary>
    string? DrawingExtension { get; }
    IReadOnlyList<CadExportFormat> ExportFormats { get; }
    /// <summary>Material names the app's default library is known to hold (shown to the model as examples).</summary>
    IReadOnlyList<string> MaterialExamples { get; }

    /// <summary>Null when the app can be driven on this PC; otherwise why not (wrong OS, not installed).</summary>
    string? Unavailable();
    /// <summary>Starts the program if needed and waits until it answers.</summary>
    Task EnsureReadyAsync(CancellationToken ct);
    /// <summary>The active document's file, or null (none open, or never saved).</summary>
    Task<string?> ActiveDocumentPathAsync(CancellationToken ct);

    Task<JsonObject> SessionAsync(CancellationToken ct);
    Task<JsonObject> DocumentAsync(CadDocumentRequest request, CancellationToken ct);
    Task<JsonObject> ExportAsync(IReadOnlyList<string> paths, IReadOnlyList<string> formats, string outputFolder, CancellationToken ct);
    /// <summary>Report sections: see <see cref="CadAppToolProvider.InspectSections"/>.</summary>
    Task<JsonObject> InspectAsync(string? path, IReadOnlySet<string> sections, CancellationToken ct);
    /// <summary>Applies <paramref name="ops"/> in order (see <see cref="CadAppToolProvider.EditOps"/>); saves only when asked.</summary>
    Task<JsonObject> EditAsync(string? path, JsonArray ops, bool save, CancellationToken ct);
    Task<JsonObject> CreateDrawingAsync(CadDrawingRequest request, CancellationToken ct);
    /// <summary>Saves pictures of <paramref name="views"/> into <paramref name="outputFolder"/>; the report lists them under "images".</summary>
    Task<JsonObject> CaptureViewsAsync(string? path, IReadOnlyList<string> views, string outputFolder, CancellationToken ct);
}
