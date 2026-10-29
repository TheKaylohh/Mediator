using System.Text;
using Cockpit.Core.Bus;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;
using Microsoft.Data.Sqlite;

namespace Cockpit.Tests;

/// <summary>V6 Phase 3: provider outcomes, failover rules and usage accounting, including real CLI captures.</summary>
static class FailoverTests
{
    static string Real(string name)
    {
        // verify/tests/Cockpit.Tests/bin/Debug/net8.0 → verify/fixtures/real
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !Directory.Exists(Path.Combine(dir, "verify", "fixtures", "real")); i++) dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\'))!;
        string p = Path.Combine(dir, "verify", "fixtures", "real", name);
        if (!File.Exists(p)) throw new Exception("real fixture missing: " + p);
        return p;
    }

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-fo", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static string Fixture(string text)
    {
        string p = Path.Combine(TempDir(), "run.jsonl");
        File.WriteAllText(p, text, new UTF8Encoding(false));
        return p;
    }

    static List<CockpitStreamEvent> Parse(ICliStreamParser parser, string text)
    {
        var events = new List<CockpitStreamEvent>(parser.Feed(text.AsSpan()));
        events.AddRange(parser.Complete());
        return events;
    }

    /// <summary>Pre-launch routing: every combination of availability and NeverDowngrade gives a distinct, explained result.</summary>
    public static Task RouterMatrix()
    {
        DateTimeOffset? None(AiProvider _) => null;
        DateTimeOffset? Both(AiProvider _) => DateTimeOffset.UtcNow.AddHours(2);
        DateTimeOffset? ClaudeLimited(AiProvider p) => p == AiProvider.ClaudeCode ? DateTimeOffset.UtcNow.AddHours(2) : null;
        bool All(AiProvider _) => true;

        var claudeDown = ProviderRouter.Route(TaskCategory.Code, null, false, None, p => p == AiProvider.GeminiCli);
        Check.True(claudeDown is { Provider: AiProvider.GeminiCli, Downgraded: true, Original: AiProvider.ClaudeCode } && claudeDown.Reason!.Contains("not installed", StringComparison.Ordinal),
            "Claude unavailable, Gemini available → Gemini, original and reason recorded");
        var geminiDown = ProviderRouter.Route(TaskCategory.Tutor, null, false, None, p => p == AiProvider.ClaudeCode);
        Check.True(geminiDown is { Provider: AiProvider.ClaudeCode, Downgraded: true, Original: AiProvider.GeminiCli }, "Gemini unavailable, Claude available → Claude");
        var ok = ProviderRouter.Route(TaskCategory.Code, null, false, None, All);
        Check.True(ok is { Provider: AiProvider.ClaudeCode, Downgraded: false, Reason: null }, "no switch needed");

        string Refusal(Func<RouteDecision> f)
        {
            try { f(); } catch (ProviderUnavailableException ex) { return ex.Message; }
            throw new Exception("Assertion failed: expected a refusal");
        }
        string none = Refusal(() => ProviderRouter.Route(TaskCategory.Code, null, false, None, _ => false));
        Check.True(none.Contains("Claude Pro", StringComparison.Ordinal) && none.Contains("Google AI Pro", StringComparison.Ordinal), "both unavailable → refused, naming both: " + none);
        string cooling = Refusal(() => ProviderRouter.Route(TaskCategory.Files, null, false, Both, All));
        Check.True(cooling.Contains("usage limit until", StringComparison.Ordinal), "both cooling down → refused with reset times: " + cooling);
        string never = Refusal(() => ProviderRouter.Route(TaskCategory.Code, null, true, ClaudeLimited, All));
        Check.True(never.Contains("never switch", StringComparison.Ordinal), "NeverDowngrade → refused even though Gemini is free");
        return Task.CompletedTask;
    }

    /// <summary>Real captures (Claude Code 2.1.309, Gemini CLI 0.65.0) are classified correctly; synthetic edge cases too.</summary>
    public static Task RealCapturesClassified()
    {
        var claude = Parse(new ClaudeStreamParser(), File.ReadAllText(Real("claude-2.1.309-invalid-api-key.stream.jsonl")));
        var init = claude.OfType<SessionInitEvent>().Single();
        Check.True(init.UsesApiKey && init.ApiKeySource == "ANTHROPIC_API_KEY", "real init reports apiKeySource ANTHROPIC_API_KEY (billing alarm signal)");
        var result = claude.OfType<RunResultEvent>().Single();
        Check.True(result.IsError && result.Subtype == "success" && result.RateLimit is null, "real result: subtype 'success' yet is_error true — is_error is what counts");
        Check.Equal(RunOutcome.AuthFailed, CockpitRuntime.Classify(1, result, ""), "invalid API key → AuthFailed (not a rate limit, not failed over)");

        string noAuth = File.ReadAllText(Real("gemini-0.65.0-no-auth.stderr.txt"));
        Check.Equal(RunOutcome.AuthFailed, CockpitRuntime.Classify(GeminiLaunchBuilder.ExitNoAuth, null, noAuth), "Gemini exit 41 + 'set an Auth method' → AuthFailed");
        Check.Equal(RunOutcome.AuthFailed, CockpitRuntime.Classify(1, null, noAuth), "…recognised from the text alone too");

        var blocked = Parse(new GeminiStreamParser(), File.ReadAllText(Real("gemini-0.65.0-network-blocked.stream.jsonl")));
        Check.True(blocked.OfType<RunResultEvent>().Any() == false && blocked.Count >= 1, "real Gemini stream cut off after init: no result event");
        // Antigravity CLI 1.4.7 real capture: deltas joined into one answer, conversation id, SUCCESS with usage.
        var agy = Parse(new GeminiStreamParser(), File.ReadAllText(Real("agy-1.4.7-ok.stream.jsonl")));
        var agyResult = agy.OfType<RunResultEvent>().Single();
        Check.True(agy.OfType<AssistantTextEvent>().Single().Text == "agy ok" && !agyResult.IsError && agyResult.SessionId == "7b27b9d8-3cdd-4b62-8236-1d5b914298cf"
            && agyResult.Usage is { InputTokens: 6801, OutputTokens: 54 }, "real agy stream: text, session, usage (output includes thinking)");
        Check.Equal(RunOutcome.NoResult, CockpitRuntime.Classify(143, null, File.ReadAllText(Real("gemini-0.65.0-network-blocked.stderr.txt"))), "EOF without result → NoResult");

        var ok = Parse(new ClaudeStreamParser(), StreamTests.SuccessRun).OfType<RunResultEvent>().Single();
        Check.Equal(RunOutcome.Succeeded, CockpitRuntime.Classify(0, ok, ""), "success");
        Check.Equal(RunOutcome.ResultThenAbnormalExit, CockpitRuntime.Classify(3, ok, ""), "a result, then exit 3 → distinct outcome");
        var limited = Parse(new ClaudeStreamParser(), StreamTests.RateLimitedRun).OfType<RunResultEvent>().Single();
        Check.Equal(RunOutcome.RateLimited, CockpitRuntime.Classify(1, limited, ""), "usage limit → RateLimited");
        return Task.CompletedTask;
    }

    /// <summary>Unknown usage stays unknown (never 0); duplicates are counted once; missing results are recorded; a V5 ledger migrates.</summary>
    public static async Task UsageUnknownNotZero()
    {
        string dir = TempDir();
        // A V5 ledger with the old NOT NULL schema and one row.
        string db = Path.Combine(dir, "usage.db");
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE usage_run (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, job_id TEXT NOT NULL, provider TEXT NOT NULL,
                  session_id TEXT, model TEXT, subtype TEXT, is_error INTEGER NOT NULL, rate_limited INTEGER NOT NULL, num_turns INTEGER, duration_ms INTEGER,
                  input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL, cache_read_tokens INTEGER NOT NULL, cache_creation_tokens INTEGER NOT NULL, notional_cost_usd REAL);
                CREATE INDEX ix_usage_provider ON usage_run(provider, ts_utc);
                INSERT INTO usage_run (ts_utc, job_id, provider, subtype, is_error, rate_limited, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens)
                VALUES ('2026-10-01T00:00:00Z', 'old', 'claude-code', 'success', 0, 0, 100, 50, 0, 0);
                """;
            cmd.ExecuteNonQuery();
        }
        await using var ledger = await UsageLedger.OpenAsync(db);
        var before = await ledger.TotalsAsync("claude-code", DateTimeOffset.MinValue);
        Check.True(before.Runs == 1 && before.Tokens.InputTokens == 100 && before.UnknownUsageRuns == 0, "V5 row survives the migration");

        var monitor = CliRunMonitor.For(AiProvider.ClaudeCode, "u1", ledger);
        string noUsage = """{"type":"result","subtype":"success","is_error":false,"session_id":"s","num_turns":1,"result":"ok"}""" + "\n";
        string partial = """{"type":"result","subtype":"success","is_error":false,"session_id":"s","num_turns":1,"result":"ok","usage":{"input_tokens":7}}""" + "\n";
        await monitor.ObserveAsync(new StringReader(noUsage + noUsage), CancellationToken.None);
        Check.True(monitor.LastResult!.Usage is null && monitor.DuplicateResults == 1, "missing usage is null, and the duplicate result is ignored");
        var m2 = CliRunMonitor.For(AiProvider.ClaudeCode, "u2", ledger);
        await m2.ObserveAsync(new StringReader(partial), CancellationToken.None);
        Check.True(m2.LastResult!.Usage is { InputTokens: 7, OutputTokens: null, Complete: false }, "partly reported usage keeps the unknown field null");
        var m3 = CliRunMonitor.For(AiProvider.ClaudeCode, "u3", ledger);
        await m3.ObserveAsync(new StringReader("""{"type":"system","subtype":"init","session_id":"s","apiKeySource":"none"}""" + "\n"), CancellationToken.None);
        await m3.RecordMissingResultAsync("claude exited with code 1 without a result");

        var after = await ledger.TotalsAsync("claude-code", DateTimeOffset.MinValue);
        Check.Equal(4, after.Runs, "old + no-usage (once, despite the duplicate) + partial + no-result");
        Check.Equal(3, after.UnknownUsageRuns, "three runs flagged as unknown usage, none counted as free");
        Check.Equal(107L, after.Tokens.InputTokens, "sums include only reported values");
        Check.True(new UsageTotals(5, null, null, null).Describe().Contains("partly unknown", StringComparison.Ordinal), "UI text says unknown, not 0");
    }

    static async Task<CockpitRuntime> Runtime(string bridgeDll, FakeLauncher launcher) => await CockpitRuntime.CreateAsync(new RuntimeOptions
    {
        Paths = new CockpitPaths(TempDir()),
        Bus = new BusOptions { PipeName = "cockpit-fo-" + Guid.NewGuid().ToString("N")[..10] },
        Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
        SwxBackend = new Cockpit.Core.Swx.SimulatedSwxBackend(),
        ClaudeExecutable = "/opt/fake/claude",
        GeminiExecutable = "/opt/fake/gemini",
        Launcher = launcher,
    });

    static async Task<List<AuditLog.AuditRow>> Routes(CockpitRuntime rt, string job) =>
        (await rt.Audit.ListAsync(job)).Where(r => r.Tool == "cockpit/route").ToList();

    /// <summary>
    /// Mid-job switch rules with real processes: a rate limit before any side effect continues on the other
    /// subscription with the same grant; a possible side effect, NeverDowngrade, or a second limit refuse; every
    /// decision is audited with original, attempted and reason.
    /// </summary>
    public static async Task MidJobFailover(string bridgeDll, string testsDll)
    {
        var launcher = new FakeLauncher(testsDll);
        await using var rt = await Runtime(bridgeDll, launcher);
        string folder = TempDir();
        string limited = Fixture(StreamTests.RateLimitedRun), gemini = Fixture(LaunchTests.GeminiRun);

        // 1. Rate limit before any side effect → Gemini, same grant.
        launcher.Script.Enqueue([limited, "exit=1"]);
        launcher.Script.Enqueue([gemini]);
        var job = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "fo-1" });
        var tools = job.Grant.AllowedTools.Order(StringComparer.Ordinal).ToList();
        string token = job.Grant.Token;
        Check.Equal(JobState.Succeeded, await job.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "the job finished on the other subscription: " + job.Detail);
        Check.True(job is { Provider: AiProvider.GeminiCli, OriginalProvider: AiProvider.ClaudeCode } && launcher.Headless.Count == 2
            && launcher.Headless[1].FileName == "/opt/fake/gemini", "Claude → Gemini after the limit");
        Check.True(job.Grant.Token == token && job.Grant.AllowedTools.Order(StringComparer.Ordinal).SequenceEqual(tools), "grant, token and tool allow-list unchanged by the switch");
        var routes = await Routes(rt, "fo-1");
        Check.True(routes.Any(r => r.ArgumentsJson.Contains("SwitchedAfterRateLimit", StringComparison.Ordinal) && r.ArgumentsJson.Contains("\"original\":\"claude-code\"", StringComparison.Ordinal)
            && r.ArgumentsJson.Contains("\"attempted\":\"gemini-cli\"", StringComparison.Ordinal) && r.Detail!.Contains("before any side effect", StringComparison.Ordinal)),
            "audit: original, attempted, reason");
        Check.True(job.RoutingHistory.Count == 2 && job.RoutingHistory[1].Contains("continuing on Google AI Pro", StringComparison.Ordinal), "routing history for the UI");

        // 2. A side effect may have happened → no switch.
        string gate = Path.Combine(TempDir(), "go");
        launcher.Script.Enqueue([limited, "exit=1", "gate=" + gate]);
        int before = launcher.Headless.Count;
        var job2 = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "fo-2" });
        var (op, _) = await rt.Operations.BeginAsync("fo-2", "swx_build_assembly", "k", "{}");
        foreach (var s in new[] { OperationState.Validated, OperationState.Approved, OperationState.Executing, OperationState.Committed })
            await rt.Operations.TransitionAsync(op!.OperationId, s);
        File.WriteAllText(gate, "");
        Check.Equal(JobState.Failed, await job2.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "no switch after a side effect");
        Check.True(job2.Outcome == RunOutcome.RateLimited && launcher.Headless.Count == before + 1 && job2.Provider == AiProvider.ClaudeCode, "stayed on Claude, outcome RateLimited");
        Check.True((await Routes(rt, "fo-2")).Any(r => r.ArgumentsJson.Contains("RefusedSideEffects", StringComparison.Ordinal) && r.Decision == "refused"), "refusal audited");

        // 3. NeverDowngrade mid-job → no switch.
        launcher.Script.Enqueue([limited, "exit=1"]);
        var job3 = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "fo-3", NeverDowngrade = true });
        Check.Equal(JobState.Failed, await job3.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "NeverDowngrade holds after launch too");
        Check.True((await Routes(rt, "fo-3")).Any(r => r.ArgumentsJson.Contains("RefusedNeverDowngrade", StringComparison.Ordinal)), "audited");

        // 4. Both limited in turn → one switch only, then a clear failure.
        string geminiQuota = Fixture(LaunchTests.GeminiQuota);
        launcher.Script.Enqueue([limited, "exit=1"]);
        launcher.Script.Enqueue([geminiQuota, "exit=1"]);
        var job4 = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "fo-4" });
        Check.Equal(JobState.Failed, await job4.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "second limit ends the job");
        var r4 = await Routes(rt, "fo-4");
        Check.True(r4.Count(r => r.ArgumentsJson.Contains("SwitchedAfterRateLimit", StringComparison.Ordinal)) == 1
            && r4.Any(r => r.ArgumentsJson.Contains("RefusedAlreadySwitched", StringComparison.Ordinal)), "exactly one switch, then a refusal");

        // 5. Pre-launch refusal is audited too (no job is issued).
        await rt.Usage.SetCooldownAsync("claude-code", DateTimeOffset.UtcNow.AddHours(2), "limit");
        await rt.Usage.SetCooldownAsync("gemini-cli", DateTimeOffset.UtcNow.AddHours(2), "limit");
        try { await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "fo-5" }); throw new Exception("Assertion failed: started"); }
        catch (ProviderUnavailableException) { }
        Check.True((await Routes(rt, "fo-5")).Any(r => r.ArgumentsJson.Contains("RefusedBothUnavailable", StringComparison.Ordinal)) && !rt.Registry.TryGet("fo-5", out _),
            "both cooling: refusal audited, nothing issued");
    }

    /// <summary>Real Claude capture end to end: an API-key run is killed by the billing guard; when metered billing is allowed it is AuthFailed, not failed over.</summary>
    public static async Task RealAuthFailures(string bridgeDll, string testsDll)
    {
        var launcher = new FakeLauncher(testsDll);
        await using var rt = await Runtime(bridgeDll, launcher);
        string folder = TempDir();
        string claude = Real("claude-2.1.309-invalid-api-key.stream.jsonl");
        var alarms = new List<string>();
        rt.BillingAlarm += (_, source) => { lock (alarms) alarms.Add(source); };

        launcher.Script.Enqueue([claude, "exit=1"]);
        var guarded = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "auth-1" });
        Check.Equal(JobState.Killed, await guarded.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "apiKeySource ANTHROPIC_API_KEY without opt-in → job killed (fail closed)");
        lock (alarms) Check.True(alarms.Contains("ANTHROPIC_API_KEY"), "billing alarm raised from the real init event");

        launcher.Script.Enqueue([claude, "exit=1"]);
        var metered = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "auth-2", AllowMeteredBilling = true });
        Check.Equal(JobState.Failed, await metered.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "invalid key → failed");
        Check.True(metered.Outcome == RunOutcome.AuthFailed && metered.Detail!.Contains("not signed in", StringComparison.Ordinal), "outcome AuthFailed with an actionable message: " + metered.Detail);
        Check.True(await rt.GetCooldownAsync("claude-code") is null && launcher.Headless.Count == 2, "no cooldown, no failover for an auth failure");

        // Gemini without auth (real stderr, exit 41): AuthFailed; usage recorded as unknown, not zero.
        launcher.Script.Enqueue([Fixture(""), "exit=41", "stderr=" + Real("gemini-0.65.0-no-auth.stderr.txt")]);
        var g = await rt.StartJobAsync(JobSpecFactory.Organize(folder, "by type", LaunchMode.Headless) with { JobId = "auth-3" });
        Check.Equal(JobState.Failed, await g.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "gemini no-auth failed");
        Check.Equal(RunOutcome.AuthFailed, g.Outcome, "Gemini exit 41 → AuthFailed");
        var agyArgs = launcher.Headless[2].ArgumentList.ToList();
        Check.True(agyArgs.Contains("--input-format") && agyArgs.Contains("--disable-slash-commands") && !agyArgs.Contains("--add-dir"),
            "headless Antigravity: prompt as a stream-json stdin event, slash commands off, user folder not added to its workspace");
        var totals = await rt.Usage.TotalsAsync("gemini-cli", DateTimeOffset.MinValue);
        Check.True(totals.Runs == 1 && totals.UnknownUsageRuns == 1, "a run without a result is recorded with unknown usage");

        // Result, then abnormal exit.
        launcher.Script.Enqueue([Fixture(StreamTests.SuccessRun), "exit=3"]);
        var abnormal = await rt.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.Headless) with { JobId = "auth-4" });
        Check.Equal(JobState.Failed, await abnormal.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "result then exit 3 is not success");
        Check.True(abnormal.Outcome == RunOutcome.ResultThenAbnormalExit && abnormal.LastResult is { IsError: false }, "distinct outcome; the result is kept");
    }
}

static class OverlayStateTests
{
    /// <summary>
    /// The overlay never shows an uncertain call as failed or stopped: a timed-out call that is still running is
    /// "outcome unknown" (amber); killing the job says new calls are blocked but that call may still finish; when it
    /// finally returns, the row says so.
    /// </summary>
    public static async Task UncertainCallsAreNotShownAsStopped(string bridgeDll)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cockpit-ui-unc", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await using var runtime = await OperationTests.Runtime(bridgeDll);
        var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(1)) { IgnoreCancellation = true, Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        runtime.Tools.Register(probe);
        var vm = new Cockpit.UI.CockpitOverlayViewModel(runtime, new Cockpit.UI.InlineDispatcher());
        var job = await runtime.StartJobAsync(OperationTests.Spec("ui-unc", dir));
        await using (var client = await runtime.ConnectClientAsync(job))
        {
            var r = await client.CallToolAsync(ProbeWriteProvider.Name, new System.Text.Json.Nodes.JsonObject { ["x"] = 1 }, TimeSpan.FromSeconds(20));
            Check.True(r.IsError, "the call timed out");
        }
        var row = vm.Jobs.Single(j => j.JobId == "ui-unc");
        var line = vm.Activity.First(a => a.Tool == ProbeWriteProvider.Name && a.IsUncertain);
        Check.True(!line.IsProblem && line.Phase.Contains("may still complete", StringComparison.Ordinal), "activity: amber 'outcome unknown', not red 'failed': " + line.Phase);
        Check.Equal(1, row.UncertainCalls, "the job row counts the uncertain call");

        runtime.KillJob("ui-unc", "test");
        await Task.Delay(100);
        Check.True(row.StateText.Contains("may still finish in the app", StringComparison.Ordinal), "after kill: says the call may still finish: " + row.StateText);

        probe.Gate.SetResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (row.UncertainCalls != 0 && sw.ElapsedMilliseconds < 10_000) await Task.Delay(20);
        Check.True(row.UncertainCalls == 0 && row.StateText == "killed: access revoked", "the late result clears it: " + row.StateText);
        Check.True(vm.Activity.Any(a => a.Phase == "finished late: done"), "the late completion is shown");
    }
}
