using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.UI;
using Mediator.UI.Workspace;

namespace Mediator.Tests;

/// <summary>V18: connectors as plug-in pieces, and the full-screen app and the quick panel shown together.</summary>
static class PluginTests
{
    static string TempDir(string name)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "v18-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Every connector describes itself; the app works with any subset, including none.</summary>
    public static void CatalogIsTheOnlyLink()
    {
        var all = ConnectorCatalog.All;
        Check.Equal(13, all.Count, "13 connectors: SolidWorks, six CAD programs, parts from code, CAD files, Office, Resolve, Blender, Tutor");
        Check.True(all.Select(c => c.Id).Distinct().Count() == all.Count && all.All(c => System.Text.RegularExpressions.Regex.IsMatch(c.Id, "^[a-z0-9-]+$")), "unique, plain ids");
        Check.True(all.All(c => c.ChatTools.Count > 0 && c.DisplayName.Length > 0 && c.Summary.Length > 0), "each one names its tools and says what it does");
        var shared = all.SelectMany(c => c.ChatTools).Distinct().Where(t => all.Count(c => c.OwnsTool(t)) != 1).ToList();
        Check.True(shared.Count == 0, "every tool a chat can get belongs to exactly one connector: " + string.Join(", ", shared));
        Check.True(ConnectorCatalog.Recipes.Select(r => r.Id).SequenceEqual(["assemble", "codecad", "resolve", "blender", "tutor"]), "the connectors' tiles, in order");
        Check.True(MediatorOverlayViewModel.DefaultActions().Select(a => a.Id).SequenceEqual(["assemble", "codecad", "organize", "resolve", "blender", "tutor", "claude-terminal"]),
            "built-in tiles (Organize, Claude Code) slot between the connectors' as before");
        Check.True(ConnectorCatalog.Programs.Select(p => p.Id).Contains("solidworks") && ConnectorCatalog.Program("outlook")?.PromptPrefix == "/outlook ", "programs describe themselves for the Software Library");

        var saved = ConnectorCatalog.All;
        try
        {
            // Without SolidWorks: CAD files still route to the CAD files connector, SolidWorks words reach nothing.
            ConnectorCatalog.Set(saved.Where(c => c.Id != "solidworks"));
            var (tools, category) = JobSpecFactory.InferToolsAndCategory("inspect bracket.stl and tell me its size", null);
            Check.True(category == TaskCategory.Cad && tools.SequenceEqual(CadFilesConnector.Tools), "without SolidWorks an STL still reaches the CAD files connector");
            Check.True(!JobSpecFactory.InferToolsAndCategory("assemble the parts in solidworks", null).Tools.Any(t => t.StartsWith("swx_", StringComparison.Ordinal)), "no SolidWorks tools without its connector");
            Check.True(JobSpecFactory.Recipe("assemble", new RecipeRequest(TempDir("a"), "", LaunchMode.None)) is null, "its tile and CLI recipe are gone");
            Check.True(!MediatorOverlayViewModel.DefaultActions().Any(a => a.Id is "assemble" or "codecad"), "no SolidWorks tiles");

            // Nothing installed: the app still chats, organizes and opens Claude Code.
            ConnectorCatalog.Set([]);
            Check.True(JobSpecFactory.InferToolsAndCategory("make an excel workbook of my budget", null).Category != TaskCategory.Office, "no Office without its connector");
            Check.True(MediatorOverlayViewModel.DefaultActions().Select(a => a.Id).SequenceEqual(["organize", "claude-terminal"]), "only the built-in tiles");
            Check.True(JobSpecFactory.InferToolsAndCategory("tidy my downloads folder", null).Category == TaskCategory.Files, "organizing is built in");
            Check.True(!ConnectorCatalog.ProbeProgram("solidworks", new ConnectorProbe(Mediator.Core.Tools.DefaultProcessRunner.Instance, TempDir("h")).Programs).Available, "an unknown program is reported, not probed");
        }
        finally { ConnectorCatalog.Set(saved); }
        Check.Equal(13, ConnectorCatalog.All.Count, "restored");
    }

