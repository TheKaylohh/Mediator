using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Cad;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Connectors.SolidWorks;

namespace Mediator.Tests;

/// <summary>
/// V16: the CAD-neutral tool layer (<see cref="CadAppToolProvider"/>) on a fake CAD program: tool names from the app's prefix
/// and capabilities, the app lease, path authorization (folders, file types, never replacing a file, the active document),
/// edit operations validated and partly approved, drawings, pictures handed to the model, and an unavailable app.
/// </summary>
static class CadAppTests
{
    sealed class FakeCadApp(string prefix, IReadOnlySet<CadCapability> capabilities) : ICadApp
    {
        public string Prefix => prefix;
        public string DisplayName => "FakeCAD";
        public string Lease => "fakecad";
        public IReadOnlySet<CadCapability> Capabilities => capabilities;
        public IReadOnlyList<string> ModelExtensions { get; } = [".fprt", ".fasm"];
        public string? DrawingExtension => ".fdrw";
        public IReadOnlyList<CadExportFormat> ExportFormats { get; } = [new("step", ".step", true, false), new("pdf", ".pdf", false, true)];
        public IReadOnlyList<string> MaterialExamples { get; } = ["Steel"];
        public string? Why;
        public string? Active;
        public readonly List<string> Calls = new();
        public JsonArray? LastOps;
        public CadDocumentRequest? LastDocument;
        public CadDrawingRequest? LastDrawing;
        public string? LastViewsFolder;
        public string? Unavailable() => Why;
        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string?> ActiveDocumentPathAsync(CancellationToken ct) => Task.FromResult(Active);
        JsonObject Log(string what) { Calls.Add(what); return new JsonObject { ["did"] = what }; }
        public Task<JsonObject> SessionAsync(CancellationToken ct) => Task.FromResult(Log("session"));
        public Task<JsonObject> DocumentAsync(CadDocumentRequest r, CancellationToken ct) { LastDocument = r; return Task.FromResult(Log("document " + r.Action + " " + r.Path)); }
        public Task<JsonObject> ExportAsync(IReadOnlyList<string> paths, IReadOnlyList<string> formats, string outputFolder, CancellationToken ct)
        {
            var r = Log($"export {paths.Count} → {outputFolder}");
            r["files"] = new JsonArray(Path.Combine(outputFolder, "a.step"));
            return Task.FromResult(r);
        }
        public Task<JsonObject> InspectAsync(string? path, IReadOnlySet<string> sections, CancellationToken ct) =>
            Task.FromResult(Log($"inspect {path ?? "(active)"} [{string.Join(",", sections.Order())}]"));
        public Task<JsonObject> EditAsync(string? path, JsonArray ops, bool save, CancellationToken ct)
        {
            LastOps = ops;
            var r = Log($"edit {path ?? "(active)"} save={save}");
            r["results"] = new JsonArray(ops.Select(o => (JsonNode)new JsonObject { ["op"] = (string?)o!["op"], ["ok"] = true }).ToArray());
            return Task.FromResult(r);
        }
        public Task<JsonObject> CreateDrawingAsync(CadDrawingRequest r, CancellationToken ct)
        {
            LastDrawing = r;
            var report = Log("drawing");
            report["saved"] = r.OutputPath;
            return Task.FromResult(report);
        }
        public Task<JsonObject> CaptureViewsAsync(string? path, IReadOnlyList<string> views, string outputFolder, CancellationToken ct)
        {
            LastViewsFolder = outputFolder;
            var images = new JsonArray();
            foreach (var v in views)
            {
                string png = Path.Combine(outputFolder, v + ".png");
                File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);
                images.Add(png);
            }
            images.Add(Path.Combine(Path.GetTempPath(), "elsewhere.png")); // outside the folder: never read
            var r = Log($"views {string.Join(",", views)}");
            r["images"] = images;
            return Task.FromResult(r);
        }
    }

    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "cad-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static JobGrant Grant(string root) => new()
    {
        JobId = "j-" + Guid.NewGuid().ToString("N")[..6], Token = "t", AllowedTools = new HashSet<string>(), FileRoots = [root],
        Paths = new PathAuthorizer([root]), ExpiresAt = DateTimeOffset.MaxValue,
    };

    static ToolCallContext Ctx(IToolProvider p, JobGrant job, string tool, string json, IReadOnlySet<string>? rows = null) =>
        new(job, p.Tools.Single(t => t.Name == tool), JsonDocument.Parse(json).RootElement, "c", rows);

    static Task<ToolResult> Call(IToolProvider p, JobGrant job, string tool, string json, IReadOnlySet<string>? rows = null) =>
        p.ExecuteAsync(Ctx(p, job, tool, json, rows), CancellationToken.None);

    static string J(string s) => JsonSerializer.Serialize(s);

    static readonly IReadOnlySet<CadCapability> All = Enum.GetValues<CadCapability>().ToHashSet();

    /// <summary>Names, capabilities, lease, the SolidWorks names kept, and an app that cannot run here.</summary>
    public static async Task ToolsetFromCapabilities()
    {
        var partial = new CadAppToolProvider(new FakeCadApp("inv", new HashSet<CadCapability> { CadCapability.Session, CadCapability.Inspect }));
        Check.True(partial.ToolNames.SetEquals(["inv_session", "inv_inspect_model"]), "only the app's capabilities become tools: " + string.Join(", ", partial.ToolNames));
        var full = new CadAppToolProvider(new FakeCadApp("fus", All));
        Check.Equal(Enum.GetValues<CadCapability>().Length - 1, full.Tools.Count, "every capability is one tool (no script tool without a script language)");
        Check.True(full.Tools.All(t => t.LeaseResource == "fakecad" && ToolRegistry.IsValidName(t.Name) && t.Name.StartsWith("fus_", StringComparison.Ordinal)), "prefixed, valid MCP names, all holding the app lease");
        Check.True(full.Tools.Single(t => t.Name == "fus_export_neutral").InputSchema.ToJsonString().Contains("\"pdf\"", StringComparison.Ordinal), "export formats come from the app");
        new ToolRegistry().Register(full); // no duplicate or invalid names

        // The SolidWorks tool names the CAD jobs already used are unchanged.
        Check.True(SolidWorksCadApp.SessionTool == "swx_session" && SolidWorksCadApp.DocumentTool == "swx_document_ops" && SolidWorksCadApp.ExportTool == "swx_export_neutral",
            "swx_session, swx_document_ops and swx_export_neutral keep their names");
        Check.True(JobSpecFactory.InferToolsAndCategory("make a drawing of bracket.slddrw", null).Tools.Contains(SolidWorksCadApp.DrawingTool), "a .slddrw request gets the CAD tools");

        string root = TempDir("unavailable");
        var off = new FakeCadApp("off", All) { Why = "FakeCAD needs Windows." };
        var res = await Call(new CadAppToolProvider(off), Grant(root), "off_session", "{}");
        Check.True(res.IsError && res.Text.Contains("needs Windows", StringComparison.Ordinal) && res.Text.Contains("Do not retry", StringComparison.Ordinal) && off.Calls.Count == 0,
            "an app that cannot run here says so once and is never called: " + res.Text);
    }

    /// <summary>Only documents inside the job's folders; new files never replace old ones; the active document's folder is checked before a change.</summary>
    public static async Task PathsAndActiveDocument()
    {
        string root = TempDir("paths"), other = TempDir("paths-other");
        var app = new FakeCadApp("fc", All);
        var p = new CadAppToolProvider(app);
        var job = Grant(root);
        string part = Path.Combine(root, "plate.fprt");
        File.WriteAllText(part, "x");
        string outside = Path.Combine(other, "secret.fprt");
        File.WriteAllText(outside, "x");

        Check.True(!(await Call(p, job, "fc_inspect_model", $$"""{"path":{{J(part)}}}""")).IsError && app.Calls[^1].Contains("plate.fprt", StringComparison.Ordinal)
            && app.Calls[^1].Contains("features", StringComparison.Ordinal) && app.Calls[^1].Contains("mass", StringComparison.Ordinal), "inspect with the default sections: " + app.Calls[^1]);
        Check.True((await Call(p, job, "fc_inspect_model", $$"""{"path":{{J(outside)}}}""")).IsError, "a document outside the job's folders is refused");
        string txt = Path.Combine(root, "notes.txt");
        File.WriteAllText(txt, "x");
        Check.True((await Call(p, job, "fc_inspect_model", $$"""{"path":{{J(txt)}}}""")).Text.Contains("not a FakeCAD document", StringComparison.Ordinal), "only the app's own file types");
        Check.True((await Call(p, job, "fc_inspect_model", """{"sections":["selection","nonsense"]}""")).IsError, "unknown sections refused");
        Check.True(!(await Call(p, job, "fc_inspect_model", """{"sections":["selection"]}""")).IsError && app.Calls[^1] == "inspect (active) [selection]", "no path = what is on screen");

        // Changing the active document: only when it lives in a folder the job may change.
        string editOps = """{"ops":[{"op":"set_material","material":"Steel"}],"save":true}""";
        app.Active = outside;
        var refused = await Call(p, job, "fc_edit_model", editOps);
        Check.True(refused.IsError && refused.Text.Contains("outside the folders", StringComparison.Ordinal), "the active document outside the job's folders is not changed: " + refused.Text);
        app.Active = null;
        Check.True((await Call(p, job, "fc_edit_model", editOps)).Text.Contains("never saved", StringComparison.Ordinal), "a never-saved active document cannot be saved by an edit");
        Check.True(!(await Call(p, job, "fc_edit_model", """{"ops":[{"op":"rebuild"}]}""")).IsError, "...but it can be changed and left unsaved");
        app.Active = part;
        Check.True(!(await Call(p, job, "fc_edit_model", editOps)).IsError && app.Calls[^1] == "edit (active) save=True", "the active document inside the folder is changed and saved");
        Check.True((await Call(p, job, "fc_document_ops", """{"action":"save"}""")).IsError == false, "save of the active document inside the folder");
        app.Active = outside;
        Check.True((await Call(p, job, "fc_document_ops", """{"action":"save"}""")).IsError, "save of an active document outside the folder is refused");
        Check.True(!(await Call(p, job, "fc_document_ops", """{"action":"close"}""")).IsError, "closing without saving needs no write access");

        // save_as and drawings never replace a file, and only write the app's types.
        string copy = Path.Combine(root, "plate-v2.fprt");
        Check.True(!(await Call(p, job, "fc_document_ops", $$"""{"action":"save_as","path":{{J(part)}},"targetPath":{{J(copy)}}}""")).IsError && app.LastDocument?.TargetPath == copy, "save_as to a new name");
        Check.True((await Call(p, job, "fc_document_ops", $$"""{"action":"save_as","path":{{J(part)}},"targetPath":{{J(part)}}}""")).Text.Contains("already exists", StringComparison.Ordinal), "save_as never replaces a file");
        Check.True((await Call(p, job, "fc_document_ops", $$"""{"action":"save_as","targetPath":{{J(Path.Combine(root, "x.exe"))}}}""")).IsError, "save_as only to the app's file types");

        // Export into a new folder below the root; unknown formats refused.
        string outFolder = Path.Combine(root, "exports");
        Check.True(!(await Call(p, job, "fc_export_neutral", $$"""{"formats":["step"],"outputFolder":{{J(outFolder)}},"paths":[{{J(part)}}]}""")).IsError, "export into a new folder below the root");
        Check.True((await Call(p, job, "fc_export_neutral", $$"""{"formats":["obj"],"outputFolder":{{J(outFolder)}}}""")).IsError, "unknown export format refused");
        Check.True((await Call(p, job, "fc_export_neutral", $$"""{"formats":["step"],"outputFolder":{{J(other)}}}""")).IsError, "export outside the job's folders refused");

        string drawing = Path.Combine(root, "plate.fdrw");
        var made = await Call(p, job, "fc_create_drawing", $$"""{"modelPath":{{J(part)}},"outputPath":{{J(drawing)}}}""");
        Check.True(!made.IsError && app.LastDrawing is { Sheet: "A3", FirstAngle: false, Isometric: true, Dimensions: true, ExportPdf: false } && app.LastDrawing.ModelPath == part,
            "drawing defaults: A3, third angle, isometric, dimensions: " + made.Text);
        File.WriteAllText(drawing, "x");
        Check.True((await Call(p, job, "fc_create_drawing", $$"""{"outputPath":{{J(drawing)}}}""")).Text.Contains("already exists", StringComparison.Ordinal), "a drawing never replaces a file");
        Check.True((await Call(p, job, "fc_create_drawing", $$"""{"outputPath":{{J(Path.Combine(root, "d.fprt"))}}}""")).IsError, "the drawing must have the drawing extension");
        Check.True((await Call(p, job, "fc_create_drawing", $$"""{"modelPath":{{J(drawing)}},"outputPath":{{J(Path.Combine(root, "d2.fdrw"))}}}""")).IsError, "a drawing is made of a model, not of a drawing");
    }

    /// <summary>Edit operations are checked before the approval, shown one per row, and only the kept rows run.</summary>
    public static async Task EditOperationsApproval()
    {
        string root = TempDir("edit");
        var app = new FakeCadApp("fc", All);
        var p = new CadAppToolProvider(app);
        var job = Grant(root);
        string part = Path.Combine(root, "plate.fprt");
        File.WriteAllText(part, "x");

        foreach (var (ops, why) in new[]
        {
            ("""[{"op":"explode"}]""", "unknown op"),
            ("""[{"op":"set_dimension","name":"D1@Sketch1"}]""", "missing value"),
            ("""[{"op":"set_dimension","name":"D1@Sketch1","value":"ten"}]""", "value is not a number"),
            ("""[{"op":"suppress"}]""", "missing feature"),
            ("""[]""", "no operations"),
        })
        {
            var r = await Call(p, job, "fc_edit_model", $$"""{"path":{{J(part)}},"ops":{{ops}}}""");
            Check.True(r.IsError && app.LastOps is null, $"{why} is refused before the app runs: {r.Text}");
        }

        string json = $$"""
            {"path":{{J(part)}},"ops":[{"op":"set_dimension","name":"D1@Boss-Extrude1","value":12.5},{"op":"suppress","feature":"Fillet1"},
            {"op":"set_property","name":"PartNo","value":"P-100"},{"op":"set_material","material":"Steel"}]}
            """;
        var preview = (await p.BuildPreviewAsync(Ctx(p, job, "fc_edit_model", json), CancellationToken.None))!;
        var rows = preview["rows"]!.AsArray();
        Check.True((bool?)preview["selectable"] == true && rows.Count == 4 && (string?)rows[0]!["primary"] == "D1@Boss-Extrude1 = 12.5 mm" && (string?)rows[1]!["primary"] == "suppress Fillet1"
            && ((string?)preview["summary"])!.Contains("unsaved", StringComparison.Ordinal), "one selectable row per operation, unsaved by default: " + preview.ToJsonString());

        var kept = await Call(p, job, "fc_edit_model", json, new HashSet<string> { "1", "3" });
        Check.True(!kept.IsError && app.LastOps!.Count == 2 && (string?)app.LastOps[0]!["op"] == "set_dimension" && (string?)app.LastOps[1]!["op"] == "set_property"
            && kept.Text.Contains("unticked by the user", StringComparison.Ordinal), "only the kept operations run, and the model is told: " + kept.Text);
        Check.True((await Call(p, job, "fc_edit_model", json, new HashSet<string>())).IsError, "nothing kept: nothing runs");
    }

    /// <summary>Pictures come back as images (only those in the call's own folder), and the folder is removed.</summary>
    public static async Task PicturesAsImages()
    {
        string root = TempDir("views"), views = TempDir("views-tmp");
        var app = new FakeCadApp("fc", All);
        var p = new CadAppToolProvider(app, views);
        var job = Grant(root);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "elsewhere.png"), [1, 2, 3]);
        var r = await Call(p, job, "fc_capture_views", "{}");
        Check.True(!r.IsError && r.Images.Count == 2 && app.Calls[^1] == "views current,iso", "default views current + iso, both returned as images: " + r.Text);
        Check.True(!r.Text.Contains("images", StringComparison.Ordinal) && (int?)r.Structured!["pictures"] == 2, "file paths are not handed to the model, the count is");
        Check.True(app.LastViewsFolder is not null && !Directory.Exists(app.LastViewsFolder), "the call's picture folder is deleted afterwards");
        Check.True((await Call(p, job, "fc_capture_views", """{"views":["sideways"]}""")).IsError, "unknown views refused");
    }
}

