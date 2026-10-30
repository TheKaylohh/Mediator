using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Cad;
using Mediator.Connectors.FreeCAD;
using Mediator.Connectors.AutoCAD;
using Mediator.Connectors.Inventor;
using Mediator.Connectors.NX;
using Mediator.Connectors.Creo;
using Mediator.Connectors.CATIA;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Connectors.SolidWorks;

namespace Mediator.Tests;

/// <summary>
/// V17: the CAD expansion. The operation catalog through the shared provider (per-program ops, approval rows, file paths in
/// operations, the Destructive script tool), the script host end to end with a stand-in "CAD program" in Python, static checks
/// of every connector script, the install locator and routing to the new programs.
/// </summary>
static class CadExpansionTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "cadx-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static JobGrant Grant(string root) => new()
    {
        JobId = "j-" + Guid.NewGuid().ToString("N")[..6], Token = "t", AllowedTools = new HashSet<string>(), FileRoots = [root],
        Paths = new PathAuthorizer([root]), ExpiresAt = DateTimeOffset.MaxValue,
    };

    static ToolCallContext Ctx(IToolProvider p, JobGrant job, string tool, string json, IReadOnlySet<string>? rows = null, JsonNode? approved = null) =>
        new(job, p.Tools.Single(t => t.Name == tool), JsonDocument.Parse(json).RootElement, "c", rows, approved);

    static string J(string s) => JsonSerializer.Serialize(s);

    /// <summary>A "CAD program" whose automation script is plain Python: proves the arguments, verbs and answers end to end.</summary>
    sealed class PyCadApp(string python, string scriptFolder) : ScriptCadApp(scriptFolder)
    {
        public override string Prefix => "py";
        public override string DisplayName => "PyCAD";
        public override bool HasLiveSession => false;
        protected override string ScriptName => "freecad.py"; // the override folder holds a stand-in of the same name
        public override IReadOnlySet<CadCapability> Capabilities { get; } = new HashSet<CadCapability>
            { CadCapability.Session, CadCapability.Feature, CadCapability.Simulate, CadCapability.Script, CadCapability.Assembly };
        public override IReadOnlyList<string> ModelExtensions { get; } = [".pycad"];
        public override IReadOnlyList<CadExportFormat> ExportFormats { get; } = [];
        public override CadScriptInfo Script => new("Python", "test");
        protected override IReadOnlyDictionary<CadCapability, string[]> SupportedOps { get; } = new Dictionary<CadCapability, string[]>
        {
            [CadCapability.Feature] = ["extrude", "fillet"], [CadCapability.Simulate] = ["fixed", "force", "solve"], [CadCapability.Assembly] = ["insert"],
        };
        protected override CadScriptRunner? Runner(out string? why) { why = null; return new CadScriptRunner(python, ["-I"], [], ".py"); }
        protected override TimeSpan Timeout(string verb) => verb == "session" ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(2);
    }

    const string StandIn = """
        import json, os, time
        a = json.load(open(os.environ["MEDIATOR_ARGS"], encoding="utf-8"))
        def out(o): json.dump(o, open(os.environ["MEDIATOR_RESULT"], "w", encoding="utf-8"))
        v = a["verb"]
        print("noise on stdout that must not break the answer")
        if v == "session": out({"running": True, "version": "1.0"})
        elif v == "model_features": out({"results": [{"op": o["op"], "ok": True, "detail": o.get("radius")} for o in a["ops"]], "path": a["path"]})
        elif v == "assembly_ops": out({"results": [{"op": o["op"], "ok": True, "detail": o["path"]} for o in a["ops"]]})
        elif v == "simulate": time.sleep(30)
        elif v == "script":
            exec(a["code"])
        """;

    static string? Python()
    {
        foreach (var c in new[] { "python3", "python" })
            if (CliLaunchBuilder.ResolveOnPath(c) is { } p) return p;
        return null;
    }

    public static async Task OpsThroughTheProvider()
    {
        string? python = Python();
        if (python is null) { Console.WriteLine("      (python3 not found: script-host part skipped)"); return; }
        string root = TempDir("ops"), scripts = TempDir("scripts");
        File.WriteAllText(Path.Combine(scripts, "freecad.py"), StandIn.Replace("\n        ", "\n"));
        var app = new PyCadApp(python, scripts);
        var p = new CadAppToolProvider(app);
        var job = Grant(root);
        string part = Path.Combine(root, "plate.pycad");
        File.WriteAllText(part, "x");

        Check.True(p.ToolNames.SetEquals(["py_session", "py_model_features", "py_simulate", "py_run_script", "py_assembly_ops"]), "tools from capabilities: " + string.Join(",", p.ToolNames));
        var featureTool = p.Tools.Single(t => t.Name == "py_model_features");
        Check.True(featureTool.InputSchema.ToJsonString().Contains("\"fillet\"", StringComparison.Ordinal) && !featureTool.InputSchema.ToJsonString().Contains("\"hole\"", StringComparison.Ordinal)
            && featureTool.Description.Contains("op:\"fillet\", radius", StringComparison.Ordinal), "the schema and description list the program's own operations: " + featureTool.Description);
        Check.True(p.Tools.Single(t => t.Name == "py_run_script").Risk == ToolRisk.Destructive, "the script tool is Destructive (always asks, never auto-approved)");

        var session = await p.ExecuteAsync(Ctx(p, job, "py_session", "{}"), CancellationToken.None);
        Check.True(!session.IsError && session.Text.Contains("\"version\":\"1.0\"", StringComparison.Ordinal), "a verb runs and its answer comes back through the result file, not stdout: " + session.Text);

        var noPath = await p.ExecuteAsync(Ctx(p, job, "py_model_features", """{"ops":[{"op":"fillet","radius":2}]}"""), CancellationToken.None);
        Check.True(noPath.IsError && noPath.Text.Contains("give the path", StringComparison.Ordinal), "a file-based program needs the document's path: " + noPath.Text);
        string json = $$"""{"path":{{J(part)}},"ops":[{"op":"fillet","radius":2},{"op":"extrude","depth":5},{"op":"hole","size":"M6"}]}""";
        var refused = await p.ExecuteAsync(Ctx(p, job, "py_model_features", json), CancellationToken.None);
        Check.True(refused.IsError && refused.Text.Contains("'hole' is not something PyCAD can do", StringComparison.Ordinal), "an operation the program lacks is refused before it runs: " + refused.Text);
        json = $$"""{"path":{{J(part)}},"ops":[{"op":"fillet","radius":2},{"op":"extrude","depth":5}]}""";
        var preview = (await p.BuildPreviewAsync(Ctx(p, job, "py_model_features", json), CancellationToken.None))!;
        Check.True((bool?)preview["selectable"] == true && preview["rows"]!.AsArray().Count == 2 && ((string?)preview["rows"]![0]!["primary"])!.Contains("radius 2", StringComparison.Ordinal),
            "one approval row per operation: " + preview.ToJsonString());
        var kept = await p.ExecuteAsync(Ctx(p, job, "py_model_features", json, new HashSet<string> { "1" }), CancellationToken.None);
        Check.True(!kept.IsError && kept.Text.Contains("\"detail\":2", StringComparison.Ordinal) && !kept.Text.Contains("extrude\",\"ok", StringComparison.Ordinal)
            && kept.Text.Contains("unticked", StringComparison.Ordinal) && kept.Text.Contains(J(part)[1..^1].Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal),
            "only the kept operation runs, on the checked path: " + kept.Text);

        string outside = Path.Combine(TempDir("outside"), "bolt.pycad");
        File.WriteAllText(outside, "x");
        var insert = await p.ExecuteAsync(Ctx(p, job, "py_assembly_ops", $$"""{"path":{{J(part)}},"ops":[{"op":"insert","path":{{J(outside)}}}]}"""), CancellationToken.None);
        Check.True(insert.IsError && insert.Text.Contains("outside", StringComparison.Ordinal), "a file named inside an operation must be in the job's folders: " + insert.Text);

        var slow = await p.ExecuteAsync(Ctx(p, job, "py_simulate", $$"""{"path":{{J(part)}},"ops":[{"op":"solve"}]}"""), CancellationToken.None);
        Check.True(slow.IsError && slow.Text.Contains("did not finish", StringComparison.Ordinal), "a hung program is stopped at its timeout: " + slow.Text);

        string code = "out({'answer': 6 * 7})";
        string scriptCall = $$"""{"path":{{J(part)}},"code":{{J(code)}}}""";
        var scriptPreview = (await p.BuildPreviewAsync(Ctx(p, job, "py_run_script", scriptCall), CancellationToken.None))!;
        Check.True((string?)scriptPreview["rows"]![0]!["primary"] == code, "the approval card shows the exact code");
        var ran = await p.ExecuteAsync(Ctx(p, job, "py_run_script", scriptCall, approved: scriptPreview), CancellationToken.None);
        Check.True(!ran.IsError && ran.Text.Contains("\"answer\":42", StringComparison.Ordinal), "approved code runs in the program's language: " + ran.Text);
        var swapped = await p.ExecuteAsync(Ctx(p, job, "py_run_script", $$"""{"code":"out({'answer': 0})"}""", approved: scriptPreview), CancellationToken.None);
        Check.True(swapped.IsError && swapped.Text.Contains("differs", StringComparison.Ordinal), "code that differs from the approved code does not run");
        var broken = await p.ExecuteAsync(Ctx(p, job, "py_run_script", """{"code":"raise SystemExit(3)"}"""), CancellationToken.None);
        Check.True(broken.IsError && broken.Text.Contains("exit code 3", StringComparison.Ordinal) && broken.Text.Contains("noise on stdout", StringComparison.Ordinal),
            "a failing script reports its exit code and output tail: " + broken.Text);
    }

    /// <summary>Every connector script: present, ASCII, compiles/parses, and handles every verb its tools call.</summary>
    public static void ScriptsAreSound()
    {
        var apps = ConnectorCatalog.All.OfType<CadAppConnector>().Select(c => (ScriptCadApp)c.CreateApp(null)).ToList();
        foreach (var app in apps)
        {
            string script = app.LoadScript();
            Check.True(script.All(c => c < 128), $"{app.DisplayName}: the script is ASCII (Windows PowerShell 5.1 and older Pythons read it safely)");
            var verbs = app.Capabilities.Select(c => c switch
            {
                CadCapability.Documents => "document", CadCapability.Drawing => "drawing", CadCapability.Views => "views", CadCapability.Script => "script",
                CadCapability.Edit => "edit", CadCapability.Export => "export", CadCapability.Inspect => "inspect", CadCapability.Sketch => "sketch",
                _ => CadVerb.For(c),
            }).Append(app.HasLiveSession ? "active_path" : "session");
            foreach (var v in verbs)
                Check.True(script.Contains($"'{v}'", StringComparison.Ordinal) || script.Contains($"\"{v}\"", StringComparison.Ordinal),
                    $"{app.DisplayName}: the script handles the '{v}' verb its tools call");
            foreach (var (cap, ops) in app.Capabilities.Where(CadVerb.TakesOps).Select(c => (c, app.Ops(c))))
                foreach (var op in ops)
                    Check.True(script.Contains($"'{op}'", StringComparison.Ordinal) || script.Contains($"\"{op}\"", StringComparison.Ordinal),
                        $"{app.DisplayName}: operation {op} ({cap}) is implemented in the script");
        }
        Check.True(Embedded("solidworks.ps1").Contains("ScriptBlock", StringComparison.Ordinal), "the SolidWorks script host is embedded");

        string dir = TempDir("compile");
        if (Python() is { } py)
            foreach (var name in new[] { "freecad.py", "nx.py" })
            {
                string f = Path.Combine(dir, name);
                File.WriteAllText(f, Embedded(name));
                var r = Run(py, "-I", "-c", $"import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read())", f);
                Check.True(r.Exit == 0, $"{name} is valid Python: {r.Output}");
            }
        string? pwsh = Environment.GetEnvironmentVariable("MEDIATOR_PWSH") is { Length: > 0 } e && File.Exists(e) ? e : CliLaunchBuilder.ResolveOnPath("pwsh");
        if (pwsh is null) { Console.WriteLine("      (pwsh not found: PowerShell parse check skipped; set MEDIATOR_PWSH)"); return; }
        foreach (var name in new[] { "autocad.ps1", "inventor.ps1", "catia.ps1", "creo.ps1", "solidworks.ps1" })
        {
            string f = Path.Combine(dir, name);
            File.WriteAllText(f, Embedded(name));
            var r = Run(pwsh, "-NoProfile", "-Command", "$t=$null;$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('" + f +
                "',[ref]$t,[ref]$e);if($e.Count){$e|ForEach-Object{$_.Message};exit 1}");
            Check.True(r.Exit == 0, $"{name} parses as PowerShell: {r.Output}");
        }
    }

    static (int Exit, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, o);
    }

    /// <summary>The newest version folder wins; the override variable and Apps &amp; features are honoured.</summary>
    public static void LocatorFindsInstalls()
    {
        string pf = TempDir("pf");
        foreach (var v in new[] { "AutoCAD 2024", "AutoCAD 2025" })
        {
            Directory.CreateDirectory(Path.Combine(pf, "Autodesk", v));
            File.WriteAllText(Path.Combine(pf, "Autodesk", v, "acad.exe"), "");
        }
        var env = new Dictionary<string, string?> { ["ProgramFiles"] = pf };
        var loc = new CadLocator(k => env.GetValueOrDefault(k), null, _ => null, windows: true);
        Check.True(loc.Find(AutoCadApp.Program)?.Contains("AutoCAD 2025", StringComparison.Ordinal) == true, "the newest version folder is chosen");
        Check.True(loc.Find(CatiaApp.Program) is null, "a program that is not there is not found");
        string fc = Path.Combine(TempDir("fc"), "FreeCADCmd.exe");
        File.WriteAllText(fc, "");
        env["MEDIATOR_FREECAD"] = fc;
        Check.Equal(fc, loc.Find(FreeCadApp.Program), "the override variable wins");
        string inv = TempDir("inv");
        Directory.CreateDirectory(Path.Combine(inv, "Bin"));
        File.WriteAllText(Path.Combine(inv, "Bin", "Inventor.exe"), "");
        var reg = new DesktopAppTests.FakeRegistry().Set(@"HKLM\" + DesktopAppLocator.UninstallKey + @"\inv", "DisplayName", "Autodesk Inventor Professional 2025")
            .Set(@"HKLM\" + DesktopAppLocator.UninstallKey + @"\inv", "InstallLocation", inv)
            .Set(@"HKLM\SOFTWARE\Classes\CATIA.Application\CLSID", "", "{x}");
        var loc2 = new CadLocator(k => env.GetValueOrDefault(k), reg, _ => null, windows: true);
        Check.Equal(Path.Combine(inv, "Bin", "Inventor.exe"), loc2.Find(InventorApp.Program), "Apps & features install location");
        Check.True(loc2.ComRegistered(CatiaApp.Program) && !loc2.ComRegistered(AutoCadApp.Program), "a registered COM class counts as installed");
        Check.True(new AutoCadApp(loc2).Unavailable() is { } why && why.Length > 0, "off Windows (or not installed) the connector says why, and its tools are never run");
    }

    public static void RoutingToTheNewPrograms()
    {
        var saved = SolidWorksConnector.SolidWorksOpen;
        SolidWorksConnector.SolidWorksOpen = () => false;
        try
        {
            foreach (var (prompt, prefix) in new[]
            {
                ("open bracket.ipt in inventor and list its parameters", "inv_"), ("make a 50 mm cube in FreeCAD", "fc_"), ("add a title block to plan.dwg", "acad_"),
                ("what does the CATIA product weigh", "catia_"), ("regenerate the creo model", "creo_"), ("export the siemens nx part to step", "nx_"),
            })
            {
                var (tools, category) = JobSpecFactory.InferToolsAndCategory(prompt, null);
                Check.True(category == TaskCategory.Cad && tools.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)) && !tools.Any(t => t.StartsWith("swx_", StringComparison.Ordinal)),
                    $"\"{prompt}\" → the {prefix} tools only ({string.Join(",", tools.Take(4))}…)");
            }
            Check.True(JobSpecFactory.InferToolsAndCategory("what's the capital of Peru", null).Category == TaskCategory.Chat, "plain questions stay chats");
            Check.True(JobSpecFactory.InferToolsAndCategory("help", "acad").Tools.Any(t => t.StartsWith("acad_", StringComparison.Ordinal)), "AutoCAD in front → its tools");
            Check.True(JobSpecFactory.WithCurrentToolset(["fc_session"]).Contains("fc_model_features"), "a saved chat with one FreeCAD tool gets them all");
        }
        finally { SolidWorksConnector.SolidWorksOpen = saved; }
        Check.True(SolidWorksCadApp.ToolNames.All(SolidWorksConnector.Tools.Contains), "every SolidWorks tool is in CAD jobs");
        var swx = new CadAppToolProvider(new SolidWorksCadApp());
        Check.True(swx.ToolNames.Contains("swx_sketch") && swx.ToolNames.Contains("swx_model_features") && swx.ToolNames.Contains("swx_run_script") && !swx.ToolNames.Contains("swx_simulate"),
            "SolidWorks: sketch, features, assembly, annotate and script tools (simulation through scripts)");
    }
    /// <summary>V18: each connector embeds its own script.</summary>
    private static string Embedded(string name) => ScriptCadApp.LoadEmbedded(name switch
    {
        "freecad.py" => typeof(FreeCadApp).Assembly, "nx.py" => typeof(NxApp).Assembly, "autocad.ps1" => typeof(AutoCadApp).Assembly,
        "inventor.ps1" => typeof(InventorApp).Assembly, "catia.ps1" => typeof(CatiaApp).Assembly, "creo.ps1" => typeof(CreoApp).Assembly,
        _ => typeof(Mediator.Connectors.SolidWorks.SolidWorksCadApp).Assembly,
    }, name, null);

}
