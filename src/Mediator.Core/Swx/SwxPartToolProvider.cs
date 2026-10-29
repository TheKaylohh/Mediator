using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Core.Tools;

namespace Mediator.Core.Swx;

/// <summary>
/// Native parametric SolidWorks parts (<see cref="SwxParametric"/>): a feature tree driven by global variables and
/// equations, so design changes rebuild with their intent. Both tools write a .SLDPRT and show an approval card first.
/// </summary>
public sealed class SwxPartToolProvider(IDesktopAppSession? solidWorks = null) : IToolProvider
{
    public const string BuildTool = "swx_build_parametric_part";
    public const string SetTool = "swx_set_part_globals";
    public const string SessionTool = "swx_session";
    public const string DocumentTool = "swx_document_ops";
    public const string ExportTool = "swx_export_neutral";
    public static IReadOnlySet<string> ToolNames { get; } = new HashSet<string>(StringComparer.Ordinal) { BuildTool, SetTool, SessionTool, DocumentTool, ExportTool };

    private readonly IDesktopAppSession _solidWorks = solidWorks ?? NoDesktopAppSession.Instance;

    private static string Json(JsonObject o) => o.ToJsonString();

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(BuildTool,
            "Build a NATIVE parametric SolidWorks part (real feature tree, not an imported STEP) and leave it open on screen. " +
            "globals: named sizes in mm (e.g. W, D, T). Every expression may use + - * / ( ) and global names; each sketch dimension " +
            "and depth becomes a SolidWorks equation, so changing a global later keeps the design intent. Features run in order: " +
            "extrude | cut (a sketch: 'on' = \"Top\" | \"Front\" | \"Right\" plane, or a model point [x,y,z] on a planar face; " +
            "'profile' = shapes {circle:{cx,cy,d}}, {rect:{cx,cy,w,h}} (centred), {polygon:[[x,y],...]}; 'depth' = expression or " +
            "\"through_all\" (cuts only, both directions); 'midPlane', 'reverse'; 'equal': true links all circles in the sketch to the " +
            "first one's diameter) and fillet {radius} (all outside edges; hole walls and walls thinner than 2×radius stay sharp). " +
            "More shapes: {spline:{points:[[x,y],...],closed}}, {arc:[start,mid,end]}, {line:[a,b]}, {centerline:[a,b]} (revolve axis), " +
            "{outline:[[x,y], {line:[x,y]}, {spline:[[x,y],...]}, {arc:[mid,end]}, ...]} (one closed profile mixing lines, splines, arcs). " +
            "Organic / freeform shapes: {op:\"plane\", name, base, offset} hangs section planes; {op:\"sketch\", name, on, profile} keeps a " +
            "named sketch; {op:\"loft\", profiles:[sketch names in order], guides?:[sketch names], surface?, closed?} blends sections " +
            "(a surface loft with guide curves is the boundary-surface equivalent); {op:\"sweep\", profile, path, guides?, surface?} (the " +
            "profile sketch must touch the path's start); {op:\"revolve\", on, profile with a centerline, angle?}; extrude with surface:true; " +
            "{op:\"knit\", solid?} joins surfaces; {op:\"thicken\", thickness, side?:out|in|both}; {op:\"shell\", thickness, openAt?:[x,y,z]}; " +
            "{op:\"chamfer\", distance, angle?}; {op:\"mirror\", plane} (build half, mirror the rest). fillet/chamfer take edgesNear:" +
            "[[x,y,z],...] to pick the nearest edges instead of all. Any feature may take a name. fitBoxMm:[x,y,z] scales the finished " +
            "part down to fit a size limit (spline points are not linked to globals). " +
            "Iterating on a design: name versions \"<name>-v1\", \"-v2\"...; saving vN deletes the older versions this tool made (and their " +
            "STLs, pictures and backups; keepIterations: true keeps them). The result includes front, right, top and iso PICTURES of the " +
            "part (views: false skips them): look at them and compare with the real object. referenceMm: the real object's overall size " +
            "(e.g. a car's length, width, height) adds a proportions check (sides sorted, within 5%). " +
            "Sketch axes: Top plane x = model X, y = -model Z (so +y is the BACK); Front x = X, y = Y (up); Right x = -Z (so +x is the " +
            "BACK), y = Y. Model Y is up; extrudes from Top/top faces go +Y. Put coordinates relative to the origin with signs, e.g. " +
            "corner holes at cx \"W/2-E\" / \"-(W/2-E)\". End a rib R short of a filleted edge (e.g. \"D/2-R\"). The result reports " +
            "rebuild errors, the bounding box and every vertical hole/boss with its distance to the edges: check it. exportStl: true also " +
            "saves a .STL next to the part (for 3D printing). " +
            "Profiles from drawings: a profile shape {import:{path:\"logo.dxf\"|\"outline.svg\", width?: mm, scale?, center?: true, " +
            "offset?: [x,y]}} draws a DXF/SVG file's lines, arcs, circles and curves (closed loops can be extruded or cut). A sketch " +
            "with lock: true fixes every entity. {op:\"curve3d\", name, points:[[x,y,z],...], closed?} makes a 3D spline to use as a " +
            "sweep path or loft guide (by its name).",
            Schema("""
            {
              "type": "object",
              "required": ["name", "outputFolder", "globals", "features"],
              "properties": {
                "name": { "type": "string" },
                "outputFolder": { "type": "string" },
                "exportStl": { "type": "boolean" },
                "fitBoxMm": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
                "referenceMm": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
                "views": { "type": "boolean" },
                "keepIterations": { "type": "boolean" },
                "globals": { "type": "object", "additionalProperties": { "type": "number" } },
                "features": { "type": "array", "items": { "type": "object", "required": ["op"], "properties": {
                  "op": { "type": "string", "enum": [OPS] },
                  "on": {}, "profile": {}, "depth": {}, "midPlane": { "type": "boolean" }, "reverse": { "type": "boolean" },
                  "equal": { "type": "boolean" }, "radius": {}, "name": { "type": "string" }, "sketch": { "type": "string" },
                  "profiles": { "type": "array" }, "guides": { "type": "array" }, "path": { "type": "string" }, "surface": { "type": "boolean" },
                  "closed": { "type": "boolean" }, "angle": {}, "thickness": {}, "side": { "type": "string", "enum": ["out", "in", "both"] },
                  "openAt": { "type": "array" }, "outward": { "type": "boolean" }, "distance": {}, "edgesNear": { "type": "array" },
                  "plane": { "type": "string" }, "base": { "type": "string" }, "offset": {}, "solid": { "type": "boolean" }, "lock": { "type": "boolean" }, "points": { "type": "array" } } } }
              },
              "additionalProperties": false
            }
            """.Replace("OPS", string.Join(", ", SwxParametric.Ops.Select(o => $"\"{o}\"")), StringComparison.Ordinal)), ToolRisk.Write, null, TimeSpan.FromMinutes(8)),

