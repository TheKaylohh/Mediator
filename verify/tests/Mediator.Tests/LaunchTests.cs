using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Connectors.SolidWorks;
using Mediator.Protocol;

namespace Mediator.Tests;

/// <summary>Records what the runtime would launch and runs stand-ins: a "CLI" that prints a stream, a "terminal" that stays up.</summary>
sealed class FakeLauncher(string testsDll) : ICliLauncher
{
    public readonly List<ProcessStartInfo> Headless = new();
    public readonly List<(ProcessStartInfo Psi, JobWorkspace Ws)> Interactive = new();
    public string? EmitFile { get; set; }
    public int LauncherExitCode { get; private set; } = -1;

    /// <summary>Per-launch scripts, used in order before falling back to <see cref="EmitFile"/>: stdout file plus options (exit=, gate=, stderr=).</summary>
    public readonly Queue<string[]> Script = new();

    public Process StartHeadless(ProcessStartInfo psi)
    {
        Headless.Add(psi);
        string[] emit;
        lock (Script) emit = Script.Count > 0 ? Script.Dequeue() : [EmitFile ?? throw new InvalidOperationException("EmitFile not set")];
        var stand = Child(["--emit", .. emit]);
        stand.RedirectStandardInput = stand.RedirectStandardOutput = stand.RedirectStandardError = true;
        stand.StandardOutputEncoding = Utf8.NoBom;
        return Process.Start(stand)!;
    }

    /// <summary>Like wt.exe: a launcher that exits almost at once. Nothing else happens here.</summary>
    public void StartInteractive(ProcessStartInfo psi, JobWorkspace ws)
    {
        Interactive.Add((psi, ws));
        using var launcher = Process.Start(Child("--sleep", "0"))!;
        launcher.WaitForExit();
        LauncherExitCode = launcher.ExitCode;
    }

    /// <summary>A long-lived process standing in for the PowerShell in the terminal tab.</summary>
    public Process StartTerminal() => Process.Start(Child("--sleep", "120"))!;

    private ProcessStartInfo Child(params string[] args)
    {
        var p = new ProcessStartInfo(ComponentLocator.DotnetHost()) { UseShellExecute = false };
        p.ArgumentList.Add(testsDll);
        foreach (var a in args) p.ArgumentList.Add(a);
        return p;
    }
}

