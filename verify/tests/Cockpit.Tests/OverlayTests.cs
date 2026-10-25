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
        /// <summary>When set, StartJobAsync succeeds with this job (V7 tests); otherwise it fails like a missing CLI.</summary>
        public RunningJob? StartWith;
        public Task<RunningJob> StartJobAsync(JobSpec spec, CancellationToken ct = default)
        {
            Started.Add(spec);
            if (StartWith is { } job) return Task.FromResult(job);
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

        public readonly Cockpit.Core.Attachments.AttachmentStore Store = new(Path.Combine(Path.GetTempPath(), "cockpit-fake-attach", Guid.NewGuid().ToString("N")[..8]));
        public readonly List<(string JobId, Cockpit.Core.Attachments.Attachment A)> AttachedToJobs = new();
        public HashSet<string> RunningJobIds { get; } = new(StringComparer.Ordinal);
        public Cockpit.Core.Attachments.Attachment StageAttachment(string path) => Store.AddPath(path);
        public Cockpit.Core.Attachments.Attachment StagePastedImage(byte[] png) => Store.AddPng(png);
        public void DiscardAttachment(Cockpit.Core.Attachments.Attachment attachment) => Store.Discard(attachment);
        public Task<bool> AttachToJobAsync(string jobId, Cockpit.Core.Attachments.Attachment attachment)
        {
            if (!RunningJobIds.Contains(jobId)) return Task.FromResult(false);
            AttachedToJobs.Add((jobId, attachment));
            return Task.FromResult(true);
        }
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
        Check.True(report.BindingsChecked >= 180, $"bindings actually checked ({report.BindingsChecked})");
        Check.True(report.ResourcesChecked >= 250, $"resource references checked ({report.ResourcesChecked})");
        Check.True(report.ThemeKeys >= 40, $"theme keys compared across the three themes ({report.ThemeKeys})");
        Check.True(report.Files >= 6, $"XAML files checked, including Themes/ and Controls/ ({report.Files})");
        Check.True(report.PackUrisChecked >= 4, $"pack URIs checked: one per theme plus App.xaml ({report.PackUrisChecked})");
        Console.WriteLine($"      xaml: {report.Files} files, {report.BindingsChecked} bindings, {report.ResourcesChecked} resource references, {report.ThemeKeys} keys in each of the 3 themes, {report.PackUrisChecked} pack URIs");
        return Task.CompletedTask;
    }

    // ── V7 ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Theme cycle, ThemeChanged, persistence in settings.json and reload; malformed settings; the hotkey parser.</summary>
    public static Task ThemeSwitchingAndPersistence()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "settings.json");
        var vm = new CockpitOverlayViewModel(new FakeRuntime(), new InlineDispatcher(), new JsonSettingsStore(path));
        Check.Equal(CockpitTheme.HoloHud, vm.CurrentTheme, "Holo HUD is the default");
        Check.Equal("Holo HUD", vm.ThemeDisplayName, "display name");
        var changes = new List<CockpitTheme>();
        var props = new List<string?>();
        vm.ThemeChanged += changes.Add;
        vm.PropertyChanged += (_, e) => props.Add(e.PropertyName);

        for (int i = 0; i < 4; i++) vm.CycleThemeCommand.Execute(null);
        Check.True(changes.SequenceEqual([CockpitTheme.ProDark, CockpitTheme.CleanLight, CockpitTheme.HoloHud, CockpitTheme.ProDark]), "cycle Holo HUD → Pro Dark → Clean Light → Holo HUD → Pro Dark");
        Check.True(props.Count(p => p == nameof(CockpitOverlayViewModel.CurrentTheme)) == 4 && props.Contains(nameof(CockpitOverlayViewModel.ThemeDisplayName)), "bindings notified");
        Check.Equal("ProDark", (string?)JsonNode.Parse(File.ReadAllText(path))!["theme"], "saved to settings.json");

        vm.CurrentTheme = CockpitTheme.ProDark;
        vm.CurrentTheme = (CockpitTheme)99;
        Check.Equal(4, changes.Count, "same or undefined theme: no change, no event");

        var reloaded = new CockpitOverlayViewModel(new FakeRuntime(), new InlineDispatcher(), new JsonSettingsStore(path));
        Check.Equal(CockpitTheme.ProDark, reloaded.CurrentTheme, "theme survives a restart");
        reloaded.CurrentTheme = CockpitTheme.CleanLight;
        Check.Equal(CockpitTheme.CleanLight, new JsonSettingsStore(path).Load().Theme, "Clean Light persisted");
        Check.True(!Directory.EnumerateFiles(dir, "*.tmp").Any(), "atomic save leaves no temp file");

        // Bad values fall back to defaults; a malformed file is kept as .bad.
        File.WriteAllText(path, """{"theme":"Neon","hotkey":"Shift+A","firstRunCompleted":true}""");
        var odd = new JsonSettingsStore(path).Load();
        Check.True(odd.Theme == CockpitTheme.HoloHud && odd.Hotkey == "Ctrl+Shift+Space" && odd.FirstRunCompleted, "unknown theme and unsafe hotkey ignored, the rest kept");
        File.WriteAllText(path, "{ broken");
        var store = new JsonSettingsStore(path);
        var broken = store.Load();
        Check.True(broken == new CockpitSettings() && store.LoadError is not null, "malformed settings: defaults and a reported error");
        Check.Equal("{ broken", File.ReadAllText(path + ".bad"), "malformed file kept as settings.json.bad");

        // Hotkeys.
        Check.True(HotkeyGesture.TryParse("ctrl+shift+space", out var g, out _) && g.ToString() == "Ctrl+Shift+Space" && g.VirtualKey == 0x20
            && g.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Shift), "default gesture round-trips");
        Check.True(HotkeyGesture.TryParse("Win+Alt+F13", out var f, out _) && f.VirtualKey == 0x7C, "F13 → VK 0x7C");
        Check.True(HotkeyGesture.TryParse("Ctrl+Alt+J", out var j, out _) && j.VirtualKey == 'J', "letter");
        foreach (var (bad, why) in new[] { ("Shift+A", "Ctrl, Alt or Win"), ("Alt+Space", "system menu"), ("Ctrl+Ctrl+A", "twice"), ("Ctrl+Hyper+A", "not a modifier"), ("Ctrl+Esc", "not supported"), ("Ctrl+Enter", "second modifier"), ("A", "modifier") })
            Check.True(!HotkeyGesture.TryParse(bad, out _, out var err) && err!.Contains(why, StringComparison.Ordinal), $"'{bad}' refused: {err}");
        foreach (var preset in HotkeyGesture.Presets) Check.True(HotkeyGesture.TryParse(preset, out _, out _), $"preset {preset} is valid");
        return Task.CompletedTask;
    }

    static ApprovalRequest Request(string jobId, string tool = "files_execute_batch") =>
        new(jobId, "call-" + Guid.NewGuid().ToString("N")[..6], tool, ToolRisk.Write, "Move 3 files", System.Text.Json.JsonDocument.Parse("{}").RootElement, null);

    /// <summary>Idle → Active → AwaitingApproval → Active → Error, the fault sources, acknowledging, and a new job clearing it.</summary>
    public static async Task ReactorStateTransitions(string bridgeDll)
    {
        await using var rt = await OperationTests.Runtime(bridgeDll);
        string work = TempDir();
        var job1 = await rt.StartJobAsync(OperationTests.Spec("reactor-1", work) with { Tools = [], ToolModes = null });
        var job2 = await rt.StartJobAsync(OperationTests.Spec("reactor-2", work) with { Tools = [], ToolModes = null });

        var fake = new FakeRuntime();
        var vm = new CockpitOverlayViewModel(fake, new InlineDispatcher());
        var seq = new List<ReactorState>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CockpitOverlayViewModel.ReactorState)) seq.Add(vm.ReactorState); };
        Check.Equal(ReactorState.Idle, vm.ReactorState, "starts idle");
        Check.Equal("STANDBY", vm.ReactorCaption, "idle caption");

        fake.Touch(job1);
        Check.Equal(ReactorState.Active, vm.ReactorState, "a running job → Active");
        Check.Equal("ACTIVE · 1 JOB", vm.ReactorCaption, "active caption");

        var answer = vm.AskAsync(Request("reactor-1"), CancellationToken.None);
        Check.Equal(ReactorState.AwaitingApproval, vm.ReactorState, "pending approval → AwaitingApproval (wins over Active)");
        Check.Equal("AWAITING APPROVAL", vm.ReactorCaption, "approval caption");
        vm.PendingApproval!.ApproveAllCommand.Execute(null);
        Check.True((await answer).Approved, "approved");
        Check.Equal(ReactorState.Active, vm.ReactorState, "answered → back to Active");

        job1.TryFinish(JobState.Failed, "the CLI crashed");
        fake.Touch(job1);
        Check.Equal(ReactorState.Error, vm.ReactorState, "a failed job → Error");
        Check.True(vm.ReactorDetail.Contains("the CLI crashed", StringComparison.Ordinal) && vm.AcknowledgeFaultCommand.CanExecute(null), "fault explained and acknowledgeable");
        vm.AcknowledgeFaultCommand.Execute(null);
        Check.Equal(ReactorState.Idle, vm.ReactorState, "clicking the orb clears the fault");
        Check.True(!vm.AcknowledgeFaultCommand.CanExecute(null), "nothing left to acknowledge");

        fake.Raise("reactor-1", new RunResultEvent(null, "error", true, "limit", null, null, null, null, new RateLimitInfo("limit", DateTimeOffset.UtcNow.AddHours(2))));
        Check.Equal(ReactorState.Error, vm.ReactorState, "a usage limit → Error (QuotaExceeded)");

        fake.Touch(job2);
        Check.Equal(ReactorState.Active, vm.ReactorState, "a new job → Active");
        Check.True(vm.Fault is null, "and it clears the last fault");
        job2.TryFinish(JobState.Ended, "done");
        fake.Touch(job2);
        Check.Equal(ReactorState.Idle, vm.ReactorState, "job ended normally → Idle");

        job2.TryFinish(JobState.Killed, "kill switch");
        fake.Alarm();
        Check.Equal(ReactorState.Error, vm.ReactorState, "billing alarm → Error");

        Check.True(seq.SequenceEqual([ReactorState.Active, ReactorState.AwaitingApproval, ReactorState.Active, ReactorState.Error, ReactorState.Idle,
            ReactorState.Error, ReactorState.Active, ReactorState.Idle, ReactorState.Error]), "transition sequence: " + string.Join(" → ", seq));
    }

    /// <summary>The paths the view hands over on drop, Ctrl+V or the Attach button, and what the ViewModel does with them.</summary>
    public static async Task DragDropAndClipboardAttachments(string bridgeDll)
    {
        await using var rt = await OperationTests.Runtime(bridgeDll);
        var fake = new FakeRuntime();
        var vm = new CockpitOverlayViewModel(fake, new InlineDispatcher());
        string dir = TempDir();
        string a = Path.Combine(dir, "spec.txt");
        File.WriteAllText(a, "spec");
        string folder = Directory.CreateDirectory(Path.Combine(dir, "refs")).FullName;

        int added = await vm.AttachPathsAsync([a, folder, Path.Combine(dir, "missing.txt")]);
        Check.Equal(2, added, "file and folder attached");
        Check.True(vm.StatusText.Contains("not attached", StringComparison.Ordinal) && vm.StatusText.Contains("does not exist", StringComparison.Ordinal), "refusal reported: " + vm.StatusText);
        var chips = vm.AttachmentChips.ToList();
        Check.True(chips.All(c => c.IsRemovable && c.RemoveCommand is not null && c.Kind == ContextKind.Attachment), "removable attachment chips");
        Check.Equal(0, await vm.AttachPathsAsync([a]), "the same file twice is ignored");
        if (OperatingSystem.IsLinux())
        {
            string link = Path.Combine(dir, "link.txt");
            File.CreateSymbolicLink(link, a);
            Check.Equal(0, await vm.AttachPathsAsync([link]), "a link is refused");
            Check.True(vm.StatusText.Contains("link", StringComparison.Ordinal), "and the reason shown");
        }

        Check.True(await vm.PasteImageAsync(AttachmentTests.TinyPng), "pasted screenshot staged");
        var image = vm.AttachmentChips.Last();
        Check.True(image.Kind == ContextKind.Image && image.Label.StartsWith("pasted-", StringComparison.Ordinal) && image.Attachment!.MediaType == "image/png", "image chip");
        Check.True(!await vm.PasteImageAsync([1, 2, 3]) && vm.StatusText.StartsWith("Image not attached", StringComparison.Ordinal), "non-PNG bytes refused");

        // × on a chip nobody received deletes the snapshot; the user's file stays.
        var fileChip = chips.First(c => c.Attachment!.Kind == Cockpit.Core.Attachments.AttachmentKind.File);
        string snapshot = fileChip.Attachment!.GrantedRoot;
        fileChip.RemoveCommand!.Execute(null);
        Check.True(!Directory.Exists(snapshot) && File.Exists(a), "snapshot deleted, original untouched");
        Check.True(!vm.ContextChips.Contains(fileChip), "chip removed");

        // Exactly one running job: the attachment is also given to it (read-only).
        var job = await rt.StartJobAsync(OperationTests.Spec("drop-1", dir) with { Tools = [], ToolModes = null });
        fake.Touch(job);
        fake.RunningJobIds.Add("drop-1");
        Check.Equal(1, await vm.AttachPathsAsync([a]), "re-attached while a job runs");
        var shared = vm.AttachmentChips.Last();
        Check.True(shared.SharedWith == job.Spec.Title && fake.AttachedToJobs.Single().JobId == "drop-1", "shared with the running job");
        shared.RemoveCommand!.Execute(null);
        Check.True(Directory.Exists(shared.Attachment!.GrantedRoot), "a snapshot a job received is not deleted by ×");
        fake.RunningJobIds.Clear(); // the runtime refuses (a headless job started without attachments)
        File.WriteAllText(Path.Combine(dir, "late.txt"), "late");
        Check.Equal(1, await vm.AttachPathsAsync([Path.Combine(dir, "late.txt")]), "still attached for the next job");
        Check.True(vm.AttachmentChips.Last().SharedWith is null && vm.StatusText.Contains("cannot take new attachments", StringComparison.Ordinal), "and the user is told why: " + vm.StatusText);
        vm.AttachmentChips.Last().RemoveCommand!.Execute(null);

        // The next job gets the included attachments; an excluded chip stays behind.
        vm.AttachmentChips.First().IsIncluded = false;
        var tile = vm.Actions.First(t => t.Id == "claude-terminal");
        Check.True(await vm.RunActionAsync(tile, "summarize these") is null, "start fails (fake CLI missing)");
        Check.Equal(1, fake.Started.Last().Attachments.Count, "only included attachments go to the job");
        Check.Equal(2, vm.AttachmentChips.Count(), "chips kept when the start failed");
        fake.StartWith = job;
        Check.True(await vm.RunActionAsync(tile, "summarize these") is not null, "start succeeds");
        Check.Equal(1, vm.AttachmentChips.Count(), "the attachment the job took is consumed; the excluded one stays");

        // Limit.
        for (int i = 0; i < CockpitOverlayViewModel.MaxAttachments + 2; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        await vm.AttachPathsAsync(Enumerable.Range(0, CockpitOverlayViewModel.MaxAttachments + 2).Select(i => Path.Combine(dir, $"f{i}.txt")));
        Check.Equal(CockpitOverlayViewModel.MaxAttachments, vm.AttachmentChips.Count(), "at most 20 attachments");

        // Drop overlay flag, Attach button, pin and dialogs.
        vm.IsDragOver = true;
        Check.True(vm.IsDragOver, "drop overlay flag");
        int browse = 0;
        vm.BrowseForAttachmentsRequested += () => browse++;
        vm.AttachCommand.Execute(null);
        Check.Equal(1, browse, "[ + Attach ] asks the view for a file picker");
        vm.Show();
        vm.IsPinned = true;
        vm.OnDeactivated();
        Check.True(vm.IsVisible, "pinned: clicking Explorer to drag a file does not hide the panel");
        vm.IsPinned = false;
        using (vm.BeginModal()) vm.OnDeactivated();
        Check.True(vm.IsVisible, "our own file picker does not hide the panel");
        vm.OnDeactivated();
        Check.True(!vm.IsVisible, "unpinned: deactivation hides");
    }

    sealed class FakeProbe : Cockpit.Core.Connectors.IConnectorProbe
    {
        public Dictionary<string, Cockpit.Core.Connectors.ToolProbe> Tools = new();
        public Dictionary<string, Cockpit.Core.Connectors.CredentialState> Creds = new();
        public Task<Cockpit.Core.Connectors.ToolProbe> ProbeToolAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Tools.TryGetValue(id, out var t) ? t : new Cockpit.Core.Connectors.ToolProbe(id, id, null, null, "not found on PATH"));
        public Cockpit.Core.Connectors.CredentialState Credentials(string cliId) => Creds.GetValueOrDefault(cliId);
        public Cockpit.Core.Connectors.AppStatus SolidWorks() => new("solidworks", "SolidWorks", true, "running (PID 4242); the worker attaches to it");
        public Cockpit.Core.Connectors.AppStatus Resolve() => new("resolve", "DaVinci Resolve", true, "not running; Studio");
        public Cockpit.Core.Connectors.AppStatus Blender() => new("blender", "Blender", false, "not found");
        public (ProcessStartInfo? StartInfo, string? Problem) SignIn(string cliId) =>
            Tools.ContainsKey(cliId) ? (new ProcessStartInfo("terminal", [cliId]), null) : (null, $"{cliId} is not installed.");
    }

    sealed class RecordingLauncher : IProcessLauncher
    {
        public readonly List<ProcessStartInfo> Started = new();
        public void Start(ProcessStartInfo psi) => Started.Add(psi);
    }

    sealed class FakeStartup : IStartupRegistration
    {
        public bool IsSupported => true;
        public bool IsEnabled { get; private set; }
        public bool Fail;
        public void SetEnabled(bool enabled)
        {
            if (Fail) throw new UnauthorizedAccessException("registry access denied (test)");
            IsEnabled = enabled;
        }
    }

    /// <summary>Connectors tab statuses and actions, + Add Custom MCP through the form, and the first-run card.</summary>
    public static async Task ConnectorsAndOnboarding(string testsDll)
    {
        var probe = new FakeProbe();
        probe.Tools["claude"] = new("claude", "Claude Code CLI", "/usr/bin/claude", "2.1.309", null);
        probe.Tools["python"] = new("python", "Python 3", "/usr/bin/python3", "3.13.17", null);
        probe.Creds["claude"] = Cockpit.Core.Connectors.CredentialState.FilePresent;
        var fake = new FakeRuntime();
        fake.Cooldowns[AiProvider.ClaudeCode] = DateTimeOffset.UtcNow.AddHours(2);
        string data = TempDir();
        var tools = new ToolRegistry();
        var mcp = new Cockpit.Core.Connectors.CustomMcpService(Path.Combine(data, "cockpit.mcp.json"), tools);
        var launcher = new RecordingLauncher();
        var c = new ConnectorsViewModel(probe, fake, mcp, new InlineDispatcher(), launcher);

        await c.RefreshAsync();
        var claude = c.Clis.Single(x => x.Id == "claude");
        var gemini = c.Clis.Single(x => x.Id == "gemini");
        Check.Equal("v2.1.309", claude.VersionText, "claude version");
        Check.True(claude.IsSignedInLikely && claude.AuthText.StartsWith("signed in (credential file found", StringComparison.Ordinal), "sign-in shown honestly: " + claude.AuthText);
        Check.True(claude.HasCooldown && claude.CooldownText.StartsWith("usage limit until", StringComparison.Ordinal) && claude.NeedsAttention, "cooldown shown");
        Check.True(!gemini.IsInstalled && gemini.VersionText == "not found on PATH" && gemini.AuthText.Contains("npm install -g @google/gemini-cli", StringComparison.Ordinal), "missing CLI with install hint");
        Check.True(!gemini.SignInCommand.CanExecute(null) && claude.SignInCommand.CanExecute(null), "Sign In only for installed CLIs");
        Check.True(c.Apps.Single(x => x.Id == "solidworks") is { IsAvailable: true } sw && sw.Detail.Contains("PID 4242", StringComparison.Ordinal), "SolidWorks PID");
        Check.True(!c.Apps.Single(x => x.Id == "blender").IsAvailable, "Blender missing");

        claude.SignInCommand.Execute(null);
        Check.True(launcher.Started.Single().ArgumentList.Contains("claude") && c.Status.Contains("/login", StringComparison.Ordinal), "Sign In opens a terminal and explains /login");

        // + Add Custom MCP, through the form.
        Check.True(!c.AddServerCommand.CanExecute(null), "Add disabled until name and command are filled");
        c.ToggleAddCommand.Execute(null);
        Check.True(c.IsAddOpen, "form opens");
        c.NewName = "Bad Name";
        c.NewCommand = "x";
        Check.True(!await c.AddServerAsync() && c.AddStatus.Contains("Name", StringComparison.Ordinal), "invalid name explained: " + c.AddStatus);
        c.NewName = "fake";
        c.NewCommand = ComponentLocator.DotnetHost();
        c.NewArgs = $"\"{testsDll}\" --fake-mcp";
        Check.True(ConnectorsViewModel.SplitArguments(c.NewArgs).SequenceEqual([testsDll, "--fake-mcp"]), "quoted arguments split");
        Check.True(ConnectorsViewModel.SplitArguments("a \"b c\" \"\" d").SequenceEqual(["a", "b c", "", "d"]), "quotes group, empty quoted argument kept");
        Check.True(await c.AddServerAsync(), "added after a health ping: " + c.AddStatus);
        Check.True(!c.IsAddOpen && c.NewName.Length == 0 && c.AddStatus.Contains("approval", StringComparison.Ordinal), "form closes and says calls need approval");
        var row = c.McpServers.Single();
        Check.True(row.IsHealthy && row.IsEnabled && row.ToolsText.Contains("ext_fake_shout", StringComparison.Ordinal) && c.HasMcpServers, "server row: " + row.HealthText);
        Check.True(tools.TryGet("ext_fake_shout", out _, out _), "tools registered");
        row.IsEnabled = false;
        Check.True(!row.IsEnabled && !tools.TryGet("ext_fake_shout", out _, out _), "unticking unregisters");
        row.IsEnabled = true;
        row.RemoveCommand.Execute(null);
        Check.True(c.McpServers.Count == 0 && !tools.TryGet("ext_fake_shout", out _, out _), "removed from the list and the registry");

        // The overlay refreshes the tab when it is selected.
        var fresh = new ConnectorsViewModel(probe, fake, null, new InlineDispatcher(), launcher);
        Check.Equal("checking…", fresh.Clis[0].VersionText, "not probed yet");
        var vm = new CockpitOverlayViewModel(fake, new InlineDispatcher(), new MemorySettingsStore()) { Connectors = fresh };
        vm.SelectedTab = CockpitOverlayViewModel.ConnectorsTab;
        await Until(() => fresh.Clis[0].VersionText == "v2.1.309", "Connectors refreshed on open");
        Check.True(!fresh.ToggleAddCommand.CanExecute(null), "no custom MCP form without a registry");

        // First-run card.
        var settings = new MemorySettingsStore();
        var overlay = new CockpitOverlayViewModel(fake, new InlineDispatcher(), settings);
        var startup = new FakeStartup();
        var card = new OnboardingViewModel(probe, startup, overlay);
        Check.True(await card.StartAsync(), "opens on the first launch");
        Check.True(card.AnyCliFound && card.Prerequisites.Single(p => p.Id == "claude").Status.Contains("signed in", StringComparison.Ordinal), "claude found and signed in");
        Check.True(card.Prerequisites.Single(p => p.Id == "gemini").IsMissing && card.Prerequisites.Single(p => p.Id == "git").IsMissing, "gemini and git missing");
        card.HotkeyText = "Shift+A";
        Check.True(card.HotkeyError.Length > 0 && !card.FinishCommand.CanExecute(null), "unsafe hotkey blocks Finish");
        card.UsePresetCommand.Execute("Ctrl+Alt+J");
        Check.True(card.HotkeyError.Length == 0 && card.FinishCommand.CanExecute(null), "preset fixes it");
        card.SelectThemeCommand.Execute(card.ThemeOptions.Single(o => o.Theme == CockpitTheme.CleanLight));
        Check.True(overlay.CurrentTheme == CockpitTheme.CleanLight && card.ThemeOptions.Single(o => o.IsSelected).Theme == CockpitTheme.CleanLight, "theme previewed live");
        card.RunOnStartup = true;
        startup.Fail = true;
        Check.True(!card.Finish() && card.IsOpen && card.Summary.Contains("could not be changed", StringComparison.Ordinal), "a registry failure keeps the card open with the reason");
        startup.Fail = false;
        HotkeyGesture? changed = null;
        card.HotkeyChanged += g => changed = g;
        Check.True(card.Finish() && !card.IsOpen, "Finish closes the card");
        Check.True(startup.IsEnabled, "Run on startup enabled");
        Check.True(settings.Current is { FirstRunCompleted: true, Hotkey: "Ctrl+Alt+J", Theme: CockpitTheme.CleanLight, RunOnStartup: true }, "settings saved");
        Check.Equal("Ctrl+Alt+J", changed?.ToString(), "hotkey re-registered");

        var again = new OnboardingViewModel(probe, startup, overlay);
        Check.True(!await again.StartAsync(), "not shown again once set up");
        probe.Tools.Remove("claude");
        var noCli = new OnboardingViewModel(probe, startup, overlay);
        Check.True(await noCli.StartAsync() && !noCli.AnyCliFound, "shown again when neither CLI is found");
        noCli.SkipCommand.Execute(null);
        Check.True(!noCli.IsOpen, "Later closes it");
    }

    /// <summary>The checker must catch each kind of mistake it claims to (run on a mutated copy of the real XAML, code and project file).</summary>
    public static Task XamlCheckerCatchesMistakes()
    {
        string src = Path.Combine(XamlChecker.RepoRoot(), "src", "Cockpit.Wpf");
        string copy = TempDir();
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal)) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string to = Path.Combine(copy, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        var clean = XamlChecker.Check(copy, typeof(CockpitOverlayViewModel).Assembly);
        Check.True(clean.Errors.Count == 0, "the unmodified copy is clean:\n  " + string.Join("\n  ", clean.Errors));

        void Mutate(string rel, string from, string to)
        {
            string p = Path.Combine(copy, rel);
            string text = File.ReadAllText(p);
            int i = text.IndexOf(from, StringComparison.Ordinal);
            Check.True(i >= 0, $"mutation anchor '{from}' found in {rel}");
            File.WriteAllText(p, text[..i] + to + text[(i + from.Length)..]);
        }
        Mutate("Themes/CleanLight.xaml", "<SolidColorBrush x:Key=\"OrbErrorBrush\"", "<SolidColorBrush x:Key=\"OrbErrorBrushRenamed\"");
        Mutate("MainWindow.xaml", "Foreground=\"{DynamicResource WarningBrush}\" Text=\"{Binding RouteText}\"", "Foreground=\"{StaticResource WarningBrush}\" Text=\"{Binding RouteText}\"");
        Mutate("MainWindow.xaml", "<TextBlock Text=\"AI COCKPIT\"", "<TextBlock Foreground=\"#FF00FF00\" Text=\"AI COCKPIT\"");
        Mutate("MainWindow.xaml", "<TextBlock Text=\"{Binding ReactorCaption}\"", "<TextBlock Style=\"{StaticResource TechLabel}\" Text=\"{Binding ReactorCaption}\"");
        Mutate("MainWindow.xaml", "State=\"{Binding ReactorState}\"", "State=\"{Binding ReactorStatus}\"");
        Mutate("Controls/HudOrbControl.xaml", "Fill=\"{Binding RingBrush, ElementName=Root}\"", "Fill=\"{Binding RingColour, ElementName=Root}\"");
        Mutate("App.xaml", "<Setter Property=\"Foreground\" Value=\"{DynamicResource MutedBrush}\" />\n        <Setter Property=\"FontSize\" Value=\"11.5\" />",
            "<Setter Property=\"Foreground\" Value=\"{DynamicResource MutedBrsh}\" />\n        <Setter Property=\"FontSize\" Value=\"11.5\" />");

        // V8 pack-URI rules
        Mutate("ThemeManager.cs", "new(ThemeSources.PackUri(theme), UriKind.Absolute)", "new($\"/AiCockpit;component/Themes/{theme}.xaml\", UriKind.Relative)");
        Mutate("App.xaml", "pack://application:,,,/AiCockpit;component/Themes/HoloHud.xaml", "pack://application:,,,/Cockpit.Wpf;component/Themes/HoloHud.xaml");
        Mutate("Themes/ProDark.xaml", "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"", "xmlns:sys=\"clr-namespace:System\"");
        Mutate("Cockpit.Wpf.csproj", "DestinationFolder=\"$(PublishDir)Themes\"", "DestinationFolder=\"$(PublishDir)\"");

        var report = XamlChecker.Check(copy, typeof(CockpitOverlayViewModel).Assembly);
        string all = string.Join("\n", report.Errors);
        foreach (var (needle, what) in new[]
        {
            ("Themes/CleanLight.xaml does not define 'OrbErrorBrush'", "a theme missing a key"),
            ("DynamicResource 'OrbErrorBrush' is not defined by theme CleanLight", "a DynamicResource one theme lacks"),
            ("StaticResource 'WarningBrush' is a theme key", "StaticResource to a theme key"),
            ("colour literal Foreground=\"#FF00FF00\"", "a hard-coded colour"),
            ("TextBlock.Style is set both as an attribute and as a property element", "a property set twice"),
            ("'ReactorStatus' is not a public property of CockpitOverlayViewModel", "a binding that does not resolve"),
            ("'RingColour' is not a dependency property", "an ElementName binding to a missing dependency property"),
            ("DynamicResource 'MutedBrsh' is not defined by the themes", "a misspelt key in App.xaml"),
            ("ThemeManager does not build its Uri from ThemeSources.PackUri(theme) with UriKind.Absolute", "a theme URI that is not absolute"),
            ("code builds a relative component URI", "a relative ;component/ URI"),
            ("names assembly 'Cockpit.Wpf', but the overlay is 'AiCockpit'", "a pack URI to the wrong assembly"),
            ("Themes/ProDark.xaml: xmlns clr-namespace:System has no ;assembly=", "a theme the loose fallback could not load"),
            ("does not copy Themes\\*.xaml to the build and publish output", "a missing loose-theme copy"),
        })
            Check.True(all.Contains(needle, StringComparison.Ordinal), $"checker catches {what}; errors were:\n{all}");
        return Task.CompletedTask;
    }

    // ── V9: hotkeys ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A RegisterHotKey stand-in: some combinations are owned by "another program".</summary>
    sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public readonly HashSet<(uint, uint)> Taken = new();
        public (uint Mods, uint Vk)? Registered;
        public int Registrations;
        public bool TryRegister(uint modifiers, uint virtualKey, out int win32Error)
        {
            Check.True(Registered is null, "the old hotkey is unregistered before a new one is registered");
            if (Taken.Contains((modifiers, virtualKey))) { win32Error = HotkeyBinding.ErrorHotkeyAlreadyRegistered; return false; }
            Registered = (modifiers, virtualKey);
            Registrations++;
            win32Error = 0;
            return true;
        }
        public void Unregister() => Registered = null;
    }

    /// <summary>HotkeyParser: valid and invalid strings, normalisation, Win32 codes.</summary>
    public static Task HotkeyParsing()
    {
        foreach (var (text, mods, vk, canonical) in new (string, uint, uint, string)[]
        {
            ("Ctrl+Shift+Space", 0x2 | 0x4, 0x20, "Ctrl+Shift+Space"),
            ("control + shift + space", 0x2 | 0x4, 0x20, "Ctrl+Shift+Space"),
            ("Shift+Ctrl+Space", 0x2 | 0x4, 0x20, "Ctrl+Shift+Space"),
            ("Alt+Space".Replace("Alt", "Ctrl+Alt", StringComparison.Ordinal), 0x2 | 0x1, 0x20, "Ctrl+Alt+Space"),
            ("ctrl+alt+c", 0x2 | 0x1, 'C', "Ctrl+Alt+C"),
            ("Win+Shift+7", 0x8 | 0x4, '7', "Shift+Win+7"),
            ("Ctrl+Shift+f12", 0x2 | 0x4, 0x7B, "Ctrl+Shift+F12"),
            ("Alt+F1", 0x1, 0x70, "Alt+F1"),
            ("Ctrl+Shift+Tab", 0x2 | 0x4, 0x09, "Ctrl+Shift+Tab"),
            ("Ctrl+Shift+Return", 0x2 | 0x4, 0x0D, "Ctrl+Shift+Enter"),
            ("Ctrl+~", 0x2, 0xC0, "Ctrl+OemTilde"),
            ("Ctrl+Shift+`", 0x2 | 0x4, 0xC0, "Ctrl+Shift+OemTilde"),
            ("Win+OemTilde", 0x8, 0xC0, "Win+OemTilde"),
        })
        {
            Check.True(HotkeyParser.TryParse(text, out uint m, out uint v, out string norm), $"'{text}' parses");
            Check.True(m == mods && v == vk && norm == canonical, $"'{text}' → mods {m:X} vk {v:X} '{norm}' (expected {mods:X} {vk:X} '{canonical}')");
            Check.True(HotkeyParser.TryParse(norm, out uint m2, out uint v2, out string again) && m2 == m && v2 == v && again == norm, $"'{norm}' round-trips");
        }
        Check.True(HotkeyGesture.TryParse("Ctrl+Shift+`", out var tilde, out _) && tilde.DisplayText == "Ctrl+Shift+~", "the tilde key shows as ~");

        foreach (var (bad, why) in new[]
        {
            ("", "modifier and a key"), ("Ctrl", "modifier and a key"), ("Ctrl+Shift", "not supported"), ("Ctrl+Alt+Shift", "not supported"),
            ("Shift+F5", "Ctrl, Alt or Win"), ("Alt+Space", "system menu"), ("Alt+Tab", "task switcher"), ("Win+Shift+Tab", "task switcher"),
            ("Ctrl+Alt+Tab", "task switcher"), ("Ctrl+Tab", "second modifier"), ("Win+Enter", "second modifier"), ("Ctrl+F25", "not supported"),
            ("Ctrl+F0", "not supported"), ("Ctrl+Esc", "not supported"), ("Ctrl+AB", "not supported"), ("Ctrl+é", "not supported"), ("Ctrl+Ctrl+A", "twice"),
            ("Hyper+A", "not a modifier"),
        })
        {
            Check.True(!HotkeyParser.TryParse(bad, out uint m, out uint v, out string n, out string? err) && m == 0 && v == 0 && n.Length == 0, $"'{bad}' refused");
            Check.True(err!.Contains(why, StringComparison.Ordinal), $"'{bad}': reason mentions '{why}' ({err})");
        }
        Check.True(!HotkeyParser.TryParse(null, out _, out _, out _), "null refused");
        return Task.CompletedTask;
    }

    /// <summary>HotkeyBinding: start with fallback, live rebind, conflicts keep the old hotkey, setup card rebinds before saving.</summary>
    public static async Task HotkeyRebinding()
    {
        var reg = new FakeRegistrar();
        var binding = new HotkeyBinding(reg);
        var seen = new List<string?>();
        binding.ActiveChanged += g => seen.Add(g?.DisplayText);

        var start = binding.Start("Ctrl+Alt+J");
        Check.True(start.Success && binding.Active?.ToString() == "Ctrl+Alt+J" && reg.Registered == (0x3u, (uint)'J'), "configured hotkey registered at start");

        // Live rebind: no restart, the old one is released first (FakeRegistrar checks the order).
        var r = binding.Rebind("Ctrl+Shift+K");
        Check.True(r.Success && binding.Active?.ToString() == "Ctrl+Shift+K" && reg.Registered == (0x6u, (uint)'K'), "rebound live");
        Check.True(binding.Rebind("ctrl+shift+k") is { Success: true } && reg.Registrations == 2, "same hotkey: nothing re-registered");

        // Conflict: another program owns Ctrl+Alt+C → the previous hotkey is registered again and stays active.
        reg.Taken.Add((0x3u, (uint)'C'));
        var conflict = binding.Rebind("Ctrl+Alt+C");
        Check.True(!conflict.Success && conflict.Active?.ToString() == "Ctrl+Shift+K" && reg.Registered == (0x6u, (uint)'K'), "conflict keeps Ctrl+Shift+K");
        Check.True(conflict.Message.Contains("already used by another program", StringComparison.Ordinal) && conflict.Message.Contains("keeping Ctrl+Shift+K", StringComparison.Ordinal), conflict.Message);
        Check.True(binding.Rebind("Shift+A") is { Success: false, Active: not null } bad && bad.Message.Contains("Ctrl, Alt or Win", StringComparison.Ordinal) && reg.Registered is not null, "an invalid string changes nothing");

        // Start with a taken configured hotkey: falls back to the default for this session (the setting is not touched).
        var reg2 = new FakeRegistrar();
        reg2.Taken.Add((0x3u, (uint)'C'));
        var b2 = new HotkeyBinding(reg2);
        var s2 = b2.Start("Ctrl+Alt+C");
        Check.True(!s2.Success && b2.Active == HotkeyGesture.Default && s2.Message.Contains("using Ctrl+Shift+Space for now", StringComparison.Ordinal), s2.Message);
        // Default taken too: no hotkey, tray only.
        var reg3 = new FakeRegistrar();
        reg3.Taken.Add((0x6u, 0x20u));
        var b3 = new HotkeyBinding(reg3);
        var s3 = b3.Start(null);
        Check.True(!s3.Success && b3.Active is null && reg3.Registered is null && s3.Message.Contains("tray icon", StringComparison.Ordinal), s3.Message);
        Check.True(b3.Start("garbage") is { Active: null }, "unparsable setting falls back to the default (taken here)");

        Check.True(seen.SequenceEqual(new string?[] { "Ctrl+Alt+J", null, "Ctrl+Shift+K", null, "Ctrl+Shift+K" }), "ActiveChanged follows every change: " + string.Join(", ", seen));
        Check.Equal("Open AI Cockpit  (Ctrl+Shift+K)", Cockpit.Wpf.TrayIcon.OpenText(binding.Active?.DisplayText), "tray menu text shows the active hotkey");
        Check.Equal("Open AI Cockpit", Cockpit.Wpf.TrayIcon.OpenText(null), "no hotkey: plain menu text");

        // Setup card: rebinds before saving; a conflict keeps the card open, saves nothing, keeps the old hotkey.
        var settings = new MemorySettingsStore();
        var overlay = new CockpitOverlayViewModel(new FakeRuntime(), new InlineDispatcher(), settings);
        var card = new OnboardingViewModel(new FakeProbe(), new FakeStartup(), overlay);
        var cardReg = new FakeRegistrar();
        var cardBinding = new HotkeyBinding(cardReg);
        cardBinding.Start(overlay.Settings.Hotkey);
        card.Rebinder = g => cardBinding.Rebind(g.ToString());
        cardReg.Taken.Add((0x3u, (uint)'J'));
        await card.StartAsync();
        card.UsePresetCommand.Execute("Ctrl+Alt+J");
        Check.True(!card.Finish() && card.IsOpen && card.HotkeyError.Contains("already used by another program", StringComparison.Ordinal), "conflict shown in the card: " + card.HotkeyError);
        Check.True(settings.Saves == 0, "nothing saved");
        Check.True(cardBinding.Active == HotkeyGesture.Default && !card.FinishCommand.CanExecute(null), "old hotkey still active; Finish waits for a new choice");
        HotkeyGesture? changed = null;
        card.HotkeyChanged += g => changed = g;
        card.UsePresetCommand.Execute("Ctrl+Shift+K");
        Check.True(card.Finish() && !card.IsOpen, "a free hotkey finishes");
        Check.True(settings.Current?.Hotkey == "Ctrl+Shift+K" && cardBinding.Active?.ToString() == "Ctrl+Shift+K" && changed?.ToString() == "Ctrl+Shift+K", "saved, registered live, announced");
    }

    /// <summary>V10: model selection and Quota Saver routing; free text runs headless (no terminal) and streams the full answer; cross-CLI continuation.</summary>
    public static async Task V10ChatRoutingAndContinuation(string bridgeDll, string testsDll)
    {
        // (a) models and policies
        Check.True(ProviderRouter.SelectModel(AiProvider.ClaudeCode, TaskCategory.Cad, "design the architecture") == "opus"
            && ProviderRouter.SelectModel(AiProvider.ClaudeCode, TaskCategory.Chat, "hi") == "haiku"
            && ProviderRouter.SelectModel(AiProvider.ClaudeCode, TaskCategory.Chat, new string('x', 200)) == "sonnet"
            && ProviderRouter.SelectModel(AiProvider.ClaudeCode, TaskCategory.Code, "fix the bug") == "sonnet"
            && ProviderRouter.SelectModel(AiProvider.GeminiCli, TaskCategory.Files, "sort") == "gemini-2.5-flash"
            && ProviderRouter.SelectModel(AiProvider.GeminiCli, TaskCategory.Tutor, "explain") == "gemini-2.5-pro", "SelectModel");
        Check.True(ProviderRouter.Route(TaskCategory.Cad, null, false, _ => null, _ => true, RoutingPolicy.ConserveClaude).Provider == AiProvider.GeminiCli
            && ProviderRouter.Route(TaskCategory.Tutor, null, false, _ => null, _ => true, RoutingPolicy.PreferClaude).Provider == AiProvider.ClaudeCode
            && ProviderRouter.Route(TaskCategory.Chat, null, false, _ => null, _ => true).Provider == AiProvider.GeminiCli
            && ProviderRouter.Route(TaskCategory.Cad, AiProvider.ClaudeCode, false, _ => null, _ => true, RoutingPolicy.ConserveClaude).Provider == AiProvider.ClaudeCode, "policies (an explicit choice still wins)");
        Check.True(ProviderRouter.ParseProviderPrefix("/gemini sum this up") == (AiProvider.GeminiCli, "sum this up") && ProviderRouter.ParseProviderPrefix("hello") is null, "/gemini prefix");
        Check.True(JobSpecFactory.InferToolsAndCategory("what is a fillet?", null) is { Category: TaskCategory.Chat, Tools.Count: 0 }
            && JobSpecFactory.InferToolsAndCategory("check the mates", "SLDWORKS").Category == TaskCategory.Cad, "tool inference");

        // (b) free text: headless assistant, no terminal, full answer streamed
        string answer = string.Concat(Enumerable.Repeat("A fillet rounds an edge between two faces. ", 20)); // > 500 chars
        string stream = Path.Combine(TempDir(), "chat.jsonl");
        string sid = "7a1b2c3d-0000-4000-8000-000000000001";
        File.WriteAllText(stream,
            new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sid, ["apiKeySource"] = "none", ["tools"] = new JsonArray(), ["mcp_servers"] = new JsonArray() }.ToJsonString() + "\n" +
            new JsonObject { ["type"] = "assistant", ["session_id"] = sid, ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer }) } }.ToJsonString() + "\n" +
            new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = answer, ["num_turns"] = 1, ["session_id"] = sid }.ToJsonString() + "\n");
        var launcher = new FakeLauncher(testsDll) { EmitFile = stream };
        await using var rt = await LaunchTests.Runtime(bridgeDll, launcher);
        var vm = new CockpitOverlayViewModel(rt, new InlineDispatcher()) { RoutingPolicy = RoutingPolicy.PreferClaude };
        vm.PromptText = "Explain what a fillet is in CAD";
        vm.SubmitPromptCommand.Execute(null);
        await Until(() => vm.Conversation.Count == 1 && !vm.Conversation[0].IsRunning && vm.Jobs.Count == 1 && !vm.Jobs[0].IsActive, "chat turn finished");
        var turn = vm.Conversation[0];
        Check.True(launcher.Headless.Count == 1 && launcher.Interactive.Count == 0, "headless, never a terminal");
        var args = launcher.Headless[0].ArgumentList.ToList();
        Check.True(args[args.IndexOf("--model") + 1] == "haiku" && turn.BadgeText == "Claude Code · haiku", $"Claude First + short chat → haiku ({turn.BadgeText})");
        Check.True(turn.ResponseText == answer && turn.ResponseText.Length > 500 && vm.SelectedTab == 0 && vm.HasConversation, "full answer, untruncated, on the Actions tab");
        Check.True(vm.Activity.Any(a => a.Detail.EndsWith("…", StringComparison.Ordinal)), "the Activity feed still truncates");

        // (c) cross-CLI continuation
        var native = ContinuableSession.From(rt.Jobs.Single())!;
        Check.True(native.NativeResume && native.LastResponseText == answer, "Claude run: native resume, answer kept");
        var toGemini = JobSpecFactory.Continue(native, "and a chamfer?", targetProvider: AiProvider.GeminiCli);
        Check.True(toGemini.ContinueSessionId is null && toGemini.Provider == AiProvider.GeminiCli
            && toGemini.Prompt.StartsWith("Previous turn user: Explain what a fillet is in CAD", StringComparison.Ordinal)
            && toGemini.Prompt.Contains("Previous turn assistant: A fillet", StringComparison.Ordinal) && toGemini.Prompt.EndsWith("Follow-up: and a chamfer?", StringComparison.Ordinal), "Claude → Gemini carries the turn as context");
        var gem = native with { SessionId = "turn-x", Provider = AiProvider.GeminiCli, NativeResume = false };
        Check.True(JobSpecFactory.Continue(gem, "more") is { ContinueSessionId: null, Provider: null } g && g.Prompt.Contains("Previous turn assistant:", StringComparison.Ordinal), "Gemini session: routed normally with context");
        Check.True(JobSpecFactory.Continue(native, "more").ContinueSessionId == sid, "Claude → Claude still resumes natively");
        var gl = new GeminiLaunchBuilder("/opt/fake/gemini").Headless(rt.Jobs.Single().Workspace, null, false, TempDir()).ArgumentList.ToList();
        Check.True(gl.Contains("--include-directories"), "Gemini sees the user's folder");
    }
}