        new(SetTool,
            "Change global variables of a parametric part (built by swx_build_parametric_part) and rebuild it in SolidWorks, e.g. " +
            "{\"values\": {\"W\": 150, \"D\": 80}}. Uses the part open in SolidWorks unless partPath is given. Returns rebuild errors " +
            "and the measured geometry (bounding box, holes with distance to edges) so you can verify the design intent held.",
            Schema("""
            {
              "type": "object",
              "required": ["values"],
              "properties": {
                "partPath": { "type": "string" },
                "values": { "type": "object", "additionalProperties": { "type": "number" } }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromMinutes(5)),

        new(SessionTool,
            "SolidWorks status: starts it if needed, then lists its version, the active document and every open document " +
            "(title, path, part/assembly/drawing, unsaved changes). Call it before opening, saving or closing documents.",
            Schema("""{ "type": "object", "properties": {}, "additionalProperties": false }"""), ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(4)),

        new(DocumentTool,
            "Open, save or close a SolidWorks document (.SLDPRT/.SLDASM/.SLDDRW). open: opens it silently (or brings it to the front). " +
            "save: saves it in place. close: a document with unsaved changes needs save: true (keep them) or discard: true (lose " +
            "them). No path = the active document.",
            Schema("""
            {
              "type": "object",
              "required": ["action"],
              "properties": {
                "action": { "type": "string", "enum": ["open", "save", "close"] },
                "path": { "type": "string" },
                "save": { "type": "boolean" },
                "discard": { "type": "boolean" }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromMinutes(4)),

        new(ExportTool,
            "Export SolidWorks documents to neutral files in outputFolder: step, stl, iges, parasolid (x_t) for parts and assemblies, " +
            "pdf for drawings. paths: the documents (opened and closed again if they were not open); none = the active document. " +
            "Files keep the document's name.",
            Schema("""
            {
              "type": "object",
              "required": ["formats", "outputFolder"],
              "properties": {
                "paths": { "type": "array", "items": { "type": "string" } },
                "formats": { "type": "array", "items": { "type": "string", "enum": ["step", "stl", "iges", "parasolid", "pdf"] }, "minItems": 1 },
                "outputFolder": { "type": "string" }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromMinutes(8)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        JsonObject preview = call.Tool.Name == BuildTool
            ? new JsonObject
            {
                ["kind"] = "swx_parametric_part",
                ["title"] = "Build a parametric SolidWorks part",
                ["summary"] = $"{(a.TryGetProperty("name", out var n) ? n.GetString() : "Part")}: " +
                              $"{(a.TryGetProperty("features", out var f) ? f.GetArrayLength() : 0)} features → {(a.TryGetProperty("outputFolder", out var o) ? o.GetString() : "?")}",
                ["globals"] = a.TryGetProperty("globals", out var g) ? JsonNode.Parse(g.GetRawText()) : null,
            }
            : call.Tool.Name is DocumentTool or ExportTool or SessionTool
            ? new JsonObject
            {
                ["kind"] = call.Tool.Name,
                ["title"] = call.Tool.Name == ExportTool ? "Export SolidWorks documents" : call.Tool.Name == SessionTool ? "Check SolidWorks" : "SolidWorks document",
                ["summary"] = a.GetRawText(),
            }
            : new JsonObject
            {
                ["kind"] = "swx_set_globals",
                ["title"] = "Change part dimensions in SolidWorks",
                ["summary"] = a.TryGetProperty("values", out var v) ? v.GetRawText() : "",
            };
        return Task.FromResult<JsonNode?>(preview);
    }

    /// <summary>
    /// The folder parts and exports are written INTO. An existing folder (an allowed folder itself, or one below it) is used as
    /// it is, never recreated or replaced; a missing one below an allowed folder may be created. Checking the folder as a
    /// file to create refused the allowed folder itself (root_itself) and every folder that already existed.
    /// </summary>
    public static PathVerdict OutputFolder(PathAuthorizer paths, string? folder)
    {
        var listed = paths.Check(folder, PathAccess.List);
        if (listed.Allowed || listed.Code != "not_found") return listed;
        return paths.Check(folder, PathAccess.CreateDirectory);
    }

    /// <summary>
    /// Every {import:{path}} profile (DXF/SVG) must be a readable file inside the job's folders; the path is replaced by the
    /// checked full path. Returns the refusal, or null.
    /// </summary>
    public static string? AuthorizeImports(JsonObject args, PathAuthorizer paths)
    {
        foreach (var imp in (args["features"] as JsonArray ?? []).OfType<JsonObject>()
                     .SelectMany(f => f["profile"] as JsonArray ?? []).OfType<JsonObject>()
                     .Select(s => s["import"]).OfType<JsonObject>())
        {
            var verdict = paths.Check((string?)imp["path"], PathAccess.Read);
            if (!verdict.Allowed) return verdict.Message;
            imp["path"] = verdict.FullPath;
        }
        return null;
    }

    /// <summary>Largest view picture sent to the model; bigger ones are listed by path only.</summary>
    public const int MaxViewBytes = 3_000_000;

    /// <summary>The rendered views (front, side, top, iso) as images, so the model can compare the part with the real object.</summary>
    public static IReadOnlyList<ToolImage> ViewImages(JsonObject result)
    {
        var images = new List<ToolImage>();
        foreach (var node in result["views"] as JsonArray ?? [])
        {
            if ((string?)node is not { } path || !File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > MaxViewBytes) continue;
                string type = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
                images.Add(new ToolImage(type, File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return images;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return ToolResult.Fail("SolidWorks parts need Windows.");
        try
        {
            await _solidWorks.EnsureReadyAsync(ct).ConfigureAwait(false);
            var args = (JsonObject)JsonNode.Parse(call.Arguments.GetRawText())!;
            JsonObject result;
            if (call.Tool.Name == BuildTool)
            {
                var verdict = OutputFolder(call.Job.Paths, (string?)args["outputFolder"]);
                if (!verdict.Allowed) return ToolResult.Fail(verdict.Message);
                args["outputFolder"] = verdict.FullPath;
                if (AuthorizeImports(args, call.Job.Paths) is { } refused) return ToolResult.Fail(refused);
                result = await Task.Run(() => SwxParametric.Build(args, TimeSpan.FromMinutes(7)), ct).ConfigureAwait(false);
            }
            else if (call.Tool.Name == SetTool)
            {
                string? part = (string?)args["partPath"];
                if (part is not null)
                {
                    var verdict = call.Job.Paths.Check(part, PathAccess.Modify);
                    if (!verdict.Allowed) return ToolResult.Fail(verdict.Message);
                    part = verdict.FullPath;
                }
                var values = args["values"]?.AsObject() ?? throw new ArgumentException("values is required");
                result = await Task.Run(() => SwxParametric.SetGlobals(part, values, TimeSpan.FromMinutes(4)), ct).ConfigureAwait(false);
            }
            else if (call.Tool.Name == SessionTool)
                return ToolResult.Ok(Json(await Task.Run(() => SwxParametric.Session(TimeSpan.FromMinutes(3)), ct).ConfigureAwait(false)));
            else if (call.Tool.Name == DocumentTool)
            {
                string action = (string?)args["action"] ?? "";
                string? path = (string?)args["path"];
                if (path is not null)
                {
                    var verdict = call.Job.Paths.Check(path, action == "open" ? PathAccess.Read : PathAccess.Modify);
                    if (!verdict.Allowed) return ToolResult.Fail(verdict.Message);
                    path = verdict.FullPath;
                    SwxParametric.DocType(path);
                }
                bool save = (bool?)args["save"] ?? false, discard = (bool?)args["discard"] ?? false;
                return ToolResult.Ok(Json(await Task.Run(() => SwxParametric.Document(action, path, save, discard, TimeSpan.FromMinutes(3)), ct).ConfigureAwait(false)));
            }
            else if (call.Tool.Name == ExportTool)
            {
                var folder = OutputFolder(call.Job.Paths, (string?)args["outputFolder"]);
                if (!folder.Allowed) return ToolResult.Fail(folder.Message);
                var paths = new List<string>();
                foreach (var p in args["paths"] as JsonArray ?? [])
                {
                    var verdict = call.Job.Paths.Check((string?)p, PathAccess.Read);
                    if (!verdict.Allowed) return ToolResult.Fail(verdict.Message);
                    paths.Add(verdict.FullPath!);
                }
                var formats = (args["formats"] as JsonArray ?? []).Select(f => (string?)f ?? "").ToList();
                var exported = await Task.Run(() => SwxParametric.Export(paths, formats, folder.FullPath!, TimeSpan.FromMinutes(7)), ct).ConfigureAwait(false);
                return new ToolResult(exported["files"] is not JsonArray { Count: > 0 }, Json(exported), exported);
            }
            else return ToolResult.Fail($"Unknown tool {call.Tool.Name}.");
            bool ok = result["rebuildErrors"] is JsonArray { Count: 0 } && (int?)result["bodies"] == 1;
            return new ToolResult((int?)result["bodies"] is null or 0, (ok ? "OK. " : "Check the problems below. ") + result.ToJsonString(), result)
            {
                Images = ViewImages(result),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or PlatformNotSupportedException or JsonException)
        {
            return ToolResult.Fail("SolidWorks: " + ex.Message);
        }
    }
}
