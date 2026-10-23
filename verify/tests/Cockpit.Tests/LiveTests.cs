using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Blender;
using Cockpit.Core.Bus;
using Cockpit.Core.CadCode;
using Cockpit.Core.Jobs;
using Cockpit.Core.Resolve;
using Cockpit.Core.Runtime;
using Cockpit.Core.Swx;
using Cockpit.Core.Tools;
using Cockpit.Core.Tutor;

namespace Cockpit.Tests;

/// <summary>
/// Blender as a Python module (pip install bpy): translates `blender -b --factory-startup -P script.py …` into
/// `python script.py`, so the real Blender API runs the generated scene script without the Blender app.
/// </summary>
sealed class BpyModuleRunner(string python) : IBlenderRunner
{
    public bool IsAvailable => true;
    public string Describe => "bpy module via " + python;
    public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        int p = args.ToList().IndexOf("-P");
        if (p < 0) throw new NotSupportedException("only -P scripts can run through the bpy module");
        var psi = new ProcessStartInfo(python);
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(args[p + 1]);
        return DefaultProcessRunner.Instance.RunAsync(psi, timeout, ct);
    }
}

/// <summary>
/// Optional: the generated scripts run for real. Enabled by COCKPIT_LIVE_PYTHON=&lt;python with build123d, bpy, pymupdf, sympy&gt;
/// (see verify/live-requirements.txt). Skipped otherwise, so the offline suite needs nothing but .NET.
/// </summary>
static class LiveTests
{
    public static string? Python => Environment.GetEnvironmentVariable("COCKPIT_LIVE_PYTHON") is { Length: > 0 } p && File.Exists(p) ? p : null;

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-live", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static Task<CockpitRuntime> Runtime(string bridgeDll, IPythonRunner? python = null, IBlenderRunner? blender = null) =>
        CockpitRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new CockpitPaths(TempDir()),
            Bus = new BusOptions { PipeName = "cockpit-live-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxBackend = new SimulatedSwxBackend(),
            Python = python ?? new LocalPythonRunner(Python, [], DefaultProcessRunner.Instance),
            Blender = blender ?? new BpyModuleRunner(Python!),
            Ffmpeg = null,
        }).ContinueWith(t => { t.Result.Approvals.Target = new AutoApprovalPrompt(ToolRisk.Destructive); return t.Result; }, TaskScheduler.Default);

    /// <summary>All seven build123d templates with their defaults → valid STEP files inside their envelopes; an oversize part is refused.</summary>
    public static async Task Build123dTemplates(string bridgeDll)
    {
        string folder = TempDir();
        await using var runtime = await Runtime(bridgeDll);
        var job = await runtime.StartJobAsync(JobSpecFactory.CodeCad(folder, "kit", LaunchMode.None) with { JobId = "live-cad" });
        await using var client = await runtime.ConnectClientAsync(job);
        var parts = new JsonArray(CodeCadToolProvider.Templates.Keys.Select(t => (JsonNode)new JsonObject
        {
            ["key"] = t, ["template"] = t, ["envelopeMm"] = new JsonArray(1000, 1000, 1000),
        }).ToArray());
        parts.Add(new JsonObject { ["key"] = "too_long", ["template"] = "shaft", ["params"] = new JsonObject { ["length"] = 250 }, ["envelopeMm"] = new JsonArray(30, 30, 200) });
        var r = await client.CallToolAsync(CodeCadToolProvider.GenerateTool, new JsonObject { ["outputFolder"] = folder, ["parts"] = parts }, TimeSpan.FromMinutes(10));
        var made = r.Structured!["parts"]!.AsArray();
        Check.Equal(7, made.Count, "all seven templates built: " + r.Text);
        foreach (var p in made)
        {
            string step = (string)p!["step"]!;
            Check.True(File.ReadLines(step).First().StartsWith("ISO-10303-21", StringComparison.Ordinal), $"{Path.GetFileName(step)} is a STEP file");
            Check.True((double)p["volumeMm3"]! > 0, $"{p["key"]} has volume");
        }
        var hub = made.Single(p => (string)p!["key"]! == "wheel_hub")!;
        var bbox = hub["bboxMm"]!.AsArray().Select(x => (double)x!).ToArray();
        Check.True(Math.Abs(bbox[0] - 140) < 1e-6 && Math.Abs(bbox[1] - 140) < 1e-6 && Math.Abs(bbox[2] - 55) < 1e-6, "wheel hub bounding box = flange Ø140 × (10 + 45) mm");
        var failed = r.Structured["failed"]!.AsArray().Single()!;
        Check.True((string)failed["key"]! == "too_long" && ((string)failed["error"]!).Contains("exceeds envelope", StringComparison.Ordinal), "oversize part refused: " + failed["error"]);
        Console.WriteLine("      " + string.Join("; ", made.Select(p => $"{p!["key"]} {p["bboxMm"]!.ToJsonString()} mm")));
    }

    /// <summary>The car scene script on the real Blender API (bpy module): a saved .blend with the approved frame count.</summary>
    public static async Task BlenderScene(string bridgeDll)
    {
        string folder = TempDir();
        await using var runtime = await Runtime(bridgeDll);
        var job = await runtime.StartJobAsync(JobSpecFactory.BlenderScene(folder, "night drive", LaunchMode.None) with { JobId = "live-bl" });
        await using var client = await runtime.ConnectClientAsync(job);
        var r = await client.CallToolAsync(BlenderToolProvider.BuildTool, new JsonObject { ["outputFolder"] = folder, ["durationSec"] = 3, ["fps"] = 24, ["buildingCount"] = 30 }, TimeSpan.FromMinutes(5));
        Check.True(!r.IsError, "bpy ran the scene script: " + r.Text);
        var report = r.Structured!["report"]!;
        Check.True((int)report["frames"]! == 72 && (int)report["buildings"]! > 0 && File.Exists((string)r.Structured["blend"]!), "72 frames, buildings placed, .blend saved");
        Console.WriteLine($"      Blender {report["blender"]}, engine {report["engine"]}, road {report["roadLengthM"]} m");
    }

    /// <summary>Real clips from ffmpeg: the built-in MP4/MOV/WAV reader agrees with ffprobe (needs ffmpeg and ffprobe on PATH).</summary>
    public static async Task MediaProbes()
    {
        string? ffmpeg = ToolLocator.Ffmpeg(), ffprobe = ToolLocator.Ffprobe();
        if (ffmpeg is null || ffprobe is null) { Console.WriteLine("      (ffmpeg/ffprobe not on PATH: skipped)"); return; }
        string dir = TempDir();
        async Task Make(string name, params string[] args)
        {
            var psi = new ProcessStartInfo(ffmpeg);
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error" }.Concat(args).Append(Path.Combine(dir, name))) psi.ArgumentList.Add(a);
            var r = await DefaultProcessRunner.Instance.RunAsync(psi, TimeSpan.FromMinutes(2), CancellationToken.None);
            Check.True(r.Ok, $"ffmpeg made {name}: {r.Tail()}");
        }
        await Make("a.mp4", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30000/1001", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2");
        await Make("b.mov", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        await Make("c.wav", "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100", "-t", "1.5", "-ac", "2");
        var native = new NativeMediaProbe();
        var ff = new FfprobeMediaProbe(ffprobe, DefaultProcessRunner.Instance);
        foreach (var name in new[] { "a.mp4", "b.mov", "c.wav" })
        {
            string path = Path.Combine(dir, name);
            var n = await native.ProbeAsync(path, "c1", CancellationToken.None);
            var f = await ff.ProbeAsync(path, "c1", CancellationToken.None);
            Check.True(n is not null && f is not null, $"{name}: both probes read it");
            Check.True(Math.Abs(n!.DurationSec - f!.DurationSec) < 0.06, $"{name}: duration {n.DurationSec:0.###} vs ffprobe {f.DurationSec:0.###}");
            Check.True(n.Fps == f.Fps && n.Width == f.Width && n.Height == f.Height && n.HasVideo == f.HasVideo, $"{name}: video {n.Fps} {n.Width}x{n.Height} vs {f.Fps} {f.Width}x{f.Height}");
            Check.True(n.AudioStreams == f.AudioStreams && n.AudioChannels == f.AudioChannels, $"{name}: audio {n.AudioStreams}×{n.AudioChannels}ch vs {f.AudioStreams}×{f.AudioChannels}ch");
            Console.WriteLine($"      {name}: {n.DurationSec:0.###} s, {n.Fps?.ToString() ?? "-"} fps, {n.Width}x{n.Height}, audio {n.AudioChannels}ch — matches ffprobe");
        }
    }

    /// <summary>PyMuPDF path of tutor_extract_pdf_chapter on a PDF PyMuPDF itself wrote, and the SymPy script of tutor_verify_matrix.</summary>
    public static async Task PdfAndSympy(string bridgeDll)
    {
        string folder = TempDir();
        string pdf = Path.Combine(folder, "book.pdf");
        string maker = Path.Combine(folder, "make.py");
        File.WriteAllText(maker, """
            import pymupdf, sys
            doc = pymupdf.open()
            for t in ["Chapter 3", "3.2 Determinants: det(A) = 0 iff A is singular", "3.3 Eigenvalues"]:
                doc.new_page().insert_text((72, 72), t)
            doc.set_toc([[1, "Chapter 3", 1], [2, "3.2 Determinants", 2], [2, "3.3 Eigenvalues", 3]])
            doc.save(sys.argv[1], garbage=3, deflate=True, use_objstms=True)
            """);
        var make = await new LocalPythonRunner(Python, [], DefaultProcessRunner.Instance).RunScriptAsync(maker, [pdf], null, TimeSpan.FromMinutes(1), CancellationToken.None);
        Check.True(make.Ok && File.Exists(pdf), "PyMuPDF wrote a test PDF: " + make.Tail());

        await using var runtime = await Runtime(bridgeDll);
        var job = await runtime.StartJobAsync(JobSpecFactory.Tutor(folder, "ch3", LaunchMode.None) with { JobId = "live-tut" });
        await using var client = await runtime.ConnectClientAsync(job);
        var r = await client.CallToolAsync(TutorToolProvider.ExtractTool, new JsonObject { ["pdfPath"] = pdf, ["firstPage"] = 2, ["lastPage"] = 3 }, TimeSpan.FromMinutes(2));
        Check.True((string)r.Structured!["engine"]! == "PyMuPDF" && r.Text.Contains("det(A) = 0 iff A is singular", StringComparison.Ordinal), "PyMuPDF extraction: " + r.Text[..Math.Min(300, r.Text.Length)]);
        Check.True(r.Structured["outline"]!.AsArray().Count == 2, "outline entries within pages 2–3");
        string native = string.Join("\n", PdfTextExtractor.Extract(pdf, 2, 3).Pages.Select(p => p.Text));
        Check.True(native.Contains("det(A) = 0 iff A is singular", StringComparison.Ordinal), "the built-in reader agrees with PyMuPDF on a PyMuPDF-written file");

        var v = await client.CallToolAsync(TutorToolProvider.VerifyTool, new JsonObject
        {
            ["A"] = new JsonArray(new JsonArray(2, 1, 1), new JsonArray(1, 2, 1), new JsonArray(1, 1, 2)), ["sympy"] = true,
        }, TimeSpan.FromSeconds(30));
        string script = (string)v.Structured!["sympyScript"]!;
        var run = await new LocalPythonRunner(Python, [], DefaultProcessRunner.Instance).RunScriptAsync(script, [], null, TimeSpan.FromMinutes(1), CancellationToken.None);
        Check.True(run.Ok && run.Stdout.Contains("det = 4", StringComparison.Ordinal), "SymPy agrees: det = 4\n" + run.Tail(600));
    }
}
