using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Connectors.Resolve;

/// <summary>V18 connector: DaVinci Resolve rough cuts: probe the clips, plan the cut, export FCPXML + EDL (or build it in Studio).</summary>
public sealed class ResolveConnector : IMediatorConnector
{
    public string Id => "resolve";
    public string DisplayName => "DaVinci Resolve";
    public string Category => "Creative";
    public string Summary => "Rough-cut timelines from a clips folder (FCPXML/EDL), built in Resolve Studio when its scripting is on.";

    /// <summary>How clips are measured. Null: ffprobe when installed, else the built-in reader.</summary>
    public IMediaProbe? MediaProbe { get; init; }
    /// <summary>Python for the Resolve scripting bridge. Null: the runtime's.</summary>
    public IPythonRunner? Python { get; init; }

    public static readonly string[] Tools = [ResolveToolProvider.ProbeTool, ResolveToolProvider.ExportTool, ResolveToolProvider.ApplyTool];
    public IReadOnlyList<string> ChatTools => Tools;

    public ConnectorInstance Start(ConnectorContext context) =>
        new([new ResolveToolProvider(MediaProbe ?? CompositeMediaProbe.Detect(context.ProcessRunner), Python ?? context.Python, context.Jobs)]);

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
        [new(70, TaskCategory.Resolve, Tools, r => r.AnyWord(["davinci", "rough cut", "footage", "timeline"]) || r.AppIs("resolve"))];

    public IReadOnlyList<ConnectorRecipe> Recipes { get; } =
    [
        new("resolve", 40, "Rough cut for DaVinci Resolve", "Probe the clips, plan the cut, review it, export FCPXML + EDL to import in one step.", "\uE714",
            TaskCategory.Resolve, true, ["davinci", "resolve", "video", "clips", "edit my", "timeline", "rough cut", "footage"], Tools,
            r => ResolveEdit(r.Folder!, r.Prompt.Length > 0 ? r.Prompt : "a short rough cut in a sensible order", r.Launch)),
    ];

    public Task<IReadOnlyList<ConnectorCheck>> DoctorAsync(ConnectorInstance? instance, bool deep, CancellationToken ct)
    {
        var checks = new List<ConnectorCheck>
        {
            new("ffprobe", ToolLocator.Ffprobe() is { } ffprobe ? "ok" : "warn", ToolLocator.Ffprobe() ?? "not on PATH — clip probing uses the built-in MP4/MOV/WAV reader"),
        };
        if (OperatingSystem.IsWindows())
        {
            checks.Add(new("DaVinci Resolve install", ComponentLocator.FindDesktopApp(DesktopApp.Resolve) is null ? "warn" : "ok", DesktopAppLocator.Default.Summary(DesktopApp.Resolve)));
            string api = ResolveScript.Environment().GetValueOrDefault("RESOLVE_SCRIPT_API") ?? "";
            checks.Add(new("Resolve scripting", Directory.Exists(api) ? "ok" : "warn", Directory.Exists(api)
                ? api + " (external scripting needs Resolve Studio with Preferences > General > External scripting: Local)"
                : "DaVinci Resolve not found — timelines are still exported as FCPXML/EDL"));
        }
        return Task.FromResult<IReadOnlyList<ConnectorCheck>>(checks);
    }

    public IReadOnlyList<ConnectorProgram> Programs { get; } =
        [new("resolve", "DaVinci Resolve", "\uE714", "Video timelines from your clips", "Creative", "rough-cut timelines from a clips folder (FCPXML/EDL)", Process: "resolve")];

    public AppStatus ProbeProgram(string programId, ProgramProbe probe)
    {
        if (!probe.WindowsApps) return new AppStatus("resolve", "DaVinci Resolve", false, "Windows only");
        var install = probe.Apps.Find(DesktopApp.Resolve);
        if (install is null)
            return new AppStatus("resolve", "DaVinci Resolve", false, $"not installed (looked in {string.Join(", ", probe.Apps.SearchedLocations(DesktopApp.Resolve))})");
        string edition = (install.ProductName ?? "").Contains("Studio", StringComparison.OrdinalIgnoreCase)
            ? "Studio: the scripting bridge can create the timeline (enable External scripting: Local)"
            : "edition not confirmed as Studio: FCPXML/EDL import works in both; the scripting bridge needs Studio";
        string running = probe.Running("Resolve") is { } id ? $"running (PID {id})" : "not running";
        return new AppStatus("resolve", "DaVinci Resolve", true, $"{running}; {edition}; {ProgramProbe.Label(install)}", install.Executable);
    }

    /// <summary>Prompt 2: a rough cut of the clips in a folder, as a Resolve-importable timeline.</summary>
    public static JobSpec ResolveEdit(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "resolve", Category = TaskCategory.Resolve, Title = $"Rough cut of {Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}",
            Tools = Tools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = 25,
            Prompt = $$"""
                Edit the video clips in {{full}}: {{request}}
                1. Call resolve_probe_clips with {"folder": "{{JobSpecFactory.J(full)}}"}.
                2. Plan a rough cut from file names, durations and the request: order, in/out points (seconds), dissolves where they help,
                   music or B-roll on track 2 with timelineSec. Give each cut a short reason. You cannot see the footage: say so, and keep
                   in/out points conservative.
                3. Call resolve_export_timeline. The user reviews every cut.
                4. Tell the user to import the .fcpxml in DaVinci Resolve (File > Import > Timeline). Only call resolve_apply_studio with
                   run=true if the user said they use Resolve Studio with external scripting enabled.
                """,
        };
    }
}
