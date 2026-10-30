using System.Globalization;
using System.Text.RegularExpressions;
using Mediator.Core.Cad;
using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;

namespace Mediator.Connectors.SolidWorks;

/// <summary>
/// V18 connector: SolidWorks. Native parametric parts, the open model (inspect, edit, sketch, features, assemblies,
/// drawings, pictures, export), STEP import, part cards and assemblies, and anything else through its API in a script.
/// The assembly work runs in mediator-swx-worker (shipped in this connector's folder); the rest talks to SolidWorks directly.
/// </summary>
public sealed partial class SolidWorksConnector : IMediatorConnector
{
    public string Id => "solidworks";
    public string DisplayName => "SolidWorks";
    public string Category => "CAD";
    public string Summary => "Parts, assemblies, drawings, STEP import and export; the open model on screen.";

    /// <summary>Null: the real out-of-process worker (or a simulated one when the runtime simulates). Tests pass a <see cref="SimulatedSwxBackend"/>.</summary>
    public ISwxBackend? Backend { get; init; }
    /// <summary>The worker to start. Null: mediator-swx-worker next to this connector, next to the app, or in the repository build.</summary>
    public ComponentCommand? Worker { get; init; }
    /// <summary>Starts SolidWorks for CAD tools. Null: the real one on Windows with the real worker, else nothing.</summary>
    public IDesktopAppSession? Session { get; init; }
    /// <summary>V16.1: the file of the document active in SolidWorks, or null. Null probe: the real SolidWorks on Windows with the real worker, else none.</summary>
    public Func<CancellationToken, Task<string?>>? ActiveDocument { get; init; }

    /// <summary>
    /// Every SolidWorks chat gets the whole toolset: building parts (native or from code / STEP), reading part cards and
    /// building assemblies. Splitting them made "make the parts and assemble them" impossible in one job. The code-to-STEP and
    /// CAD-file tools belong to their own connectors and are granted only when those are installed.
    /// </summary>
    public static readonly string[] Tools =
    [
        SwxPartToolProvider.BuildTool, SwxPartToolProvider.SetTool,                // native parametric parts
        SolidWorksCadApp.SessionTool, SolidWorksCadApp.DocumentTool, SolidWorksCadApp.ExportTool, // open/save/close, STEP/STL/PDF/DXF
        SolidWorksCadApp.InspectTool, SolidWorksCadApp.EditTool,                   // V16: read and change any part or assembly
        SolidWorksCadApp.DrawingTool, SolidWorksCadApp.ViewsTool,                  // V16: drawings, pictures of the model
        SolidWorksCadApp.SketchTool, SolidWorksCadApp.FeatureTool,                 // V17: sketches and features on the open part
        SolidWorksCadApp.AssemblyTool, SolidWorksCadApp.AnnotateTool,              // V17: live assembly work, drawing annotation
        SolidWorksCadApp.ScriptTool,                                               // V17: anything else, through the SolidWorks API
        "cad_generate_parametric_step", "cad_generate_custom_step", SwxToolProvider.ImportStepTool, // code → STEP (parts-from-code connector) → part
        SwxToolProvider.PartCardsTool, SwxToolProvider.BuildTool,                  // part cards → assembly
        .. CadAppConnector.CadFileTools,                                           // V15: read and convert CAD files (CAD files connector)
    ];

    public IReadOnlyList<string> ChatTools => Tools;

    public bool OwnsTool(string tool) => tool.StartsWith("swx_", StringComparison.Ordinal);

    public ConnectorInstance Start(ConnectorContext context) => new SolidWorksInstance(this, context);

    // ── Routing ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// V16.1: whether SolidWorks is running (checked at most every 5 s). While it is, a request about parts, features,
    /// dimensions, materials... is about the model on screen, even without the word "SolidWorks". Tests replace it.
    /// </summary>
    public static Func<bool> SolidWorksOpen { get; set; } = ProbeSolidWorks;

    private static long _swCheckedAt = long.MinValue / 2;
    private static volatile bool _swOpen;

