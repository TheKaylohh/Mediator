using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Security;
using Cockpit.Core.Swx;
using Cockpit.Core.Tools;

namespace Cockpit.Core.CadCode;

/// <summary>One numeric template parameter in millimetres (counts are whole numbers).</summary>
public sealed record CadParam(string Name, double Default, double Min, double Max, bool Integer = false);

/// <summary>
/// Parametric parts as code (build123d on OpenCascade), exported to STEP for SolidWorks (swx_import_step). Built-in
/// templates only take numbers, so the model never writes code for them; a custom build123d script is a separate,
/// Destructive tool that always asks and shows the code. Each part's bounding box is checked against its declared
/// envelope before it is exported, and nothing is overwritten. Recommended: run the generator in the restricted WSL
/// distro (a <see cref="WslPythonRunner"/>), where it cannot see your Windows files.
/// </summary>
public sealed class CodeCadToolProvider(IPythonRunner python) : IToolProvider
{
    public const string GenerateTool = "cad_generate_parametric_step";
    public const string CustomTool = "cad_generate_custom_step";
    private const int MaxParts = 40;

    public static readonly IReadOnlyDictionary<string, CadParam[]> Templates = new Dictionary<string, CadParam[]>(StringComparer.Ordinal)
    {
        ["shaft"] = [new("diameter", 25, 2, 300), new("length", 200, 5, 2000)],
        ["strut_tube"] = [new("outer_diameter", 50, 10, 200), new("wall", 3, 1, 20), new("length", 400, 50, 1500)],
        ["wheel_hub"] = [new("flange_diameter", 140, 60, 400), new("flange_thickness", 10, 3, 40), new("hub_diameter", 70, 30, 200), new("hub_length", 45, 10, 150),
            new("bore_diameter", 30, 5, 150), new("bolt_count", 5, 3, 10, true), new("bolt_circle_diameter", 112, 40, 300), new("bolt_diameter", 14, 6, 20)],
        ["brake_disc"] = [new("outer_diameter", 288, 150, 450), new("thickness", 25, 5, 40), new("friction_inner_diameter", 160, 80, 400),
            new("hat_diameter", 165, 60, 250), new("hat_height", 40, 10, 80), new("bore_diameter", 65, 20, 150),
            new("bolt_count", 5, 3, 10, true), new("bolt_circle_diameter", 112, 40, 300), new("bolt_diameter", 14, 6, 20)],
        ["coil_spring"] = [new("mean_diameter", 110, 20, 300), new("wire_diameter", 12, 2, 30), new("free_length", 320, 30, 800), new("active_turns", 7, 2, 20)],
        ["control_arm"] = [new("length", 380, 100, 800), new("width", 260, 50, 500), new("thickness", 12, 4, 40),
            new("bushing_outer_diameter", 55, 20, 120), new("bushing_inner_diameter", 14, 5, 80), new("ball_joint_diameter", 40, 10, 80)],
        ["flange_bracket"] = [new("width", 80, 10, 400), new("depth", 60, 10, 400), new("height", 70, 10, 400), new("thickness", 6, 2, 30), new("hole_diameter", 9, 2, 40)],
    };