static class LaunchTests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception("Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    public static Task<MediatorRuntime> Runtime(string bridgeDll, FakeLauncher launcher, IApprovalPrompt? prompt = null, TimeSpan? startupGrace = null) =>
        MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir()),
            Bus = new BusOptions { PipeName = "mediator-ln-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Simulate = true,
            ClaudeExecutable = "/opt/fake/claude",
            GeminiExecutable = "/opt/fake/gemini",
            Launcher = launcher,
            InteractivePollInterval = TimeSpan.FromMilliseconds(50),
            InteractiveStartupGrace = startupGrace ?? TimeSpan.FromSeconds(3),
        }).ContinueWith(t => { t.Result.Approvals.Target = prompt ?? new AutoApprovalPrompt(); return t.Result; }, TaskScheduler.Default);

    /// <summary>
    /// Gemini V5 item 1: wt.exe exits within milliseconds; the job must not end with it. The bridge connects 200 ms
    /// later and works; the job ends only on interactive.done, a kill (which also ends the terminal), a closed tab,
    /// or a terminal that never starts.
    /// </summary>
    public static async Task InteractiveSurvivesLauncherExit(string bridgeDll, string testsDll)
    {
        var launcher = new FakeLauncher(testsDll);
        await using var runtime = await Runtime(bridgeDll, launcher);
        string folder = TempDir();
        File.WriteAllText(Path.Combine(folder, "a.sldprt"), "p");

        // ── 1. launcher exits, bridge connects later, done file ends the job ──
        var job = await runtime.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Interactive) with { JobId = "int-1" });
        Check.Equal(0, launcher.LauncherExitCode, "the stand-in wt.exe already exited");
        string script = File.ReadAllText(Path.Combine(job.Workspace.Root, "launch-claude.ps1"));
        Check.True(script.Contains(InteractiveSession.PidFileName, StringComparison.Ordinal) && script.Contains("finally", StringComparison.Ordinal)
            && script.Contains(InteractiveSession.DoneFileName, StringComparison.Ordinal), "launch script writes interactive.pid and, in finally, interactive.done");
        Check.True(script.IndexOf(InteractiveSession.PidFileName, StringComparison.Ordinal) < script.IndexOf("try {", StringComparison.Ordinal), "pid written before the CLI starts");
        Check.True(File.ReadAllBytes(Path.Combine(job.Workspace.Root, "launch-claude.ps1")).AsSpan().StartsWith(Utf8.WithBom.GetPreamble()), "script has a BOM");

        await Task.Delay(200);
        Check.Equal(JobState.Running, job.State, "still running after the launcher exited");
        using var terminal = launcher.StartTerminal();
        job.Interactive!.WritePid(terminal);
        await using (var client = await runtime.ConnectClientAsync(job))
        {
            var cards = await client.CallToolAsync("swx_part_cards", new JsonObject { ["folder"] = folder }, TimeSpan.FromSeconds(30));
            Check.True(!cards.IsError, "tools/call through a bridge that connected 200 ms after the launcher exited: " + cards.Text);
        }
        await Task.Delay(300);
        Check.Equal(JobState.Running, job.State, "still running while the terminal is alive and no done file exists");
        // V6: a done file from another session, a malformed one and a half-written one are all ignored.
        File.WriteAllText(job.Interactive.DonePath, "0123456789abcdef0123456789abcdef 0");
        await Task.Delay(300);
        Check.Equal(JobState.Running, job.State, "a done file with another session's nonce is ignored");
        Check.True(job.Interactive.LastRejected?.Contains("another session", StringComparison.Ordinal) == true, "and the reason is recorded");
        File.WriteAllText(job.Interactive.DonePath, job.Interactive.Nonce);
        await Task.Delay(300);
        Check.Equal(JobState.Running, job.State, "a half-written done file (nonce only) is ignored");
        File.WriteAllText(job.Interactive.DonePath, "garbage");
        await Task.Delay(300);
        Check.Equal(JobState.Running, job.State, "a malformed done file is ignored");
        job.Interactive.WriteDone(0);
        Check.Equal(JobState.Succeeded, await job.Completion.WaitAsync(TimeSpan.FromSeconds(5)), "interactive.done 0 → Succeeded");
        Check.True(!runtime.Registry.TryGet("int-1", out _), "grant revoked when the CLI exited");
        terminal.Kill();

        // ── 2. kill ends the terminal process tree too ──
        var job2 = await runtime.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Interactive) with { JobId = "int-2" });
        using var terminal2 = launcher.StartTerminal();
        job2.Interactive!.WritePid(terminal2);
        await Task.Delay(150);
        Check.True(runtime.KillJob("int-2", "test"), "kill accepted");
        Check.True(terminal2.WaitForExit(5000), "the terminal recorded in interactive.pid was terminated");
        Check.Equal(JobState.Killed, job2.State, "killed");

        // ── 3. tab closed without done → Ended ──
        var notices = new List<string>();
        runtime.Notice += (_, m) => { lock (notices) notices.Add(m); };
        var job3 = await runtime.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Interactive) with { JobId = "int-3" });
        using (var terminal3 = launcher.StartTerminal())
        {
            job3.Interactive!.WritePid(terminal3);
            await Task.Delay(150);
            terminal3.Kill();
        }
        Check.Equal(JobState.Ended, await job3.Completion.WaitAsync(TimeSpan.FromSeconds(5)), "closed tab → Ended");
        lock (notices) Check.True(notices.Any(n => n.Contains("closed", StringComparison.Ordinal)), "the user is told the tab was closed");

        // ── 4. a recycled PID is not mistaken for the terminal ──
        var job4 = await runtime.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Interactive) with { JobId = "int-4" });
        File.WriteAllText(job4.Interactive!.PidPath, InteractiveSession.FormatPid(job4.Interactive.Nonce, Environment.ProcessId, DateTime.UtcNow.AddHours(-3)));
        Check.Equal(JobState.Ended, await job4.Completion.WaitAsync(TimeSpan.FromSeconds(5)), "same PID, different start time → treated as gone");

        // ── 5. terminal never starts ──
        var quick = new FakeLauncher(testsDll);
        await using var rt2 = await Runtime(bridgeDll, quick, startupGrace: TimeSpan.FromMilliseconds(400));
        var job5 = await rt2.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Interactive) with { JobId = "int-5" });
        Check.Equal(JobState.Failed, await job5.Completion.WaitAsync(TimeSpan.FromSeconds(5)), "no interactive.pid within the grace → Failed");
        Check.True(job5.Detail!.Contains("did not start", StringComparison.Ordinal), "reason given");
    }

    // Antigravity CLI (agy 1.4.7) stream-json shape, as in the real capture verify/fixtures/real/agy-1.4.7-ok.stream.jsonl,
    // extended with tool steps from the documented step_update/tool_info fields.
    public const string GeminiRun =
        "\uFEFF" +
        """{"event":"init","conversation_id":"g-1","init":{"cwd":"C:/jobs/x","tools":["call_mcp_tool"],"permission_mode":"request-review","model":"gemini-3.8-flash-high"}}""" + "\r\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-1","step_index":0,"state":"DONE","step_type":"user_input"}}""" + "\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-1","step_index":1,"state":"ACTIVE","step_type":"tool","tool_name":"files_plan_by_rules","tool_info":{"name":"files_plan_by_rules","parameters":{"sourceFolder":"D:/Downloads"}}}}""" + "\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-1","step_index":1,"state":"DONE","step_type":"tool","tool_name":"files_plan_by_rules","tool_info":{"name":"files_plan_by_rules","output":"{\"batchId\":\"b1\",\"moves\":3}"}}}""" + "\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-1","step_index":2,"state":"DONE","step_type":"tool","tool_name":"files_execute_batch","tool_info":{"name":"files_execute_batch","parameters":{"batchId":"b1"},"error":{"type":"tool_error","message":"The user denied this action."}}}}""" + "\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-1","step_index":3,"state":"ACTIVE","step_type":"agent_response","text_delta":"The batch was not run "}}""" + "\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-1","step_index":3,"state":"DONE","step_type":"agent_response","text_delta":"because you denied it."}}""" + "\n" +
        """{"event":"result","result":{"conversation_id":"g-1","status":"SUCCESS","response":"The batch was not run because you denied it.","duration_seconds":10.0,"num_turns":2,"usage":{"input_tokens":1500,"output_tokens":400,"total_tokens":1900}}}""";

    public const string GeminiQuota =
        """{"event":"init","conversation_id":"g-2","init":{"model":"gemini-3.8-flash-high"}}""" + "\n" +
        """{"event":"step_update","step_update":{"conversation_id":"g-2","step_index":1,"state":"DONE","step_type":"tool","tool_info":{"name":"x","error":{"type":"api_error","message":"[API Error: 429 RESOURCE_EXHAUSTED] Quota exceeded. Please retry in 37.2s."}}}}""" + "\n";

    public const string AgyResultOnly =
        """{"event":"result","result":{"conversation_id":"g-3","status":"SUCCESS","response":"Done.\n","usage":{"input_tokens":800,"output_tokens":120,"cache_read_tokens":300}}}""";

    static List<MediatorStreamEvent> Parse(string text, int chunk, TimeProvider? time = null)
    {
        var p = new GeminiStreamParser(time);
        var events = new List<MediatorStreamEvent>();
        for (int i = 0; i < text.Length; i += chunk) events.AddRange(p.Feed(text.AsSpan(i, Math.Min(chunk, text.Length - i))));
        events.AddRange(p.Complete());
        return events;
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    public static Task GeminiParser()
    {
        string Shape(IEnumerable<MediatorStreamEvent> es) => string.Join(",", es.Select(e => e.GetType().Name));
        var whole = Parse(GeminiRun, GeminiRun.Length);
        foreach (int size in new[] { 1, 3, 17 }) Check.Equal(Shape(whole), Shape(Parse(GeminiRun, size)), $"same events in {size}-char chunks");
        Check.Equal("SessionInitEvent,ToolUseEvent,ToolResultEvent,ToolUseEvent,ToolResultEvent,AssistantTextEvent,RunResultEvent", Shape(whole), "event sequence (agent deltas joined per step)");
        Check.Equal("gemini-3.8-flash-high", ((SessionInitEvent)whole[0]).Model, "BOM stripped, model read");
        Check.Equal("The batch was not run because you denied it.", ((AssistantTextEvent)whole[5]).Text, "deltas joined");
        Check.True(((ToolResultEvent)whole[4]).IsError && ((ToolResultEvent)whole[4]).Text == "The user denied this action.", "tool error status and message");
        var result = (RunResultEvent)whole[^1];
        Check.True(!result.IsError && result.Usage == new UsageTotals(1500, 400, null, null) && result.NumTurns == 2, "result stats (V6: cache fields Gemini did not report are unknown, not 0)");

        var now = DateTimeOffset.Parse("2026-10-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var quota = Parse(GeminiQuota, 7, new FixedTime(now));
        var synthetic = (RunResultEvent)quota[^1];
        Check.True(synthetic.IsError && synthetic.RateLimit is not null, "a stream that ends after a 429 yields a failed result with a rate limit");
        Check.Equal(now.AddSeconds(38), synthetic.RateLimit!.ResetsAt, "'retry in 37.2s' → reset time");

        var daily = Parse("""{"event":"result","result":{"status":"ERROR","error":{"message":"You have exhausted your daily quota (RESOURCE_EXHAUSTED)"}}}""", 50, new FixedTime(now));
        Check.Equal(DateTimeOffset.Parse("2026-10-23T07:00:00Z", System.Globalization.CultureInfo.InvariantCulture), ((RunResultEvent)daily[^1]).RateLimit!.ResetsAt,
            "daily quota → next midnight Pacific (PDT in October = 07:00 UTC)");

        var noDeltas = Parse(AgyResultOnly, 9);
        Check.True(noDeltas[0] is AssistantTextEvent { Text: "Done." } && ((RunResultEvent)noDeltas[1]).Usage == new UsageTotals(800, 120, 300, null), "result without deltas: response shown once");
        return Task.CompletedTask;
    }

    public static Task RouterRules()
    {
        DateTimeOffset? None(AiProvider _) => null;
        bool All(AiProvider _) => true;
        Check.Equal(AiProvider.ClaudeCode, ProviderRouter.Route(TaskCategory.Cad, null, false, None, All).Provider, "cad → Claude");
        Check.Equal(AiProvider.ClaudeCode, ProviderRouter.Route(TaskCategory.Blender, null, false, None, All).Provider, "blender → Claude");
        Check.Equal(AiProvider.GeminiCli, ProviderRouter.Route(TaskCategory.Tutor, null, false, None, All).Provider, "tutor → Gemini");
        Check.Equal(AiProvider.GeminiCli, ProviderRouter.Route(TaskCategory.Resolve, null, false, None, All).Provider, "resolve → Gemini");
        Check.Equal(AiProvider.GeminiCli, ProviderRouter.Route(TaskCategory.Cad, AiProvider.GeminiCli, false, None, All).Provider, "explicit provider wins");
        var noGemini = ProviderRouter.Route(TaskCategory.Files, null, false, None, p => p == AiProvider.ClaudeCode);
        Check.True(noGemini.Provider == AiProvider.ClaudeCode && noGemini.Downgraded && noGemini.Notice!.Contains("not installed", StringComparison.Ordinal), "missing CLI falls back with a notice");
        Check.Equal((TaskCategory.Tutor, "chapter 3"), ProviderRouter.ParsePrefix("/tutor chapter 3")!.Value, "prefix parsed");
        Check.Equal(null, ProviderRouter.ParsePrefix("/nope x"), "unknown prefix ignored");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gemini V5 item 3: (a) Claude cooling down → the job runs on Gemini CLI, with .gemini/settings.json wired to the
    /// bridge, billing variables scrubbed, the stream parsed and usage recorded under gemini-cli; (b) NeverDowngrade
    /// refuses with a clear message and issues nothing; a Gemini quota error then routes Gemini-first work to Claude.
    /// </summary>
    public static async Task FailoverToGemini(string bridgeDll, string testsDll)
    {
        var launcher = new FakeLauncher(testsDll);
        await using var runtime = await Runtime(bridgeDll, launcher);
        string folder = TempDir();
        string fixture = Path.Combine(TempDir(), "gemini.jsonl");
        File.WriteAllText(fixture, GeminiRun, new UTF8Encoding(false));
        launcher.EmitFile = fixture;
        var notices = new List<string>();
        runtime.Notice += (_, m) => { lock (notices) notices.Add(m); };

        await runtime.Usage.SetCooldownAsync("claude-code", DateTimeOffset.UtcNow.AddHours(3), "Claude AI usage limit reached");
        string saved = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "must-not-reach-gemini");
        RunningJob job;
        try { job = await runtime.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Headless) with { JobId = "route-a" }); }
        finally { Environment.SetEnvironmentVariable("GEMINI_API_KEY", saved.Length == 0 ? null : saved); }

        Check.Equal(AiProvider.GeminiCli, job.Provider, "cad job switched to Gemini while Claude is cooling down");
        Check.True(job.RoutingNotice!.Contains("using Google AI Pro", StringComparison.Ordinal), "routing notice: " + job.RoutingNotice);
        lock (notices) Check.True(notices.Any(n => n.Contains("usage limit until", StringComparison.Ordinal)), "notice raised for the UI/CLI");

        var psi = launcher.Headless.Single();
        Check.Equal("/opt/fake/gemini", psi.FileName, "gemini launched");
        string[] args = [.. psi.ArgumentList];
        // Antigravity: stream-json in and out, no user folder in its workspace, an auto-selected model (--model).
        Check.True(args.Take(4).SequenceEqual(new[] { "--input-format", "stream-json", "--output-format", "stream-json" })
            && !args.Contains("--add-dir") && args[Array.IndexOf(args, "--model") + 1].StartsWith("gemini-3.8-", StringComparison.Ordinal), "agy flags: " + string.Join(' ', args));
        Check.Equal(job.Workspace.Root, psi.WorkingDirectory, "runs in the job workspace so .agents/mcp_config.json is found");
        Check.True(!psi.Environment.ContainsKey("GEMINI_API_KEY") && !psi.Environment.ContainsKey("GOOGLE_GENAI_USE_VERTEXAI"), "Google billing variables scrubbed");

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(job.Workspace.Root, ".agents", "mcp_config.json")))!;
        var server = settings["mcpServers"]!["mediator"]!;
        Check.Equal(ComponentLocator.DotnetHost(), (string)server["command"]!, "mcp_config.json runs the bridge");
        var serverArgs = server["args"]!.AsArray().Select(a => (string)a!).ToList();
        Check.True(serverArgs[0] == bridgeDll && serverArgs.Contains("route-a") && !serverArgs.Contains(job.Grant.Token), "bridge args carry this job's id but not its token");
        Check.Equal(job.Grant.Token, (string?)server["env"]!["MEDIATOR_JOB_TOKEN"], "the token travels in the bridge's environment");

        Check.Equal(JobState.Succeeded, await job.Completion.WaitAsync(TimeSpan.FromSeconds(20)), "Gemini stream parsed to a successful result: " + job.Detail);
        var totals = await runtime.Usage.TotalsAsync("gemini-cli", DateTimeOffset.MinValue);
        Check.True(totals.Runs == 1 && totals.Tokens.InputTokens == 1500, "usage recorded under gemini-cli");

        // (b) never downgrade
        ProviderUnavailableException? refused = null;
        try { await runtime.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Headless) with { JobId = "route-b", NeverDowngrade = true }); }
        catch (ProviderUnavailableException ex) { refused = ex; }
        Check.True(refused is not null && refused.Message.Contains("never switch subscriptions", StringComparison.Ordinal) && refused.Message.Contains("usage limit until", StringComparison.Ordinal),
            "NeverDowngrade refuses with the reason: " + refused?.Message);
        Check.True(!runtime.Registry.TryGet("route-b", out _) && launcher.Headless.Count == 1, "nothing issued or launched");

        // Gemini hits its quota → Gemini-first categories go to Claude once Claude is back.
        File.WriteAllText(fixture, GeminiQuota);
        var quotaJob = await runtime.StartJobAsync(JobSpecFactory.Organize(folder, "by type", LaunchMode.Headless) with { JobId = "route-c" });
        Check.Equal(AiProvider.GeminiCli, quotaJob.Provider, "files → Gemini by default");
        Check.Equal(JobState.Failed, await quotaJob.Completion.WaitAsync(TimeSpan.FromSeconds(20)), "quota run failed");
        Check.True(await runtime.GetCooldownAsync("gemini-cli") is not null, "Gemini cooldown recorded from RESOURCE_EXHAUSTED");
        ProviderUnavailableException? both = null;
        try { await runtime.RouteAsync(TaskCategory.Files); } catch (ProviderUnavailableException ex) { both = ex; }
        Check.True(both is not null && both.Message.Contains("Claude Pro", StringComparison.Ordinal) && both.Message.Contains("Google AI Pro", StringComparison.Ordinal), "both limited → clear error naming both");
    }
}
