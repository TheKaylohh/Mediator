using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Context;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;
using Cockpit.Core.Blender;
using Cockpit.Core.CadCode;
using Cockpit.Core.Resolve;
using Cockpit.Core.Swx;
using Cockpit.Core.Tutor;
using Cockpit.UI;

namespace Cockpit.Tests;

static class OverlayTests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception("Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    static JsonObject FourMatePlan(string output, string a, string b)
    {
        JsonObject Mate(string key, string type, string fa, string fb, double conf) => new()
        {
            ["key"] = key, ["type"] = type, ["componentA"] = "a", ["faceA"] = fa, ["componentB"] = "b", ["faceB"] = fb, ["confidence"] = conf,
        };
        return new JsonObject
        {
            ["outputPath"] = output,
            ["components"] = new JsonArray(new JsonObject { ["key"] = "a", ["partPath"] = a, ["fixed"] = true }, new JsonObject { ["key"] = "b", ["partPath"] = b }),
            ["mates"] = new JsonArray(Mate("m1", "Concentric", "c1", "c1", 0.95), Mate("m2", "Coincident", "p1", "p1", 0.40),
                Mate("m3", "Parallel", "p1", "p1", 0.7), Mate("m4", "Concentric", "c1", "c1", 0.8)),
        };
    }

    /// <summary>
    /// Gemini item 6: the ViewModel is the runtime's approval prompt. 4 mates are proposed, the user unticks one, only 3
    /// are built; then the kill switch ends a job whose call is waiting for approval, within 100 ms.
    /// </summary>
    public static async Task ApprovalSubsetAndKillSwitch(string bridgeDll)
    {
        string parts = TempDir();
        string a = Path.Combine(parts, "a.sldprt"), b = Path.Combine(parts, "b.sldprt");
        File.WriteAllText(a, "p");
        File.WriteAllText(b, "p");
        var swx = new SimulatedSwxBackend();
        await using var runtime = await RuntimeTests.Runtime(bridgeDll, new AutoApprovalPrompt(), swx);
        var vm = new CockpitOverlayViewModel(runtime, new InlineDispatcher());
        runtime.Approvals.Target = vm;
        int attention = 0;
        vm.AttentionRequested += () => attention++;

        var job = await runtime.StartJobAsync(JobSpecFactory.Assemble(parts, LaunchMode.None, Path.Combine(parts, "x.SLDASM")) with { JobId = "ui-job" });
        await Until(() => vm.Jobs.Any(j => j.JobId == "ui-job" && j.IsActive), "job row in the Jobs tab");
        Check.Equal("Jobs (1)", vm.JobsTabHeader, "tab header counts active jobs");

        await using var client = await runtime.ConnectClientAsync(job);
        await client.CallToolAsync("swx_part_cards", new JsonObject { ["paths"] = new JsonArray(a, b) }, TimeSpan.FromSeconds(30));
        Check.True(vm.PendingApproval is null, "read-only call needs no approval");

        var build = client.CallToolAsync("swx_build_assembly", FourMatePlan(Path.Combine(parts, "x.SLDASM"), a, b), TimeSpan.FromSeconds(30));
        await Until(() => vm.PendingApproval is not null, "approval card");
        var card = vm.PendingApproval!;
        Check.True(vm.IsVisible && attention == 1, "an approval shows the panel and asks for attention");
        Check.True(card.IsSelectable && card.Rows.Count == 4, "four selectable mate rows");
        Check.True(card.Rows.Single(r => r.Key == "m2").IsLowConfidence, "the 0.40 mate is flagged low-confidence");
        vm.Hide();
        Check.True(vm.IsVisible, "the panel cannot be hidden while an approval is open");

        card.Rows.Single(r => r.Key == "m2").IsChecked = false;
        Check.Equal("3 of 4 selected", card.SelectionText, "selection count updates");
        card.ApproveSelectedCommand.Execute(null);
        var result = await build.WaitAsync(TimeSpan.FromSeconds(15));

        Check.True(!result.IsError, "build ran: " + result.Text);
        Check.True(swx.Builds.Last().Mates.Select(m => m.Mate.Key).SequenceEqual(new[] { "m1", "m3", "m4" }), "only the 3 ticked mates were executed");
        Check.True(result.Text.Contains("1 mate(s) were excluded by the user", StringComparison.Ordinal), "the model is told one mate was excluded");
        Check.True(vm.PendingApproval is null && !card.ApproveSelectedCommand.CanExecute(null), "card closed and cannot be answered twice");
        var audit = await runtime.Audit.ListAsync(job.JobId);
        Check.Equal("approved_subset", audit.Last().Decision, "partial approval audited as approved_subset");

        // ── Kill switch while an approval is pending ──
        var pending = client.CallToolAsync("swx_build_assembly", FourMatePlan(Path.Combine(parts, "y.SLDASM"), a, b), TimeSpan.FromSeconds(30));
        await Until(() => vm.PendingApproval is not null, "second approval card");
        var sw = Stopwatch.StartNew();
        vm.KillAllCommand.Execute(null);
        long killMs = sw.ElapsedMilliseconds;
        var row = vm.Jobs.Single(j => j.JobId == "ui-job");
        Check.True(row.State == JobState.Killed && killMs < 100, $"job shown Killed {killMs} ms after the kill switch");
        Check.True(vm.PendingApproval is null, "pending approval removed with its job");
        Check.True(!vm.KillAllCommand.CanExecute(null) && !row.KillCommand.CanExecute(null), "nothing left to kill");
        try { Check.True((await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsError, "pending call did not run"); }
        catch (McpClientException) { }
        Check.Equal(1, swx.Builds.Count, "no second build happened");
        vm.Hide();
        Check.True(!vm.IsVisible, "panel hides once no approval is open");
    }

    sealed class FakeRuntime : ICockpitRuntime
    {
        public readonly List<JobSpec> Started = new();
        public readonly Dictionary<AiProvider, DateTimeOffset?> Cooldowns = new() { [AiProvider.ClaudeCode] = null, [AiProvider.GeminiCli] = null };
        public event Action<ToolCallEvent>? ToolCallActivity;
        public event Action<RunningJob>? JobChanged;
        public event Action<string, CockpitStreamEvent>? StreamEvent;
        public event Action<string, string>? BillingAlarm;
        public event Action<string, string>? Notice;
        public SwappableApprovalPrompt Approvals { get; } = new();
        public IReadOnlyList<RunningJob> Jobs => [];
        public Task<RunningJob> StartJobAsync(JobSpec spec, CancellationToken ct = default)
        {
            Started.Add(spec);
            throw new InvalidOperationException("claude is not on PATH (fake)");
        }
        public bool KillJob(string jobId, string reason) => false;
        public Task EndJobAsync(string jobId) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetCooldownAsync(string provider) =>
            Task.FromResult(Cooldowns[provider == "claude-code" ? AiProvider.ClaudeCode : AiProvider.GeminiCli]);
        public Task<RouteDecision> RouteAsync(TaskCategory category, AiProvider? provider = null, bool neverDowngrade = false) =>
            Task.FromResult(ProviderRouter.Route(category, provider, neverDowngrade, p => Cooldowns[p], _ => true));
        public void Raise(string jobId, CockpitStreamEvent e) => StreamEvent?.Invoke(jobId, e);
        public void Activity(ToolCallEvent e) => ToolCallActivity?.Invoke(e);
        public void Alarm() => BillingAlarm?.Invoke("j", "ANTHROPIC_API_KEY");
        public void Say(string m) => Notice?.Invoke("j", m);
        public void Touch(RunningJob j) => JobChanged?.Invoke(j);
    }

    /// <summary>Zero-token routing, context chips, folder resolution, provider pills, banners.</summary>
    public static async Task RoutingAndContext()
    {
        var rt = new FakeRuntime();
        var vm = new CockpitOverlayViewModel(rt, new InlineDispatcher());
        string folder = TempDir();
        string other = TempDir();

        Check.Equal("assemble", vm.Route("assemble the parts in this folder")!.Id, "assembly prompt → SolidWorks action");
        Check.Equal("organize", vm.Route("please sort and tidy my downloads")!.Id, "organize prompt → organize action");
        Check.Equal("claude-terminal", vm.Route("finish my app to a POC")!.Id, "code prompt → Claude Code terminal");
        Check.Equal("claude-terminal", vm.Route("what is the weather")!.Id, "anything else → Claude Code terminal");
        Check.Equal(null, vm.Route("   "), "empty prompt routes nowhere");

        vm.PromptText = "/org";
        Check.True(vm.FilteredActions.Count == 1 && vm.FilteredActions[0].Id == "organize", "/ filters the action list like Toolbox search");
        Check.True(vm.SuggestionText.Contains("Organize a folder", StringComparison.Ordinal), "suggestion shows what Enter will do");
        vm.PromptText = "";
        Check.Equal(vm.Actions.Count, vm.FilteredActions.Count, "clearing the prompt shows every action");

        var assemble = vm.Actions.Single(x => x.Id == "assemble");
        Check.True(await vm.RunActionAsync(assemble, "assemble") is null && vm.StatusText.StartsWith("Which folder?", StringComparison.Ordinal), "folder actions need a folder");

        vm.OnHotkey(new ContextSnapshot("explorer", folder, folder, [Path.Combine(folder, "x.sldprt")], false));
        Check.True(vm.IsVisible, "hotkey shows the panel");
        Check.Equal(2, vm.ContextChips.Count, "folder chip + selected file chip");
        Check.Equal(folder, vm.ResolveFolder("assemble"), "the Explorer folder is the default target");
        Check.Equal(other, vm.ResolveFolder($"assemble the parts in {other} please"), "a path in the prompt wins over the chip");
        vm.ContextChips[0].ToggleCommand.Execute(null);
        Check.Equal(folder, vm.ResolveFolder("assemble"), "with the folder chip off, the selected file's folder is used");
        vm.ContextChips[1].IsIncluded = false;
        Check.Equal(null, vm.ResolveFolder("assemble"), "no included chips → no folder");
        vm.ContextChips[0].IsIncluded = true;

        vm.UseTerminal = true;
        vm.KeepSubscription = true;
        await vm.RunActionAsync(assemble, "assemble");
        Check.Equal(LaunchMode.Interactive, rt.Started.Single().Launch, "terminal mode launches interactively");
        Check.True(rt.Started.Single().NeverDowngrade, "'keep subscription' reaches the job");
        Check.True(rt.Started.Single().Roots.Single().StartsWith(folder, StringComparison.Ordinal), "job rooted at the folder");
        Check.Equal("claude is not on PATH (fake)", vm.StatusText, "start failures surface in the status line");
        vm.KeepSubscription = false;

        vm.OnHotkey(null);
        Check.True(!vm.IsVisible, "hotkey again hides it");
        vm.OnHotkey(new ContextSnapshot("SLDWORKS", "SOLIDWORKS - housing.SLDPRT", null, [], false));
        Check.True(vm.ContextChips.Single().Kind == ContextKind.App && !vm.ContextChips.Single().IsIncluded, "foreground app chip is shown but off by default");

        rt.Activity(new ToolCallEvent("j", "c", "files_execute_batch", "tool_error", "denied"));
        rt.Raise("j", new AssistantTextEvent("s", "Reading the cards"));
        Check.True(vm.Activity.Count == 2 && vm.Activity[1].IsProblem && vm.Activity[0].Tool == "claude", "activity feed newest first, errors flagged");
        rt.Alarm();
        Check.True(vm.HasBillingBanner, "billing alarm shows the red banner");
        vm.DismissBannerCommand.Execute(null);
        Check.True(!vm.HasBillingBanner, "banner dismissable");
        rt.Say("Claude Pro is at its usage limit until 15:00; using Google AI Pro instead.");
        Check.True(vm.StatusText.StartsWith("Claude Pro is at its usage limit", StringComparison.Ordinal), "runtime notices reach the status line");
    }

    /// <summary>
    /// Gemini V5 item 7: all actions enabled; keyword and prefix routing choose the right action, tool allow-list and
    /// subscription; when Claude is cooling down the active pill switches to Google AI Pro, and back when it is not.
    /// </summary>
    public static async Task AllActionsAndFailoverPills()
    {
        var rt = new FakeRuntime();
        var vm = new CockpitOverlayViewModel(rt, new InlineDispatcher());
        Check.True(vm.Actions.Count == 7 && vm.Actions.All(a => a.IsEnabled), "every action is enabled: " + string.Join(", ", vm.Actions.Select(a => a.Id)));
        foreach (var id in new[] { "assemble", "codecad", "organize", "resolve", "blender", "tutor" })
            Check.True(vm.Actions.Any(a => a.Id == id), $"{id} action present");

        (string Prompt, string Action, string Tool, AiProvider Provider)[] cases =
        [
            ("assemble these SolidWorks parts", "assemble", SwxToolProvider.BuildTool, AiProvider.ClaudeCode),
            ("design a brake disc and wheel hub for a Jetta", "codecad", CodeCadToolProvider.GenerateTool, AiProvider.ClaudeCode),
            ("/cad assemble the housing", "assemble", SwxToolProvider.PartCardsTool, AiProvider.ClaudeCode),
            ("/cad", "assemble", SwxToolProvider.BuildTool, AiProvider.ClaudeCode),
            ("edit my clips into a rough cut", "resolve", ResolveToolProvider.ExportTool, AiProvider.GeminiCli),
            ("/resolve teaser for the drive", "resolve", ResolveToolProvider.ProbeTool, AiProvider.GeminiCli),
            ("blender scene of a car driving at night", "blender", BlenderToolProvider.BuildTool, AiProvider.ClaudeCode),
            ("explain chapter 3 eigenvalues", "tutor", TutorToolProvider.VerifyTool, AiProvider.GeminiCli),
            ("/tutor determinants", "tutor", TutorToolProvider.ExtractTool, AiProvider.GeminiCli),
            ("tidy my downloads folder", "organize", "files_execute_batch", AiProvider.GeminiCli),
        ];
        foreach (var (prompt, action, tool, provider) in cases)
        {
            vm.PromptText = prompt;
            await vm.RefreshRouteAsync();
            var tile = vm.SuggestedAction!;
            Check.Equal(action, tile.Id, $"'{prompt}' → action");
            Check.True(tile.Tools.Contains(tool), $"'{prompt}' → tool allow-list includes {tool}");
            var spec = CockpitOverlayViewModel.SpecFor(tile.Id, Path.GetTempPath(), CockpitOverlayViewModel.PromptBody(prompt), LaunchMode.Headless);
            Check.True(spec.Tools.SequenceEqual(tile.Tools) && spec.Category == tile.Category, $"'{prompt}' → job recipe uses the tile's tools and category");
            Check.Equal(ProviderRouter.DisplayName(provider), tile.Provider, $"'{prompt}' → subscription");
            Check.True(vm.Providers.Single(p => p.IsActive).Id == ProviderRouter.Id(provider), $"'{prompt}' → active pill");
        }
        Check.Equal("teaser for the drive", CockpitOverlayViewModel.PromptBody("/resolve teaser for the drive"), "prefix stripped from the job prompt");

        // Failover: Claude at its limit → the CAD action shows Google AI Pro, the pill moves, the reason is shown.
        rt.Cooldowns[AiProvider.ClaudeCode] = DateTimeOffset.UtcNow.AddHours(2);
        vm.PromptText = "design a coil spring";
        await vm.RefreshProvidersAsync();
        Check.Equal(ProviderRouter.DisplayName(AiProvider.GeminiCli), vm.SuggestedAction!.Provider, "cad falls over to Gemini while Claude is cooling down");
        Check.True(vm.Providers.Single(p => p.IsActive).Id == "gemini-cli" && vm.Providers.Single(p => p.Id == "claude-code").IsCoolingDown, "active pill switched; Claude pill shows its cooldown");
        Check.True(vm.RouteText.Contains("using Google AI Pro", StringComparison.Ordinal), "the switch is explained: " + vm.RouteText);

        vm.KeepSubscription = true;
        await vm.RefreshRouteAsync();
        Check.True(vm.RouteText.Contains("never switch", StringComparison.Ordinal) && !vm.Providers.Any(p => p.IsActive), "keep-subscription: no pill, the refusal is shown");
        vm.KeepSubscription = false;

        rt.Cooldowns[AiProvider.ClaudeCode] = null;
        await vm.RefreshProvidersAsync();
        Check.True(vm.Providers.Single(p => p.IsActive).Id == "claude-code" && vm.RouteText.Length == 0, "back on Claude once the limit resets");
    }

    /// <summary>The WPF view cannot be compiled offline, so its XAML is checked against the ViewModel types instead.</summary>
    public static Task XamlBindings()
    {
        string wpf = Path.Combine(XamlChecker.RepoRoot(), "src", "Cockpit.Wpf");
        var report = XamlChecker.Check(wpf, typeof(CockpitOverlayViewModel).Assembly);
        Check.True(report.Errors.Count == 0, "XAML problems:\n  " + string.Join("\n  ", report.Errors));
        Check.True(report.BindingsChecked >= 60, $"bindings actually checked ({report.BindingsChecked})");
        Check.True(report.ResourcesChecked >= 40, $"resource references checked ({report.ResourcesChecked})");
        return Task.CompletedTask;
    }
}
