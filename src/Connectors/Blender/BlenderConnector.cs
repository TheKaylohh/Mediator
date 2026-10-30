using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Connectors.Blender;

/// <summary>V18 connector: Blender scenes (a car driving through a procedural city) and preview renders.</summary>
public sealed class BlenderConnector : IMediatorConnector
{
    public string Id => "blender";
    public string DisplayName => "Blender";
    public string Category => "Creative";
    public string Summary => "Build an animated 3D scene and render a preview.";

    /// <summary>How Blender is run. Null: MEDIATOR_BLENDER, PATH or the newest install found.</summary>
    public IBlenderRunner? Blender { get; init; }
    /// <summary>ffmpeg for preview videos (null: none, frames only).</summary>
    public string? Ffmpeg { get; init; } = ToolLocator.Ffmpeg();

    public static readonly string[] Tools = [BlenderToolProvider.BuildTool, BlenderToolProvider.RenderTool];
    public IReadOnlyList<string> ChatTools => Tools;

    public ConnectorInstance Start(ConnectorContext context) =>
        new([new BlenderToolProvider(Blender ?? BlenderRunner.Detect(context.ProcessRunner), context.ProcessRunner, Ffmpeg)]);

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
        [new(80, TaskCategory.Blender, Tools, r => r.AnyWord(["blender", "render", "3d scene"]) || r.AppIs("blender"))];

    public IReadOnlyList<ConnectorRecipe> Recipes { get; } =
    [
        new("blender", 50, "Blender car scene", "A car driving through a procedural city with a chase camera; renders a preview.", "\uF158",
            TaskCategory.Blender, true, ["blender", "scene", "3d", "render", "animat", "car driving"], Tools,
            r => BlenderScene(r.Folder!, r.Prompt.Length > 0 ? r.Prompt : "a car driving through a city at night", r.Launch)),
    ];

    public Task<IReadOnlyList<ConnectorCheck>> DoctorAsync(ConnectorInstance? instance, bool deep, CancellationToken ct)
    {
        var blender = Blender ?? BlenderRunner.Detect();
        return Task.FromResult<IReadOnlyList<ConnectorCheck>>(
        [
            new("Blender", blender.IsAvailable ? "ok" : "warn", blender.IsAvailable ? blender.Describe : blender.Describe + " — scenes are written as scripts only"),
            new("ffmpeg", Ffmpeg is not null ? "ok" : "warn", Ffmpeg ?? "not on PATH — no preview MP4 (frames only)"),
        ]);
    }

    public IReadOnlyList<ConnectorProgram> Programs { get; } =
        [new("blender", "Blender", "\uF158", "3D scenes and renders", "Creative", "build a scene and render a preview", Process: "blender")];

    public AppStatus ProbeProgram(string programId, ProgramProbe probe)
    {
        if (probe.Forced("blender", out var forced))
            return forced is null
                ? new AppStatus("blender", "Blender", false, "not found")
                : new AppStatus("blender", "Blender", true, forced);
        var install = probe.Apps.Find(DesktopApp.Blender);
        return install is null
            ? new AppStatus("blender", "Blender", false, $"not found (looked in {string.Join(", ", probe.Apps.SearchedLocations(DesktopApp.Blender))})")
            : new AppStatus("blender", "Blender", true, ProgramProbe.Label(install), install.Executable);
    }

    /// <summary>V15: Word, Excel and PowerPoint are optional (the Office tools write the files themselves; the app only
    /// opens them). Outlook is usable only as classic desktop Outlook with a mail profile.</summary>

    /// <summary>Prompt 3: an animated car scene in Blender.</summary>
    public static JobSpec BlenderScene(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "blender", Category = TaskCategory.Blender, Title = "Blender car scene",
            Tools = Tools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = 15,
            Prompt = $$"""
                Create an animated Blender scene: {{request}}
                Output folder: {{full}}
                1. Translate the request into blender_build_car_scene parameters (road shape as roadPoints in metres, speed, duration, fps,
                   camera offset, building count, style night or day, engine). Describe the look in your own words; do not copy any
                   game's or brand's assets, logos or names into the scene.
                2. Call blender_build_car_scene with outputFolder "{{JobSpecFactory.J(full)}}".
                3. If it saved a .blend, call blender_render_preview for a short low-resolution preview.
                Report what was built and how to open it (Blender > File > Open).
                """,
        };
    }
}
