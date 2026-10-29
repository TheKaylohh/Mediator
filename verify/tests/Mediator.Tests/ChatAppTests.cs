using System.Text.Json.Nodes;
using Mediator.Core.Jobs;
using Mediator.Core.Swx;
using Mediator.UI;

namespace Mediator.Tests;

/// <summary>V10: chat-app features (search, rename, star, retry, edit, recall) and design iterations (versions, views, proportions).</summary>
static class ChatAppTests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "chatapp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static SavedChat Chat(string id, string title, string prompt, int minutesAgo, bool pinned = false) =>
        new(id, title, DateTimeOffset.Now.AddMinutes(-minutesAgo), [new SavedTurn("j-" + id, prompt, "Claude", "answer about " + prompt, [], false)], null, pinned);

    public static async Task SearchRenameStarRetryEdit()
    {
        var store = new ChatStore(TempDir());
        store.Save(Chat("20260101-000000-aaaaaa", "Old chat", "fillet radius", 30));
        store.Save(Chat("20260101-000000-bbbbbb", "Newer chat", "brake disc", 10));
        store.Save(Chat("20260101-000000-cccccc", "Starred chat", "F40 body", 60, pinned: true));
        Check.True(store.List().Select(c => c.Title).SequenceEqual(["Starred chat", "Newer chat", "Old chat"]), "starred first, then newest");

        var rt = new ModelChoiceTests.GatedRuntime();
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher()) { Chats = store };
        vm.ChatSearch = "BRAKE";
        Check.True(vm.SavedChats.Count == 1 && vm.SavedChats[0].Title == "Newer chat", "search matches prompts, case-insensitive");
        vm.ChatSearch = "answer about fillet";
        Check.True(vm.SavedChats.Count == 1 && vm.SavedChats[0].Title == "Old chat", "search matches answers");
        vm.ChatSearch = "";
        Check.Equal(3, vm.SavedChats.Count, "clearing the search lists every chat");

        vm.OpenSavedChatCommand.Execute(vm.SavedChats.First(c => c.Title == "Old chat"));
        vm.ChatTitle = "Fillet notes";
        Check.True(store.Load("20260101-000000-aaaaaa") is { CustomTitle: "Fillet notes", DisplayTitle: "Fillet notes", Title: "fillet radius" },
            "rename is saved as a custom title (the prompt title is kept)");
        vm.IsChatPinned = true;
        Check.True(store.Load("20260101-000000-aaaaaa")!.Pinned && store.Load("20260101-000000-aaaaaa")!.CustomTitle == "Fillet notes",
            "star is saved and keeps the title");
        vm.PinSavedChatCommand.Execute(vm.SavedChats.First(c => c.Title == "Newer chat"));
        Check.True(store.Load("20260101-000000-bbbbbb")!.Pinned, "a sidebar star saves another chat without opening it");
        vm.ChatTitle = "";
        Check.True(store.Load("20260101-000000-aaaaaa")!.CustomTitle is null && vm.ChatTitle == "fillet radius", "an empty title goes back to the first prompt");

        Check.True(vm.RecallLastPrompt() && vm.PromptText == "fillet radius", "Up in an empty box recalls the last prompt");
        Check.True(!vm.RecallLastPrompt(), "never overwrites text being typed");
        vm.PromptText = "";
        vm.EditTurnCommand.Execute(vm.Conversation[0]);
        Check.Equal("fillet radius", vm.PromptText, "Edit puts the prompt back in the box");
        Check.Equal("answer about fillet radius", vm.LastResponse, "last answer for Ctrl+Shift+C");

        vm.PromptText = "";
        Check.True(vm.RetryCommand.CanExecute(null), "Retry is offered when idle");
        vm.RetryCommand.Execute(null);
        Check.True(rt.Started.Count == 1 && rt.Started[0].Prompt.Contains("fillet radius", StringComparison.Ordinal) && vm.IsChatBusy && !vm.RetryCommand.CanExecute(null),
            "Retry resends the last prompt and is off while it runs");
        Check.True(vm.Conversation.All(t => t.JobId != "j-20260101-000000-aaaaaa"), "the retried turn's old answer is replaced");
        rt.Gate.SetResult();
        for (int i = 0; i < 100 && vm.IsChatBusy; i++) await Task.Delay(10);

        vm.ClearConversationCommand.Execute(null);
        Check.True(!vm.IsChatPinned && vm.ChatTitle == "New chat", "a new chat starts unstarred and untitled");
    }

    /// <summary>A long CAD recipe used to look frozen for minutes: partial messages now show live progress on the turn.</summary>
    public static void LiveProgressWhileWriting()
    {
        var start = ClaudeStreamParser.ParseLine("""{"type":"stream_event","session_id":"s1","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"mcp__mediator__swx_build_parametric_part","input":{}}}}""");
        var delta = ClaudeStreamParser.ParseLine("""{"type":"stream_event","session_id":"s1","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"name\":\"F40-v1\""}}}""");
        var text = ClaudeStreamParser.ParseLine("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Reference sheet"}}}""");
        var other = ClaudeStreamParser.ParseLine("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use"}}}""");
        Check.True(start is [ProgressEvent { Kind: "tool", Tool: "mcp__mediator__swx_build_parametric_part" }], "a tool call starting is progress");
        Check.True(delta is [ProgressEvent { Kind: "tool", Tool: null, Chars: 16 }], "tool input pieces count characters");
        Check.True(text is [ProgressEvent { Kind: "text", Chars: 15 }] && other.Count == 0, "text pieces count; other stream events are ignored, not logged as unknown");

        var rt = new ModelChoiceTests.GatedRuntime();
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher());
        var turn = new ChatTurnViewModel("job-p", "make an F40", "CAD") { IsRunning = true, ResponseText = "I'll start by loading the tools." };
        vm.Conversation.Add(turn);
        var running = (Dictionary<string, ChatTurnViewModel>)typeof(MediatorOverlayViewModel)
            .GetField("_turns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
        running["job-p"] = turn;
        var handler = typeof(MediatorOverlayViewModel).GetMethod("OnStreamEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var e in start.Concat(delta).Concat(delta)) handler.Invoke(vm, ["job-p", e]);
        Check.Equal("Preparing swx_build_parametric_part… 32 characters", turn.ThinkingSummary, "the turn shows the tool being prepared and how much is written");
        foreach (var e in text) handler.Invoke(vm, ["job-p", e]);
        Check.Equal("Writing… 15 characters", turn.ThinkingSummary, "switching to text restarts the count");
    }

    public static void IterationsVersionsAndViews()
    {
        Check.True(SwxParametric.IterationKey("F40-v3.SLDPRT") == ("F40", 3), "-v3");
        Check.True(SwxParametric.IterationKey("f40_V12.STL") == ("F40", 12), "_V12, any case");
        Check.True(SwxParametric.IterationKey("F40 v2.previous.SLDPRT") == ("F40", 2), "backup of v2");
        Check.True(SwxParametric.IterationKey("F40-v2-20261028-082158.SLDPRT") == ("F40", 2), "timestamped copy of v2");
        Check.True(SwxParametric.IterationKey("F40-v4.view-front.png") == ("F40", 4), "view of v4");
        Check.True(SwxParametric.IterationKey("Bracket.SLDPRT") == ("BRACKET", 0), "no version = v0");

        string d = TempDir();
        string P(string n) => Path.Combine(d, n);
        string[] files =
        [
            P("F40.SLDPRT"), P("F40-v1.SLDPRT"), P("F40-v1.STL"), P("F40-v1.view-iso.png"), P("F40-v2.SLDPRT"), P("F40-v3.previous.SLDPRT"),
            P("F40-v3-20261028-082158.SLDPRT"), P("F40-v4.SLDPRT"), P("F40-v4.STL"), P("F40-v4.view-front.png"), P("F40-v4.previous.SLDPRT"),
            P("F40-v5.SLDPRT"), P("F40-v2-mine.SLDPRT"), P("F40 notes v1.SLDPRT"), P("Bracket-v1.SLDPRT"), P("F40-v1-user.SLDPRT"),
        ];
        var manifest = new HashSet<string>(files.Where(f => !f.Contains("mine") && !f.Contains("user") && !f.Contains(".previous")), StringComparer.OrdinalIgnoreCase)
        {
            P("F40-v3.SLDPRT"), // deleted earlier, but its backup is ours
        };
        manifest.Add(P("F40-v4.SLDPRT"));
        var old = SwxParametric.OlderIterations(P("F40-v4.SLDPRT"), files, manifest).Select(Path.GetFileName).Order().ToArray();
        string[] expected = ["F40-v1.SLDPRT", "F40-v1.STL", "F40-v1.view-iso.png", "F40-v2.SLDPRT", "F40-v3-20261028-082158.SLDPRT", "F40-v3.previous.SLDPRT", "F40-v4.previous.SLDPRT", "F40.SLDPRT"];
        Check.True(old.SequenceEqual(expected.Order()), "v1-v3 and v4's backups go; v4's own STL and views, v5, other designs and files Mediator did not make stay: " + string.Join(", ", old));

        var close = SwxParametric.Proportions([101.6, 46.2, 26.0], [4358, 1970, 1124]);
        Check.True((bool)close["matches"]! && close["deviationPct"]!.AsArray().Count == 3, "a scaled F40 matches its real proportions: " + close.ToJsonString());
        var blob = SwxParametric.Proportions([30, 101.6, 60], [4358, 1970, 1124]);
        Check.True(!(bool)blob["matches"]! && ((string)blob["note"]!).Contains("next version", StringComparison.Ordinal), "a tall blob is flagged: " + blob.ToJsonString());

        string png = P("F40-v4.view-front.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);
        var images = SwxPartToolProvider.ViewImages(new JsonObject { ["views"] = new JsonArray(png, P("missing.png")) });
        Check.True(images.Count == 1 && images[0].MediaType == "image/png", "existing views are returned to the model as images");
        var tool = new SwxPartToolProvider().Tools.Single(t => t.Name == SwxPartToolProvider.BuildTool);
        foreach (string field in new[] { "referenceMm", "views", "keepIterations" })
            Check.True(tool.InputSchema["properties"]![field] is not null, "build tool accepts " + field);
    }
}