    private static bool ProbeSolidWorks()
    {
        if (!OperatingSystem.IsWindows()) return false;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _swCheckedAt) > 5000)
        {
            _swOpen = SolidWorksLauncher.IsRunning();
            Interlocked.Exchange(ref _swCheckedAt, now);
        }
        return _swOpen;
    }

    /// <summary>Words that, while SolidWorks is open, mean the model on screen (start of a word: "part" also matches "parts").</summary>
    private static readonly string[] OpenModelWords =
        ["part", "assembl", "drawing", "feature", "dimension", "sketch", "extrude", "fillet", "chamfer", "hole", "material", "mass", "weigh",
         "bom", "bill of material", "interference", "configuration", "mate", "face", "edge", "suppress", "custom propert", "propert",
         "export", "save as", "save it", "save this", "save the", "screenshot", "picture", "the model", "this model", "open model", "selected", "selection", "thickness",
         "diameter", "radius", "rebuild", "volume", "center of mass", "centre of mass", "equation", "global variable", "iso view", "isometric"];

    private static readonly string[] CreateIntent = ["create", "make", "model", "design", "build", "3d print", "stl", "print"];

    /// <summary>"mate" as a CAD word only: mate, mates, mated, mating (not "material", "matey").</summary>
    [GeneratedRegex(@"\bmat(?:e|es|ed|ing)\b")]
    private static partial Regex MateWord();

    /// <summary>
    /// What makes "assembly" mean a SolidWorks assembly and not a .NET assembly or assembly language: a part, SolidWorks,
    /// a mate or CAD in the same request ("assemble the parts in this folder").
    /// </summary>
    [GeneratedRegex(@"\b(?:parts?|solidworks|sldprt|sldasm|cad|bolts?|brackets?|shafts?|housing)\b")]
    private static partial Regex AssemblyContext();

    /// <summary>"cad" as a whole word only ("cadence", "decade" are not CAD).</summary>
    [GeneratedRegex(@"\bcad\b")]
    private static partial Regex CadWord();

    /// <summary>
    /// Every SolidWorks job gets the whole CAD toolset (parts AND assemblies), so "make the parts and mate them" works in
    /// one job. "cad" as a word counts only with a make/create word: "explain what a fillet is in CAD" is a question.
    /// V14.1: "assembly", "mate" and "stl" only in their CAD sense, so a coding request ("load the assembly", "the
    /// material", "C++ STL") is not a SolidWorks job (which started SolidWorks).
    /// </summary>
    private static bool AboutSolidWorks(RouteInput r) =>
        r.AnyWord(["solidworks", "sldprt", "sldasm", "slddrw"]) || r.AppIs("sldworks")
        || (r.AnyWord(CreateIntent) && CadWord().IsMatch(r.Text)) || MateWord().IsMatch(r.Text)
        || (r.HasWord("assembl") && AssemblyContext().IsMatch(r.Text))
        || (r.AnyWord(OpenModelWords) && SolidWorksOpen()); // V16.1: about the model open in SolidWorks

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
    [
        new(10, TaskCategory.Cad, Tools, AboutSolidWorks),
        new(30, TaskCategory.Cad, Tools, r => r.AnyWord(["parametric", "build123d", "3d print", "generate part", "make part"])),
    ];

    /// <summary>True when the request itself asks for SolidWorks work (used to start SolidWorks early only for those).</summary>
    public static bool AsksForSolidWorks(string? prompt) =>
        JobSpecFactory.MatchConnectors(prompt, null).Tools.Contains(SwxPartToolProvider.BuildTool, StringComparer.Ordinal);

    /// <summary>
    /// V14.1: SolidWorks is started ahead of the model only for a SolidWorks action (Assemble, parametric parts) or a request
    /// whose own words ask for CAD. Having the CAD tools is not enough: a follow-up in a chat that once used CAD keeps them.
    /// </summary>
    public static bool WarmsSolidWorks(JobSpec spec) =>
        spec.Tools.Any(t => SwxToolProvider.ToolNames.Contains(t) || SwxPartToolProvider.ToolNames.Contains(t) || SolidWorksCadApp.ToolNames.Contains(t))
        && (spec.Kind is "assemble" or "codecad" || AsksForSolidWorks(spec.UserText ?? spec.Prompt));

    // ── Tiles, programs ───────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<ConnectorRecipe> Recipes { get; } =
    [
        new("assemble", 10, "Assemble SolidWorks parts", "Read the parts in a folder, propose mates, review them, build a new .SLDASM.", "\uE7B8",
            TaskCategory.Cad, true, ["assembl", "solidworks", "sldprt", "mate"], Tools,
            r => Assemble(r.Folder!, r.Launch, r.Output ?? RequestedAssemblyPath(r.Prompt, r.Folder!))),
        new("codecad", 20, "Design parametric parts", "Hubs, discs, springs, arms as build123d code → STEP → SolidWorks parts, checked against size envelopes.", "\uE8B9",
            TaskCategory.Cad, true, ["design", "parametric", "generate part", "make part", "model a", "step", "build123d", "create", "3d print", "stl", "solidworks", "cad ", " cad", "suspension", "hub", "brake", "spring", "bracket"], Tools,
            r => CodeCad(r.Folder!, r.Prompt.Length > 0 ? r.Prompt : "the parts described in this folder's notes", r.Launch)),
    ];

    public IReadOnlyList<ConnectorProgram> Programs { get; } =
        [new("solidworks", "SolidWorks", "\uF158", "Parts, assemblies, drawings, STEP import and export", "CAD",
            "build parametric parts and assemblies, read and change the open model, drawings, pictures, STEP/STL export", Process: "sldworks")];

    public AppStatus ProbeProgram(string programId, ProgramProbe probe)
    {
        if (!probe.WindowsApps) return new AppStatus("solidworks", "SolidWorks", false, "Windows only");
        var install = probe.Apps.Find(DesktopApp.SolidWorks);
        string installed = install is null ? "installation not found in the registry or the default folder" : $"installed: {ProgramProbe.Label(install)}";
        if (probe.Running("SLDWORKS") is { } pid)
            return new AppStatus("solidworks", "SolidWorks", true, $"running (PID {pid}); the worker attaches to it. {installed}", install?.Executable);
        return install is null
            ? new AppStatus("solidworks", "SolidWorks", false, $"not installed (looked in {string.Join(", ", probe.Apps.SearchedLocations(DesktopApp.SolidWorks))})")
            : new AppStatus("solidworks", "SolidWorks", true, $"{installed}; not running: the Mediator starts it when a SolidWorks job needs it", install.Executable);
    }

    // ── mediator-cli doctor ─────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ConnectorCheck>> DoctorAsync(ConnectorInstance? instance, bool deep, CancellationToken ct)
    {
        var checks = new List<ConnectorCheck>();
        if (instance is not SolidWorksInstance sw) return [new("SolidWorks", "fail", "the connector did not start")];
        // V16.1: every SolidWorks tool a CAD chat is given must be registered (an old build lacks the V16 ones).
        var registered = sw.Providers.SelectMany(p => p.Tools.Select(t => t.Name)).ToHashSet(StringComparer.Ordinal);
        var missing = Tools.Where(t => t.StartsWith("swx_", StringComparison.Ordinal) && !registered.Contains(t)).ToList();
        checks.Add(new("SolidWorks tools", missing.Count == 0 ? "ok" : "fail", missing.Count == 0
            ? $"{Tools.Count(t => t.StartsWith("swx_", StringComparison.Ordinal))} swx_ tools registered (inspect, edit, drawing, views, documents, export, parts, assemblies)"
            : "not registered: " + string.Join(", ", missing)));
        if (deep)
        {
            try
            {
                var health = await sw.Backend.CallAsync("health", new System.Text.Json.Nodes.JsonObject(), TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
                checks.Add(new("SolidWorks", "ok", health?.ToString() ?? "?"));
            }
            catch (Exception ex) when (ex is SwxWorkerException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                checks.Add(new("SolidWorks", "fail", ex.Message));
            }
        }
        else
        {
            var worker = SolidWorksInstance.LocateWorker();
            checks.Add(new("SolidWorks worker", worker is null ? "warn" : "ok", worker?.Display ?? "mediator-swx-worker not found (SolidWorks jobs will fail). Build src/Connectors/SolidWorks/Worker on Windows."));
        }
        if (OperatingSystem.IsWindows())
        {
            checks.Add(new("SolidWorks install", ComponentLocator.FindDesktopApp(DesktopApp.SolidWorks) is null ? "warn" : "ok", DesktopAppLocator.Default.Summary(DesktopApp.SolidWorks)));
            string? redist = DesktopAppLocator.Default.FindAll(DesktopApp.SolidWorks).Select(i => i.InteropDirectory).FirstOrDefault(d => d is not null);
            checks.Add(new("SolidWorks interop", redist is null ? "warn" : "ok", redist ?? "api\\redist not found: mediator-swx-worker is built without SolidWorks support and reports \"SolidWorks assemblies not installed on this machine\""));
        }
        else checks.Add(new("SolidWorks install", "warn", "not Windows: SolidWorks checks skipped"));
        return checks;
    }

    // ── Job recipes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns (model replies, one per tool call) a CAD job may take: several parts, their fixes and version iterations,
    /// part cards and the assembly all happen in one run.
    /// </summary>
    public const int CadMaxTurns = JobSpecFactory.CadMaxTurns;

    /// <summary>Prompt 1: parametric parts as code → STEP → SolidWorks parts → assembly.</summary>
    public static JobSpec CodeCad(string folder, string request, LaunchMode launch)
    {
        string full = Path.GetFullPath(folder);
        return new JobSpec
        {
            Kind = "codecad", Category = TaskCategory.Cad, Title = $"Parametric parts in {Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}",
            Tools = Tools, Roots = [full], WorkingDirectory = full, Launch = launch, MaxTurns = CadMaxTurns,
            Prompt = $$"""
                Design parametric parts for: {{request}}
                Output folder: {{full}}
                A. A single part described by its dimensions or features (plates, holes, bosses, ribs, fillets), or anything where the user
                   wants design intent / to change dimensions later in SolidWorks: build it NATIVELY with swx_build_parametric_part
                   (outputFolder "{{JobSpecFactory.J(full)}}"). Make every size a global (mm) and position features with expressions of those globals
                   (e.g. corner holes at "W/2-E"), so the part survives dimension changes. Check the returned report (no rebuild errors,
                   holes at the right edge distances, features centred). If the user asks to change dimensions, call swx_set_part_globals
                   and verify the report again. The part stays open in SolidWorks. Skip steps 1-3 below.
                   Organic or styled shapes (vehicles, figurines, handles, housings, anything to 3D print) are also case A. Build them
                   with curves, not boxes: draw sections with splines/outlines on offset planes (op plane + named sketches), loft them in
                   order (add guide sketches to control the silhouette; a surface loft with guides stands in for a boundary surface),
                   sweep a profile along a path for tubes and rims, revolve round parts, and use surface + knit + thicken for skins.
                   For symmetric shapes build one half and mirror it. Finish with fillets (edgesNear picks specific edges) and cuts or
                   extrudes for details. Keep ONE solid body with no thin walls (>= 1.5 mm). For a size limit set fitBoxMm and check
                   the reported bounding box against it ("4 cubic inch box": assume a 4 in = 101.6 mm cube unless they say volume, and
                   state the assumption). If a feature fails, read the log line, fix that feature and rebuild.
                   When the request names a real object (a car model, a product, a character), it must be recognisable as THAT object,
                   not a generic smooth shape. Work in a loop:
                   1. Reference sheet first (in your answer): the real overall size (length, width, height in mm; web search if you can,
                      else what you know) and 6-10 defining features with where they sit along the length (e.g. for a car: wheelbase and
                      overhangs, nose shape, windscreen and roof position, side intakes, engine cover, rear wing, lights, wheel size).
                   2. Build "<name>-v1" with referenceMm set to that size. Draw sections from the real proportions (positions as
                      fractions of the length), not from guesses; add every defining feature as its own feature (cut, extrude, loft).
                   3. Compare: look at the returned front, right, top and iso pictures (a car built along X shows its side profile in the front view) and the proportions check, feature by feature
                      against the sheet. Write the differences as a short list.
                   4. Rework and build "<name>-v2", "-v3"... (older versions are deleted automatically) until the proportions match and
                      every defining feature is visible in the pictures, at most 6 versions. Finish with the checklist: each feature, done
                      or approximated, and what still differs. Set exportStl true when the user wants to print it. Never assemble unrelated parts from the folder.
                   Several parts that go together (a mechanism, a kit, "make the parts and mate them"): one job does it all, one tool
                   call at a time. Build every part with swx_build_parametric_part (check each report), then call swx_part_cards on
                   the output folder, then swx_build_assembly with only the parts you built and mates read from the cards
                   (concentric for matching cylinders, coincident for opposed faces, default planes "front"/"top"/"right" to
                   centre parts without matching faces), largest part fixed, saved under the assembly name the user asked for.
                   A DXF/SVG drawing the user gives (logo, plate outline, gasket) becomes a profile shape "import" (path, width?) and
                   is extruded or cut; lock: true keeps its exact shape. A 3D path (pipe, handlebar, wire) is op curve3d, then sweep.
                   Existing SolidWorks files: swx_session shows what is open; swx_document_ops opens, saves, saves as or closes (never
                   discard the user's unsaved work unless they say so); swx_export_neutral writes STEP/IGES/Parasolid/STL/3MF (PDF/DXF/DWG
                   for drawings). swx_inspect_model reads any part or assembly (feature tree, dimensions, equations, properties, mass and
                   material, configurations, the user's selection, BOM, mates, interference); swx_edit_model changes dimensions, globals,
                   suppression, properties, material and configurations (left unsaved for review unless save: true); swx_create_drawing
                   makes a drawing with views and dimensions; swx_capture_views shows you pictures of the model.
                B. Otherwise (templated mechanical parts, assemblies):
                1. Choose built-in templates in cad_generate_parametric_step (wheel_hub, brake_disc, coil_spring, strut_tube, control_arm,
                   shaft, flange_bracket) with realistic millimetre parameters and an envelopeMm for each part. State any dimension you assume.
                   Use cad_generate_custom_step only if no template fits; the user reviews that code.
                2. Import the STEP files with swx_import_step (outputFolder "{{JobSpecFactory.J(full)}}").
                3. Read them with swx_part_cards and, if the user asked for an assembly, call swx_build_assembly with mates you are confident in.
                Report every part with its bounding box, and which dimensions the user should check against real measurements.
                """,
        };
    }

    public static string DefaultAssemblyPath(string folder) =>
        Path.Combine(Path.GetFullPath(folder), string.Create(CultureInfo.InvariantCulture, $"Mediator-Assembly-{DateTime.Now:yyyyMMdd-HHmmss}.SLDASM"));

    /// <summary>
    /// The assembly file a request names ("...named Train_Master_Assembly.SLDASM", "save as C:\X\Car.sldasm"), placed in
    /// <paramref name="folder"/> unless the request gives a full path; null when it names none.
    /// </summary>
    public static string? RequestedAssemblyPath(string? request, string folder)
    {
        var m = AssemblyFileName().Match(request ?? "");
        if (!m.Success) return null;
        string name = m.Groups["name"].Value;
        string? dir = m.Groups["dir"].Success && m.Groups["dir"].Value.Length > 0 ? m.Groups["dir"].Value : null;
        // A typed drive path ("D:\Out\") is already absolute: on Windows it is normalised, elsewhere kept as typed.
        if (dir is not null && !OperatingSystem.IsWindows()) return dir + name;
        try { return Path.GetFullPath(Path.Combine(dir ?? folder, name)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Path.Combine(Path.GetFullPath(folder), name); }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<dir>[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*)?(?<name>[\w\-.]+\.sldasm)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AssemblyFileName();

    public static JobSpec Assemble(string folder, LaunchMode launch, string? outputPath = null)
    {
        string full = Path.GetFullPath(folder);
        string output = Path.GetFullPath(outputPath ?? DefaultAssemblyPath(full));
        var roots = new List<string> { full };
        string outDir = Path.GetDirectoryName(output)!;
        // The user chose the output folder; it becomes a second root unless it already lies inside the first.
        if (!Mediator.Core.Security.PathSyntax.Current.IsSameOrInside(outDir, full)) roots.Add(outDir);
        return new JobSpec
        {
            Kind = "assemble",
            Category = TaskCategory.Cad,
            Title = $"Assemble parts in {Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))}",
            Tools = Tools,
            Roots = roots,
            WorkingDirectory = full,
            Launch = launch,
            MaxTurns = CadMaxTurns,
            Prompt = $$"""
                Create a new SolidWorks assembly from the part files in this folder: {{full}}
                0. If the request needs parts that are not in the folder yet, build each one first with swx_build_parametric_part
                   (outputFolder "{{JobSpecFactory.J(full)}}", one call per part, checking each report), then continue with all the parts.
                1. Call swx_part_cards with {"folder": "{{full.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}.
                2. From the cards (bounding boxes, cylinders, planes, mate references) work out how the parts fit together.
                   Prefer concentric mates between cylinders of equal radius and coincident mates between opposed planes.
                   To centre or align parts without matching faces (a cylinder on a block) mate their default planes: faceA/faceB
                   "front", "top" or "right" (e.g. Coincident right-right and front-front centres the boiler on the chassis).
                   A part used several times is one component per copy (key, position). Give every mate a confidence (0-1) and a one-line reason. Leave out mates you are unsure about.
                3. Call swx_build_assembly once with outputPath "{{output.Replace("\\", "\\\\", StringComparison.Ordinal)}}" (or the assembly name
                   the user asked for, in that folder), the first
                   (largest) part fixed, and your mates. The user reviews and may untick mates before anything is built.
                4. Report which mates were built, which failed, and what the user should check in SolidWorks.
                Use only the mediator tools. Do not try to edit the part files.
                """,
        };
    }
}
