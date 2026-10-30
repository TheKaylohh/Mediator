using Mediator.Core.Jobs;
using Mediator.UI;
using Mediator.UI.Workspace;

namespace Mediator.Tests;

/// <summary>V16.2: the full-screen workspace: layout rules, saved preferences, the view model's regions and modes.</summary>
static class WorkspaceTests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>The conversation keeps half of a large window; sidebars shrink, then hide, right first, and come back.</summary>
    public static void LayoutProtectsTheConversation()
    {
        var prefs = new WorkspacePrefs();
        var wide = WorkspaceLayout.Fit(1920, prefs);
        Check.True(wide is { LeftShown: true, RightShown: true, LeftWidth: WorkspaceLayout.LeftDefault, RightWidth: WorkspaceLayout.RightDefault }
            && wide.CenterWidth(1920) >= 960, "1920: both sidebars at their sizes, conversation ≥ 50%: " + WorkspaceLayout.Describe(wide));

        var greedy = WorkspaceLayout.Fit(1920, prefs with { LeftWidth = 420, RightWidth = 640 });
        Check.True(greedy.CenterWidth(1920) >= 960 && greedy.LeftShown && greedy.RightShown, "the widest sidebars still leave half: " + WorkspaceLayout.Describe(greedy));
        foreach (int w in new[] { 1280, 1366, 1600, 2560, 3840 })
        {
            var f = WorkspaceLayout.Fit(w, prefs with { LeftWidth = 420, RightWidth = 640 });
            Check.True(f.CenterWidth(w) >= Math.Ceiling(w * 0.5), $"{w}: conversation ≥ 50% ({WorkspaceLayout.Describe(f)})");
        }

        var narrow = WorkspaceLayout.Fit(1000, prefs);
        Check.True(narrow.LeftShown && narrow.RightShown && narrow.RightWidth == WorkspaceLayout.RightMin && narrow.CenterWidth(1000) >= WorkspaceLayout.CenterMinSmall,
            "1000: the right sidebar shrinks first: " + WorkspaceLayout.Describe(narrow));
        var small = WorkspaceLayout.Fit(800, prefs);
        Check.True(small is { LeftShown: true, RightShown: false, RightAutoHidden: true } && small.CenterWidth(800) >= WorkspaceLayout.CenterMinSmall,
            "800: the right sidebar is hidden for lack of room (not by choice): " + WorkspaceLayout.Describe(small));
        var tiny = WorkspaceLayout.Fit(560, prefs);
        Check.True(tiny is { LeftShown: false, RightShown: false, LeftAutoHidden: true }, "560: only the conversation");
        Check.True(WorkspaceLayout.Fit(1920, prefs) == wide, "the user's choices are kept: both come back on a wide window");
        Check.True(!WorkspaceLayout.Fit(1920, prefs with { RightVisible = false }).RightShown, "a sidebar the user hid stays hidden");
        Check.True(WorkspaceLayout.MaxLeft(1920, wide) == WorkspaceLayout.LeftMax && WorkspaceLayout.MaxLeft(1300, WorkspaceLayout.Fit(1300, prefs)) < WorkspaceLayout.LeftMax,
            "dragging is capped so the conversation keeps its share");
    }

    /// <summary>Sizes, visibility, mode and pins survive a restart, with custom actions and Save to (lost before V16.2).</summary>
    public static void PreferencesPersist()
    {
        string path = Path.Combine(TempDir(), "settings.json");
        var store = new JsonSettingsStore(path);
        var saved = new MediatorSettings
        {
            SaveFolder = @"C:\Work\Out",
            CustomActions = [new CustomActionDef("a1", "Summarize", "Short summary", "Summarize this", NeedsFolder: true)],
            Workspace = new WorkspacePrefs { LeftWidth = 333, RightWidth = 512, RightVisible = false, Mode = RightMode.Software, PinnedSoftware = ["solidworks"] },
        };
        store.Save(saved);
        var loaded = new JsonSettingsStore(path).Load();
        Check.True(loaded.Workspace == saved.Workspace, "workspace preferences round-trip: " + loaded.Workspace.ToJson().ToJsonString());
        Check.True(loaded.SaveFolder == @"C:\Work\Out" && loaded.CustomActions is [{ Id: "a1", Title: "Summarize", NeedsFolder: true }],
            "custom actions and the Save to folder survive a restart");
        File.WriteAllText(path, """{"workspace":{"leftWidth":99999,"rightWidth":"wide","mode":"Nope","leftVisible":false}}""");
        var odd = new JsonSettingsStore(path).Load().Workspace;
        Check.True(odd.LeftWidth == WorkspaceLayout.LeftMax && odd.RightWidth == WorkspaceLayout.RightDefault && odd.Mode == RightMode.Cockpit && !odd.LeftVisible,
            "out-of-range or unknown values fall back, the rest is kept");
        Check.True(new MediatorSettings() == new MediatorSettings(), "default settings compare equal (the pinned list compares by value)");
    }

    /// <summary>The regions and modes on a real conversation view model.</summary>
    public static void RegionsAndModes()
    {
        var chats = new ChatStore(TempDir());
        string project = TempDir();
        chats.Save(new SavedChat("20260101-000000-aaaaaa", "Bracket", DateTimeOffset.Now.AddMinutes(-5),
            [new SavedTurn("j-1", "make a bracket", "Claude", $"Saved {Path.Combine(project, "bracket.step")}.", [], false)], null, Pinned: true, Folders: [project]));
        chats.Save(new SavedChat("20260101-000000-bbbbbb", "Budget", DateTimeOffset.Now.AddMinutes(-1),
            [new SavedTurn("j-2", "budget sheet", "Claude", "done", [], false)], null));
        var settings = new MemorySettingsStore();
        var vm = new MediatorOverlayViewModel(new ModelChoiceTests.GatedRuntime(), new InlineDispatcher(), settings) { Chats = chats };
        var ws = vm.Workspace;

        Check.True(ws.Modes.Select(m => m.Title).SequenceEqual(["Cockpit", "Artifacts", "Access", "Activity", "Knowledge", "Software"]), "six modes, in the top bar's order");
        Check.True(ws.Cockpit.IsActive && ws.Modes.Count(m => m.IsActive) == 1, "one mode shows at a time (Cockpit first)");
        Check.True(ws.PinnedChats.Count == 1 && ws.PinnedChats[0].Title == "Bracket" && ws.RecentChats.Any(c => c.Title == "Budget"), "pinned and recent chats");
        Check.True(ws.Projects.Count == 1 && ws.Projects[0].Folder == Path.TrimEndingDirectorySeparator(project) && ws.Projects[0].ChatCount == 1, "a folder a chat worked in is a project");

        ws.AvailableWidth = 1920;
        ws.Activity.Query = "kept";
        ws.SelectMode(RightMode.Activity);
        Check.True(ws.Activity.IsActive && !ws.Cockpit.IsActive && settings.Current.Workspace.Mode == RightMode.Activity, "a mode button switches the panel and is saved");
        ws.SelectMode(RightMode.Cockpit);
        ws.SelectMode(RightMode.Activity);
        Check.Equal("kept", ws.Activity.Query, "a mode keeps its state while another shows");
        ws.SelectMode(RightMode.Activity);
        Check.True(!ws.IsRightShown && !settings.Current.Workspace.RightVisible, "pressing the shown mode again hides the right sidebar (saved)");
        ws.SelectMode(RightMode.Access);
        Check.True(ws.IsRightShown && ws.Access.IsActive, "another mode opens it again");

        ws.LeftWidth = 5000;
        Check.True(ws.LeftWidth == ws.LeftMaxWidth && ws.LeftWidth <= WorkspaceLayout.LeftMax && settings.Current.Workspace.LeftWidth == ws.LeftWidth,
            "a splitter drag is clamped and saved");
        ws.AvailableWidth = 800;
        Check.True(!ws.IsRightShown && ws.IsLeftShown && settings.Current.Workspace.RightVisible, "a narrow window hides the right sidebar without changing the choice");
        ws.AvailableWidth = 1920;
        Check.True(ws.IsRightShown, "and it comes back when the window grows");
        ws.ToggleLeftCommand.Execute(null);
        Check.True(!ws.IsLeftShown && ws.LeftWidth == 0 && !settings.Current.Workspace.LeftVisible, "the left sidebar collapses (column width 0)");
        ws.ToggleLeftCommand.Execute(null);

        vm.OpenSavedChatCommand.Execute(vm.SavedChats.First(c => c.Title == "Bracket"));
        File.WriteAllText(Path.Combine(project, "bracket.step"), "ISO-10303-21;");
        ws.SelectMode(RightMode.Artifacts);
        Check.True(ws.Artifacts.Items.Count == 1 && ws.Artifacts.Items[0].Name == "bracket.step" && ws.Artifacts.Items[0].Kind == "CAD" && ws.Artifacts.HasSelection,
            "a file an answer names (and that exists) is an artifact, selected for the viewer");
        Check.True(ws.Cockpit.Outputs == ws.Artifacts.Items, "the Cockpit's Outputs are the same list");
        ws.Artifacts.KindFilter = "PDF";
        Check.True(ws.Artifacts.Items.Count == 0, "kind filter");

        ws.PaletteQuery = "";
        ws.OpenPaletteCommand.Execute(null);
        Check.True(ws.IsPaletteOpen && ws.PaletteResults.Any(r => r.Title == "Open Software") && ws.PaletteResults.Any(r => r.Title.Contains("Budget", StringComparison.Ordinal)),
            "the palette lists commands, modes and chats");
        ws.PaletteQuery = "knowl";
        ws.RunFirstPaletteItemCommand.Execute(null);
        Check.True(ws.Knowledge.IsActive && !ws.IsPaletteOpen, "Enter runs the first match and closes the palette");
        Check.True(ws.ProfileItems.Select(p => p.Title).SequenceEqual(["Usage & Analytics", "Providers", "Connections (MCP servers)", "Keyboard Shortcuts", "Appearance", "Settings", "Logs & Diagnostics", "About"]),
            "profile menu: no subscription, billing or plan items");

        ws.NewChatCommand.Execute(null);
        Check.True(!vm.HasConversation && ws.SessionText == "New chat", "New chat (Ctrl+N) clears the conversation");
        vm.StartAppChat("SolidWorks", "sldworks", null, "SolidWorks (Running). Mediator can: parts, drawings.");
        Check.True(vm.ContextChips.Any(c => c.Kind == ContextKind.App && c.Label == "SolidWorks"), "Software Library: a new chat about a program carries it as context");
        Check.True(SoftwareModeViewModel.Describe("excel").Prefix == "/office " && SoftwareModeViewModel.Describe("solidworks").Category == "CAD", "categories and tool prefixes per program");
    }
}
