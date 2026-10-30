using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mediator.Connectors.Blender;
using Mediator.Core.Bus;
using Mediator.Connectors.PartsFromCode;
using Mediator.Core.Jobs;
using Mediator.Connectors.Resolve;
using Mediator.Core.Runtime;
using Mediator.Connectors.SolidWorks;
using Mediator.Core.Tools;
using Mediator.Connectors.Tutor;

namespace Mediator.Tests;

// ── Fakes for the helper programs ──────────────────────────────────────────────────────────────────

sealed class FakeMediaProbe(Dictionary<string, Func<string, string, ClipInfo>> byName) : IMediaProbe
{
    public string Describe => "fake probe";
    public Task<ClipInfo?> ProbeAsync(string path, string id, CancellationToken ct) =>
        Task.FromResult(byName.TryGetValue(Path.GetFileName(path), out var make) ? make(path, id) : null);
}

/// <summary>A Python runner whose "execution" is a C# function of the script text (or a real interpreter for live tests).</summary>
sealed class FakePythonRunner(Func<string, IReadOnlyList<string>, ProcessResult> run, bool available = true) : IPythonRunner
{
    public readonly List<string> Scripts = new();
    public bool IsAvailable => available;
    public string Describe => "fake python";
    public string? MapPath(string hostPath) => Path.GetFullPath(hostPath);
    public Task<ProcessResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken ct)
    {
        Scripts.Add(scriptPath);
        return Task.FromResult(run(File.ReadAllText(scriptPath), args));
    }
}

sealed class FakeBlender : IBlenderRunner
{
    public readonly List<IReadOnlyList<string>> Calls = new();
    public bool IsAvailable => true;
    public string Describe => "fake blender";

    public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add(args);
        int p = args.ToList().IndexOf("-P");
        if (p >= 0)
        {
            // build: honour the script's own contract — write the blend it names and print the JSON report
            string script = File.ReadAllText(args[p + 1]);
            var config = JsonNode.Parse(JsonSerializer.Deserialize<string>(Regex.Match(script, @"json\.loads\((""(?:[^""\\]|\\.)*"")\)").Groups[1].Value)!)!;
            string blend = (string)config["blendPath"]!;
            File.WriteAllText(blend, "BLENDER-fake");
            return Task.FromResult(new ProcessResult(0, $"Info: Saved\n{{\"ok\": true, \"blend\": {JsonSerializer.Serialize(blend)}, \"frames\": {config["frames"]}, \"engine\": \"BLENDER_EEVEE\", \"roadLengthM\": 240.0, \"carDistanceM\": 120.0, \"buildings\": 50, \"blender\": \"fake\"}}", "", false));
        }
        // render: write the frames the -o pattern and -s/-e range ask for
        string pattern = args[args.ToList().IndexOf("-o") + 1];
        int s = int.Parse(args[args.ToList().IndexOf("-s") + 1], System.Globalization.CultureInfo.InvariantCulture);
        int e = int.Parse(args[args.ToList().IndexOf("-e") + 1], System.Globalization.CultureInfo.InvariantCulture);
        for (int f = s; f <= e; f++) File.WriteAllText(pattern.Replace("####", f.ToString("0000", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) + ".png", "png");
        return Task.FromResult(new ProcessResult(0, "Finished", "", false));
    }
}

/// <summary>Approves everything except the rows named, like a user unticking them.</summary>
sealed class UntickPrompt(params string[] untick) : IApprovalPrompt
{
    public readonly List<ApprovalRequest> Seen = new();
    public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        lock (Seen) Seen.Add(request);
        var rows = request.Preview?["rows"]?.AsArray().Select(r => (string)r!["key"]!).ToList() ?? [];
        bool selectable = (bool?)request.Preview?["selectable"] ?? false;
        return Task.FromResult(selectable && rows.Any(untick.Contains)
            ? new ApprovalResponse(true, rows.Where(k => !untick.Contains(k)).ToHashSet(StringComparer.Ordinal))
            : ApprovalResponse.ApproveAll);
    }
}