/// <summary>
/// V16.1, from the Windows test battery: SolidWorks requests that did not get the SolidWorks tools (no "SolidWorks" in the
/// prompt, chats saved by V15), the open document's folder, the running build, and files SolidWorks holds open.
/// </summary>
static class CadRoutingTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "cadr-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>While SolidWorks is open, requests about the model get the whole toolset; old chats get the V16 tools.</summary>
    public static void OpenModelRequestsAndOldChats()
    {
        var saved = SolidWorksConnector.SolidWorksOpen;
        try
        {
            string[] prompts =
            [
                "what does the open part weigh and what material is it?", "list the dimensions of this part", "how big is the selected hole?",
                "set the material to 6061 Alloy", "suppress Fillet1 and set PartNo to BR-100", "list the BOM and check for interference",
                "make an A3 drawing with a PDF", "show me the model from the front", "save this as bracket-v2",
            ];
            SolidWorksConnector.SolidWorksOpen = () => true;
            foreach (var p in prompts)
            {
                var (tools, category) = JobSpecFactory.InferToolsAndCategory(p, null);
                Check.True(category == TaskCategory.Cad && tools.Contains(SolidWorksCadApp.InspectTool) && tools.Contains(SolidWorksCadApp.EditTool),
                    $"SolidWorks open: \"{p}\" → the SolidWorks tools ({category})");
            }
            Check.True(JobSpecFactory.InferToolsAndCategory("what is the capital of France?", null).Category == TaskCategory.Chat, "a plain question stays a chat");
            SolidWorksConnector.SolidWorksOpen = () => false;
            Check.True(JobSpecFactory.InferToolsAndCategory("what does the open part weigh?", null).Category != TaskCategory.Cad, "SolidWorks closed: a question does not start it");
        }
        finally { SolidWorksConnector.SolidWorksOpen = saved; }

        // A chat saved by V15 had only the V15 SolidWorks tools: reopened or continued, it has today's.
        string[] v15 = ["swx_build_parametric_part", "swx_set_part_globals", "swx_session", "swx_document_ops", "swx_export_neutral", "swx_part_cards"];
        var upgraded = JobSpecFactory.WithCurrentToolset(v15);
        Check.True(SolidWorksConnector.Tools.All(upgraded.Contains), "an old SolidWorks chat gets every current SolidWorks tool");
        Check.True(!JobSpecFactory.WithCurrentToolset([Mediator.Connectors.CadFiles.CadFileToolProvider.InspectTool]).Any(t => t.StartsWith("swx_", StringComparison.Ordinal)),
            "a CAD-file chat does not gain SolidWorks (V18: only its own connector's tools)");
        var reopened = new SavedResume("s1", AiProvider.ClaudeCode, Path.GetTempPath(), "j1", true, "what's open in SolidWorks?", "a part") { Tools = [.. v15], Roots = [], Category = TaskCategory.Cad }
            .ToSession(DateTimeOffset.Now);
        Check.True(reopened.Spec.Tools.Contains(SolidWorksCadApp.InspectTool) && reopened.Spec.Tools.Contains(SolidWorksCadApp.DrawingTool), "a reopened V15 chat has the V16 tools");
        var follow = JobSpecFactory.Continue(reopened, "now make it 25 mm");
        Check.True(follow.Tools.Contains(SolidWorksCadApp.EditTool), "and so do its follow-ups");
    }

    /// <summary>A SolidWorks job works on the open document: its folder joins the job's folders, the prompt names it.</summary>
    public static async Task OpenDocumentFolderShared(string bridgeDll)
    {
        string models = TempDir("open"), other = TempDir("other");
        string part = Path.Combine(models, "bracket.SLDPRT");
        File.WriteAllText(part, "p");
        string? active = part;
        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir("rt")),
            Bus = new BusOptions { PipeName = "mediator-cadr-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Simulate = true,
            Connectors = TestConnectors.All(activeDocument: _ => Task.FromResult<string?>(active)),
        });
        var cad = JobSpecFactory.Assistant(null, "what does the open part weigh?", LaunchMode.None, SolidWorksConnector.Tools, TaskCategory.Cad, runtime.ChatDirectory);
        var job = await runtime.StartJobAsync(cad);
        Check.True(job.Spec.Roots.Contains(models, StringComparer.OrdinalIgnoreCase) && job.Spec.Prompt.Contains(part, StringComparison.Ordinal),
            "a CAD chat with no folder gets the open document's folder and is told the file: " + string.Join(";", job.Spec.Roots));
        var withFolder = await runtime.StartJobAsync(cad with { Roots = [other], JobId = null });
        Check.True(withFolder.Spec.Roots.Count == 2, "a chosen folder stays, the open document's folder is added");
        var chat = await runtime.StartJobAsync(JobSpecFactory.Assistant(null, "hello", LaunchMode.None, [], TaskCategory.Chat, runtime.ChatDirectory));
        Check.True(chat.Spec.Roots.Count == 0 && !chat.Spec.Prompt.Contains("SolidWorks", StringComparison.Ordinal), "other jobs never get it");
        active = null;
        var none = await runtime.StartJobAsync(cad with { JobId = null });
        Check.True(none.Spec.Roots.Count == 0, "nothing open: no folder added");
    }

    /// <summary>The running build is recorded; a different build is told apart (V15 left no record).</summary>
    public static void RunningBuildRecorded()
    {
        string data = TempDir("instance");
        Check.True(RunningInstance.Read(data) is null && RunningInstance.Describe(null).Contains("older", StringComparison.Ordinal), "no record = an older version");
        RunningInstance.Write(data);
        var me = RunningInstance.Read(data)!;
        Check.True(me.IsThisBuild && me.ProcessId == Environment.ProcessId && me.Version == MediatorVersion.Name, "this build recorded and recognised: " + RunningInstance.Describe(me));
        var old = me with { Build = "000000000000", Version = "V16" };
        Check.True(!old.IsThisBuild && RunningInstance.Describe(old).Contains("V16 (build 000000000000)", StringComparison.Ordinal), "another build is told apart");
        Check.True(RunningInstance.OthersToReplace(me, System.Diagnostics.Process.GetCurrentProcess().ProcessName).All(p => p.Id != Environment.ProcessId),
            "this process is never closed as the old one");
    }
}