    /// <summary>A connector folder loads in its own context and shares Mediator.Core with the app; a broken one is skipped.</summary>
    public static void LoaderFindsFolders()
    {
        string repo = XamlChecker.RepoRoot();
        var dev = ConnectorLoader.Candidates(Path.Combine(repo, "verify")).ToList();
        Check.True(dev.Count == 13 && dev.All(File.Exists), $"a checkout finds the 13 connector builds ({dev.Count})");
        string office = dev.Single(p => p.EndsWith("Mediator.Connector.Office.dll", StringComparison.Ordinal));
        var loaded = ConnectorLoader.Load(office);
        Check.True(loaded.Error is null && loaded.Connectors.Single() is { Id: "office" } c && c.ChatTools.SequenceEqual(OfficeConnector.Tools),
            $"the Office connector loads from its file ({loaded.Error})");
        Check.True(loaded.Connectors[0].GetType().Assembly != typeof(OfficeConnector).Assembly && loaded.Connectors[0] is IMediatorConnector,
            "in its own load context, through the shared contract");

        string app = TempDir("app");
        string folder = Path.Combine(app, ConnectorLoader.FolderName, "Broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Mediator.Connector.Broken.dll"), "not an assembly");
        Directory.CreateDirectory(Path.Combine(app, ConnectorLoader.FolderName, "Empty"));
        var results = ConnectorLoader.LoadAll(app);
        Check.True(results.Count == 1 && results[0].Error is not null && results[0].Connectors.Count == 0, "a broken connector is reported and skipped; an empty folder is ignored");
    }

    /// <summary>A runtime starts only the connectors it is given; a job is granted only tools that exist.</summary>
    public static async Task RuntimeWithoutConnectors(string bridgeDll)
    {
        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir("data")),
            Bus = new BusOptions { PipeName = "mediator-v18-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Simulate = true,
            Connectors = [new OfficeConnector()],
        });
        Check.True(runtime.ConnectorInstances.Count == 1 && runtime.ConnectorInstances[0].Connector is OfficeConnector, "one connector started");
        Check.True(!runtime.Tools.TryGet(SwxPartToolProvider.BuildTool, out _, out _) && runtime.Tools.TryGet(OfficeToolProvider.ReadTool, out _, out _), "only its tools are registered");
        string dir = TempDir("work");
        var job = await runtime.StartJobAsync(JobSpecFactory.Assistant(dir, "budget", LaunchMode.None, [.. OfficeConnector.Tools, SwxPartToolProvider.BuildTool], TaskCategory.Office));
        Check.True(job.Grant.AllowedTools.Contains(OfficeToolProvider.ReadTool) && !job.Grant.AllowedTools.Contains(SwxPartToolProvider.BuildTool),
            "a tool whose connector is not installed is left out of the grant");
        await runtime.EndJobAsync(job.JobId);
    }

    /// <summary>V18.1: the full-screen app and the quick panel can be on screen together.</summary>
    public static void OneViewAtATime()
    {
        var vm = new MediatorOverlayViewModel(new ModelChoiceTests.GatedRuntime(), new InlineDispatcher(), new MemorySettingsStore());
        vm.Show();
        vm.IsWorkspaceOpen = true;
        Check.True(vm.IsVisible, "opening the full-screen app leaves the quick panel up");
        vm.Hide();
        vm.OnHotkey(null);
        Check.True(vm.IsVisible, "the hotkey and the tray open the quick panel while the full-screen app is open");

        vm.ShowPanelTab(MediatorOverlayViewModel.ConnectorsTab);
        Check.True(vm.IsWorkspaceOpen && vm.IsVisible && vm.SelectedTab == MediatorOverlayViewModel.ConnectorsTab,
            "a settings tab asked for by the full-screen app opens on the quick panel and leaves the full-screen app open");
        vm.OpenFullViewCommand.Execute(null);
        Check.True(vm.IsVisible, "Open full view leaves the quick panel as it was");
    }

    /// <summary>The full-screen app reopens where it was; a bad saved place is ignored.</summary>
    public static void WindowPlacementPersists()
    {
        var prefs = new WorkspacePrefs { Window = new WindowBounds(120, 80, 1400, 900, false) };
        var back = WorkspacePrefs.FromJson(prefs.ToJson());
        Check.True(back == prefs && back.Window is { Left: 120, Width: 1400, Maximized: false }, "bounds round-trip in settings.json");
        Check.True(WindowBounds.FromJson(new() { ["left"] = 0, ["top"] = 0, ["width"] = 100, ["height"] = 50 }) is null, "too small: centred and maximized instead");
        Check.True(WindowBounds.FromJson(new() { ["left"] = 0, ["top"] = 0 }) is null && WorkspacePrefs.FromJson(new()).Window is null, "missing: default placement");
    }
}
