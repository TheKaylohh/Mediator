using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Mediator.Core.Bus;
using Mediator.Core.Isolation;
using Mediator.Core.Security;
using Mediator.Core.Tools;

namespace Mediator.Connectors.PartsFromCode;

/// <summary>One numeric template parameter in millimetres (counts are whole numbers).</summary>
public sealed record CadParam(string Name, double Default, double Min, double Max, bool Integer = false);

/// <summary>
/// Parametric parts as code (build123d on OpenCascade), exported to STEP for SolidWorks (swx_import_step). Built-in
/// templates only take numbers, so the model never writes code for them; a custom build123d script is a separate,
/// Destructive tool that always asks and shows the code. Each part's bounding box is checked against its declared
/// envelope before it is exported, and nothing is overwritten.
/// V6: the generator writes into a private staging folder; outputs are accepted only if they are plain files with the
/// expected names, pass <see cref="StepValidator"/> and a separate re-import (solids, volume, size), and are then
/// copied into the user's folder through the job's path authorizer. Custom (model-written) code runs only in the
/// sandbox, bound to the exact approved script, arguments and policy; see docs/v6/ISOLATION.md.
/// </summary>
public sealed class CodeCadToolProvider(IPythonRunner python, ISandboxRunner sandbox, SandboxPython? sandboxPython, string stagingRoot) : IToolProvider
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
            "a variable named result (millimetres). It runs only inside the sandbox (no network, no access to your files); the user always reviews the code first.",
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

    /// <summary>Said with every generated part, so a fit-check model is never mistaken for a validated production part.</summary>
    public const string FitCheckNotice = "These are fit-check models in millimetres: their solids, volume and bounding box were verified, but not materials, tolerances, loads or manufacturability. Do not use them for production without engineering review.";

    // ── Isolation policy ────────────────────────────────────────────────────────────────────────────

    /// <summary>Policy id recorded in approvals and the audit. Bump it when what the sandbox enforces changes.</summary>
    public const string CustomPolicy = "cad-custom-v1";
    public const long MaxStepBytes = StepValidator.MaxBytes;

    private SandboxLimits LimitsFor(ToolCallContext call) => new() { Wall = call.Tool.ExecutionTimeout < TimeSpan.FromMinutes(10) ? call.Tool.ExecutionTimeout : TimeSpan.FromMinutes(10) };

    private bool SandboxUsable => sandbox.Unavailable is null && sandboxPython is not null;
    private string SandboxProblem => sandbox.Unavailable ?? (sandboxPython is null ? "no Python for the sandbox is configured" : "");

    /// <summary>
    /// What a custom-code approval is bound to: the exact script, the exact arguments, the output folder, the policy,
    /// the sandbox and its limits. Execution recomputes this and refuses if it differs from what the user approved.
    /// </summary>
    private JsonObject Binding(ToolCallContext call, PartRequest part, string folder) => new()
    {
        ["scriptSha256"] = Sha256(part.Script ?? ""),
        ["argumentsSha256"] = Sha256(call.Arguments.GetRawText()),
        ["outputFolder"] = folder,
        ["policy"] = CustomPolicy,
        ["runner"] = sandbox.Id,
        ["limits"] = Describe(LimitsFor(call)),
    };

    private static string Describe(SandboxLimits l) => string.Create(CultureInfo.InvariantCulture,
        $"as={l.AddressSpaceBytes >> 20}MiB cpu={l.CpuSeconds}s nproc={l.Processes} fsize={l.FileSizeBytes >> 20}MiB nofile={l.OpenFiles} wall={l.Wall.TotalSeconds:0}s net=none");

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        JsonObject preview;
        try
        {
            var parts = ParseParts(call.Arguments);
            bool custom = call.Tool.Name == CustomTool;
            var fv = call.Job.Paths.Check(call.Arguments.GetProperty("outputFolder").GetString(), PathAccess.List);
            string where = custom
                ? (SandboxUsable ? $"Runs model-written Python inside the sandbox ({sandbox.Describe}). Read it before approving."
                                 : $"CANNOT RUN: custom code needs the sandbox and none is available ({SandboxProblem}). It will not run on this machine's Python.")
                : $"{parts.Count} part(s) · runner: {(SandboxUsable ? sandbox.Describe : python.Describe)}";
            preview = new JsonObject
            {
                ["kind"] = "cad_parts",
                ["title"] = custom ? $"Run a custom build123d script ({parts[0].Key})" : "Generate parametric STEP parts",
                ["summary"] = where + $" → {(fv.Allowed ? fv.FullPath : fv.Message)}",
                ["selectable"] = !custom,
                ["rows"] = custom
                    ? new JsonArray(parts[0].Script!.Split('\n').Take(400).Select((l, i) => (JsonNode)new JsonObject { ["key"] = $"line{i + 1}", ["primary"] = l.TrimEnd() }).ToArray())
                    : new JsonArray(parts.Select(p => (JsonNode)new JsonObject
                    {
                        ["key"] = p.Key,
                        ["primary"] = $"{p.Key}: {p.Template}",
                        ["detail"] = string.Join(", ", p.Params.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value:0.##}")))
                            + string.Create(CultureInfo.InvariantCulture, $" · envelope {p.EnvelopeMm[0]:0.#}×{p.EnvelopeMm[1]:0.#}×{p.EnvelopeMm[2]:0.#} mm"),
                    }).ToArray()),
            };
            if (custom && fv.Allowed) preview["binding"] = Binding(call, parts[0], fv.FullPath);
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
        bool custom = call.Tool.Name == CustomTool;
        if (call.Tool.Name == GenerateTool && call.ApprovedRows is { } keep) parts = parts.Where(p => keep.Contains(p.Key)).ToList();
        if (parts.Count == 0) return ToolResult.Fail("No parts were approved.");

        var folderVerdict = call.Job.Paths.Check(call.Arguments.GetProperty("outputFolder").GetString(), PathAccess.List);
        if (!folderVerdict.Allowed) return ToolResult.Fail(folderVerdict.Message);
        string folder = folderVerdict.FullPath;

        if (custom)
        {
            // Model-written code: the sandbox is required. There is no fallback to the host interpreter.
            if (!SandboxUsable)
                return ToolResult.Fail($"Refused: custom code runs only inside the sandbox, and none is available ({SandboxProblem}). It was not run.");
            var binding = Binding(call, parts[0], folder);
            if (call.ApprovedPreview?["binding"] is not JsonNode approved || !JsonNode.DeepEquals(approved, binding))
                return ToolResult.Fail("Refused: what would run (script, arguments, folder, sandbox policy or limits) is not exactly what was approved. It was not run.");
        }
        else if (!SandboxUsable && !python.IsAvailable)
            return ToolResult.Fail($"Cannot run build123d: {python.Describe}. Install it with: pip install build123d");

        // Private staging: the generator writes here, never into the user's folder.
        string stage = Path.Combine(stagingRoot, call.Job.JobId, "sandbox", Guid.NewGuid().ToString("N")[..12]);
        string stageIn = Path.Combine(stage, "in"), stageOut = Path.Combine(stage, "out");
        Directory.CreateDirectory(stageIn);
        Directory.CreateDirectory(stageOut);
        try
        {
            bool isolated = SandboxUsable;
            string? StepPath(string key) => isolated ? "/out/" + key + ".step" : python.MapPath(Path.Combine(stageOut, key + ".step"));
            if (!isolated && parts.Any(p => StepPath(p.Key) is null))
                return ToolResult.Fail($"The staging folder {stageOut} is not visible to {python.Describe}.");
            var config = new JsonObject
            {
                ["parts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject
                {
                    ["key"] = p.Key, ["template"] = p.Template,
                    ["params"] = new JsonObject(p.Params.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                    ["envelope"] = new JsonArray(p.EnvelopeMm.Select(v => (JsonNode)v).ToArray()),
                    ["step"] = StepPath(p.Key),
                    ["script"] = p.Script,
                }).ToArray()),
            };
            string scriptText = Build123dScript.Render(config);
            string script = Path.Combine(stageIn, "generate_parts.py");
            await File.WriteAllTextAsync(script, scriptText, ct).ConfigureAwait(false);

            ProcessResult run = isolated
                ? await sandbox.RunAsync(new SandboxRequest(sandboxPython!.Executable, ["-I", "/in/generate_parts.py"], stageIn, stageOut,
                    sandboxPython.Binds, new Dictionary<string, string>(), LimitsFor(call)), ct).ConfigureAwait(false)
                : await python.RunScriptAsync(script, [], null, call.Tool.ExecutionTimeout, ct).ConfigureAwait(false);
            JsonNode? report = LastJson(run.Stdout);
            if (report?["results"] is not JsonArray results)
            {
                string tail = run.TimedOut ? "it was stopped at the time limit" : run.Tail(800);
                return ToolResult.Fail(tail.Contains("No module named 'build123d'", StringComparison.Ordinal)
                    ? $"build123d is not installed for {(isolated ? "the sandbox Python" : python.Describe)}. Install it with: pip install build123d"
                    : $"The generator failed: {tail}");
            }

            // 1. Only plain files with the expected names, under the size limit.
            var expected = parts.Select(p => p.Key + ".step").ToHashSet(StringComparer.Ordinal);
            var (accepted, refused) = SandboxOutputs.Inspect(stageOut, expected.Contains, MaxStepBytes);
            var made = new JsonArray();
            var failed = new JsonArray();
            foreach (var r in refused) failed.Add(new JsonObject { ["key"] = r.Split(':')[0], ["error"] = r });
            var reported = results.OfType<JsonObject>().ToDictionary(r => (string)r["key"]!, r => r, StringComparer.Ordinal);

            // 2. Structural check in Mediator, independent of the generator.
            var structurallyOk = new List<SandboxOutputs.Accepted>();
            foreach (var a in accepted)
            {
                StepValidator.Report sr;
                using (var fs = File.OpenRead(a.StagedPath)) sr = StepValidator.Check(fs);
                if (sr.Ok) structurallyOk.Add(a);
                else failed.Add(new JsonObject { ["key"] = Path.GetFileNameWithoutExtension(a.Name), ["error"] = "STEP check: " + sr.Error });
            }

            // 3. Geometric re-import in a fresh process that sees only the STEP files (sandboxed for model-written code).
            var geometry = structurallyOk.Count == 0 ? new Dictionary<string, JsonObject>() : await ReimportAsync(structurallyOk, stage, isolated, call, ct).ConfigureAwait(false);

            foreach (var part in parts)
            {
                string name = part.Key + ".step";
                if (failed.Any(f => (string?)f!["key"] == part.Key)) continue;
                var staged = structurallyOk.FirstOrDefault(a => a.Name == name);
                reported.TryGetValue(part.Key, out var gen);
                if (staged is null)
                {
                    failed.Add(new JsonObject { ["key"] = part.Key, ["error"] = (string?)gen?["error"] ?? "no file written" });
                    continue;
                }
                if (!geometry.TryGetValue(name, out var geo) || (bool?)geo["ok"] != true)
                {
                    failed.Add(new JsonObject { ["key"] = part.Key, ["error"] = "re-import: " + ((string?)geo?["error"] ?? "the STEP could not be re-read") });
                    continue;
                }
                if (GeometryProblem(part, geo, gen) is string problem)
                {
                    failed.Add(new JsonObject { ["key"] = part.Key, ["error"] = "re-import: " + problem });
                    continue;
                }
                // 4. Into the user's folder: a new file, created through the job's authorizer.
                string dest = SafeFiles.Unique(folder, name);
                await using (var src = File.OpenRead(staged.StagedPath))
                await using (var dst = call.Job.Paths.CreateNew(dest))
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                made.Add(new JsonObject
                {
                    ["key"] = part.Key, ["step"] = dest, ["bboxMm"] = geo["bbox"]?.DeepClone(), ["volumeMm3"] = geo["volume"]?.DeepClone(),
                    ["solids"] = geo["solids"]?.DeepClone(),
                });
            }

            // The approved generator is kept next to the parts, as a record of exactly what ran.
            string scriptCopy = SafeFiles.Unique(folder, custom ? parts[0].Key + "_custom.py" : "generate_parts.py");
            call.Job.Paths.WriteNewText(scriptCopy, scriptText);

            var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Generated {made.Count} of {parts.Count} part(s) in {folder}"));
            sb.Append(isolated ? " (sandboxed)." : " (host Python: built-in templates only).");
            if (parts.Count < proposed) sb.Append(CultureInfo.InvariantCulture, $" {proposed - parts.Count} part(s) were excluded by the user; do not generate them.");
            foreach (var f in failed) sb.Append(CultureInfo.InvariantCulture, $" {f!["key"]}: {f["error"]}.");
            if (made.Count > 0) sb.Append(" Next: swx_import_step with these STEP files, then swx_part_cards and swx_build_assembly.");
            if (made.Count > 0) sb.Append(" " + FitCheckNotice);
            var structured = new JsonObject
            {
                ["parts"] = made, ["failed"] = failed, ["script"] = scriptCopy,
                ["runner"] = isolated ? sandbox.Id : python.Describe, ["isolated"] = isolated,
                ["validation"] = "fit-check geometry (units: millimetres); solids, volume and bounding box verified; NOT engineering-validated: no material, tolerance, load or manufacturability check",
            };
            return made.Count == 0 ? new ToolResult(true, sb.ToString(), structured) : ToolResult.Ok(sb.ToString(), structured);
        }
        finally
        {
            // Never follows links: a link the generator planted is removed, its target is untouched.
            try { Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? GeometryProblem(PartRequest part, JsonObject geo, JsonObject? gen)
    {
        int solids = (int?)geo["solids"] ?? 0;
        double volume = (double?)geo["volume"] ?? 0;
        var bbox = geo["bbox"]?.AsArray().Select(v => (double)v!).ToArray() ?? [];
        if (solids < 1) return "no solid body in the file";
        if (volume <= 0) return "zero volume";
        if (bbox.Length != 3) return "no bounding box";
        for (int i = 0; i < 3; i++)
            if (bbox[i] > part.EnvelopeMm[i] + 0.01) return string.Create(CultureInfo.InvariantCulture, $"bounding box {bbox[0]:0.###}×{bbox[1]:0.###}×{bbox[2]:0.###} mm exceeds the envelope");
        if (gen?["bbox"] is JsonArray g && g.Count == 3)
            for (int i = 0; i < 3; i++)
                if (Math.Abs((double)g[i]! - bbox[i]) > 0.05) return "the re-imported size differs from what the generator reported";
        return null;
    }

    private async Task<Dictionary<string, JsonObject>> ReimportAsync(List<SandboxOutputs.Accepted> files, string stage, bool isolated, ToolCallContext call, CancellationToken ct)
    {
        string vin = Path.Combine(stage, "verify-in"), vout = Path.Combine(stage, "verify-out");
        Directory.CreateDirectory(vin);
        Directory.CreateDirectory(vout);
        foreach (var f in files) File.Copy(f.StagedPath, Path.Combine(vin, f.Name));
        string script = Path.Combine(vin, "validate_step.py");
        await File.WriteAllTextAsync(script, StepReimportScript, ct).ConfigureAwait(false);
        var names = files.Select(f => f.Name).ToList();
        ProcessResult run = isolated
            ? await sandbox.RunAsync(new SandboxRequest(sandboxPython!.Executable, ["-I", "/in/validate_step.py", .. names.Select(n => "/in/" + n)], vin, vout,
                sandboxPython.Binds, new Dictionary<string, string>(), LimitsFor(call) with { Wall = TimeSpan.FromMinutes(5) }), ct).ConfigureAwait(false)
            : await python.RunScriptAsync(script, names.Select(n => python.MapPath(Path.Combine(vin, n)) ?? n).ToList(), null, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (LastJson(run.Stdout)?["files"] is JsonArray arr)
            foreach (var o in arr.OfType<JsonObject>())
                if ((string?)o["name"] is string n) map[Path.GetFileName(n)] = o;
        return map;
    }

    private static JsonNode? LastJson(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try { return JsonNode.Parse(line); } catch (JsonException) { }
        }
        return null;
    }

    /// <summary>Fixed re-import check: a fresh interpreter reads each STEP back with OpenCascade and reports solids, volume, size.</summary>
    public const string StepReimportScript = """
# MEDIATOR_STEP_VALIDATOR: re-reads STEP files independently of the code that wrote them.
import json, sys
out = []
try:
    from build123d import import_step
except ImportError as e:
    print(json.dumps({"files": [{"name": n, "ok": False, "error": "No module named 'build123d'"} for n in sys.argv[1:]]}))
    sys.exit(4)
for path in sys.argv[1:]:
    try:
        shape = import_step(path)
        solids = shape.solids()
        size = shape.bounding_box().size
        vol = sum(s.volume for s in solids)
        valid = all((s.is_valid() if callable(s.is_valid) else s.is_valid) for s in solids)
        out.append({"name": path, "ok": bool(solids) and valid, "solids": len(solids), "volume": round(vol, 1),
                    "bbox": [round(size.X, 3), round(size.Y, 3), round(size.Z, 3)],
                    "error": None if solids and valid else ("no solid bodies" if not solids else "invalid solid")})
    except Exception as e:
        out.append({"name": path, "ok": False, "error": "%s: %s" % (type(e).__name__, e)})
print(json.dumps({"files": out}))
""";
}

/// <summary>The interpreter used inside the sandbox and the host folders it needs bound read-only (its own prefix).</summary>
public sealed record SandboxPython(string Executable, IReadOnlyList<string> Binds)
{
    /// <summary>Asks a host Python (fixed code, no user input) for its prefixes. Linux only. Null if it cannot be used.</summary>
    public static SandboxPython? Probe(string? hostPython)
    {
        if (hostPython is null || !OperatingSystem.IsLinux()) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(hostPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-I", "-c", "import json,sys;print(json.dumps([sys.prefix,sys.base_prefix,sys.executable]))" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            var v = JsonSerializer.Deserialize<string[]>(text.Trim());
            if (v is not { Length: 3 }) return null;
            var binds = new[] { v[0], v[1] }.Where(d => d != "/usr" && !d.StartsWith("/usr/", StringComparison.Ordinal)).Distinct().ToList();
            return new SandboxPython(v[2], binds);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>The build123d generator script: a fixed template library plus the approved part list as JSON.</summary>
public static class Build123dScript
{
    public static string Render(JsonObject config) =>
        Template.Replace("__CONFIG_JSON__", JsonSerializer.Serialize(config.ToJsonString()), StringComparison.Ordinal);

    private const string Template = """
# Generated by Mediator (cad_generate_parametric_step). Requires: pip install build123d
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
    scope = {"__name__": "mediator_custom"}
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
