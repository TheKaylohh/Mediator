using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Cockpit.Core.Bus;
using Cockpit.Core.Security;
using Cockpit.Core.Tools;

namespace Cockpit.Core.Resolve;

/// <summary>
/// DaVinci Resolve without driving its UI: probe the clips, build the rough cut as FCPXML 1.9 + EDL that Resolve
/// imports in one step (Free and Studio), and optionally run a script against Resolve's scripting API to create the
/// project. The model refers to clips by id ("c1"); paths and durations come from the probe cache, so it cannot cut
/// outside a clip or point at a file it was not shown.
/// </summary>
public sealed class ResolveToolProvider : IToolProvider
{
    public const string Lease = "resolve";
    public const string ProbeTool = "resolve_probe_clips";
    public const string ExportTool = "resolve_export_timeline";
    public const string ApplyTool = "resolve_apply_studio";
    private const int MaxClips = 500;
    private static readonly string[] MediaExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".mxf", ".wav", ".mp3", ".m4a", ".aac"];

    private readonly IMediaProbe _probe;
    private readonly IPythonRunner _python;
    private readonly ConcurrentDictionary<(string JobId, string ClipId), ClipInfo> _clips = new();

    public ResolveToolProvider(IMediaProbe probe, IPythonRunner python, JobRegistry jobs)
    {
        _probe = probe;
        _python = python;
        jobs.JobRevoked += jobId => { foreach (var k in _clips.Keys.Where(k => k.JobId == jobId).ToList()) _clips.TryRemove(k, out _); };
    }

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ProbeTool,
            "List the video and audio clips in a folder with duration (s), frame rate, resolution and audio streams. Read-only. " +
            "Returns clip ids (c1, c2, …) that resolve_export_timeline uses.",
            Schema("""{ "type": "object", "required": ["folder"], "properties": { "folder": { "type": "string" }, "recursive": { "type": "boolean" } }, "additionalProperties": false }"""),
            ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(10)),

        new(ExportTool,
            "Write a rough-cut timeline as Final Cut Pro XML 1.9 and a CMX3600 EDL into a new 'edits' folder next to the clips " +
            "(never overwrites). DaVinci Resolve imports it with File > Import > Timeline. Track 1 plays in order; tracks 2+ " +
            "(B-roll, music) need timelineSec. The user reviews every cut and may drop some.",
            Schema("""
            {
              "type": "object",
              "required": ["name", "cuts"],
              "properties": {
                "name": { "type": "string" },
                "fps":  { "type": "string", "description": "Sequence frame rate, e.g. 24, 25, 29.97, 30000/1001. Default: the first video clip's." },
                "cuts": { "type": "array", "items": { "type": "object", "required": ["key", "clip", "inSec", "outSec"],
                  "properties": {
                    "key": { "type": "string" }, "clip": { "type": "string", "description": "Clip id from resolve_probe_clips." },
                    "inSec": { "type": "number" }, "outSec": { "type": "number" },
                    "track": { "type": "integer" }, "timelineSec": { "type": "number" },
                    "transition": { "type": "string", "enum": ["cut", "dissolve"] }, "transitionSec": { "type": "number" },
                    "reason": { "type": "string" } } } }
              },
              "additionalProperties": false
            }
            """), ToolRisk.Write, null, TimeSpan.FromMinutes(5)),

        new(ApplyTool,
            "Create a Resolve project from an exported .fcpxml through Resolve's scripting API: new project, clips into the Media " +
            "Pool, timeline imported. Writes apply_resolve.py next to the timeline; runs it only if run=true. External scripting " +
            "needs DaVinci Resolve Studio running with Preferences > General > External scripting set to Local; with Resolve Free, " +
            "import the .fcpxml by hand instead.",
            Schema("""{ "type": "object", "required": ["fcpxmlPath", "projectName"], "properties": { "fcpxmlPath": { "type": "string" }, "projectName": { "type": "string" }, "run": { "type": "boolean" } }, "additionalProperties": false }"""),
            ToolRisk.Write, Lease, TimeSpan.FromMinutes(10)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(call.Tool.Name switch
    {
        ExportTool => ExportPreview(call),
        ApplyTool => new JsonObject
        {
            ["kind"] = "resolve_apply",
            ["title"] = "Create a DaVinci Resolve project",
            ["summary"] = $"Project '{Str(call.Arguments, "projectName")}' from {Path.GetFileName(Str(call.Arguments, "fcpxmlPath"))}" +
                (call.Arguments.TryGetProperty("run", out var r) && r.ValueKind == JsonValueKind.True ? " — the script will RUN against Resolve now." : " — script written only, not run."),
            ["selectable"] = false,
            ["rows"] = new JsonArray(),
        },
        _ => null,
    });

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            return call.Tool.Name switch
            {
                ProbeTool => await ProbeAsync(call, ct).ConfigureAwait(false),
                ExportTool => Export(call),
                ApplyTool => await ApplyAsync(call, ct).ConfigureAwait(false),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            };
        }
        catch (FormatException ex) { return ToolResult.Fail(ex.Message); }
    }

    // ── probe ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<ToolResult> ProbeAsync(ToolCallContext call, CancellationToken ct)
    {
        var folderVerdict = call.Job.Paths.Check(Str(call.Arguments, "folder"), PathAccess.List);
        if (!folderVerdict.Allowed) return ToolResult.Fail(folderVerdict.Message);
        string folder = folderVerdict.FullPath;
        bool recursive = call.Arguments.TryGetProperty("recursive", out var rec) && rec.ValueKind == JsonValueKind.True;
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true })
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains($"{Path.DirectorySeparatorChar}edits{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxClips + 1).ToList();
        // Every file is authorized on its own: a link inside the folder (or a linked subfolder) is skipped and reported.
        var refused = files.Select(f => call.Job.Paths.Check(f, PathAccess.Read)).Where(v => !v.Allowed).ToList();
        files = files.Except(refused.Select(v => v.FullPath)).ToList();
        if (files.Count == 0 && refused.Count > 0) return ToolResult.Fail("Every media file was refused: " + string.Join("; ", refused.Take(5).Select(v => v.Message)));
        if (files.Count == 0) return ToolResult.Fail($"No video or audio files ({string.Join(" ", MediaExtensions)}) in {folder}.");
        if (files.Count > MaxClips) return ToolResult.Fail($"More than {MaxClips} media files; pick a smaller folder.");

        var clips = new JsonArray();
        var unreadable = new JsonArray();
        int n = 0;
        foreach (var f in files)
        {
            string id = "c" + (++n).ToString(CultureInfo.InvariantCulture);
            var info = await _probe.ProbeAsync(f, id, ct).ConfigureAwait(false);
            if (info is null) { unreadable.Add(Path.GetFileName(f)); continue; }
            _clips[(call.Job.JobId, id)] = info;
            clips.Add(new JsonObject
            {
                ["id"] = id, ["file"] = info.FileName, ["durationSec"] = Math.Round(info.DurationSec, 3),
                ["fps"] = info.Fps?.ToString(), ["width"] = info.Width, ["height"] = info.Height,
                ["video"] = info.HasVideo, ["audioStreams"] = info.AudioStreams, ["codec"] = info.Codec,
            });
        }
        var result = new JsonObject
        {
            ["folder"] = folder, ["probedWith"] = _probe.Describe, ["clips"] = clips,
            ["unreadable"] = unreadable.Count == 0 ? null : unreadable,
            ["refused"] = refused.Count == 0 ? null : new JsonArray(refused.Select(v => (JsonNode)v.Message).ToArray()),
        };
        return clips.Count == 0 ? ToolResult.Fail("No clip could be read: " + result.ToJsonString()) : ToolResult.Ok(result.ToJsonString(), result);
    }

    // ── export ────────────────────────────────────────────────────────────────────────────────────────

    private (TimelinePlan Plan, string OutputFolder) ParsePlan(ToolCallContext call, IReadOnlySet<string>? keep)
    {
        var a = call.Arguments;
        var cuts = new List<EditCut>();
        foreach (var c in a.GetProperty("cuts").EnumerateArray())
        {
            string key = Str(c, "key"), clipId = Str(c, "clip");
            if (keep is not null && !keep.Contains(key)) continue;
            if (!_clips.TryGetValue((call.Job.JobId, clipId), out var clip))
                throw new FormatException($"{key}: unknown clip '{clipId}'. Call resolve_probe_clips first and use its ids.");
            cuts.Add(new EditCut(key, clip, Num(c, "inSec")!.Value, Num(c, "outSec")!.Value,
                c.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 1,
                Num(c, "timelineSec"),
                c.TryGetProperty("transition", out var tr) && tr.GetString() == "dissolve" ? CutTransition.Dissolve : CutTransition.Cut,
                Num(c, "transitionSec") ?? 1.0));
        }
        if (cuts.Count == 0) throw new FormatException("No cuts left to export.");
        if (cuts.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != cuts.Count) throw new FormatException("Cut keys must be unique.");
        var firstVideo = cuts.Select(c => c.Clip).FirstOrDefault(c => c.HasVideo);
        var fps = a.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.String ? FrameRate.Parse(f.GetString()!) : firstVideo?.Fps ?? new FrameRate(25, 1);
        string folder = Path.Combine(Path.GetDirectoryName(cuts[0].Clip.Path)!, "edits");
        var verdict = call.Job.Paths.Check(folder, Directory.Exists(folder) ? PathAccess.List : PathAccess.CreateDirectory);
        if (!verdict.Allowed) throw new FormatException(verdict.Message);
        return (new TimelinePlan(Str(a, "name"), fps, firstVideo?.Width ?? 1920, firstVideo?.Height ?? 1080, cuts), folder);
    }

    private JsonNode ExportPreview(ToolCallContext call)
    {
        try
        {
            var (plan, folder) = ParsePlan(call, null);
            var (main, connected, total) = TimelineWriter.Layout(plan);
            var reasons = call.Arguments.GetProperty("cuts").EnumerateArray().ToDictionary(c => Str(c, "key"), c => c.TryGetProperty("reason", out var r) ? r.GetString() : null);
            return new JsonObject
            {
                ["kind"] = "resolve_timeline",
                ["title"] = $"Export timeline '{plan.Name}'",
                ["summary"] = string.Create(CultureInfo.InvariantCulture, $"{main.Count + connected.Count} cuts, {total / plan.Fps.Fps:0.0} s at {plan.Fps} fps → {folder}"),
                ["selectable"] = true,
                ["rows"] = new JsonArray(main.Concat(connected).Select(p => (JsonNode)new JsonObject
                {
                    ["key"] = p.Cut.Key,
                    ["primary"] = string.Create(CultureInfo.InvariantCulture, $"{p.Cut.Clip.FileName}  {p.Cut.InSec:0.00}–{p.Cut.OutSec:0.00} s  ({p.DurationFrames / plan.Fps.Fps:0.0} s)"),
                    ["detail"] = string.Create(CultureInfo.InvariantCulture, $"track {p.Cut.Track} at {p.OffsetFrames / plan.Fps.Fps:0.00} s, {(p.Cut.Transition == CutTransition.Dissolve ? $"dissolve {p.Cut.TransitionSec:0.0} s" : "cut")}")
                        + (reasons.GetValueOrDefault(p.Cut.Key) is { Length: > 0 } why ? " · " + why : ""),
                }).ToArray()),
            };
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return new JsonObject { ["kind"] = "resolve_timeline", ["title"] = "Invalid timeline", ["summary"] = ex.Message, ["selectable"] = false, ["rows"] = new JsonArray() };
        }
    }

    private ToolResult Export(ToolCallContext call)
    {
        var (plan, folder) = ParsePlan(call, call.ApprovedRows);
        int proposed = call.Arguments.GetProperty("cuts").GetArrayLength();
        string xml = TimelineWriter.Fcpxml(plan);
        string edl = TimelineWriter.Edl(plan);
        if (!Directory.Exists(folder)) call.Job.Paths.EnsureDirectory(folder);
        string stem = SafeFiles.Slug(plan.Name, "timeline");
        string xmlPath = SafeFiles.Unique(folder, stem + ".fcpxml");
        call.Job.Paths.WriteNewText(xmlPath, xml);
        string edlPath = SafeFiles.Unique(folder, Path.GetFileNameWithoutExtension(xmlPath) + ".edl");
        call.Job.Paths.WriteNewText(edlPath, edl);
        var (_, _, total) = TimelineWriter.Layout(plan);
        string text = string.Create(CultureInfo.InvariantCulture,
            $"Wrote {xmlPath} and {edlPath}: {plan.Cuts.Count} cuts, {total / plan.Fps.Fps:0.0} s at {plan.Fps} fps.")
            + (plan.Cuts.Count < proposed ? $" {proposed - plan.Cuts.Count} cut(s) were excluded by the user; do not add them back." : "")
            + " In Resolve: File > Import > Timeline and pick the .fcpxml (keep the clips where they are), or call resolve_apply_studio.";
        return ToolResult.Ok(text, new JsonObject { ["fcpxml"] = xmlPath, ["edl"] = edlPath, ["cuts"] = plan.Cuts.Count, ["durationSec"] = Math.Round(total / plan.Fps.Fps, 3) });
    }

    // ── apply (Studio scripting) ──────────────────────────────────────────────────────────────────────

    private async Task<ToolResult> ApplyAsync(ToolCallContext call, CancellationToken ct)
    {
        var xmlVerdict = call.Job.Paths.Check(Str(call.Arguments, "fcpxmlPath"), PathAccess.Read);
        if (!xmlVerdict.Allowed) return ToolResult.Fail(xmlVerdict.Message);
        string xmlPath = xmlVerdict.FullPath;
        if (!xmlPath.EndsWith(".fcpxml", StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail($"{xmlPath} is not an .fcpxml file.");
        // Media comes from the timeline file. The file may have been edited since export, so every media path is
        // authorized again: a timeline cannot make Resolve import files from outside the job's folders.
        XDocument timeline;
        using (var xs = call.Job.Paths.OpenRead(xmlPath))
            timeline = XDocument.Load(System.Xml.XmlReader.Create(xs, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null }));
        var media = new List<string>();
        foreach (var src in timeline.Descendants("media-rep").Select(m => (string?)m.Attribute("src")).Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(src, UriKind.Absolute, out var uri) || !uri.IsFile) return ToolResult.Fail($"The timeline references '{src}', which is not a local file URL.");
            var mv = call.Job.Paths.Check(uri.LocalPath, PathAccess.Read);
            if (!mv.Allowed) return ToolResult.Fail("The timeline references media that is not allowed: " + mv.Message);
            media.Add(mv.FullPath);
        }
        if (media.Count == 0) return ToolResult.Fail("The timeline lists no media.");
        string project = Str(call.Arguments, "projectName");
        string script = SafeFiles.Unique(Path.GetDirectoryName(xmlPath)!, "apply_resolve.py");
        call.Job.Paths.WriteNewText(script, ResolveScript.Render(project, xmlPath, media));

        bool run = call.Arguments.TryGetProperty("run", out var r) && r.ValueKind == JsonValueKind.True;
        if (!run) return ToolResult.Ok($"Wrote {script}. Run it with Resolve Studio open (python \"{script}\"), or import {Path.GetFileName(xmlPath)} by hand (File > Import > Timeline).",
            new JsonObject { ["script"] = script, ["ran"] = false });
        if (!_python.IsAvailable) return ToolResult.Fail($"Wrote {script} but cannot run it: {_python.Describe}.");

        var result = await _python.RunScriptAsync(script, [], ResolveScript.Environment(), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        string lastLine = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        JsonNode? report = null;
        try { report = JsonNode.Parse(lastLine); } catch (JsonException) { }
        bool ok = result.Ok && (bool?)report?["ok"] == true;
        return new ToolResult(!ok, ok
            ? $"Resolve project '{project}' created: {report?["clips"]} clips imported, timeline '{report?["timeline"]}' ({report?["method"]})."
            : $"The Resolve script failed: {(string?)report?["error"] ?? result.Tail()}", new JsonObject { ["script"] = script, ["ran"] = true, ["report"] = report?.DeepClone() });
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>The DaVinci Resolve scripting script. Names from Resolve's scripting README (Developer/Scripting); verify against your version.</summary>
public static class ResolveScript
{
    public static IReadOnlyDictionary<string, string> Environment()
    {
        var env = new Dictionary<string, string>();
        if (!OperatingSystem.IsWindows()) return env;
        string programData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
        // fusionscript.dll sits next to Resolve.exe: use the installation actually found (V8), else the default folder.
        string installDir = Runtime.ComponentLocator.FindDesktopApp(Runtime.DesktopApp.Resolve)?.InstallDirectory
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Blackmagic Design", "DaVinci Resolve");
        env["RESOLVE_SCRIPT_API"] = Path.Combine(programData, "Blackmagic Design", "DaVinci Resolve", "Support", "Developer", "Scripting");
        env["RESOLVE_SCRIPT_LIB"] = Path.Combine(installDir, "fusionscript.dll");
        env["PYTHONPATH"] = Path.Combine(env["RESOLVE_SCRIPT_API"], "Modules");
        return env;
    }

    public static string Render(string projectName, string fcpxmlPath, IReadOnlyList<string> media)
    {
        static string Py(string s) => JsonSerializer.Serialize(s); // a JSON string is a valid Python string literal
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by AI Cockpit. Creates a DaVinci Resolve project from a Cockpit timeline.");
        sb.AppendLine("# Needs DaVinci Resolve Studio running, with Preferences > System > General > External scripting using: Local.");
        sb.AppendLine("import json, os, sys");
        sb.AppendLine("api = os.environ.get('RESOLVE_SCRIPT_API', r'C:\\ProgramData\\Blackmagic Design\\DaVinci Resolve\\Support\\Developer\\Scripting')");
        sb.AppendLine("sys.path.append(os.path.join(api, 'Modules'))");
        sb.AppendLine("def done(**r):\n    print(json.dumps(r))\n    sys.exit(0 if r.get('ok') else 2)");
        sb.AppendLine("try:\n    import DaVinciResolveScript as dvr\nexcept ImportError as e:\n    done(ok=False, error='DaVinciResolveScript not found (' + str(e) + '). Is Resolve installed? Check RESOLVE_SCRIPT_API.')");
        sb.AppendLine("resolve = dvr.scriptapp('Resolve')");
        sb.AppendLine("if resolve is None:\n    done(ok=False, error='Could not connect to Resolve. Start DaVinci Resolve Studio and enable external scripting (Local). Resolve Free allows scripts only from its own console.')");
        sb.AppendLine(CultureInfo.InvariantCulture, $"name = {Py(projectName)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"timeline_file = {Py(fcpxmlPath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"media = {JsonSerializer.Serialize(media)}");
        sb.AppendLine("pm = resolve.GetProjectManager()");
        sb.AppendLine("project = pm.CreateProject(name)");
        sb.AppendLine("if not project:\n    done(ok=False, error='A project named ' + repr(name) + ' already exists (or the database is read-only). Choose another projectName.')");
        sb.AppendLine("pool = project.GetMediaPool()");
        sb.AppendLine("items = pool.ImportMedia(media) or []");
        sb.AppendLine("timeline = pool.ImportTimelineFromFile(timeline_file, {'timelineName': name, 'importSourceClips': False})");
        sb.AppendLine("method = 'ImportTimelineFromFile'");
        sb.AppendLine("if not timeline:");
        sb.AppendLine("    timeline = pool.CreateEmptyTimeline(name)");
        sb.AppendLine("    method = 'CreateEmptyTimeline+AppendToTimeline (FCPXML import failed; cuts not applied)'");
        sb.AppendLine("    if timeline and items:\n        pool.AppendToTimeline(items)");
        sb.AppendLine("pm.SaveProject()");
        sb.AppendLine("resolve.OpenPage('edit')");
        sb.AppendLine("done(ok=bool(timeline), clips=len(items), timeline=name if timeline else None, method=method, error=None if timeline else 'No timeline could be created.')");
        return sb.ToString();
    }
}