static class ProviderTests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    internal static Task<MediatorRuntime> Runtime(string bridgeDll, IApprovalPrompt prompt, IMediaProbe? probe = null, IPythonRunner? python = null,
        IBlenderRunner? blender = null, SimulatedSwxBackend? swx = null) =>
        MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir()),
            Bus = new BusOptions { PipeName = "mediator-pv-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Simulate = true,
            Python = python ?? new FakePythonRunner((_, _) => new ProcessResult(1, "", "no python", false), available: false),
            Connectors = TestConnectors.All(swx: swx, mediaProbe: probe ?? new FakeMediaProbe(new()), blender: blender ?? new FakeBlender(), noFfmpeg: true),
        }).ContinueWith(t => { t.Result.Approvals.Target = prompt; return t.Result; }, TaskScheduler.Default);

    /// <summary>Python's own parser, when a python3 is around: proves a generated script is syntactically valid.</summary>
    static void PythonSyntax(string path, string what)
    {
        string? py = Environment.GetEnvironmentVariable("MEDIATOR_LIVE_PYTHON") ?? CliLaunchBuilder.ResolveOnPath("python3");
        if (py is null) { Console.WriteLine($"      (no python3: syntax of {what} not checked)"); return; }
        var psi = new ProcessStartInfo(py) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-I", "-c", "import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read())", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Check.True(p.ExitCode == 0, $"{what} parses as Python: {err}");
    }

    static ClipInfo Clip(string path, string id, double dur, bool video = true) =>
        new(id, path, dur, video ? new FrameRate(30000, 1001) : null, video ? 1920 : null, video ? 1080 : null, video, 1, 2, 48000, video ? "h264" : "pcm_s16le", "fake");

    // ── DaVinci Resolve ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gemini V5 item 4: probe and export through the bus with a 4-clip plan, one cut unticked in the approval; the FCPXML
    /// is well-formed, has exactly the 3 approved asset-clips, and every time is a whole number of 29.97 fps frames.
    /// </summary>
    public static async Task ResolveTimeline(string bridgeDll)
    {
        string folder = TempDir();
        foreach (var f in new[] { "A_intro.mp4", "B_street.mov", "C_drive.mp4", "music.wav", "notes.txt" }) File.WriteAllText(Path.Combine(folder, f), "x");
        var probe = new FakeMediaProbe(new()
        {
            ["A_intro.mp4"] = (p, id) => Clip(p, id, 10), ["B_street.mov"] = (p, id) => Clip(p, id, 8),
            ["C_drive.mp4"] = (p, id) => Clip(p, id, 12), ["music.wav"] = (p, id) => Clip(p, id, 30, video: false),
        });
        var prompt = new UntickPrompt("k2");
        await using var runtime = await Runtime(bridgeDll, prompt, probe);
        var job = await runtime.StartJobAsync(ResolveConnector.ResolveEdit(folder, "a 10 second teaser", LaunchMode.None) with { JobId = "res-1" });
        await using var client = await runtime.ConnectClientAsync(job);

        var probed = await client.CallToolAsync(ResolveToolProvider.ProbeTool, new JsonObject { ["folder"] = folder }, TimeSpan.FromSeconds(30));
        Check.True(!probed.IsError, "probe: " + probed.Text);
        var clips = probed.Structured!["clips"]!.AsArray();
        Check.Equal(4, clips.Count, "four media files, the .txt ignored");
        Check.Equal("30000/1001", (string?)clips[0]!["fps"], "exact NTSC rate reported");

        JsonObject Cut(string key, string clip, double i, double o, int track = 1, double? at = null, string transition = "cut") => new()
        {
            ["key"] = key, ["clip"] = clip, ["inSec"] = i, ["outSec"] = o, ["track"] = track, ["timelineSec"] = at, ["transition"] = transition,
        };
        var cuts = new JsonArray(Cut("k1", "c1", 1.0, 3.5), Cut("k2", "c2", 0, 2), Cut("k3", "c3", 2, 6, transition: "dissolve"), Cut("k4", "c4", 0, 4, track: 2, at: 0.5));
        var export = await client.CallToolAsync(ResolveToolProvider.ExportTool, new JsonObject { ["name"] = "Teaser v1", ["cuts"] = cuts }, TimeSpan.FromSeconds(30));
        Check.True(!export.IsError, "export: " + export.Text);
        Check.True(export.Text.Contains("1 cut(s) were excluded", StringComparison.Ordinal), "the model is told about the excluded cut");
        var preview = prompt.Seen.Single(r => r.Tool == ResolveToolProvider.ExportTool).Preview!;
        Check.True((bool)preview["selectable"]! && preview["rows"]!.AsArray().Count == 4, "approval showed all 4 cuts as selectable rows");

        string xmlPath = (string)export.Structured!["fcpxml"]!;
        Check.True(xmlPath.StartsWith(Path.Combine(folder, "edits"), StringComparison.Ordinal), "written into an edits subfolder");
        var doc = XDocument.Load(xmlPath); // throws if not well-formed
        Check.Equal("1.9", (string?)doc.Root!.Attribute("version"), "FCPXML 1.9");
        var assetClips = doc.Descendants("asset-clip").ToList();
        Check.Equal(3, assetClips.Count, "exactly the 3 approved asset-clips");
        Check.True(!assetClips.Any(c => (string?)c.Attribute("name") == "B_street"), "the unticked clip is absent");
        Check.Equal("1001/30000s", (string?)doc.Descendants("format").Single().Attribute("frameDuration"), "frame duration 1001/30000 s");

        var spine = doc.Descendants("spine").Single().Elements().ToList();
        var k1 = spine[0];
        Check.True((string?)k1.Attribute("offset") == "0s" && (string?)k1.Attribute("start") == "30030/30000s" && (string?)k1.Attribute("duration") == "75075/30000s",
            "k1: in 1.0 s = 30 frames, 2.5 s long = 75 frames, as rationals");
        Check.True(spine[1].Name.LocalName == "transition" && (string?)spine[1].Attribute("offset") == "60060/30000s" && (string?)spine[1].Attribute("duration") == "30030/30000s",
            "1 s dissolve centred on the cut at frame 75");
        Check.True((string?)spine[2].Attribute("offset") == "75075/30000s" && (string?)spine[2].Attribute("duration") == "120120/30000s", "k3 follows k1 directly (k2 removed)");
        var music = k1.Element("asset-clip")!;
        Check.True((string?)music.Attribute("lane") == "-1" && (string?)music.Attribute("offset") == "45045/30000s", "music connected under k1 on lane -1, offset in k1's time (30 + 15 frames)");
        Check.Equal("195195/30000s", (string?)doc.Descendants("sequence").Single().Attribute("duration"), "sequence = 75 + 120 frames");
        Check.True(doc.Descendants("media-rep").All(m => ((string)m.Attribute("src")!).StartsWith("file:///", StringComparison.Ordinal)), "media referenced by file URL");
        foreach (var t in doc.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName is "offset" or "start" or "duration"))
        {
            var m = Regex.Match(t.Value, @"^(\d+)/30000s$|^(\d+)s$");
            Check.True(m.Success && (m.Groups[2].Success || long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) % 1001 == 0), $"{t.Name} {t.Value} is a whole frame");
        }

        string edl = File.ReadAllText((string)export.Structured["edl"]!);
        Check.True(edl.StartsWith("TITLE: Teaser v1", StringComparison.Ordinal) && Regex.Matches(edl, @"^\d{3}  AX", RegexOptions.Multiline).Count == 2, "EDL has the 2 approved main-track events");
        Check.True(edl.Contains("001  AX       V     C        00:00:01:00 00:00:03:15 01:00:00:00 01:00:02:15", StringComparison.Ordinal), "CMX3600 event line with NDF timecode");

        // The model cannot cut outside a clip or use a clip it was not shown.
        var bad = await client.CallToolAsync(ResolveToolProvider.ExportTool, new JsonObject { ["name"] = "x", ["cuts"] = new JsonArray(Cut("z1", "c9", 0, 1)) }, TimeSpan.FromSeconds(30));
        Check.True(bad.IsError && bad.Text.Contains("unknown clip 'c9'", StringComparison.Ordinal), "unknown clip id refused");
        var past = await client.CallToolAsync(ResolveToolProvider.ExportTool, new JsonObject { ["name"] = "x", ["cuts"] = new JsonArray(Cut("z1", "c2", 5, 9)) }, TimeSpan.FromSeconds(30));
        Check.True(past.IsError && past.Text.Contains("outside B_street.mov", StringComparison.Ordinal), "cut beyond the clip's end refused");

        var again = await client.CallToolAsync(ResolveToolProvider.ExportTool, new JsonObject { ["name"] = "Teaser v1", ["cuts"] = new JsonArray(Cut("k1", "c1", 0, 1)) }, TimeSpan.FromSeconds(30));
        Check.True(((string)again.Structured!["fcpxml"]!).EndsWith("Teaser_v1-2.fcpxml", StringComparison.Ordinal) && File.Exists(xmlPath), "second export gets a new name; nothing overwritten");

        var apply = await client.CallToolAsync(ResolveToolProvider.ApplyTool, new JsonObject { ["fcpxmlPath"] = xmlPath, ["projectName"] = "Teaser" }, TimeSpan.FromSeconds(30));
        Check.True(!apply.IsError && !(bool)apply.Structured!["ran"]!, "apply without run only writes the script: " + apply.Text);
        string script = File.ReadAllText((string)apply.Structured!["script"]!);
        Check.True(script.Contains("ImportTimelineFromFile", StringComparison.Ordinal) && script.Contains("A_intro.mp4", StringComparison.Ordinal) && !script.Contains("B_street", StringComparison.Ordinal),
            "Resolve script imports the timeline and only the media it uses");
        PythonSyntax((string)apply.Structured!["script"]!, "apply_resolve.py");
    }

    /// <summary>The built-in MP4/WAV header reader on synthetic files.</summary>
    public static async Task NativeProbe()
    {
        string dir = TempDir();
        static byte[] Box(string type, params byte[][] content)
        {
            int len = 8 + content.Sum(c => c.Length);
            var b = new byte[len];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)len);
            Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            int o = 8;
            foreach (var c in content) { c.CopyTo(b, o); o += c.Length; }
            return b;
        }
        static byte[] U32(params uint[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), v[i]); return b; }
        static byte[] Z(int n) => new byte[n];
        byte[] mvhd = Box("mvhd", U32(0, 0, 0, 1000, 10_000), Z(80));
        byte[] tkhdV = Box("tkhd", U32(0, 0, 0, 1, 0, 10_000), Z(8 + 8 + 36), U32(1920u << 16, 1080u << 16));
        byte[] mdhdV = Box("mdhd", U32(0, 0, 0, 30000, 300_300), Z(4));
        byte[] hdlrV = Box("hdlr", U32(0, 0), Encoding.ASCII.GetBytes("vide"), Z(13));
        byte[] stts = Box("stts", U32(0, 1, 300, 1001));
        // QuickTime (.mov) style: a data-handler hdlr ('alis') inside minf must not override the media handler ('vide').
        byte[] dataHandler = Box("hdlr", U32(0, 0), Encoding.ASCII.GetBytes("alis"), Z(13));
        byte[] video = Box("trak", tkhdV, Box("mdia", mdhdV, hdlrV, Box("minf", dataHandler, Box("stbl", stts))));
        byte[] mp4a = Box("mp4a", Z(6), new byte[] { 0, 1 }, Z(8), new byte[] { 0, 2, 0, 16 }, Z(4), U32(48000u << 16));
        byte[] stsd = Box("stsd", U32(0, 1), mp4a);
        byte[] audio = Box("trak", Box("tkhd", Z(84)), Box("mdia", Box("mdhd", U32(0, 0, 0, 48000, 480_000), Z(4)), Box("hdlr", U32(0, 0), Encoding.ASCII.GetBytes("soun"), Z(13)), Box("minf", Box("stbl", stsd))));
        string mp4 = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(mp4, [.. Box("ftyp", Encoding.ASCII.GetBytes("isom"), Z(4)), .. Box("moov", mvhd, video, audio), .. Box("mdat", Z(16))]);
        var info = await new NativeMediaProbe().ProbeAsync(mp4, "c1", CancellationToken.None);
        Check.True(info is { DurationSec: 10, Width: 1920, Height: 1080, HasVideo: true, AudioStreams: 1, AudioChannels: 2, AudioRate: 48000 }, "MP4 header: " + info);
        Check.Equal(new FrameRate(30000, 1001), info!.Fps, "300 samples of 1001/30000 s → 29.97 fps");

        string wav = Path.Combine(dir, "a.wav");
        using (var w = new BinaryWriter(File.Create(wav)))
        {
            w.Write("RIFF"u8); w.Write(36 + 96000); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
            w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(96000); w.Write(new byte[96000]);
        }
        var winfo = await new NativeMediaProbe().ProbeAsync(wav, "c2", CancellationToken.None);
        Check.True(winfo is { DurationSec: 0.5, HasVideo: false, AudioChannels: 2, AudioRate: 48000 }, "WAV header: " + winfo);
        Check.Equal(null, await new NativeMediaProbe().ProbeAsync(Path.Combine(dir, "x.mkv"), "c3", CancellationToken.None), "MKV needs ffprobe");
        var parsed = FfprobeMediaProbe.Parse("""{"streams":[{"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"avg_frame_rate":"24000/1001"},{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000"}],"format":{"duration":"12.512"}}""", "v.mov", "c1");
        Check.True(parsed is { Width: 3840, Codec: "hevc", AudioRate: 48000, DurationSec: 12.512 } && parsed.Fps == new FrameRate(24000, 1001), "ffprobe JSON parsed");
    }

    // ── Blender ──────────────────────────────────────────────────────────────────────────────────

    public static async Task BlenderScene(string bridgeDll)
    {
        string folder = TempDir();
        var blender = new FakeBlender();
        var prompt = new UntickPrompt();
        await using var runtime = await Runtime(bridgeDll, prompt, blender: blender);
        var job = await runtime.StartJobAsync(BlenderConnector.BlenderScene(folder, "a red car through a city at night", LaunchMode.None) with { JobId = "bl-1" });
        await using var client = await runtime.ConnectClientAsync(job);

        var args = new JsonObject
        {
            ["outputFolder"] = folder, ["durationSec"] = 6, ["fps"] = 30, ["engine"] = "EEVEE", ["carSpeedKmh"] = 72, ["buildingCount"] = 80,
            ["roadPoints"] = new JsonArray(new JsonArray(0, 0), new JsonArray(60, 20), new JsonArray(130, -15)), ["cameraOffset"] = new JsonArray(-10, 2, 4), ["style"] = "night",
        };
        var built = await client.CallToolAsync(BlenderToolProvider.BuildTool, args, TimeSpan.FromSeconds(30));
        Check.True(!built.IsError, "build: " + built.Text);
        var preview = prompt.Seen.Single(r => r.Tool == BlenderToolProvider.BuildTool).Preview!;
        Check.True(((string)preview["summary"]!).Contains("180 frames", StringComparison.Ordinal) && preview["rows"]!.AsArray().Count == 6, "approval shows the scene parameters");
        var call = blender.Calls.Single();
        Check.True(call.Take(3).SequenceEqual(new[] { "-b", "--factory-startup", "-P" }) && call.Contains("--python-exit-code"), "blender -b --factory-startup -P <script> --python-exit-code 1");
        string scriptPath = (string)built.Structured!["script"]!;
        string script = File.ReadAllText(scriptPath);
        foreach (var expect in new[] { "\\u0022carSpeedKmh\\u0022:72", "\\u0022buildingCount\\u0022:80", "\\u0022fps\\u0022:30", "[-10,2,4]" })
            Check.True(script.Contains(expect, StringComparison.Ordinal), $"approved parameter {expect} embedded in the script");
        Check.True(script.Contains("BLENDER_EEVEE_NEXT", StringComparison.Ordinal) && script.Contains("keyframe_insert", StringComparison.Ordinal), "engine chosen at run time; animation keyed");
        PythonSyntax(scriptPath, "build_scene.py");
        string blend = (string)built.Structured["blend"]!;
        Check.True(File.Exists(blend) && blend.StartsWith(folder, StringComparison.Ordinal), "the .blend landed in the output folder");

        var second = await client.CallToolAsync(BlenderToolProvider.BuildTool, new JsonObject { ["outputFolder"] = folder }, TimeSpan.FromSeconds(30));
        Check.True(((string)second.Structured!["blend"]!).EndsWith("car_scene-2.blend", StringComparison.Ordinal), "a second scene never overwrites the first");

        var outside = await client.CallToolAsync(BlenderToolProvider.BuildTool, new JsonObject { ["outputFolder"] = Path.GetTempPath() }, TimeSpan.FromSeconds(30));
        Check.True(outside.IsError && outside.Text.Contains("outside", StringComparison.Ordinal), "output folder must be inside the job's roots");
        var silly = await client.CallToolAsync(BlenderToolProvider.BuildTool, new JsonObject { ["outputFolder"] = folder, ["carSpeedKmh"] = 9000 }, TimeSpan.FromSeconds(30));
        Check.True(silly.IsError && silly.Text.Contains("carSpeedKmh must be between 1 and 400", StringComparison.Ordinal), "parameter ranges enforced");

        var render = await client.CallToolAsync(BlenderToolProvider.RenderTool, new JsonObject { ["blendFile"] = blend, ["startFrame"] = 1, ["endFrame"] = 12, ["resolutionPercent"] = 25 }, TimeSpan.FromSeconds(30));
        Check.True(!render.IsError && (int)render.Structured!["frames"]! == 12, "render preview: " + render.Text);
        var rargs = blender.Calls.Last();
        Check.True(rargs[0] == "-b" && rargs[1] == blend && rargs[^1] == "-a" && rargs.Contains("resolution_percentage = 25", StringComparer.Ordinal) == false
            && rargs.Any(a => a.Contains("resolution_percentage = 25", StringComparison.Ordinal)), "render args: blend first, settings, -a last");
        Check.True(render.Text.Contains("install ffmpeg", StringComparison.Ordinal), "no ffmpeg → frames only, with a hint");
    }

    // ── Code-CAD → SolidWorks ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gemini V5 item 6: a 4-part suspension kit through the bus, one part unticked; the 3 generated STEP files are imported
    /// as new SolidWorks parts (swx_import_step), read as part cards, and assembled — the full Prompt 1 pipeline.
    /// </summary>
    public static async Task CodeCadPipeline(string bridgeDll)
    {
        string folder = TempDir();
        var python = new FakePythonRunner(FakeCad.Run);
        var prompt = new UntickPrompt("strut");
        var swx = new SimulatedSwxBackend();
        await using var runtime = await Runtime(bridgeDll, prompt, python: python, swx: swx);
        var job = await runtime.StartJobAsync(SolidWorksConnector.CodeCad(folder, "front suspension corner", LaunchMode.None) with { JobId = "cad-1" });
        await using var client = await runtime.ConnectClientAsync(job);

        JsonObject Part(string key, string template, JsonObject p, params double[] env) => new()
        {
            ["key"] = key, ["template"] = template, ["params"] = p, ["envelopeMm"] = new JsonArray(env.Select(v => (JsonNode)v).ToArray()),
        };
        var parts = new JsonArray(
            Part("hub", "wheel_hub", new JsonObject { ["bolt_circle_diameter"] = 112, ["bolt_count"] = 5 }, 145, 145, 60),
            Part("disc", "brake_disc", new JsonObject { ["outer_diameter"] = 288, ["thickness"] = 25 }, 290, 290, 45),
            Part("spring", "coil_spring", new JsonObject { ["free_length"] = 320, ["active_turns"] = 7 }, 125, 125, 335),
            Part("strut", "strut_tube", new JsonObject(), 55, 55, 405));
        var gen = await client.CallToolAsync(CodeCadToolProvider.GenerateTool, new JsonObject { ["outputFolder"] = folder, ["parts"] = parts }, TimeSpan.FromSeconds(30));
        Check.True(!gen.IsError, "generate: " + gen.Text);
        Check.True(gen.Text.Contains("1 part(s) were excluded by the user", StringComparison.Ordinal), "unticked strut reported");
        Check.True(gen.Text.Contains("fit-check models in millimetres", StringComparison.Ordinal) && ((string)gen.Structured!["validation"]!).Contains("NOT engineering-validated", StringComparison.Ordinal),
            "every result says fit-check, units mm, not engineering-validated");
        var made = gen.Structured!["parts"]!.AsArray().Select(p => (string)p!["step"]!).ToList();
        Check.True(made.Count == 3 && made.All(File.Exists) && !File.Exists(Path.Combine(folder, "strut.step")), "3 STEP files, no strut");
        Check.Equal(2, python.Scripts.Count, "generator + independent re-import validator");
        string script = File.ReadAllText((string)gen.Structured!["script"]!); // the record of what ran, kept next to the parts
        Check.True(!(bool)gen.Structured!["isolated"]! && FakeCad.ValidatorRuns > 0, "host path (no sandbox Python here) and the validator re-read the STEP files");
        Check.True(script.Contains("\\u0022bolt_circle_diameter\\u0022:112", StringComparison.Ordinal) && !script.Contains("strut_tube\\u0022,\\u0022params", StringComparison.Ordinal),
            "approved parameters embedded; the unticked part is not in the script");
        PythonSyntax((string)gen.Structured!["script"]!, "generate_parts.py");

        var bad = await client.CallToolAsync(CodeCadToolProvider.GenerateTool, new JsonObject
        {
            ["outputFolder"] = folder,
            ["parts"] = new JsonArray(Part("hub2", "wheel_hub", new JsonObject { ["bolt_circle_diameter"] = 60, ["hub_diameter"] = 70 }, 200, 200, 100)),
        }, TimeSpan.FromSeconds(30));
        Check.True(bad.IsError && bad.Text.Contains("bolt holes must sit on the flange", StringComparison.Ordinal), "impossible geometry refused before Python runs");
        var unknown = await client.CallToolAsync(CodeCadToolProvider.GenerateTool, new JsonObject { ["outputFolder"] = folder, ["parts"] = new JsonArray(Part("x", "piston", new JsonObject(), 1, 1, 1)) }, TimeSpan.FromSeconds(30));
        Check.True(unknown.IsError && unknown.Text.Contains("unknown template 'piston'", StringComparison.Ordinal), "unknown template refused");

        var imported = await client.CallToolAsync(SwxToolProvider.ImportStepTool, new JsonObject { ["paths"] = new JsonArray(made.Select(m => (JsonNode)m).ToArray()), ["outputFolder"] = folder }, TimeSpan.FromSeconds(30));
        Check.True(!imported.IsError && imported.Structured!["parts"]!.AsArray().Count == 3, "STEP → SLDPRT: " + imported.Text);
        var cards = await client.CallToolAsync(SwxToolProvider.PartCardsTool, new JsonObject { ["folder"] = folder }, TimeSpan.FromSeconds(30));
        Check.True(!cards.IsError && cards.Structured!["cards"]!.AsArray().Count == 3, "part cards for the imported parts");
        string P(string name) => Path.Combine(folder, name + ".SLDPRT");
        var build = await client.CallToolAsync(SwxToolProvider.BuildTool, new JsonObject
        {
            ["outputPath"] = Path.Combine(folder, "corner.SLDASM"),
            ["components"] = new JsonArray(new JsonObject { ["key"] = "hub", ["partPath"] = P("hub"), ["fixed"] = true },
                new JsonObject { ["key"] = "disc", ["partPath"] = P("disc") }, new JsonObject { ["key"] = "spring", ["partPath"] = P("spring") }),
            ["mates"] = new JsonArray(new JsonObject { ["key"] = "hub-disc", ["type"] = "Concentric", ["componentA"] = "hub", ["faceA"] = "c1", ["componentB"] = "disc", ["faceB"] = "c1", ["confidence"] = 0.9 }),
        }, TimeSpan.FromSeconds(30));
        Check.True(!build.IsError && swx.Builds.Single().Components.Count == 3, "assembled from the generated parts: " + build.Text);

        var audit = await runtime.Audit.ListAsync("cad-1");
        Check.True(audit.Any(r => r.Tool == CodeCadToolProvider.GenerateTool && r.Decision == "approved_subset" && r.Outcome == "ok"), "generation audited as approved_subset");
        Check.True(audit.Count(r => r.Tool == SwxToolProvider.ImportStepTool) == 1 && audit.Any(r => r.Tool == SwxToolProvider.BuildTool && r.Outcome == "ok"), "import and build audited");

        var custom = await client.CallToolAsync(CodeCadToolProvider.CustomTool, new JsonObject { ["outputFolder"] = folder, ["key"] = "bracket2", ["script"] = "result = Box(10, 20, 30)", ["envelopeMm"] = new JsonArray(11, 21, 31) }, TimeSpan.FromSeconds(30));
        var customAsk = prompt.Seen.Single(r => r.Tool == CodeCadToolProvider.CustomTool);
        Check.True(customAsk.Risk == ToolRisk.Destructive && ((string)customAsk.Preview!["rows"]![0]!["primary"]!).Contains("result = Box", StringComparison.Ordinal), "custom code is Destructive and shown to the user");
        // V6: no sandbox Python is configured for this runtime, so model-written code is refused, never run on the host.
        Check.True(custom.IsError && custom.Text.Contains("custom code runs only inside the sandbox", StringComparison.Ordinal), "custom code refused without a sandbox: " + custom.Text);
        Check.True(((string)customAsk.Preview!["summary"]!).Contains("CANNOT RUN", StringComparison.Ordinal), "the approval said it cannot run");
        Check.Equal(2, python.Scripts.Count, "nothing ran for the custom script (only the generator and validator of the template call)");
    }

    // ── Tutor ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Gemini V5 item 6: exact determinant, inverse, RREF and eigenpair checks on 3×3 matrices, no Python needed.</summary>
    public static async Task MatrixVerifier(string bridgeDll)
    {
        await using var runtime = await Runtime(bridgeDll, new UntickPrompt());
        string folder = TempDir();
        var job = await runtime.StartJobAsync(TutorConnector.Tutor(folder, "chapter 3", LaunchMode.None) with { JobId = "tut-1" });
        await using var client = await runtime.ConnectClientAsync(job);
        JsonArray M(params int[][] rows) => new(rows.Select(r => (JsonNode)new JsonArray(r.Select(x => (JsonNode)x).ToArray())).ToArray());

        var a = M([2, 1, 1], [1, 2, 1], [1, 1, 2]);
        var r = await client.CallToolAsync(TutorToolProvider.VerifyTool, new JsonObject
        {
            ["A"] = a,
            ["operations"] = new JsonArray("determinant", "inverse", "rref", "rank", "charpoly", "eigenvalues"),
            ["eigenpairs"] = new JsonArray(
                new JsonObject { ["lambda"] = 4, ["vector"] = new JsonArray(1, 1, 1) },
                new JsonObject { ["lambda"] = "1", ["vector"] = new JsonArray(1, -1, 0) },
                new JsonObject { ["lambda"] = 2, ["vector"] = new JsonArray(1, 0, 0) },
                new JsonObject { ["lambda"] = 1, ["vector"] = new JsonArray(1, 1, 1) },
                new JsonObject { ["lambda"] = 4, ["vector"] = new JsonArray(0, 0, 0) }),
            ["claims"] = new JsonObject
            {
                ["determinant"] = "4", ["rank"] = 2,
                ["inverse"] = new JsonArray(new JsonArray("3/4", "-1/4", "-0.25"), new JsonArray("-1/4", "3/4", "-1/4"), new JsonArray("-1/4", "-1/4", "0.75")),
            },
            ["sympy"] = true,
        }, TimeSpan.FromSeconds(30));
        Check.True(!r.IsError, "verify: " + r.Text);
        var s = r.Structured!;
        Check.Equal("4", (string?)s["determinant"], "det = 4");
        Check.Equal("3/4", (string?)s["inverse"]![0]![0], "inverse[0][0] = 3/4 exactly");
        Check.Equal("-1/4", (string?)s["inverse"]![2]![1], "inverse[2][1] = -1/4");
        Check.True(s["rref"]!.ToJsonString() == M([1, 0, 0], [0, 1, 0], [0, 0, 1]).ToJsonString().Replace("1", "\"1\"").Replace("0", "\"0\""), "rref = I");
        Check.Equal("[\"1\",\"-6\",\"9\",\"-4\"]", s["charpoly"]!.ToJsonString(), "det(λI−A) = λ³ − 6λ² + 9λ − 4");
        Check.Equal("[\"1\",\"4\"]", s["eigenvaluesExactRational"]!.ToJsonString(), "exact eigenvalues 1 and 4");
        var numeric = s["eigenvaluesNumeric"]!.AsArray().Select(x => double.Parse((string)x!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Check.True(numeric.Count == 3 && Math.Abs(numeric[0] - 1) < 1e-5 && Math.Abs(numeric[1] - 1) < 1e-5 && Math.Abs(numeric[2] - 4) < 1e-9, "numeric eigenvalues ≈ 1, 1, 4");
        var pairs = s["eigenpairs"]!.AsArray();
        Check.True((bool)pairs[0]!["isEigenpair"]! && (bool)pairs[1]!["isEigenpair"]!, "(4, [1,1,1]) and (1, [1,-1,0]) are eigenpairs");
        Check.True(!(bool)pairs[2]!["isEigenpair"]! && !(bool)pairs[2]!["lambdaIsEigenvalue"]!, "2 is not an eigenvalue");
        Check.True(!(bool)pairs[3]!["isEigenpair"]! && (bool)pairs[3]!["lambdaIsEigenvalue"]!, "1 is an eigenvalue but [1,1,1] is not its eigenvector");
        Check.True(!(bool)pairs[4]!["isEigenpair"]!, "the zero vector is never an eigenvector");
        Check.True((bool)s["claims"]!["determinant"]! && !(bool)s["claims"]!["rank"]! && (bool)s["claims"]!["inverse"]!, "student claims graded: det right, rank wrong, inverse right (decimals read exactly)");
        Check.True(File.Exists((string)s["sympyScript"]!), "SymPy script written to the job's artifacts");
        PythonSyntax((string)s["sympyScript"]!, "check_matrix.py");

        var singular = await client.CallToolAsync(TutorToolProvider.VerifyTool, new JsonObject { ["A"] = M([1, 2, 3], [4, 5, 6], [7, 8, 9]) }, TimeSpan.FromSeconds(30));
        var ss = singular.Structured!;
        Check.True((string?)ss["determinant"] == "0" && (int)ss["rank"]! == 2 && !(bool)ss["invertible"]!, "singular: det 0, rank 2, no inverse");
        Check.Equal("[[\"1\",\"0\",\"-1\"],[\"0\",\"1\",\"2\"],[\"0\",\"0\",\"0\"]]", ss["rref"]!.ToJsonString(), "rref of the classic singular matrix");

        var product = await client.CallToolAsync(TutorToolProvider.VerifyTool, new JsonObject
        {
            ["A"] = M([1, 2], [3, 4]), ["B"] = new JsonArray(new JsonArray("1/2", 0), new JsonArray(0, "1/3")), ["operations"] = new JsonArray("multiply"),
            ["claims"] = new JsonObject { ["product"] = new JsonArray(new JsonArray("1/2", "2/3"), new JsonArray("3/2", "4/3")) },
        }, TimeSpan.FromSeconds(30));
        Check.True((bool)product.Structured!["claims"]!["product"]!, "A·B with fractions checked exactly");
        var mismatch = await client.CallToolAsync(TutorToolProvider.VerifyTool, new JsonObject { ["A"] = M([1, 2]), ["B"] = M([1, 2]), ["operations"] = new JsonArray("multiply") }, TimeSpan.FromSeconds(30));
        Check.True(mismatch.IsError && mismatch.Text.Contains("inner sizes differ", StringComparison.Ordinal), "size mismatch explained");
    }

    /// <summary>A hand-built PDF (compressed object stream, Flate content, a Type0 font with a ToUnicode map) read by the built-in reader.</summary>
    public static async Task PdfChapter(string bridgeDll)
    {
        string folder = TempDir();
        string pdf = Path.Combine(folder, "book.pdf");
        File.WriteAllBytes(pdf, TestPdf.Build());
        await using var runtime = await Runtime(bridgeDll, new UntickPrompt());
        var job = await runtime.StartJobAsync(TutorConnector.Tutor(folder, "chapter 3", LaunchMode.None) with { JobId = "tut-2" });
        await using var client = await runtime.ConnectClientAsync(job);
        var r = await client.CallToolAsync(TutorToolProvider.ExtractTool, new JsonObject { ["pdfPath"] = pdf, ["firstPage"] = 2, ["lastPage"] = 3 }, TimeSpan.FromSeconds(30));
        Check.True(!r.IsError, "extract: " + r.Text);
        Check.True(r.Text.Contains("## Page 2", StringComparison.Ordinal) && r.Text.Contains("3.2 Determinants", StringComparison.Ordinal) && !r.Text.Contains("Chapter 3 intro", StringComparison.Ordinal), "only pages 2–3");
        Check.True(r.Text.Contains("(Ax = b) is solvable", StringComparison.Ordinal), "literal string with escaped parentheses");
        Check.True(r.Text.Contains("det(λI − A) = 0", StringComparison.Ordinal), "Type0 font decoded through its ToUnicode map (λ, −)");
        Check.True(r.Text.Contains("Eigen values", StringComparison.Ordinal), "a wide TJ gap becomes a space");
        Check.True(((string)r.Structured!["engine"]!).StartsWith("built-in", StringComparison.Ordinal), "built-in reader when Python is unavailable");
        string pack = (string)r.Structured["studyPack"]!;
        Check.True(File.Exists(pack) && pack.Contains(Path.Combine("tut-2", "artifacts"), StringComparison.Ordinal), "study pack in the job's artifacts, not next to the book");
        var outside = await client.CallToolAsync(TutorToolProvider.ExtractTool, new JsonObject { ["pdfPath"] = Path.Combine(Path.GetTempPath(), "x.pdf"), ["firstPage"] = 1, ["lastPage"] = 1 }, TimeSpan.FromSeconds(30));
        Check.True(outside.IsError && outside.Text.Contains("outside", StringComparison.Ordinal), "PDF must be inside the job's folders");
    }
}

/// <summary>Writes a small but realistic PDF 1.5: pages 2-3 live in Flate streams, fonts sit in a compressed object stream.</summary>
static class TestPdf
{
    static byte[] Flate(string s)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(Encoding.Latin1.GetBytes(s));
        return ms.ToArray();
    }

    public static byte[] Build()
    {
        var objs = new Dictionary<int, byte[]>();
        void Obj(int n, string body) => objs[n] = Encoding.Latin1.GetBytes(body);
        void Stream(int n, string dict, byte[] data, bool flate = true) =>
            objs[n] = [.. Encoding.Latin1.GetBytes($"<< {dict} /Length {data.Length}{(flate ? " /Filter /FlateDecode" : "")} >>\nstream\n"), .. data, .. "\nendstream"u8.ToArray()];

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /Resources << /Font << /F1 10 0 R /F2 11 0 R >> >> >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /Contents 20 0 R >>");
        Obj(4, "<< /Type /Page /Parent 2 0 R /Contents [21 0 R 22 0 R] >>");
        Obj(5, "<< /Type /Page /Parent 2 0 R /Contents 23 0 R >>");
        Stream(20, "", Encoding.Latin1.GetBytes("BT /F1 12 Tf 72 720 Td (Chapter 3 intro) Tj ET"), flate: false);
        Stream(21, "", Flate("BT /F1 14 Tf 72 720 Td (3.2 Determinants) Tj 0 -20 Td (A system \\(Ax = b\\) is solvable) Tj ET"));
        Stream(22, "", Flate("BT /F1 12 Tf 72 680 Td [(when det)-50(\\(A\\) ) (\\226 0.)] TJ ET"));
        // Page 3: Type0 font, 2-byte codes 0001=λ 0002=− 0003=I 0004=A 0005..= via bfrange onto "det() =0" etc.
        Stream(23, "", Flate("BT /F2 12 Tf 72 720 Td <000600070008000900010003000b0002000b0004000a000b000c000b000d> Tj ET BT /F1 12 Tf 72 700 Td [(Eigen)-400(values)] TJ ET"));
        string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
            + "7 beginbfchar <0001> <03BB> <0002> <2212> <0003> <0049> <0004> <0041> <0006> <0064> <0007> <0065> <0008> <0074> endbfchar\n"
            + "1 beginbfrange <0009> <000d> [<0028> <0029> <0020> <003D> <0030>] endbfrange\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end";
        Stream(24, "", Flate(cmap));
        // Object stream holding the two font dictionaries (10 and 11).
        string f10 = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
        string f11 = "<< /Type /Font /Subtype /Type0 /BaseFont /DejaVu /Encoding /Identity-H /ToUnicode 24 0 R >>";
        string header = $"10 0 11 {f10.Length + 1} ";
        Stream(30, $"/Type /ObjStm /N 2 /First {header.Length}", Flate(header + f10 + " " + f11));

        var ms = new MemoryStream();
        ms.Write("%PDF-1.5\n%\xE2\xE3\xCF\xD3\n"u8);
        foreach (var (n, body) in objs.OrderBy(kv => kv.Key))
        {
            ms.Write(Encoding.ASCII.GetBytes($"{n} 0 obj\n"));
            ms.Write(body);
            ms.Write("\nendobj\n"u8);
        }
        ms.Write("trailer\n<< /Root 1 0 R /Size 31 >>\n%%EOF\n"u8);
        return ms.ToArray();
    }
}
