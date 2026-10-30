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
        ["features", "dimensions", "equations", "parameters", "properties", "mass", "configurations", "selection", "components", "mates", "interference",
         "bodies", "layers", "blocks", "entities"];
    public static readonly string[] DefaultSections = ["features", "equations", "parameters", "properties", "mass", "configurations", "components", "mates"];

    public static readonly string[] Sheets = ["A4", "A3", "A2", "A1", "A0", "A", "B", "C", "D", "E"];
    public static readonly string[] ViewNames = ["current", "iso", "front", "back", "left", "right", "top", "bottom", "trimetric"];
    public const int MaxViewBytes = 3_000_000;
    private const int MaxPaths = 200, MaxOps = 50, MaxScriptChars = 60_000;
    public static readonly TimeSpan ScriptDefaultTimeout = TimeSpan.FromMinutes(5), ScriptMaxTimeout = TimeSpan.FromMinutes(30);

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
                $"Change an existing {n} document (path, or the active one), in order, then rebuild. ops: " + OpsHelp(app, CadCapability.Edit) +
                (app.Ops(CadCapability.Edit).Contains("set_material") ? $" Material examples: {string.Join(", ", app.MaterialExamples.Select(m => "\"" + m + "\""))}." : "") +
                " save: false (default) leaves the changes open and unsaved for the user to review (undo reverts them); true saves the file. The user " +
                "approves the list and may untick operations. Returns each operation's result and the rebuilt state: check it.",
                OpsSchema(app, CadCapability.Edit), ToolRisk.Write, TimeSpan.FromMinutes(6));

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

        if (app.Capabilities.Contains(CadCapability.Sketch))
            Add(CadCapability.Sketch,
                $"Draw ONE sketch in a {n} {(app.Capabilities.Contains(CadCapability.Feature) ? "part (path, or the active document), for model_features to extrude, cut, revolve, sweep or loft by its name" : "document (path, or the active one)")}. " +
                CadOpCatalog.SketchEntities + ". Returns the sketch's name and whether it is fully defined. save: false (default) leaves it unsaved.",
                """
                {
                  "type": "object",
                  "required": ["on", "entities"],
                  "properties": {
                    "path": { "type": "string" }, "save": { "type": "boolean" }, "name": { "type": "string" }, "on": {},
                    "entities": { "type": "array", "minItems": 1, "items": { "type": "object", "required": ["type"] } },
                    "constraints": { "type": "array", "items": { "type": "object" } },
                    "dimensions": { "type": "array", "items": { "type": "object" } }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.Write, TimeSpan.FromMinutes(5));

        foreach (var (cap, what, minutes) in new[]
                 {
                     (CadCapability.Feature, $"Model a {n} part (path, or the active document) with features, in order: ", 8),
                     (CadCapability.Assembly, $"Build or change a {n} assembly (path, or the active document), in order: ", 8),
                     (CadCapability.Annotate, $"Annotate a {n} drawing (path, or the active document), in order: ", 6),
                     (CadCapability.Simulate, $"Run a {n} structural analysis on a part (path, or the active document), in order: ", 20),
                 })
            if (app.Capabilities.Contains(cap))
                Add(cap, what + OpsHelp(app, cap) + " Units: mm, degrees, N, MPa. Each operation is a row the user may untick; one that fails is " +
                    "reported and the rest still run. save: false (default) leaves the changes unsaved for review. Look at the result with capture_views.",
                    OpsSchema(app, cap), ToolRisk.Write, TimeSpan.FromMinutes(minutes));

        if (app.Capabilities.Contains(CadCapability.Script) && app.Script is { } script)
            Add(CadCapability.Script,
                $"Run a {script.Language} script against {n}'s own automation API: anything the user can do in {n} that the other {p}_ tools do not cover " +
                "(other feature types, simulation and CAM setup, batch work, custom checks). The user reads and approves the code every time. " +
                script.Guide + " path: the document the script works on (its folder must be shared with the job). timeoutSeconds: default 300, up to 1800. " +
                "Prefer the other tools when they can do the job.",
                """
                {
                  "type": "object",
                  "required": ["code"],
                  "properties": {
                    "code": { "type": "string" }, "path": { "type": "string" }, "purpose": { "type": "string" },
                    "timeoutSeconds": { "type": "integer" }
                  },
                  "additionalProperties": false
                }
                """, ToolRisk.Destructive, ScriptMaxTimeout + TimeSpan.FromMinutes(1));

        return list;
    }

    /// <summary>"{op:"extrude", sketch, depth}; ...": the operations this program supports, with their fields.</summary>
    private static string OpsHelp(ICadApp app, CadCapability cap) =>
        string.Join("; ", app.Ops(cap).Select(name => CadOpCatalog.Find(cap, name)).OfType<CadOp>()
            .Select(o => o.Help.StartsWith("{}", StringComparison.Ordinal) ? $"{{op:\"{o.Name}\"}}{o.Help[2..]}"
                : o.Help.StartsWith('{') ? $"{{op:\"{o.Name}\", {o.Help[1..]}" : $"{{op:\"{o.Name}\"}} {o.Help}")) + ".";

    /// <summary>The schema of an ops tool: path, save, and ops whose "op" is one of the program's operations; other fields are free.</summary>
    private static string OpsSchema(ICadApp app, CadCapability cap) => $$"""
        {
          "type": "object",
          "required": ["ops"],
          "properties": {
            "path": { "type": "string" },
            "save": { "type": "boolean" },
            "ops": { "type": "array", "minItems": 1, "items": { "type": "object", "required": ["op"], "properties": {
              "op": { "type": "string", "enum": [{{Enum(app.Ops(cap))}}] } } } }
          },
          "additionalProperties": false
        }
        """;

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
        if (!_app.HasLiveSession) return (null, $"{_app.DisplayName} works on files here: give the path of the document.");
        string? active = await _app.ActiveDocumentPathAsync(ct).ConfigureAwait(false);
        if (active is null)
            return saves ? (null, $"The active {_app.DisplayName} document was never saved: save it first with {Name(CadCapability.Documents)} save_as.") : (null, null);
        var (_, error) = Doc(job, active, PathAccess.Modify);
        return error is null ? (null, null) : (null, $"The active document {Path.GetFileName(active)} is outside the folders this job may change ({error}). Ask the user to pick its folder.");
    }

    /// <summary>The operations, checked: one this program supports, with its required fields. Index keys "1".."n" are the approval rows.</summary>
    public (List<(string Key, JsonObject Op)> Ops, string? Error) ParseOps(JsonElement a, CadCapability cap = CadCapability.Edit)
    {
        var supported = _app.Ops(cap);
        if (!a.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() == 0) return ([], "ops must be a non-empty array.");
        if (ops.GetArrayLength() > MaxOps) return ([], $"At most {MaxOps} operations per call.");
        var list = new List<(string, JsonObject)>();
        int i = 0;
        foreach (var e in ops.EnumerateArray())
        {
            i++;
            if (JsonNode.Parse(e.GetRawText()) is not JsonObject o) return ([], $"Operation {i} must be an object.");
            string op = (string?)o["op"] ?? "";
            if (!supported.Contains(op) || CadOpCatalog.Find(cap, op) is not { } def)
                return ([], $"Operation {i}: '{op}' is not something {_app.DisplayName} can do here ({string.Join(", ", supported)}).");
            if (def.Required.FirstOrDefault(r => o[r] is null) is { } missing) return ([], $"Operation {i} ({op}) needs '{missing}'.");
            if (op is "set_dimension" or "set_global" or "add_global" && !(o["value"] is JsonValue value && value.TryGetValue(out double _)))
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
        _ => Summarize(o),
    };

    /// <summary>"hole · size M6 · points [[10,10]]": an operation's fields in one line, for its approval row.</summary>
    private static string Summarize(JsonObject o)
    {
        var parts = new List<string> { (string?)o["op"] ?? "" };
        foreach (var (k, v) in o)
        {
            if (k == "op" || v is null || parts.Count >= 6) continue;
            string text = v is JsonValue ? v.ToString() : v.ToJsonString();
            parts.Add($"{k} {(text.Length > 60 ? text[..60] + "…" : text)}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Files named inside operations (assembly insert/replace, a block to insert) must be readable inside the job's folders;
    /// each "path" is replaced by its checked full path. Returns the refusal, or null.
    /// </summary>
    private static string? AuthorizeOpPaths(JobGrant job, IEnumerable<JsonObject> ops)
    {
        foreach (var o in ops)
        {
            if ((string?)o["path"] is not { } raw) continue;
            var v = job.Paths.Check(raw, PathAccess.Read);
            if (!v.Allowed) return v.Message;
            if (!File.Exists(v.FullPath)) return $"{v.FullPath} does not exist.";
            o["path"] = v.FullPath;
        }
        return null;
    }

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
            CadCapability.Edit => OpsPreview(a, CadCapability.Edit, $"Change {target}", Card),
            CadCapability.Feature => OpsPreview(a, CadCapability.Feature, $"Model {target}", Card),
            CadCapability.Assembly => OpsPreview(a, CadCapability.Assembly, $"Change assembly {target}", Card),
            CadCapability.Annotate => OpsPreview(a, CadCapability.Annotate, $"Annotate drawing {target}", Card),
            CadCapability.Simulate => OpsPreview(a, CadCapability.Simulate, $"Analyse {target}", Card),
            CadCapability.Sketch => Card($"Sketch in {target}", $"{(a.TryGetProperty("entities", out var en) && en.ValueKind == JsonValueKind.Array ? en.GetArrayLength() : 0)} entities on {(a.TryGetProperty("on", out var on) ? on.GetRawText() : "?")}" +
                (Bool(a, "save") ? ". The file is SAVED." : ". Left unsaved."), false, []),
            CadCapability.Script => Card($"Run a {_app.Script?.Language ?? "script"} script in {app}" + (Str(a, "path") is { } sp ? $" on {Path.GetFileName(sp)}" : ""),
                (Str(a, "purpose") is { Length: > 0 } purpose ? purpose + " · " : "") + "It can do anything the program's API allows. Read the code before approving.",
                false, [("code", Str(a, "code") ?? "", "")]),
            CadCapability.Drawing => Card($"New {app} drawing", $"{(Str(a, "modelPath") is { } m ? Path.GetFileName(m) : "the active model")} → {Str(a, "outputPath")}", false,
                [("sheet", $"Sheet {Str(a, "sheet") ?? "A3"}, {(Str(a, "projection") == "first" ? "first" : "third")}-angle", (Bool(a, "isometric", true) ? "3 views + isometric" : "3 views") +
                    (Bool(a, "dimensions", true) ? ", model dimensions" : "") + (Bool(a, "exportPdf") ? ", PDF" : ""))]),
            _ => null,
        };
        return Task.FromResult(preview);
    }

    private JsonObject OpsPreview(JsonElement a, CadCapability cap, string title, Func<string, string, bool, IEnumerable<(string, string, string)>, JsonObject> card)
    {
        var (ops, error) = ParseOps(a, cap);
        bool save = Bool(a, "save");
        return card(title, error ?? $"{ops.Count} operation(s). " + (save ? "The file is SAVED." : "Left unsaved for you to review (undo reverts them)."),
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
                CadCapability.Sketch => await SketchAsync(call, ct).ConfigureAwait(false),
                CadCapability.Feature or CadCapability.Assembly or CadCapability.Annotate or CadCapability.Simulate => await OpsAsync(call, capability, ct).ConfigureAwait(false),
                CadCapability.Script => await ScriptAsync(call, ct).ConfigureAwait(false),
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

    private async Task<ToolResult> OpsAsync(ToolCallContext call, CadCapability cap, CancellationToken ct)
    {
        var a = call.Arguments;
        var (ops, opError) = ParseOps(a, cap);
        if (opError is not null) return ToolResult.Fail(opError);
        bool save = Bool(a, "save");
        var (path, error) = await WritableDoc(call.Job, Str(a, "path"), save, ct).ConfigureAwait(false);
        if (error is not null) return ToolResult.Fail(error);
        var kept = call.ApprovedRows is { } keep ? ops.Where(o => keep.Contains(o.Key)).ToList() : ops;
        if (kept.Count == 0) return ToolResult.Fail("The user unticked every operation; nothing was changed.");
        if (AuthorizeOpPaths(call.Job, kept.Select(o => o.Op)) is { } refused) return ToolResult.Fail(refused);
        var report = await _app.RunOpsAsync(cap, path, new JsonArray(kept.Select(o => (JsonNode)o.Op.DeepClone()).ToArray()), save, ct).ConfigureAwait(false);
        if (kept.Count < ops.Count) report["excludedByUser"] = $"{ops.Count - kept.Count} operation(s) were unticked by the user and not run; do not redo them.";
        bool anyOk = report["results"] is JsonArray results && results.OfType<JsonObject>().Any(r => (bool?)r["ok"] == true);
        return Report(report, !anyOk);
    }

    private async Task<ToolResult> SketchAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        if (!a.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array || entities.GetArrayLength() == 0)
            return ToolResult.Fail("entities must be a non-empty array.");
        if (entities.GetArrayLength() > 400) return ToolResult.Fail("At most 400 entities per sketch.");
        bool save = Bool(a, "save");
        var (path, error) = await WritableDoc(call.Job, Str(a, "path"), save, ct).ConfigureAwait(false);
        if (error is not null) return ToolResult.Fail(error);
        var sketch = JsonNode.Parse(a.GetRawText())!.AsObject();
        sketch.Remove("path");
        sketch.Remove("save");
        return Report(await _app.SketchAsync(path, sketch, save, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The escape hatch: the program's own automation language. Destructive (always asks, never auto-approved), the user sees
    /// the code; the target document must be inside the job's folders; output and errors come back to the model.
    /// </summary>
    private async Task<ToolResult> ScriptAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        string code = Str(a, "code") ?? "";
        if (code.Trim().Length == 0) return ToolResult.Fail("code is empty.");
        if (code.Length > MaxScriptChars) return ToolResult.Fail($"The script is over {MaxScriptChars:N0} characters; split it.");
        // The approved code is what runs: a preview that differs (the call changed after approval) is refused.
        if (call.ApprovedPreview?["rows"] is JsonArray rows && rows.FirstOrDefault()?["primary"] is JsonValue shown && (string?)shown != code)
            return ToolResult.Fail("The script differs from the one the user approved; nothing ran.");
        var (path, error) = Str(a, "path") is null ? (null, null) : await WritableDoc(call.Job, Str(a, "path"), saves: false, ct).ConfigureAwait(false);
        if (error is not null) return ToolResult.Fail(error);
        var timeout = a.TryGetProperty("timeoutSeconds", out var t) && t.TryGetInt32(out int secs)
            ? TimeSpan.FromSeconds(Math.Clamp(secs, 5, (int)ScriptMaxTimeout.TotalSeconds)) : ScriptDefaultTimeout;
        var report = await _app.RunScriptAsync(code, path, timeout, ct).ConfigureAwait(false);
        return Report(report, report["error"] is not null);
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
