using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Security;

namespace Mediator.Connectors.CadFiles;

/// <summary>
/// V15: CAD files without opening a CAD program. cad_inspect_file describes a mesh (STL, OBJ, PLY, 3MF), a STEP or
/// IGES model, or a DXF/SVG profile; cad_convert_mesh writes a mesh as another format (and can scale, change units and
/// re-centre it) into a NEW file. STEP ↔ mesh conversion needs a CAD kernel: the descriptions point at SolidWorks.
/// </summary>
public sealed class CadFileToolProvider : IToolProvider
{
    public const string InspectTool = "cad_inspect_file";
    public const string ConvertTool = "cad_convert_mesh";
    public static readonly string[] Names = [InspectTool, ConvertTool];

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(InspectTool,
            "Describe a CAD file: STL/OBJ/PLY/3MF meshes (size in mm, triangles, surface area, volume, watertight or open edges, shells, inside-out " +
            "normals, unit hints), STEP/IGES models (writing system, units, part names, solids vs surfaces, approximate size) and DXF/SVG profiles " +
            "(outlines, circles, size). Call it before converting, printing or importing a file. Native .SLDPRT/.SLDASM need SolidWorks.",
            Schema("""{ "type": "object", "required": ["path"], "properties": { "path": { "type": "string" } }, "additionalProperties": false }"""),
            ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(3)),

        new(ConvertTool,
            "Write a mesh (.stl, .obj, .ply, .3mf) as a NEW .stl (binary, or ascii true), .obj, .ply or .3mf file; never replaces a file. Options: " +
            "scale (factor), or fromUnits+toUnits (mm, cm, m, in) e.g. an inch STL to mm; center true puts the part's base centre at the origin. " +
            "For STEP to STL use SolidWorks: swx_import_step, then swx_export_neutral. The user approves the new file.",
            Schema("""{ "type": "object", "required": ["path", "outputPath"], "properties": { "path": { "type": "string" }, "outputPath": { "type": "string" }, "scale": { "type": "number" }, "fromUnits": { "type": "string", "enum": ["mm", "cm", "m", "in"] }, "toUnits": { "type": "string", "enum": ["mm", "cm", "m", "in"] }, "center": { "type": "boolean" }, "ascii": { "type": "boolean" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromMinutes(5)),
    ];

    private static string? Str(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        if (call.Tool.Name != ConvertTool) return Task.FromResult<JsonNode?>(null);
        var a = call.Arguments;
        string detail = string.Join(" · ", new[]
        {
            a.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number ? "scale ×" + s.GetDouble().ToString(CultureInfo.InvariantCulture) : null,
            Str(a, "fromUnits") is { } f && Str(a, "toUnits") is { } t ? $"{f} → {t}" : null,
            a.TryGetProperty("center", out var c) && c.ValueKind == JsonValueKind.True ? "centred" : null,
        }.Where(x => x is not null));
        JsonNode preview = new JsonObject
        {
            ["kind"] = "cad.convert", ["title"] = "Write " + Path.GetFileName(Str(a, "outputPath") ?? "a mesh"), ["summary"] = "A new mesh file; the original is not changed.", ["selectable"] = false,
            ["rows"] = new JsonArray(new JsonObject { ["key"] = "out", ["primary"] = $"{Str(a, "path")} → {Str(a, "outputPath")}", ["detail"] = detail }),
        };
        return Task.FromResult<JsonNode?>(preview);
    }

    public Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(call.Tool.Name switch
            {
                InspectTool => Inspect(call.Job, call.Arguments),
                ConvertTool => Convert(call.Job, call.Arguments),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            });
        }
        catch (PathDeniedException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
        catch (Exception ex) when (ex is CadFileException or IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException or EndOfStreamException or OverflowException)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }

    private static ToolResult Inspect(JobGrant job, JsonElement a)
    {
        var verdict = job.Paths.VerifyExisting(Str(a, "path") ?? "", PathAccess.Read);
        if (!verdict.Allowed) return ToolResult.Fail(verdict.Message);
        return ToolResult.Ok(CadFileInspector.Describe(verdict.FullPath));
    }

    public static double UnitMm(string unit) => unit switch
    {
        "mm" => 1, "cm" => 10, "m" => 1000, "in" => 25.4,
        _ => throw new CadFileException($"Unknown unit '{unit}' (mm, cm, m, in)."),
    };

    private static ToolResult Convert(JobGrant job, JsonElement a)
    {
        string input = Str(a, "path") ?? "", output = Str(a, "outputPath") ?? "";
        string outExt = Path.GetExtension(output).ToLowerInvariant();
        if (!Mesh.WritableExtensions.Contains(outExt)) return ToolResult.Fail("outputPath must end in .stl, .obj, .ply or .3mf.");
        Mesh mesh;
        using (var fs = job.Paths.OpenRead(input)) mesh = Mesh.Load(fs, Path.GetExtension(input), Path.GetFileName(input));
        double scale = mesh.UnitMm ?? 1; // 3MF in other units: written in millimetres
        if (a.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Number) scale *= s.GetDouble();
        if (Str(a, "fromUnits") is { } from && Str(a, "toUnits") is { } to) scale *= UnitMm(from) / UnitMm(to);
        if (!(scale > 0) || double.IsInfinity(scale)) return ToolResult.Fail("scale must be a positive number.");
        mesh.Transform(scale, a.TryGetProperty("center", out var c) && c.ValueKind == JsonValueKind.True);
        mesh.UnitMm = null;
        using (var buffer = new MemoryStream())
        {
            mesh.Write(buffer, outExt, a.TryGetProperty("ascii", out var asc) && asc.ValueKind == JsonValueKind.True); // fully built before the file exists
            using var outFile = job.Paths.CreateNew(output);
            buffer.WriteTo(outFile);
        }
        string full = job.Paths.Check(output, PathAccess.Read).FullPath;
        return ToolResult.Ok($"Wrote {full}.\n" + CadFileInspector.DescribeMesh(Mesh.Load(full)), new JsonObject { ["path"] = full });
    }
}