    public sealed record PartRequest(string Key, string Template, IReadOnlyDictionary<string, double> Params, double[] EnvelopeMm, string? Script);

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(GenerateTool,
            "Generate parametric mechanical parts with build123d and export each as STEP into outputFolder (new files only). " +
            "Templates (millimetres): " + string.Join("; ", Templates.Select(t => $"{t.Key}({string.Join(", ", t.Value.Select(p => p.Name))})")) + ". " +
            "Each part needs envelopeMm [x, y, z]: the generator fails a part whose bounding box does not fit. The user ticks which parts to make. " +
            "Then import them with swx_import_step and assemble with swx_part_cards / swx_build_assembly.",
            Schema("""
            {
              "type": "object",
              "required": ["outputFolder", "parts"],
              "properties": {
                "outputFolder": { "type": "string" },
                "parts": { "type": "array", "items": { "type": "object", "required": ["key", "template", "envelopeMm"],
                  "properties": { "key": { "type": "string" }, "template": { "type": "string" },
                                  "params": { "type": "object" }, "envelopeMm": { "type": "array", "items": { "type": "number" } } } } }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromMinutes(15)),

        new(CustomTool,
            "Run a custom build123d script for one part the templates cannot express. The script must assign the final solid to " +
            "a variable named result (millimetres). It executes as Python on this machine, so the user always reviews the code first.",
            Schema("""
            {
              "type": "object",
              "required": ["outputFolder", "key", "script", "envelopeMm"],
              "properties": { "outputFolder": { "type": "string" }, "key": { "type": "string" }, "script": { "type": "string" },
                              "envelopeMm": { "type": "array", "items": { "type": "number" } } },
              "additionalProperties": false
            }
            """), ToolRisk.Destructive, null, TimeSpan.FromMinutes(15)),
    ];

    /// <summary>Parses and validates parts; throws <see cref="FormatException"/> with a model-readable reason.</summary>
    public static List<PartRequest> ParseParts(JsonElement args)
    {
        var parts = new List<PartRequest>();
        if (args.TryGetProperty("script", out var script))
        {
            parts.Add(new PartRequest(Key(args), "custom", new Dictionary<string, double>(), Envelope(args), script.GetString() ?? ""));
            if (parts[0].Script!.Length > 20_000) throw new FormatException("The script is limited to 20 000 characters.");
            return parts;
        }
        foreach (var p in args.GetProperty("parts").EnumerateArray())
        {
            string key = Key(p), template = p.GetProperty("template").GetString() ?? "";
            if (!Templates.TryGetValue(template, out var specs)) throw new FormatException($"{key}: unknown template '{template}'. Use one of: {string.Join(", ", Templates.Keys)}.");
            var values = specs.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);
            if (p.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var prop in ps.EnumerateObject())
                {
                    var spec = specs.FirstOrDefault(s => s.Name == prop.Name) ?? throw new FormatException($"{key}: {template} has no parameter '{prop.Name}' (it has {string.Join(", ", specs.Select(s => s.Name))}).");
                    double v = prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetDouble() : throw new FormatException($"{key}.{prop.Name} must be a number.");
                    if (v < spec.Min || v > spec.Max || (spec.Integer && v != Math.Floor(v)))
                        throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{key}.{prop.Name} = {v} is outside {spec.Min}–{spec.Max}{(spec.Integer ? " (whole number)" : "")}."));
                    values[prop.Name] = v;
                }
            CheckGeometry(key, template, values);
            parts.Add(new PartRequest(key, template, values, Envelope(p), null));
        }
        if (parts.Count is 0 or > MaxParts) throw new FormatException($"Give 1–{MaxParts} parts.");
        if (parts.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != parts.Count) throw new FormatException("Part keys must be unique.");
        return parts;
    }

    private static string Key(JsonElement e)
    {
        string key = e.GetProperty("key").GetString() ?? "";
        if (key.Length is 0 or > 40 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new FormatException($"Part key '{key}' must be 1–40 letters, digits, '_' or '-' (it becomes the file name).");
        return key;
    }

    private static double[] Envelope(JsonElement e)
    {
        var env = e.GetProperty("envelopeMm").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (env.Length != 3 || env.Any(v => v <= 0 || v > 5000)) throw new FormatException("envelopeMm is [x, y, z] in millimetres, each 0–5000.");
        return env;
    }

    /// <summary>Relations between parameters that would otherwise produce broken or self-intersecting solids.</summary>
    private static void CheckGeometry(string key, string template, IReadOnlyDictionary<string, double> p)
    {
        void Require(bool ok, string why) { if (!ok) throw new FormatException($"{key}: {why}"); }
        switch (template)
        {
            case "strut_tube": Require(p["wall"] * 2 < p["outer_diameter"], "wall must be less than half the outer diameter."); break;
            case "wheel_hub":
                Require(p["bore_diameter"] < p["hub_diameter"] - 4, "bore must be smaller than the hub (≥2 mm wall).");
                Require(p["hub_diameter"] < p["flange_diameter"], "hub must be smaller than the flange.");
                Require(p["bolt_circle_diameter"] - p["bolt_diameter"] > p["hub_diameter"] && p["bolt_circle_diameter"] + p["bolt_diameter"] < p["flange_diameter"],
                    "bolt holes must sit on the flange, between the hub and the flange edge.");
                break;
            case "brake_disc":
                Require(p["friction_inner_diameter"] < p["outer_diameter"] - 20, "friction ring needs ≥10 mm radial width.");
                Require(p["hat_diameter"] >= p["friction_inner_diameter"], "hat_diameter must be at least friction_inner_diameter so the hat joins the ring.");
                Require(p["hat_diameter"] < p["outer_diameter"], "hat must be smaller than the disc.");
                Require(p["bore_diameter"] < p["bolt_circle_diameter"] - p["bolt_diameter"] && p["bolt_circle_diameter"] + p["bolt_diameter"] < p["hat_diameter"] - 12,
                    "bolt holes must sit on the hat face, between the bore and the hat wall.");
                break;
            case "coil_spring":
                Require(p["wire_diameter"] * 1.1 < p["free_length"] / p["active_turns"], "pitch (free_length / active_turns) must exceed the wire diameter, or the coils overlap.");
                Require(p["wire_diameter"] < p["mean_diameter"] / 2, "wire too thick for the coil diameter.");
                break;
            case "control_arm": Require(p["bushing_inner_diameter"] < p["bushing_outer_diameter"] - 4, "bushing inner must be smaller than outer."); break;
            case "flange_bracket": Require(p["hole_diameter"] < Math.Min(p["width"], p["depth"]) - 2 * p["thickness"], "hole too large for the bracket."); break;
        }
    }

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        JsonObject preview;
        try
        {
            var parts = ParseParts(call.Arguments);
            bool custom = call.Tool.Name == CustomTool;
            preview = new JsonObject
            {
                ["kind"] = "cad_parts",
                ["title"] = custom ? $"Run a custom build123d script ({parts[0].Key})" : "Generate parametric STEP parts",
                ["summary"] = (custom ? "This executes model-written Python on this machine. Read it before approving." : $"{parts.Count} part(s)")
                    + $" → {call.Arguments.GetProperty("outputFolder").GetString()} · runner: {python.Describe}",
                ["selectable"] = !custom,
                ["rows"] = custom
                    ? new JsonArray(parts[0].Script!.Split('\n').Take(60).Select((l, i) => (JsonNode)new JsonObject { ["key"] = $"line{i + 1}", ["primary"] = l.TrimEnd() }).ToArray())
                    : new JsonArray(parts.Select(p => (JsonNode)new JsonObject
                    {
                        ["key"] = p.Key,
                        ["primary"] = $"{p.Key}: {p.Template}",
                        ["detail"] = string.Join(", ", p.Params.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value:0.##}")))
                            + string.Create(CultureInfo.InvariantCulture, $" · envelope {p.EnvelopeMm[0]:0.#}×{p.EnvelopeMm[1]:0.#}×{p.EnvelopeMm[2]:0.#} mm"),
                    }).ToArray()),
            };
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            preview = new JsonObject { ["kind"] = "cad_parts", ["title"] = "Invalid part list", ["summary"] = ex.Message, ["selectable"] = false, ["rows"] = new JsonArray() };
        }
        return Task.FromResult<JsonNode?>(preview);
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        List<PartRequest> parts;
        try { parts = ParseParts(call.Arguments); }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException) { return ToolResult.Fail(ex.Message); }
        int proposed = parts.Count;
        if (call.Tool.Name == GenerateTool && call.ApprovedRows is { } keep) parts = parts.Where(p => keep.Contains(p.Key)).ToList();
        if (parts.Count == 0) return ToolResult.Fail("No parts were approved.");

        var folderVerdict = call.Job.Paths.Check(call.Arguments.GetProperty("outputFolder").GetString(), PathAccess.List);
        if (!folderVerdict.Allowed) return ToolResult.Fail(folderVerdict.Message);
        string folder = folderVerdict.FullPath;
        if (!python.IsAvailable) return ToolResult.Fail($"Cannot run build123d: {python.Describe}. Install it with: pip install build123d");
        if (python.MapPath(folder) is not string runnerFolder) return ToolResult.Fail($"{folder} is not visible to {python.Describe}; use a folder under the WSL exchange folder.");

        // Host and runner paths for every output; unique names so nothing is overwritten.
        var outputs = parts.ToDictionary(p => p.Key, p => SafeFiles.Unique(folder, p.Key + ".step"));
        string script = SafeFiles.Unique(folder, "generate_parts.py");
        var config = new JsonObject
        {
            ["parts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p.Key, ["template"] = p.Template,
                ["params"] = new JsonObject(p.Params.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                ["envelope"] = new JsonArray(p.EnvelopeMm.Select(v => (JsonNode)v).ToArray()),
                ["step"] = python.MapPath(outputs[p.Key]),
                ["script"] = p.Script,
            }).ToArray()),
        };
        call.Job.Paths.WriteNewText(script, Build123dScript.Render(config));

        var run = await python.RunScriptAsync(script, [], null, call.Tool.ExecutionTimeout, ct).ConfigureAwait(false);
        JsonNode? report = null;
        foreach (var line in run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try { report = JsonNode.Parse(line); break; } catch (JsonException) { }
        }
        if (report?["results"] is not JsonArray results)
        {
            string tail = run.Tail(800);
            return ToolResult.Fail(tail.Contains("No module named 'build123d'", StringComparison.Ordinal)
                ? $"build123d is not installed for {python.Describe}. Install it with: pip install build123d"
                : $"The generator failed: {tail}");
        }

        var made = new JsonArray();
        var failed = new JsonArray();
        foreach (var r in results.OfType<JsonObject>())
        {
            string key = (string)r["key"]!;
            // The generator wrote this file: re-authorize it (a link or a file outside the root is not accepted as output).
            var produced = call.Job.Paths.Check(outputs[key], PathAccess.Read);
            if ((bool?)r["ok"] == true && produced.Allowed)
                made.Add(new JsonObject { ["key"] = key, ["step"] = outputs[key], ["bboxMm"] = r["bbox"]?.DeepClone(), ["volumeMm3"] = r["volume"]?.DeepClone() });
            else
                failed.Add(new JsonObject { ["key"] = key, ["error"] = (string?)r["error"] ?? (produced.Code == "not_found" ? "no file written" : produced.Message) });
        }
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Generated {made.Count} of {parts.Count} part(s) in {folder}."));
        if (parts.Count < proposed) sb.Append(CultureInfo.InvariantCulture, $" {proposed - parts.Count} part(s) were excluded by the user; do not generate them.");
        foreach (var f in failed) sb.Append(CultureInfo.InvariantCulture, $" {f!["key"]}: {f["error"]}.");
        if (made.Count > 0) sb.Append(" Next: swx_import_step with these STEP files, then swx_part_cards and swx_build_assembly.");
        var structured = new JsonObject { ["parts"] = made, ["failed"] = failed, ["script"] = script, ["runner"] = python.Describe };
        return made.Count == 0 ? new ToolResult(true, sb.ToString(), structured) : ToolResult.Ok(sb.ToString(), structured);
    }
}

/// <summary>The build123d generator script: a fixed template library plus the approved part list as JSON.</summary>
public static class Build123dScript
{
    public static string Render(JsonObject config) =>
        Template.Replace("__CONFIG_JSON__", JsonSerializer.Serialize(config.ToJsonString()), StringComparison.Ordinal);

    private const string Template = """
# Generated by AI Cockpit (cad_generate_parametric_step). Requires: pip install build123d
import json, os, sys, traceback
CONFIG = json.loads(__CONFIG_JSON__)
try:
    from build123d import (Align, Box, Circle, Cylinder, Helix, Plane, PolarLocations, Polygon, Pos, Rot,
                           export_step, extrude, offset, sweep)
except ImportError as e:
    print(json.dumps({"results": None, "error": "No module named 'build123d' (" + str(e) + ")"}))
    sys.exit(4)

BASE = (Align.CENTER, Align.CENTER, Align.MIN)

def shaft(p):
    return Cylinder(p["diameter"] / 2, p["length"], align=BASE)

def strut_tube(p):
    r, L = p["outer_diameter"] / 2, p["length"]
    return Cylinder(r, L, align=BASE) - Cylinder(r - p["wall"], L, align=BASE)

def bolt_holes(part, count, circle_d, bolt_d, height, z=0.0):
    for loc in PolarLocations(circle_d / 2, int(count)).locations:
        part -= Pos(0, 0, z) * loc * Cylinder(bolt_d / 2, height, align=BASE)
    return part

def wheel_hub(p):
    ft, hl = p["flange_thickness"], p["hub_length"]
    part = Cylinder(p["flange_diameter"] / 2, ft, align=BASE) + Pos(0, 0, ft) * Cylinder(p["hub_diameter"] / 2, hl, align=BASE)
    part -= Cylinder(p["bore_diameter"] / 2, ft + hl, align=BASE)
    return bolt_holes(part, p["bolt_count"], p["bolt_circle_diameter"], p["bolt_diameter"], ft)

def brake_disc(p):
    t, hh, hd = p["thickness"], p["hat_height"], p["hat_diameter"]
    ring = Cylinder(p["outer_diameter"] / 2, t, align=BASE) - Cylinder(p["friction_inner_diameter"] / 2, t, align=BASE)
    top = 8.0
    hat = Cylinder(hd / 2, hh, align=BASE) - Cylinder(hd / 2 - 6.0, hh - top, align=BASE)
    part = ring + hat
    part -= Cylinder(p["bore_diameter"] / 2, hh, align=BASE)
    return bolt_holes(part, p["bolt_count"], p["bolt_circle_diameter"], p["bolt_diameter"], top, z=hh - top)

def coil_spring(p):
    pitch = p["free_length"] / p["active_turns"]
    helix = Helix(pitch=pitch, height=p["free_length"], radius=p["mean_diameter"] / 2)
    profile = Plane(origin=helix @ 0, z_dir=helix % 0) * Circle(p["wire_diameter"] / 2)
    return sweep(profile, path=helix, is_frenet=True)

def control_arm(p):
    L, w, th = p["length"], p["width"], p["thickness"]
    pivots = [(0.0, 0.0), (0.0, w), (L, w / 2)]
    plate = extrude(offset(Polygon(*pivots, align=None), amount=p["bushing_outer_diameter"] / 2), amount=th)
    for x, y in pivots[:2]:
        plate -= Pos(x, y, 0) * Cylinder(p["bushing_inner_diameter"] / 2, th, align=BASE)
    plate -= Pos(L, w / 2, 0) * Cylinder(p["ball_joint_diameter"] / 2, th, align=BASE)
    return plate

def flange_bracket(p):
    w, d, h, t, hd = p["width"], p["depth"], p["height"], p["thickness"], p["hole_diameter"]
    part = Box(w, d, t, align=(Align.CENTER, Align.MIN, Align.MIN)) + Box(w, t, h, align=(Align.CENTER, Align.MIN, Align.MIN))
    part -= Pos(0, t + (d - t) / 2, 0) * Cylinder(hd / 2, t, align=BASE)
    part -= Pos(0, t / 2, t + (h - t) / 2) * Rot(90, 0, 0) * Cylinder(hd / 2, t * 2)
    return part

TEMPLATES = {f.__name__: f for f in (shaft, strut_tube, wheel_hub, brake_disc, coil_spring, control_arm, flange_bracket)}

def custom(source):
    scope = {"__name__": "cockpit_custom"}
    exec("from build123d import *\n" + source, scope)
    if "result" not in scope:
        raise ValueError("the script must assign the final solid to a variable named result")
    return scope["result"]

results = []
for part in CONFIG["parts"]:
    key = part["key"]
    try:
        if os.path.exists(part["step"]):
            raise FileExistsError(part["step"] + " already exists")
        solid = custom(part["script"]) if part["template"] == "custom" else TEMPLATES[part["template"]](part["params"])
        valid = solid.is_valid() if callable(solid.is_valid) else solid.is_valid
        size = solid.bounding_box().size
        bbox = [round(size.X, 3), round(size.Y, 3), round(size.Z, 3)]
        volume = round(solid.volume, 1)
        if not valid or volume <= 0:
            raise ValueError("the solid is not valid (volume %.1f mm3)" % volume)
        env = part["envelope"]
        over = [axis for axis, have, limit in zip("XYZ", bbox, env) if have > limit + 1e-3]
        if over:
            raise ValueError("bounding box %s mm exceeds envelope %s mm on %s" % (bbox, env, ",".join(over)))
        if not export_step(solid, part["step"]):
            raise IOError("export_step failed")
        results.append({"key": key, "ok": True, "bbox": bbox, "volume": volume})
    except Exception as e:
        results.append({"key": key, "ok": False, "error": "%s: %s" % (type(e).__name__, e)})
        traceback.print_exc(file=sys.stderr)
print(json.dumps({"results": results}))
""";
}
