using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Security;
using Cockpit.Core.Tools;

namespace Cockpit.Core.Blender;

/// <summary>
/// Blender in background mode only: no UI to drive, no lease needed (several `blender -b` renders can run at once).
/// blender_build_car_scene writes a deterministic bpy script from approved parameters and runs it to produce a
/// .blend; blender_render_preview renders frames (and an MP4 when ffmpeg is installed). Nothing is overwritten.
/// </summary>
public sealed class BlenderToolProvider(IBlenderRunner blender, IProcessRunner runner, string? ffmpeg) : IToolProvider
{
    public const string BuildTool = "blender_build_car_scene";
    public const string RenderTool = "blender_render_preview";

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(BuildTool,
            "Build an animated Blender scene of a car driving through a procedural city: a road along roadPoints, a low-poly car " +
            "with wheels that roll with the distance travelled, city blocks with lit windows (style night) and a chase camera. " +
            "Writes build_scene.py and, when Blender is installed, runs it headless to save a new .blend. The user approves the parameters.",
            Schema("""
            {
              "type": "object",
              "required": ["outputFolder"],
              "properties": {
                "outputFolder": { "type": "string" },
                "durationSec": { "type": "number" }, "fps": { "type": "integer" },
                "engine": { "type": "string", "enum": ["EEVEE", "CYCLES", "WORKBENCH"] },
                "width": { "type": "integer" }, "height": { "type": "integer" },
                "roadPoints": { "type": "array", "items": { "type": "array", "items": { "type": "number" } }, "description": "[[x, y], …] in metres" },
                "roadWidthM": { "type": "number" }, "carSpeedKmh": { "type": "number" },
                "cameraOffset": { "type": "array", "items": { "type": "number" }, "description": "[behind (negative), side, up] in metres" },
                "buildingCount": { "type": "integer" }, "seed": { "type": "integer" },
                "style": { "type": "string", "enum": ["night", "day"] },
                "run": { "type": "boolean", "description": "Run Blender now (default true when installed)." }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromMinutes(15)),

        new(RenderTool,
            "Render preview frames of a .blend in the background (PNG sequence in a new folder, plus preview.mp4 if ffmpeg is installed).",
            Schema("""
            {
              "type": "object",
              "required": ["blendFile"],
              "properties": {
                "blendFile": { "type": "string" }, "startFrame": { "type": "integer" }, "endFrame": { "type": "integer" },
                "frameStep": { "type": "integer" }, "resolutionPercent": { "type": "integer" }, "makeVideo": { "type": "boolean" },
                "fps": { "type": "integer", "description": "Playback rate of the preview MP4 (the scene's fps; default 24)." }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromHours(2)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        JsonNode? preview;
        try
        {
            preview = call.Tool.Name switch
            {
                BuildTool => ScenePreview(call),
                RenderTool => RenderPreview(call),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            preview = new JsonObject { ["kind"] = "blender", ["title"] = "Invalid request", ["summary"] = ex.Message, ["selectable"] = false, ["rows"] = new JsonArray() };
        }
        return Task.FromResult(preview);
    }

    private JsonNode ScenePreview(ToolCallContext call)
    {
        var p = CarSceneParameters.From(call.Arguments);
        JsonNode Row(string key, string text) => new JsonObject { ["key"] = key, ["primary"] = text };
        string I(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["kind"] = "blender_scene",
            ["title"] = "Build Blender car scene",
            ["summary"] = I($"{p.DurationSec:0.#} s at {p.Fps} fps ({p.Frames} frames), {p.Engine}, {p.Style}") + (blender.IsAvailable ? " — Blender will run headless." : " — script only (Blender not found)."),
            ["selectable"] = false,
            ["rows"] = new JsonArray(
                Row("duration", I($"Duration {p.DurationSec:0.#} s, {p.Fps} fps, {p.Width}×{p.Height}")),
                Row("engine", $"Render engine {p.Engine}"),
                Row("road", I($"Road: {p.RoadPoints.Count} points, {p.RoadWidthM:0.#} m wide")),
                Row("car", I($"Car speed {p.CarSpeedKmh:0.#} km/h")),
                Row("camera", I($"Chase camera offset [{p.CameraOffset[0]:0.#}, {p.CameraOffset[1]:0.#}, {p.CameraOffset[2]:0.#}] m")),
                Row("city", $"{p.BuildingCount} buildings, seed {p.Seed}, {p.Style}")),
        };
    }

    private static JsonNode RenderPreview(ToolCallContext call)
    {
        var (blend, s, e, step, pct, _) = RenderArgs(call);
        return new JsonObject
        {
            ["kind"] = "blender_render",
            ["title"] = "Render preview frames",
            ["summary"] = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(blend)}: frames {s}–{e}{(step > 1 ? " every " + step.ToString(CultureInfo.InvariantCulture) : "")} at {pct}% resolution"),
            ["selectable"] = false,
            ["rows"] = new JsonArray(),
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            return call.Tool.Name switch
            {
                BuildTool => await BuildAsync(call, ct).ConfigureAwait(false),
                RenderTool => await RenderAsync(call, ct).ConfigureAwait(false),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            };
        }
        catch (FormatException ex) { return ToolResult.Fail(ex.Message); }
    }

    private async Task<ToolResult> BuildAsync(ToolCallContext call, CancellationToken ct)
    {
        var folderVerdict = call.Job.Paths.Check(call.Arguments.GetProperty("outputFolder").GetString(), PathAccess.List);
        if (!folderVerdict.Allowed) return ToolResult.Fail(folderVerdict.Message);
        string folder = folderVerdict.FullPath;
        var p = CarSceneParameters.From(call.Arguments);
        string blend = SafeFiles.Unique(folder, "car_scene.blend");
        string script = SafeFiles.Unique(folder, Path.GetFileNameWithoutExtension(blend) + "_build.py");
        call.Job.Paths.WriteNewText(script, CarSceneScript.Render(p, blend));

        bool run = !call.Arguments.TryGetProperty("run", out var r) || r.ValueKind != JsonValueKind.False;
        if (!run || !blender.IsAvailable)
            return ToolResult.Ok($"Wrote {script}." + (blender.IsAvailable ? "" : $" Blender was not found ({blender.Describe}); run it with: blender -b --factory-startup -P \"{script}\"."),
                new JsonObject { ["script"] = script, ["ran"] = false });

        var result = await blender.RunAsync(["-b", "--factory-startup", "-P", script, "--python-exit-code", "1"], TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        var report = LastJsonLine(result.Stdout);
        bool ok = result.Ok && (bool?)report?["ok"] == true && call.Job.Paths.Check(blend, PathAccess.Read).Allowed;
        return new ToolResult(!ok, ok
            ? $"Saved {blend} ({report?["frames"]} frames, road {report?["roadLengthM"]} m, car travels {report?["carDistanceM"]} m, {report?["buildings"]} buildings, {report?["engine"]}, Blender {report?["blender"]}). Next: blender_render_preview."
            : $"Blender failed: {(string?)report?["error"] ?? result.Tail()}",
            new JsonObject { ["script"] = script, ["blend"] = ok ? blend : null, ["ran"] = true, ["report"] = report?.DeepClone() });
    }

    private static (string Blend, int Start, int End, int Step, int Percent, bool Video) RenderArgs(ToolCallContext call)
    {
        var a = call.Arguments;
        var blendVerdict = call.Job.Paths.Check(a.GetProperty("blendFile").GetString(), PathAccess.Read);
        if (!blendVerdict.Allowed) throw new FormatException(blendVerdict.Message);
        string blend = blendVerdict.FullPath;
        if (!blend.EndsWith(".blend", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"{blend} is not a .blend file.");
        int Int(string name, int fallback, int min, int max)
        {
            if (!a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return fallback;
            int i = v.GetInt32();
            return i < min || i > max ? throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name} must be {min}-{max}.")) : i;
        }
        int start = Int("startFrame", 1, 0, 1_000_000), end = Int("endFrame", start + 47, start, start + 20_000);
        return (blend, start, end, Int("frameStep", 1, 1, 1000), Int("resolutionPercent", 50, 1, 100),
            !a.TryGetProperty("makeVideo", out var mv) || mv.ValueKind != JsonValueKind.False);
    }

    private async Task<ToolResult> RenderAsync(ToolCallContext call, CancellationToken ct)
    {
        var (blend, start, end, step, pct, video) = RenderArgs(call);
        if (!blender.IsAvailable) return ToolResult.Fail($"Blender not found ({blender.Describe}).");
        string outDir = call.Job.Paths.EnsureDirectory(SafeFiles.Unique(Path.GetDirectoryName(blend)!, Path.GetFileNameWithoutExtension(blend) + "_preview"));
        if (call.Job.Paths.VerifyExisting(blend, PathAccess.Read) is { Allowed: false }) return ToolResult.Fail($"{blend} changed before the render started.");
        // Argument order matters: settings first, -a (render animation) last. The .blend keeps its own engine.
        var args = new List<string>
        {
            "-b", blend, "--python-expr", string.Create(CultureInfo.InvariantCulture, $"import bpy; bpy.context.scene.render.resolution_percentage = {pct}"),
            "-o", Path.Combine(outDir, "frame_####"), "-F", "PNG", "-x", "1",
            "-s", start.ToString(CultureInfo.InvariantCulture), "-e", end.ToString(CultureInfo.InvariantCulture),
            "-j", step.ToString(CultureInfo.InvariantCulture), "-a",
        };
        var result = await blender.RunAsync(args, call.Tool.ExecutionTimeout, ct).ConfigureAwait(false);
        // Only plain files that Blender wrote count; a link planted in the output folder is ignored.
        var frames = Directory.GetFiles(outDir, "frame_*.png").Where(f => call.Job.Paths.Check(f, PathAccess.Read).Allowed).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (!result.Ok || frames.Count == 0) return ToolResult.Fail($"Render failed ({frames.Count} frames): {result.Tail()}");

        string? mp4 = null;
        string note = "";
        if (video && step == 1 && ffmpeg is not null)
        {
            mp4 = Path.Combine(outDir, "preview.mp4");
            var psi = new ProcessStartInfo(ffmpeg);
            foreach (var x in new[] { "-hide_banner", "-loglevel", "error", "-framerate", FpsOf(call), "-start_number", start.ToString(CultureInfo.InvariantCulture),
                         "-i", Path.Combine(outDir, "frame_%04d.png"), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-n", mp4 })
                psi.ArgumentList.Add(x);
            var enc = await runner.RunAsync(psi, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
            if (!enc.Ok || !File.Exists(mp4)) { note = " ffmpeg failed: " + enc.Tail(300); mp4 = null; }
        }
        else if (video) note = ffmpeg is null ? " (install ffmpeg for an MP4)" : " (no MP4 when frameStep > 1)";
        return ToolResult.Ok($"Rendered {frames.Count} frames into {outDir}." + (mp4 is null ? "" : $" Video: {mp4}.") + note,
            new JsonObject { ["folder"] = outDir, ["frames"] = frames.Count, ["video"] = mp4 });
    }

    private static string FpsOf(ToolCallContext call) =>
        call.Arguments.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number && f.GetInt32() is > 0 and <= 120
            ? f.GetInt32().ToString(CultureInfo.InvariantCulture) : "24";

    private static JsonNode? LastJsonLine(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try { return JsonNode.Parse(line); } catch (JsonException) { }
        }
        return null;
    }
}
