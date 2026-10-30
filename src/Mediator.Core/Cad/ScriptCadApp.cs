using System.Text.Json.Nodes;

namespace Mediator.Core.Cad;

/// <summary>
/// V17: a CAD program driven by one automation script in its own language (FreeCAD and NX: Python; AutoCAD, Inventor, Creo,
/// CATIA: Windows PowerShell talking to the program's COM API). The script holds one function per tool and dispatches on
/// args.verb ("session", "inspect", "feature", "script", ...); this class only builds the arguments and reads the answer
/// (<see cref="CadScriptHost"/>). The scripts ship embedded (Cad/Scripts); a file of the same name in the override folder
/// (&lt;data&gt;\cad-scripts) is used instead, so an API call that differs on a given version can be fixed without a rebuild.
/// </summary>
public abstract class ScriptCadApp(string? overrideFolder = null) : ICadApp
{
    public abstract string Prefix { get; }
    public abstract string DisplayName { get; }
    public virtual string Lease => Prefix + "-cad";
    public abstract IReadOnlySet<CadCapability> Capabilities { get; }
    public abstract IReadOnlyList<string> ModelExtensions { get; }
    public virtual string? DrawingExtension => null;
    public abstract IReadOnlyList<CadExportFormat> ExportFormats { get; }
    public virtual IReadOnlyList<string> MaterialExamples => [];
    public virtual bool HasLiveSession => true;
    public abstract CadScriptInfo Script { get; }
    /// <summary>Operations per capability; the default is none (only what <see cref="SupportedOps"/> lists).</summary>
    public IReadOnlyList<string> Ops(CadCapability capability) => SupportedOps.TryGetValue(capability, out var ops) ? ops : [];
    protected abstract IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; }

    /// <summary>The script's file name in Cad/Scripts ("freecad.py").</summary>
    protected abstract string ScriptName { get; }

    /// <summary>The interpreter, or null with <paramref name="why"/> when the program (or its automation) is not available here.</summary>
    protected abstract CadScriptRunner? Runner(out string? why);

    /// <summary>How long one call may take.</summary>
    protected virtual TimeSpan Timeout(string verb) => verb is "simulate" or "export" or "feature" ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(4);

    public string? Unavailable() => Runner(out var why) is null ? why ?? $"{DisplayName} was not found on this PC." : null;

    /// <summary>The scripts start or attach to the program themselves.</summary>
    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>The connector's script: the override file when there is one, else the embedded copy.</summary>
    public string LoadScript() => LoadEmbedded(GetType().Assembly, ScriptName, overrideFolder);

    /// <summary>
    /// A connector script: <paramref name="overrideFolder"/>/<paramref name="name"/> when present, else the copy embedded in
    /// the connector's own assembly (resource "Mediator.Cad.Scripts.&lt;name&gt;").
    /// </summary>
    public static string LoadEmbedded(System.Reflection.Assembly connector, string name, string? overrideFolder)
    {
        if (overrideFolder is not null)
        {
            string local = Path.Combine(overrideFolder, name);
            if (File.Exists(local)) return File.ReadAllText(local);
        }
        using var s = connector.GetManifestResourceStream("Mediator.Cad.Scripts." + name)
            ?? throw new CadAppException($"The automation script {name} is missing from this build.");
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    /// <summary>Runs one verb of the script with <paramref name="args"/> and returns its report.</summary>
    protected async Task<JsonObject> RunAsync(string verb, JsonObject args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var runner = Runner(out var why) ?? throw new CadAppException(why ?? $"{DisplayName} was not found on this PC.");
        args["verb"] = verb;
        var limit = timeout ?? Timeout(verb);
        var output = await CadScriptHost.RunAsync(runner, LoadScript(), args, limit, ct).ConfigureAwait(false);
        return CadScriptHost.ToReport(output, DisplayName, limit);
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());

    public async Task<string?> ActiveDocumentPathAsync(CancellationToken ct) =>
        HasLiveSession ? (string?)(await RunAsync("active_path", new JsonObject(), ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false))["path"] : null;

    public Task<JsonObject> SessionAsync(CancellationToken ct) => RunAsync("session", new JsonObject(), ct);

    public Task<JsonObject> DocumentAsync(CadDocumentRequest r, CancellationToken ct) => RunAsync("document", new JsonObject
    {
        ["action"] = r.Action, ["path"] = r.Path, ["save"] = r.Save, ["discard"] = r.Discard, ["target"] = r.TargetPath,
    }, ct);

    public Task<JsonObject> ExportAsync(IReadOnlyList<string> paths, IReadOnlyList<string> formats, string outputFolder, CancellationToken ct) =>
        RunAsync("export", new JsonObject
        {
            ["paths"] = Strings(paths), ["folder"] = outputFolder,
            ["formats"] = new JsonArray(formats.Select(f => ExportFormats.First(x => x.Name == f))
                .Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["ext"] = f.Extension }).ToArray()),
        }, ct);

    public Task<JsonObject> InspectAsync(string? path, IReadOnlySet<string> sections, CancellationToken ct) =>
        RunAsync("inspect", new JsonObject { ["path"] = path, ["sections"] = Strings(sections) }, ct);

    public Task<JsonObject> EditAsync(string? path, JsonArray ops, bool save, CancellationToken ct) =>
        RunAsync("edit", new JsonObject { ["path"] = path, ["ops"] = ops, ["save"] = save }, ct);

    public Task<JsonObject> CreateDrawingAsync(CadDrawingRequest r, CancellationToken ct) => RunAsync("drawing", new JsonObject
    {
        ["model"] = r.ModelPath, ["output"] = r.OutputPath, ["sheet"] = r.Sheet, ["firstAngle"] = r.FirstAngle,
        ["isometric"] = r.Isometric, ["dimensions"] = r.Dimensions, ["pdf"] = r.ExportPdf,
    }, ct);

    public Task<JsonObject> CaptureViewsAsync(string? path, IReadOnlyList<string> views, string outputFolder, CancellationToken ct) =>
        RunAsync("views", new JsonObject { ["path"] = path, ["views"] = Strings(views), ["folder"] = outputFolder }, ct);

    public Task<JsonObject> SketchAsync(string? path, JsonObject sketch, bool save, CancellationToken ct) =>
        RunAsync("sketch", new JsonObject { ["path"] = path, ["sketch"] = sketch, ["save"] = save }, ct);

    public Task<JsonObject> RunOpsAsync(CadCapability capability, string? path, JsonArray ops, bool save, CancellationToken ct) =>
        RunAsync(CadVerb.For(capability), new JsonObject { ["path"] = path, ["ops"] = ops, ["save"] = save }, ct);

    public Task<JsonObject> RunScriptAsync(string code, string? path, TimeSpan timeout, CancellationToken ct) =>
        RunAsync("script", new JsonObject { ["path"] = path, ["code"] = code }, ct, timeout);
}
