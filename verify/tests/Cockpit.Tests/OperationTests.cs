using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Files;
using Cockpit.Core.Runtime;
using Cockpit.Core.Security;

namespace Cockpit.Tests;

/// <summary>A Write tool whose behaviour each test controls: returns at once, or blocks (optionally ignoring cancellation, like a COM call).</summary>
sealed class ProbeWriteProvider(TimeSpan timeout) : IToolProvider
{
    public const string Name = "ops_probe_write";
    public int Executions;
    public volatile bool IgnoreCancellation;
    public TaskCompletionSource? Gate; // when set, ExecuteAsync waits for it
    public string? MarkerFile;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(Name, "test write tool", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"integer"},"tag":{"type":"string"}},"additionalProperties":false}""")!,
            ToolRisk.Write, null, timeout),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        int n = Interlocked.Increment(ref Executions);
        if (MarkerFile is not null) File.WriteAllText(MarkerFile, "executing");
        if (Gate is { } g) await (IgnoreCancellation ? g.Task : g.Task.WaitAsync(ct));
        return ToolResult.Ok($"side effect #{n} for {call.Arguments.GetRawText()}", new JsonObject { ["n"] = n });
    }
}

static class OperationTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-ops", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static async Task<CockpitRuntime> Runtime(string bridgeDll, string? dataDir = null) => await CockpitRuntime.CreateAsync(new RuntimeOptions
    {
        Paths = new CockpitPaths(dataDir ?? TempDir("data")),
        Bus = new BusOptions { PipeName = "cockpit-ops-" + Guid.NewGuid().ToString("N")[..10] },
        Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
        SwxBackend = new Cockpit.Core.Swx.SimulatedSwxBackend(),
        MediaProbe = new FakeMediaProbe(new()),
        Python = new FakePythonRunner(FakeCad.Run, available: false),
        Blender = new FakeBlender(),
    });

    public static JobSpec Spec(string jobId, string dir) => new()
    {
        Kind = "ops", Title = "ops test", Tools = [ProbeWriteProvider.Name], Roots = [dir], WorkingDirectory = dir, Launch = LaunchMode.None,
        Lifetime = TimeSpan.FromMinutes(10), JobId = jobId, ToolModes = new Dictionary<string, ApprovalMode> { [ProbeWriteProvider.Name] = ApprovalMode.Auto },
    };

    static string? OpId(McpToolCallResult r) => (string?)r.Structured?["operation"]?["id"];

    public static async Task StateMachineRules()
    {
        await using var store = await OperationStore.OpenAsync(Path.Combine(TempDir("sm"), "ops.db"));
        var args = JsonDocument.Parse("""{"b":1,"a":[1,2,{"y":true,"x":null}]}""").RootElement;
        var reordered = JsonDocument.Parse("""{ "a": [1, 2, {"x": null, "y": true}], "b": 1.0 }""").RootElement;
        Check.Equal(OperationStore.RequestKey("j", "t", args), OperationStore.RequestKey("j", "t", reordered), "canonical key ignores key order, spacing and 1 vs 1.0");
        Check.True(OperationStore.RequestKey("j", "t", args) != OperationStore.RequestKey("j2", "t", args), "key is per job");

        var (op, _) = await store.BeginAsync("j", "t", "k1");
        async Task Illegal(OperationState to, string what)
        {
            try { await store.TransitionAsync(op!.OperationId, to); throw new Exception("Assertion failed: allowed " + what); }
            catch (OperationStateException) { }
        }
        await Illegal(OperationState.Executing, "pending → executing (skipping approval)");
        await Illegal(OperationState.Committed, "pending → committed");
        foreach (var s in new[] { OperationState.Validated, OperationState.Approved, OperationState.Executing, OperationState.Committed })
            await store.TransitionAsync(op!.OperationId, s);
        await Illegal(OperationState.Executing, "committed → executing (replay)");
        await Illegal(OperationState.Failed, "committed → failed");
        var (again, existing) = await store.BeginAsync("j", "t", "k1");
        Check.True(again is null && existing!.State == OperationState.Committed, "a committed request cannot start a new attempt");
        var history = await store.HistoryAsync(op!.OperationId);
        Check.Equal("pending,validated,approved,executing,committed", string.Join(",", history.Select(h => h.To)), "every transition recorded");
    }

    /// <summary>Same call after commit returns the stored result; concurrent duplicates run once; different arguments run.</summary>
    public static async Task ReplayAndDuplicates(string bridgeDll)
    {
        string dir = TempDir("work");
        await using var runtime = await Runtime(bridgeDll);
        var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(30));
        runtime.Tools.Register(probe);
        var job = await runtime.StartJobAsync(Spec("ops-replay", dir));
        await using var client = await runtime.ConnectClientAsync(job);

        var first = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 1, ["tag"] = "a" }, TimeSpan.FromSeconds(20));
        var replay = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["tag"] = "a", ["x"] = 1 }, TimeSpan.FromSeconds(20));
        Check.True(!first.IsError && !replay.IsError && replay.Text.Contains("already completed", StringComparison.Ordinal) && replay.Text.Contains("side effect #1", StringComparison.Ordinal),
            "replay after commit returns the stored result: " + replay.Text);
        Check.Equal(1, probe.Executions, "the side effect happened once");

        // Two identical calls at the same time: one runs, the other is refused as in progress.
        probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var c1 = client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 2 }, TimeSpan.FromSeconds(20));
        await WaitUntil(() => probe.Executions == 2, "first concurrent call started");
        var c2 = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 2 }, TimeSpan.FromSeconds(20));
        probe.Gate.SetResult();
        var r1 = await c1;
        Check.True(!r1.IsError && c2.IsError && c2.Text.Contains("is executing", StringComparison.Ordinal), "concurrent duplicate refused: " + c2.Text);
        Check.Equal(2, probe.Executions, "no duplicate side effect");
        probe.Gate = null;

        var other = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 3 }, TimeSpan.FromSeconds(20));
        Check.True(!other.IsError && probe.Executions == 3, "different arguments are a different operation");
        var status = await client.CallToolAsync(OperationsToolProvider.StatusTool, new JsonObject(), TimeSpan.FromSeconds(20));
        Check.True(!status.IsError && status.Structured!["operations"]!.AsArray().Count(o => (string?)o!["state"] == "committed") == 3, "operation status lists 3 committed");
        var audit = await runtime.Audit.ListAsync("ops-replay");
        Check.True(audit.Count(a => a.Decision == "replayed") == 1 && audit.Count(a => a.Detail?.Contains("duplicate of", StringComparison.Ordinal) == true) == 1, "replay and refusal audited");
    }

    /// <summary>A tool that ignores cancellation (like a blocked COM call) outlives its timeout: outcome unknown, retry refused, late result recorded, then replayed.</summary>
    public static async Task TimeoutDuringExecution(string bridgeDll)
    {
        string dir = TempDir("work");
        await using var runtime = await Runtime(bridgeDll);
        var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(1)) { IgnoreCancellation = true, Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        runtime.Tools.Register(probe);
        var job = await runtime.StartJobAsync(Spec("ops-timeout", dir));
        await using var client = await runtime.ConnectClientAsync(job);

        var r = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 1 }, TimeSpan.FromSeconds(20));
        Check.True(r.IsError && r.Text.Contains("outcome is unknown", StringComparison.Ordinal), "timeout reported as outcome unknown, not as failure: " + r.Text);
        string op = OpId(r)!;
        Check.Equal(OperationState.OutcomeUnknown, (await runtime.Operations.GetAsync(op))!.State, "state outcome_unknown");
        var retry = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 1 }, TimeSpan.FromSeconds(20));
        Check.True(retry.IsError && retry.Text.Contains("outcome_unknown", StringComparison.Ordinal) && probe.Executions == 1, "retry refused while the outcome is unknown: " + retry.Text);

        probe.Gate.SetResult(); // the "COM call" finally returns
        await WaitUntil(async () => (await runtime.Operations.GetAsync(op))!.State == OperationState.Committed, "late result recorded as committed");
        var after = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 1 }, TimeSpan.FromSeconds(20));
        Check.True(!after.IsError && after.Text.Contains("already completed", StringComparison.Ordinal) && probe.Executions == 1, "after the late commit the call replays; still one side effect");
        var audit = await runtime.Audit.ListAsync("ops-timeout");
        Check.True(audit.Any(a => a.Outcome == "outcome_unknown") && audit.Any(a => a.Decision == "late" && a.Outcome == "late_ok"), "audit shows unknown, then the late result");
    }

    /// <summary>The bridge is killed mid-call: cancellation is requested, not assumed; the side effect completes once; reconnect replays.</summary>
    public static async Task KillBridgeMidCall(string bridgeDll)
    {
        string dir = TempDir("work");
        await using var runtime = await Runtime(bridgeDll);
        var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(60)) { IgnoreCancellation = true, Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        runtime.Tools.Register(probe);
        var job = await runtime.StartJobAsync(Spec("ops-killbridge", dir));
        var client = await runtime.ConnectClientAsync(job);
        var pending = client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 7 }, TimeSpan.FromSeconds(30));
        await WaitUntil(() => probe.Executions == 1, "call is executing");
        Process.GetProcessById(client.ProcessId).Kill(entireProcessTree: true);
        try { await pending; } catch (Exception) { /* the client side of a killed bridge fails; expected */ }
        await client.DisposeAsync();

        var op = (await runtime.Operations.ListAsync("ops-killbridge")).Single();
        await WaitUntil(async () => (await runtime.Operations.GetAsync(op.OperationId))!.State is OperationState.CancellationRequested or OperationState.OutcomeUnknown, "cancellation requested");
        Check.True((await runtime.Operations.GetAsync(op.OperationId))!.State != OperationState.Cancelled, "never claims the running call was cancelled");

        probe.Gate.SetResult();
        await WaitUntil(async () => (await runtime.Operations.GetAsync(op.OperationId))!.State == OperationState.Committed, "the side effect's real outcome is recorded");
        await using var again = await runtime.ConnectClientAsync(job);
        var r = await again.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 7 }, TimeSpan.FromSeconds(20));
        Check.True(!r.IsError && r.Text.Contains("already completed", StringComparison.Ordinal) && probe.Executions == 1, "reconnected client gets the stored result; one side effect: " + r.Text);
        var history = await runtime.Operations.HistoryAsync(op.OperationId);
        Check.True(history.Select(h => h.To).Contains("cancellation_requested"), "history shows the cancellation request: " + string.Join(",", history.Select(h => h.To)));
    }

    /// <summary>Child-process Core (Program.cs --ops-core): starts a job and a call that blocks after writing a marker.</summary>
    public static async Task<int> CoreChildAsync(string bridgeDll, string dataDir, string marker, string workDir)
    {
        var runtime = await Runtime(bridgeDll, dataDir);
        var probe = new ProbeWriteProvider(TimeSpan.FromMinutes(5)) { Gate = new TaskCompletionSource(), MarkerFile = marker, IgnoreCancellation = true };
        runtime.Tools.Register(probe);
        var job = await runtime.StartJobAsync(Spec("ops-crash", workDir));
        var client = await runtime.ConnectClientAsync(job);
        await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 42 }, TimeSpan.FromMinutes(5));
        return 0; // never reached: the parent kills this process
    }

    /// <summary>
    /// The Core process is killed (SIGKILL, whole tree) while a call executes. The next Core reconciles it to
    /// recovery_required (no reconciler for this tool), refuses the identical call, and accepts the user's verdict.
    /// A files_execute_batch interrupted the same way is settled from the journal instead.
    /// </summary>
    public static async Task KillCoreAndRestart(string bridgeDll, string testsDll)
    {
        string data = TempDir("crash-data"), work = TempDir("crash-work"), marker = Path.Combine(work, "..", Path.GetFileName(work) + ".marker");
        marker = Path.GetFullPath(marker);
        var psi = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { testsDll, "--ops-core", bridgeDll, data, marker, work }) psi.ArgumentList.Add(a);
        using var child = Process.Start(psi)!;
        _ = child.StandardOutput.ReadToEndAsync();
        _ = child.StandardError.ReadToEndAsync();
        var sw = Stopwatch.StartNew();
        while (!File.Exists(marker) && sw.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(50);
        Check.True(File.Exists(marker), "the child Core started executing the call");
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();

        // The store as the dead Core left it: executing.
        await using (var raw = await OperationStore.OpenAsync(Path.Combine(data, "operations.db")))
        {
            var left = (await raw.ListAsync("ops-crash")).Single();
            Check.Equal(OperationState.Executing, left.State, "the crash left the operation executing");
        }

        await using var runtime = await Runtime(bridgeDll, data);
        Check.True(runtime.Recovery.NeedsUser == 1, $"restart reconciliation flagged it for the user ({runtime.Recovery})");
        var op = (await runtime.Operations.ListAsync("ops-crash")).Single();
        Check.Equal(OperationState.RecoveryRequired, op.State, "recovery_required after restart");
        var history = await runtime.Operations.HistoryAsync(op.OperationId);
        Check.Equal("pending,validated,approved,executing,outcome_unknown,recovery_required", string.Join(",", history.Select(h => h.To)), "full history survives the crash");

        var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(30));
        runtime.Tools.Register(probe);
        var job = await runtime.StartJobAsync(Spec("ops-crash", work)); // same job id as before the crash
        await using var client = await runtime.ConnectClientAsync(job);
        var blind = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 42 }, TimeSpan.FromSeconds(20));
        Check.True(blind.IsError && blind.Text.Contains("recovery_required", StringComparison.Ordinal) && probe.Executions == 0, "no blind replay after the crash: " + blind.Text);

        await runtime.Operations.ResolveAsync(op.OperationId, tookEffect: true, "checked the output by hand");
        var resolved = await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 42 }, TimeSpan.FromSeconds(20));
        Check.True(resolved.IsError && resolved.Text.Contains("committed", StringComparison.Ordinal) && probe.Executions == 0, "after the user's verdict it is treated as done, still not re-run");

        // Files: an execute op left 'executing' is settled from the journal by the provider reconciler.
        string root = TempDir("files");
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        var plan = await runtime.Journal.PlanAsync("ops-files", [new(Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt"))], new PathAuthorizer([root]), null, CancellationToken.None);
        await runtime.Journal.ExecuteAsync(plan.BatchId, "ops-files", null, CancellationToken.None);
        string args = new JsonObject { ["batchId"] = plan.BatchId }.ToJsonString();
        var (fop, _) = await runtime.Operations.BeginAsync("ops-files", FilesToolProvider.Execute, "k-files", args);
        foreach (var s in new[] { OperationState.Validated, OperationState.Approved, OperationState.Executing }) await runtime.Operations.TransitionAsync(fop!.OperationId, s);
        var verdict = await runtime.Operations.ReconcileAfterRestartAsync(o =>
            runtime.Tools.TryGet(o.Tool, out var p, out _) && p is IOperationReconciler r ? r.ReconcileAsync(o) : Task.FromResult<(OperationState, string)?>(null));
        Check.True(verdict.Settled == 1 && (await runtime.Operations.GetAsync(fop!.OperationId))!.State == OperationState.Committed, "journal reconciler settled the files op as committed");
    }

    static async Task WaitUntil(Func<bool> condition, string what) => await WaitUntil(() => Task.FromResult(condition()), what);

    static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new Exception("Timed out waiting: " + what);
    }
}
