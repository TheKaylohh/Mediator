using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Security;

namespace Mediator.Core.Cad;

/// <summary>
/// V16: the chat tools of one desktop CAD program (<see cref="ICadApp"/>): session, documents, export, inspect, edit,
/// drawings and pictures. Everything that does not depend on the program lives here, once: the schemas the model sees,
/// approval previews, path authorization (inside the job's folders, right file types, new files never replace old
/// ones), the app lease and error handling. A new CAD connector only implements <see cref="ICadApp"/>.
/// </summary>
public sealed class CadAppToolProvider : IToolProvider
{
    /// <summary>What inspect_model can report. Empty = <see cref="DefaultSections"/>.</summary>
    public static readonly string[] InspectSections =
        ["features", "dimensions", "equations", "properties", "mass", "configurations", "selection", "components", "mates", "interference"];
    public static readonly string[] DefaultSections = ["features", "equations", "properties", "mass", "configurations", "components", "mates"];

    /// <summary>edit_model operations and their required fields.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> EditOps = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["set_dimension"] = ["name", "value"],
        ["set_global"] = ["name", "value"],
        ["suppress"] = ["feature"],
        ["unsuppress"] = ["feature"],
        ["rename_feature"] = ["feature", "newName"],
        ["set_property"] = ["name", "value"],
        ["delete_property"] = ["name"],
        ["set_material"] = ["material"],
        ["activate_configuration"] = ["name"],
        ["add_configuration"] = ["name"],
        ["suppress_component"] = ["component"],
        ["unsuppress_component"] = ["component"],
        ["rebuild"] = [],
    };

    public static readonly string[] Sheets = ["A4", "A3", "A2", "A1", "A0", "A", "B", "C", "D", "E"];
    public static readonly string[] ViewNames = ["current", "iso", "front", "back", "left", "right", "top", "bottom", "trimetric"];
    public const int MaxViewBytes = 3_000_000;
    private const int MaxPaths = 200, MaxOps = 50;

    private readonly ICadApp _app;
    private readonly string _viewsRoot;
    private readonly Dictionary<string, CadCapability> _byName;

    /// <param name="viewsRoot">Where pictures are rendered before they are handed to the model (deleted afterwards).</param>
    public CadAppToolProvider(ICadApp app, string? viewsRoot = null)
    {
        _app = app;
        _viewsRoot = viewsRoot ?? Path.Combine(Path.GetTempPath(), "Mediator-views");
        Tools = Build(app);
        _byName = app.Capabilities.ToDictionary(c => CadVerb.ToolName(app.Prefix, c), StringComparer.Ordinal);
        ToolNames = Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<ToolDescriptor> Tools { get; }
    public IReadOnlySet<string> ToolNames { get; }

    public string Name(CadCapability c) => CadVerb.ToolName(_app.Prefix, c);

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;
    private static string Enum(IEnumerable<string> values) => string.Join(", ", values.Select(v => JsonSerializer.Serialize(v)));

    private static IReadOnlyList<ToolDescriptor> Build(ICadApp app)
    {
        string p = app.Prefix, n = app.DisplayName;
        string exts = string.Join("/", app.ModelExtensions.Append(app.DrawingExtension).OfType<string>().Select(e => e.TrimStart('.').ToUpperInvariant()));
        var list = new List<ToolDescriptor>();
        void Add(CadCapability c, string description, string schema, ToolRisk risk, TimeSpan timeout) =>
            list.Add(new ToolDescriptor(CadVerb.ToolName(p, c), description, Schema(schema), risk, app.Lease, timeout));

        if (app.Capabilities.Contains(CadCapability.Session))
            Add(CadCapability.Session,
                $"{n} status: starts it if needed, then lists its version, the active document and every open document (title, path, " +
                $"part/assembly/drawing, unsaved changes). Call it first when the user talks about \"this part\", \"the open model\" or what is on screen.",
                """{ "type": "object", "properties": {}, "additionalProperties": false }""", ToolRisk.ReadOnly, TimeSpan.FromMinutes(4));

        if (app.Capabilities.Contains(CadCapability.Documents))
            Add(CadCapability.Documents,
                $"Open, save, save as or close a {n} document ({exts}). open: opens it silently (or brings it to the front). save: saves it in " +
                "place. save_as: saves it under targetPath (a NEW file; the open document becomes that file). close: a document with unsaved " +
                "changes needs save: true (keep them) or discard: true (lose them; only when the user says so). No path = the active document.",
                """
                {
                  "type": "object",
                  "required": ["action"],
                  "properties": {
                    "action": { "type": "string", "enum": ["open", "save", "save_as", "close"] },
                    "path": { "type": "string" }, "targetPath": { "type": "string" },
                    "save": { "type": "boolean" }, "discard": { "type": "boolean" }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.Write, TimeSpan.FromMinutes(4));

        if (app.Capabilities.Contains(CadCapability.Export))
        {
            string models = string.Join(", ", app.ExportFormats.Where(f => f.ForModels).Select(f => f.Name));
            string drawings = string.Join(", ", app.ExportFormats.Where(f => f.ForDrawings).Select(f => f.Name));
            Add(CadCapability.Export,
                $"Export {n} documents to other files in outputFolder: {models} for parts and assemblies; {drawings} for drawings. paths: the " +
                "documents (opened and closed again if they were not open); none = the active document. Files keep the document's name.",
                $$"""
                {
                  "type": "object",
                  "required": ["formats", "outputFolder"],
                  "properties": {
                    "paths": { "type": "array", "items": { "type": "string" } },
                    "formats": { "type": "array", "items": { "type": "string", "enum": [{{Enum(app.ExportFormats.Select(f => f.Name))}}] }, "minItems": 1 },
                    "outputFolder": { "type": "string" }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.Write, TimeSpan.FromMinutes(8));
        }

        if (app.Capabilities.Contains(CadCapability.Inspect))
            Add(CadCapability.Inspect,
                $"Read a {n} part or assembly without changing it (path, or the active document). sections (default: features, equations, " +
                "properties, mass, configurations, components, mates): features = the feature tree with type, suppressed and rebuild errors; " +
                "dimensions = every dimension's full name (e.g. D1@Boss-Extrude1) and value in mm or degrees, for edit_model set_dimension; " +
                "equations = global variables and equations; properties = custom properties (file and active configuration); mass = mass, " +
                "volume, surface area, centre of mass, density and material; configurations = names and the active one; selection = what the " +
                "user has selected on screen (faces with area/radius, edges with length, features, components): use it for \"this face\" or " +
                "\"the selected hole\"; components = an assembly's parts with quantities (BOM); mates = mates with errors; interference = an " +
                "assembly's interfering components and volumes (slower; only when asked).",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string" },
                    "sections": { "type": "array", "items": { "type": "string", "enum": [{{Enum(InspectSections)}}] } }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.ReadOnly, TimeSpan.FromMinutes(5));

        if (app.Capabilities.Contains(CadCapability.Edit))
            Add(CadCapability.Edit,
                $"Change an existing {n} part or assembly (path, or the active document), in order, then rebuild. ops: " +
                "{op:\"set_dimension\", name:\"D1@Boss-Extrude1\", value, unit?: mm|in|deg} (names from inspect_model dimensions); " +
                "{op:\"set_global\", name:\"W\", value} (mm); {op:\"suppress\"|\"unsuppress\", feature}; {op:\"rename_feature\", feature, newName}; " +
                "{op:\"set_property\", name, value, configuration?} (custom property; configuration \"\" or none = the file); {op:\"delete_property\", name, configuration?}; " +
                $"{{op:\"set_material\", material}} (a name from the material library, e.g. {string.Join(", ", app.MaterialExamples.Select(m => "\"" + m + "\""))}); " +
                "{op:\"activate_configuration\", name}; {op:\"add_configuration\", name, description?}; {op:\"suppress_component\"|\"unsuppress_component\", " +
                "component:\"Bolt-2\"} (assemblies); {op:\"rebuild\"}. save: false (default) leaves the changes open and unsaved for the user to " +
                "review (Ctrl+Z undoes them); true saves the file. The user approves the list and may untick operations. Returns each operation's " +
                "result, rebuild errors and the new mass and size: check them.",
                $$"""
                {
                  "type": "object",
                  "required": ["ops"],
                  "properties": {
                    "path": { "type": "string" },
                    "save": { "type": "boolean" },
                    "ops": { "type": "array", "minItems": 1, "items": { "type": "object", "required": ["op"], "properties": {
                      "op": { "type": "string", "enum": [{{Enum(EditOps.Keys)}}] },
                      "name": { "type": "string" }, "value": {}, "unit": { "type": "string", "enum": ["mm", "in", "deg"] },
                      "feature": { "type": "string" }, "newName": { "type": "string" }, "configuration": { "type": "string" },
                      "material": { "type": "string" }, "description": { "type": "string" }, "component": { "type": "string" } } } }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.Write, TimeSpan.FromMinutes(6));

        if (app.Capabilities.Contains(CadCapability.Drawing) && app.DrawingExtension is { } dwg)
            Add(CadCapability.Drawing,
                $"Make a NEW {n} drawing ({dwg.TrimStart('.').ToUpperInvariant()}) of a part or assembly (modelPath, or the active document; it must " +
                "be saved): the three standard views (third-angle projection unless projection is \"first\"), an isometric view (isometric: false " +
                $"leaves it out) and the model's dimensions (dimensions: false leaves them out). sheet: {string.Join(", ", Sheets)} (default A3). " +
                "outputPath: the new drawing file (never replaces one). exportPdf: true also writes a PDF next to it. The drawing stays open.",
                $$"""
                {
                  "type": "object",
                  "required": ["outputPath"],
                  "properties": {
                    "modelPath": { "type": "string" }, "outputPath": { "type": "string" },
                    "sheet": { "type": "string", "enum": [{{Enum(Sheets)}}] },
                    "projection": { "type": "string", "enum": ["third", "first"] },
                    "isometric": { "type": "boolean" }, "dimensions": { "type": "boolean" }, "exportPdf": { "type": "boolean" }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.Write, TimeSpan.FromMinutes(8));

        if (app.Capabilities.Contains(CadCapability.Views))
            Add(CadCapability.Views,
                $"Pictures of a {n} document (path, or the active one) returned to you as images, to see what the model looks like before " +
                "answering or after a change. views (default [\"current\", \"iso\"]): current = exactly what the user sees; iso, front, back, left, " +
                "right, top, bottom, trimetric turn the model to that view (it stays there on screen). Drawings: current only.",
                $$"""
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string" },
                    "views": { "type": "array", "items": { "type": "string", "enum": [{{Enum(ViewNames)}}] }, "maxItems": 6 }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.ReadOnly, TimeSpan.FromMinutes(4));

        return list;
    }

    // ── Arguments ────────────────────────────────────────────────────────────────────────────────────

    private static string? Str(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement a, string name, bool fallback = false) =>
        a.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;
    private static List<string> Strings(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    private CadCapability? CapabilityOf(string tool) => _byName.TryGetValue(tool, out var c) ? c : null;

    private bool IsDocument(string path) => _app.ModelExtensions.Append(_app.DrawingExtension).OfType<string>()
        .Contains(Path.GetExtension(path).ToLowerInvariant());

    private string DocTypes => string.Join(", ", _app.ModelExtensions.Append(_app.DrawingExtension).OfType<string>());

    /// <summary>A document path the job may use with <paramref name="access"/>; a refusal message otherwise.</summary>
    private (string? Path, string? Error) Doc(JobGrant job, string? raw, PathAccess access)
    {
        if (raw is null) return (null, null);
        var v = job.Paths.Check(raw, access);
        if (!v.Allowed) return (null, v.Message);
        if (!IsDocument(v.FullPath)) return (null, $"{Path.GetFileName(v.FullPath)} is not a {_app.DisplayName} document ({DocTypes}).");
        return (v.FullPath, null);
    }

    /// <summary>
    /// The document a change will be written to: the given path, or the active document's file. An active document outside the
    /// job's folders cannot be changed (a job only writes where the user allowed it); a never-saved one can, if it is not saved.
    /// </summary>
    private async Task<(string? Path, string? Error)> WritableDoc(JobGrant job, string? raw, bool saves, CancellationToken ct)
    {
        if (raw is not null) return Doc(job, raw, PathAccess.Modify);
        string? active = await _app.ActiveDocumentPathAsync(ct).ConfigureAwait(false);
        if (active is null)
            return saves ? (null, $"The active {_app.DisplayName} document was never saved: save it first with {Name(CadCapability.Documents)} save_as.") : (null, null);
        var (_, error) = Doc(job, active, PathAccess.Modify);
        return error is null ? (null, null) : (null, $"The active document {Path.GetFileName(active)} is outside the folders this job may change ({error}). Ask the user to pick its folder.");
    }

    /// <summary>The edit operations, checked: a known op with its required fields. Index keys "1".."n" are the approval rows.</summary>
    public static (List<(string Key, JsonObject Op)> Ops, string? Error) ParseOps(JsonElement a)
    {
        if (!a.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() == 0) return ([], "ops must be a non-empty array.");
        if (ops.GetArrayLength() > MaxOps) return ([], $"At most {MaxOps} operations per call.");
        var list = new List<(string, JsonObject)>();
        int i = 0;
        foreach (var e in ops.EnumerateArray())
        {
            i++;
            if (JsonNode.Parse(e.GetRawText()) is not JsonObject o) return ([], $"Operation {i} must be an object.");
            string op = (string?)o["op"] ?? "";
            if (!EditOps.TryGetValue(op, out var required)) return ([], $"Operation {i}: unknown op '{op}' ({string.Join(", ", EditOps.Keys)}).");
            if (required.FirstOrDefault(r => o[r] is null) is { } missing) return ([], $"Operation {i} ({op}) needs '{missing}'.");
            if (op is "set_dimension" or "set_global" && !(o["value"] is JsonValue value && value.TryGetValue(out double _)))
                return ([], $"Operation {i} ({op}): value must be a number.");
            list.Add((i.ToString(System.Globalization.CultureInfo.InvariantCulture), o));
        }
        return (list, null);
    }

    private static string DescribeOp(JsonObject o) => (string?)o["op"] switch
    {
        "set_dimension" => $"{o["name"]} = {o["value"]} {(string?)o["unit"] ?? "mm"}",
        "set_global" => $"global {o["name"]} = {o["value"]} mm",
        "suppress" or "unsuppress" => $"{o["op"]} {o["feature"]}",
        "rename_feature" => $"rename {o["feature"]} → {o["newName"]}",
        "set_property" => $"property {o["name"]} = \"{o["value"]}\"" + ((string?)o["configuration"] is { Length: > 0 } c ? $" ({c})" : ""),
        "delete_property" => $"delete property {o["name"]}",
        "set_material" => $"material: {o["material"]}",
        "activate_configuration" => $"activate configuration {o["name"]}",
        "add_configuration" => $"add configuration {o["name"]}",
        "suppress_component" or "unsuppress_component" => $"{((string)o["op"]!).Replace("_component", "", StringComparison.Ordinal)} component {o["component"]}",
        _ => (string?)o["op"] ?? "",
    };

    // ── Previews ─────────────────────────────────────────────────────────────────────────────────────

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        string app = _app.DisplayName;
        JsonObject Card(string title, string summary, bool selectable, IEnumerable<(string Key, string Primary, string Detail)> rows) => new()
        {
            ["kind"] = call.Tool.Name, ["title"] = title, ["summary"] = summary, ["selectable"] = selectable,
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["key"] = r.Key, ["primary"] = r.Primary, ["detail"] = r.Detail }).ToArray()),
        };
        string target = Str(a, "path") is { } path ? Path.GetFileName(path) : "the active document";
        JsonNode? preview = CapabilityOf(call.Tool.Name) switch
        {
            CadCapability.Documents => Card($"{app}: {Str(a, "action")} {target}",
                Str(a, "action") switch
                {
                    "save_as" => $"Saves it as {Str(a, "targetPath")} (a new file).",
                    "close" when Bool(a, "discard") && !Bool(a, "save") => "Closes it and DISCARDS unsaved changes.",
                    "close" => Bool(a, "save") ? "Saves and closes it." : "Closes it.",
                    "save" => "Saves it in place.",
                    _ => "Opens it in " + app + ".",
                }, false, []),
            CadCapability.Export => Card($"Export {app} documents", $"{string.Join(", ", Strings(a, "formats"))} → {Str(a, "outputFolder")}", false,
                (Strings(a, "paths") is { Count: > 0 } ps ? ps : ["(the active document)"]).Select(p => (p, Path.GetFileName(p), ""))),
            CadCapability.Edit => EditPreview(a, target, Card),
            CadCapability.Drawing => Card($"New {app} drawing", $"{(Str(a, "modelPath") is { } m ? Path.GetFileName(m) : "the active model")} → {Str(a, "outputPath")}", false,
                [("sheet", $"Sheet {Str(a, "sheet") ?? "A3"}, {(Str(a, "projection") == "first" ? "first" : "third")}-angle", (Bool(a, "isometric", true) ? "3 views + isometric" : "3 views") +
                    (Bool(a, "dimensions", true) ? ", model dimensions" : "") + (Bool(a, "exportPdf") ? ", PDF" : ""))]),
            _ => null,
        };
        return Task.FromResult(preview);
    }

    private static JsonObject EditPreview(JsonElement a, string target, Func<string, string, bool, IEnumerable<(string, string, string)>, JsonObject> card)
    {
        var (ops, error) = ParseOps(a);
        bool save = Bool(a, "save");
        return card($"Change {target}", error ?? $"{ops.Count} change(s), then rebuild. " + (save ? "The file is SAVED." : "Left unsaved for you to review (Ctrl+Z undoes)."),
            error is null, ops.Select(o => (o.Key, DescribeOp(o.Op), "")));
    }

    // ── Execution ────────────────────────────────────────────────────────────────────────────────────

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        if (CapabilityOf(call.Tool.Name) is not { } capability) return ToolResult.Fail($"Unknown tool {call.Tool.Name}.");
        if (_app.Unavailable() is { } why)
            return new ToolResult(true, $"{_app.DisplayName}: {why} Do not retry; tell the user.", new JsonObject { ["code"] = "unavailable" });
        try
        {
            try { await _app.EnsureReadyAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                return ToolResult.Fail($"{_app.DisplayName} could not be started: {ex.Message}");
            }
            return capability switch
            {
                CadCapability.Session => Report(await _app.SessionAsync(ct).ConfigureAwait(false)),
                CadCapability.Documents => await DocumentAsync(call, ct).ConfigureAwait(false),
                CadCapability.Export => await ExportAsync(call, ct).ConfigureAwait(false),
                CadCapability.Inspect => await InspectAsync(call, ct).ConfigureAwait(false),
                CadCapability.Edit => await EditAsync(call, ct).ConfigureAwait(false),
                CadCapability.Drawing => await DrawingAsync(call, ct).ConfigureAwait(false),
                CadCapability.Views => await ViewsAsync(call, ct).ConfigureAwait(false),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            };
        }
        catch (Exception ex) when (ex is CadAppException or ArgumentException or InvalidOperationException or TimeoutException or PlatformNotSupportedException or JsonException)
        {
            return ToolResult.Fail($"{_app.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>A report as the result: an error when it lists problems and produced nothing.</summary>
    private static ToolResult Report(JsonObject report, bool failed = false) => new(failed, report.ToJsonString(), report);

    private async Task<ToolResult> DocumentAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        string action = Str(a, "action") ?? "";
        bool save = Bool(a, "save"), discard = Bool(a, "discard");
        string? path = null, target = null, error;
        switch (action)
        {
            case "open":
                (path, error) = Doc(call.Job, Str(a, "path"), PathAccess.Read);
                break;
            case "save":
                (path, error) = await WritableDoc(call.Job, Str(a, "path"), saves: true, ct).ConfigureAwait(false);
                break;
            case "close":
                (path, error) = save ? await WritableDoc(call.Job, Str(a, "path"), saves: true, ct).ConfigureAwait(false) : Doc(call.Job, Str(a, "path"), PathAccess.Read);
                break;
            case "save_as":
                (path, error) = Doc(call.Job, Str(a, "path"), PathAccess.Read);
                if (error is null)
                {
                    var v = call.Job.Paths.Check(Str(a, "targetPath") ?? throw new ArgumentException("save_as needs targetPath."), PathAccess.CreateFile);
                    if (!v.Allowed) error = v.Code == "exists" ? $"{v.FullPath} already exists; choose a new name." : v.Message;
                    else if (!IsDocument(v.FullPath)) error = $"targetPath must be a {_app.DisplayName} document ({DocTypes}).";
                    else target = v.FullPath;
                }
                break;
            default:
                return ToolResult.Fail("action must be open, save, save_as or close.");
        }
        if (error is not null) return ToolResult.Fail(error);
        return Report(await _app.DocumentAsync(new CadDocumentRequest(action, path, save, discard, target), ct).ConfigureAwait(false));
    }

    private async Task<ToolResult> ExportAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        var folder = OutputFolder(call.Job.Paths, Str(a, "outputFolder"));
        if (!folder.Allowed) return ToolResult.Fail(folder.Message);
        var formats = Strings(a, "formats");
        if (formats.Count == 0) return ToolResult.Fail("formats is required.");
        if (formats.FirstOrDefault(f => !_app.ExportFormats.Any(x => x.Name == f)) is { } bad)
            return ToolResult.Fail($"Unknown format {bad} ({string.Join(", ", _app.ExportFormats.Select(f => f.Name))}).");
        var raw = Strings(a, "paths");
        if (raw.Count > MaxPaths) return ToolResult.Fail($"More than {MaxPaths} documents; narrow the selection.");
        var paths = new List<string>();
        foreach (var p in raw)
        {
            var (full, error) = Doc(call.Job, p, PathAccess.Read);
            if (error is not null) return ToolResult.Fail(error);
            paths.Add(full!);
        }
        var report = await _app.ExportAsync(paths, formats, folder.FullPath, ct).ConfigureAwait(false);
        return Report(report, report["files"] is not JsonArray { Count: > 0 });
    }

    private async Task<ToolResult> InspectAsync(ToolCallContext call, CancellationToken ct)
    {
        var (path, error) = Doc(call.Job, Str(call.Arguments, "path"), PathAccess.Read);
        if (error is not null) return ToolResult.Fail(error);
        var sections = Strings(call.Arguments, "sections");
        if (sections.FirstOrDefault(s => !InspectSections.Contains(s)) is { } bad) return ToolResult.Fail($"Unknown section {bad} ({string.Join(", ", InspectSections)}).");
        var set = (sections.Count == 0 ? DefaultSections : [.. sections]).ToHashSet(StringComparer.Ordinal);
        return Report(await _app.InspectAsync(path, set, ct).ConfigureAwait(false));
    }

    private async Task<ToolResult> EditAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        var (ops, opError) = ParseOps(a);
        if (opError is not null) return ToolResult.Fail(opError);
        bool save = Bool(a, "save");
        var (path, error) = await WritableDoc(call.Job, Str(a, "path"), save, ct).ConfigureAwait(false);
        if (error is not null) return ToolResult.Fail(error);
        // Honour a partial approval: only the operations the user kept run.
        var kept = call.ApprovedRows is { } keep ? ops.Where(o => keep.Contains(o.Key)).ToList() : ops;
        if (kept.Count == 0) return ToolResult.Fail("The user unticked every operation; nothing was changed.");
        var report = await _app.EditAsync(path, new JsonArray(kept.Select(o => (JsonNode)o.Op.DeepClone()).ToArray()), save, ct).ConfigureAwait(false);
        if (kept.Count < ops.Count) report["excludedByUser"] = $"{ops.Count - kept.Count} operation(s) were unticked by the user and not run; do not redo them.";
        bool anyOk = report["results"] is JsonArray results && results.OfType<JsonObject>().Any(r => (bool?)r["ok"] == true);
        return Report(report, !anyOk);
    }

    private async Task<ToolResult> DrawingAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        string drawingExt = _app.DrawingExtension ?? throw new CadAppException("This program has no drawings.");
        var (model, error) = Doc(call.Job, Str(a, "modelPath"), PathAccess.Read);
        if (error is not null) return ToolResult.Fail(error);
        if (model is not null && !_app.ModelExtensions.Contains(Path.GetExtension(model).ToLowerInvariant()))
            return ToolResult.Fail("modelPath must be a part or assembly.");
        var output = call.Job.Paths.Check(Str(a, "outputPath"), PathAccess.CreateFile);
        if (!output.Allowed) return ToolResult.Fail(output.Code == "exists" ? $"{output.FullPath} already exists; choose a new name." : output.Message);
        if (!output.FullPath.EndsWith(drawingExt, StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail($"outputPath must end in {drawingExt.ToUpperInvariant()}.");
        if (Bool(a, "exportPdf") && File.Exists(Path.ChangeExtension(output.FullPath, ".pdf")))
            return ToolResult.Fail($"{Path.ChangeExtension(output.FullPath, ".pdf")} already exists; choose another drawing name or leave exportPdf off.");
        string sheet = Str(a, "sheet") ?? "A3";
        if (!Sheets.Contains(sheet)) return ToolResult.Fail($"sheet must be one of {string.Join(", ", Sheets)}.");
        var request = new CadDrawingRequest(model, output.FullPath, sheet, Str(a, "projection") == "first", Bool(a, "isometric", true), Bool(a, "dimensions", true), Bool(a, "exportPdf"));
        var report = await _app.CreateDrawingAsync(request, ct).ConfigureAwait(false);
        return Report(report, report["saved"] is not JsonValue);
    }

    private async Task<ToolResult> ViewsAsync(ToolCallContext call, CancellationToken ct)
    {
        var (path, error) = Doc(call.Job, Str(call.Arguments, "path"), PathAccess.Read);
        if (error is not null) return ToolResult.Fail(error);
        var views = Strings(call.Arguments, "views");
        if (views.FirstOrDefault(v => !ViewNames.Contains(v)) is { } bad) return ToolResult.Fail($"Unknown view {bad} ({string.Join(", ", ViewNames)}).");
        if (views.Count == 0) views = ["current", "iso"];
        string folder = Path.Combine(_viewsRoot, call.Job.JobId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var report = await _app.CaptureViewsAsync(path, views.Distinct(StringComparer.Ordinal).ToList(), folder, ct).ConfigureAwait(false);
            var images = LoadImages(report["images"] as JsonArray, folder);
            report.Remove("images");
            report["pictures"] = images.Count;
            return new ToolResult(images.Count == 0, report.ToJsonString(), report) { Images = images };
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The pictures the app saved inside <paramref name="folder"/> (others are ignored), as images for the model.</summary>
    public static IReadOnlyList<ToolImage> LoadImages(JsonArray? paths, string folder)
    {
        var images = new List<ToolImage>();
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var node in paths ?? [])
        {
            if ((string?)node is not { } p) continue;
            string full = Path.GetFullPath(p);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
            try
            {
                if (new FileInfo(full).Length > MaxViewBytes) continue;
                string type = Path.GetExtension(full).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
                images.Add(new ToolImage(type, File.ReadAllBytes(full)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return images;
    }

    /// <summary>
    /// The folder outputs are written INTO. An existing folder (an allowed folder itself, or one below it) is used as it is,
    /// never recreated or replaced; a missing one below an allowed folder may be created.
    /// </summary>
    public static PathVerdict OutputFolder(PathAuthorizer paths, string? folder)
    {
        var listed = paths.Check(folder, PathAccess.List);
        if (listed.Allowed || listed.Code != "not_found") return listed;
        return paths.Check(folder, PathAccess.CreateDirectory);
    }
}
