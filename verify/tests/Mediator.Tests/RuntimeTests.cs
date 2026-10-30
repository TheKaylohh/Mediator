using System.Diagnostics;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Runtime;
using Mediator.Connectors.SolidWorks;

namespace Mediator.Tests;

static class RuntimeTests
{
    internal static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static async Task<MediatorRuntime> Runtime(string bridgeDll, IApprovalPrompt prompt, SimulatedSwxBackend swx)
    {
        var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir()),
            Bus = new BusOptions { PipeName = "mediator-rt-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Simulate = true,
            Connectors = TestConnectors.All(swx: swx),
        });
        runtime.Approvals.Target = prompt;
        return runtime;
    }

    sealed class HangingPrompt : IApprovalPrompt
    {
        public readonly TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            Asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ApprovalResponse.Deny;
        }
    }

    static JsonObject Plan(string output, string shaft, string housing) => new()
    {
        ["outputPath"] = output,
        ["components"] = new JsonArray(
            new JsonObject { ["key"] = "shaft", ["partPath"] = shaft, ["fixed"] = true },
            new JsonObject { ["key"] = "housing", ["partPath"] = housing }),
        ["mates"] = new JsonArray(
            new JsonObject { ["key"] = "axis", ["type"] = "Concentric", ["componentA"] = "shaft", ["faceA"] = "c1", ["componentB"] = "housing", ["faceB"] = "c1", ["confidence"] = 0.9, ["reason"] = "equal radius" },
            new JsonObject { ["key"] = "seat", ["type"] = "Coincident", ["componentA"] = "shaft", ["faceA"] = "p1", ["componentB"] = "housing", ["faceB"] = "p1", ["alignment"] = "AntiAligned", ["confidence"] = 0.6 }),
    };

    /// <summary>
    /// Gemini item 4: the composed runtime, a simulated SolidWorks backend and AutoApprovalPrompt, driven through
    /// the real bridge child process exactly as Claude Code would, then the complete audit trail is checked.
    /// </summary>
    public static async Task AssembleThroughBridgeWithAudit(string bridgeDll)
    {
        string parts = TempDir();
        string shaft = Path.Combine(parts, "shaft.sldprt"), housing = Path.Combine(parts, "housing.SLDPRT");
        File.WriteAllText(shaft, "part");
        File.WriteAllText(housing, "part");
        File.WriteAllText(Path.Combine(parts, "~$shaft.sldprt"), "lock file"); // SolidWorks lock files are skipped

        var swx = new SimulatedSwxBackend();
        var auto = new AutoApprovalPrompt();
        await using var runtime = await Runtime(bridgeDll, auto, swx);
        var activity = new List<ToolCallEvent>();
        runtime.ToolCallActivity += e => { lock (activity) activity.Add(e); };

        string output = Path.Combine(parts, "out.SLDASM");
        var job = await runtime.StartJobAsync(SolidWorksConnector.Assemble(parts, LaunchMode.None, output) with { JobId = "asm-it" });
        Check.Equal(JobState.Running, job.State, "job running");
        Check.True(File.Exists(job.Workspace.ClaudeMcpConfigPath), "MCP config written");

        await using (var client = await runtime.ConnectClientAsync(job))
        {
            var tools = await client.ListToolsAsync();
            // V10: an assembly job may build missing parts first, so it sees the whole CAD toolset (and nothing else).
            var expected = SolidWorksConnector.Tools.Append("mediator_operation_status").Order(StringComparer.Ordinal);
            Check.True(tools.OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(expected), "only the CAD tools (+ operation status) are visible: " + string.Join(",", tools));

            var cards = await client.CallToolAsync("swx_part_cards", new JsonObject { ["folder"] = parts }, TimeSpan.FromSeconds(30));
            Check.True(!cards.IsError, "part cards: " + cards.Text);
            Check.Equal(2, cards.Structured!["cards"]!.AsArray().Count, "two parts (lock file skipped)");
            Check.True(!cards.Text.Contains("U0lN", StringComparison.Ordinal), "persist references never reach the model");

            var build = await client.CallToolAsync("swx_build_assembly", Plan(output, shaft, housing), TimeSpan.FromSeconds(30));
            Check.True(!build.IsError, "build: " + build.Text);
            Check.True(File.Exists(output + SimulatedSwxBackend.SimulatedSuffix) && !File.Exists(output), "simulated build wrote only <output>.simulated.json");
            Check.Equal(2, swx.Builds.Single().Mates.Count, "both mates sent to the backend");

            var outside = await client.CallToolAsync("swx_part_cards", new JsonObject { ["folder"] = Path.GetTempPath() }, TimeSpan.FromSeconds(30));
            Check.True(outside.IsError && outside.Text.Contains("outside", StringComparison.Ordinal), "a folder outside the job's roots is refused");

            swx.FailWith = m => m == "part_cards" ? new SwxWorkerException(SwxWorkerException.ModalDialogBlocking, "SolidWorks is waiting on a dialog: \"Rebuild?\"",
                new JsonObject { ["title"] = "SOLIDWORKS", ["buttons"] = new JsonArray("Yes", "No") }) : null;
            var blocked = await client.CallToolAsync("swx_part_cards", new JsonObject { ["folder"] = parts }, TimeSpan.FromSeconds(30));
            Check.True(blocked.IsError && blocked.Text.Contains("Do not retry", StringComparison.Ordinal), "MODAL_DIALOG_BLOCKING tells the model to stop");
            Check.Equal("MODAL_DIALOG_BLOCKING", (string?)blocked.Structured?["code"], "structured code reaches the client");
            Check.Equal("No", (string?)blocked.Structured?["dialog"]?["buttons"]?[1], "dialog buttons reach the client");
            swx.FailWith = null;
        }

        await runtime.EndJobAsync(job.JobId);
        Check.Equal(JobState.Ended, await job.Completion.WaitAsync(TimeSpan.FromSeconds(5)), "job ended");
        Check.True(!runtime.Registry.TryGet(job.JobId, out _), "grant revoked at the end of the job");
        Check.Equal(1, auto.Asked, "one approval asked (the build); read-only calls ran automatically");

        var rows = await runtime.Audit.ListAsync(job.JobId);
        string Trail(IEnumerable<AuditLog.AuditRow> r) => string.Join(" | ", r.Select(x => $"{x.Tool}:{x.Decision}:{x.Outcome}"));
        Check.Equal("swx_part_cards:auto:ok | swx_build_assembly:approved:ok | swx_part_cards:auto:tool_error | swx_part_cards:auto:tool_error",
            Trail(rows), "complete audit trail in order");
        Check.True(rows.All(r => r.JobId == job.JobId && r.CallId.Length > 0), "every row tied to the job and the call");
        Check.True(rows[1].ArgumentsJson.Contains("out.SLDASM", StringComparison.Ordinal) && rows[1].Risk == "Write", "build arguments and risk recorded");
        Check.True(rows[3].Detail!.Contains("MODAL_DIALOG_BLOCKING", StringComparison.Ordinal), "dialog error detail audited");
        lock (activity)
            Check.True(activity.Any(a => a.Tool == "swx_build_assembly" && a.Phase == "awaiting_approval") && activity.Count(a => a.Phase == "ok") == 2,
                "activity forwarded for the overlay");
    }

    /// <summary>Kill switch while a call waits for approval: the call ends, the job is Killed, and the kill is audited.</summary>
    public static async Task KillDuringApproval(string bridgeDll)
    {
        string parts = TempDir();
        string shaft = Path.Combine(parts, "a.sldprt"), housing = Path.Combine(parts, "b.sldprt");
        File.WriteAllText(shaft, "p");
        File.WriteAllText(housing, "p");
        var prompt = new HangingPrompt();
        await using var runtime = await Runtime(bridgeDll, prompt, new SimulatedSwxBackend());
        var job = await runtime.StartJobAsync(SolidWorksConnector.Assemble(parts, LaunchMode.None, Path.Combine(parts, "k.SLDASM")) with { JobId = "kill-it" });
        await using var client = await runtime.ConnectClientAsync(job);
        await client.CallToolAsync("swx_part_cards", new JsonObject { ["paths"] = new JsonArray(shaft, housing) }, TimeSpan.FromSeconds(30));

        var pending = client.CallToolAsync("swx_build_assembly", Plan(Path.Combine(parts, "k.SLDASM"), shaft, housing), TimeSpan.FromSeconds(30));
        await prompt.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        Check.True(runtime.KillJob(job.JobId, "test kill"), "kill accepted");
        Check.True(sw.ElapsedMilliseconds < 100, $"kill returned in {sw.ElapsedMilliseconds} ms");
        Check.Equal(JobState.Killed, job.State, "job state Killed immediately");

        try
        {
            var r = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check.True(r.IsError, "pending call did not succeed: " + r.Text);
        }
        catch (McpClientException) { /* the bus closed the connection: also an acceptable end for the call */ }

        var rows = await runtime.Audit.ListAsync(job.JobId);
        Check.True(rows.Any(r => r.Tool == "mediator/kill" && r.Decision == "killed" && r.Detail == "test kill"), "kill audited");
        Check.True(rows.Any(r => r.Tool == "swx_build_assembly" && r.Decision == "cancelled"), "the waiting build is audited as cancelled");
        Check.True(!rows.Any(r => r.Tool == "swx_build_assembly" && r.Outcome == "ok"), "nothing was built");
        Check.True(!runtime.Leases.Snapshot().ContainsKey(SwxToolProvider.Lease), "SolidWorks lease released");
    }

    public static async Task ConsolePrompt()
    {
        var preview = JsonNode.Parse("""
            {"kind":"swx_mates","title":"Build out.SLDASM","summary":"4 mates","selectable":true,"rows":[
              {"key":"m1","primary":"shaft c1 ⟂ housing c1","confidence":0.9},{"key":"m2","primary":"m2"},
              {"key":"m3","primary":"m3","detail":"why"},{"key":"m4","primary":"m4"}]}
            """);
        var request = new ApprovalRequest("job", "call", "swx_build_assembly", ToolRisk.Write, "Build", default, preview);

        var output = new StringWriter();
        var select = await new ConsoleApprovalPrompt(new StringReader("x\ns\n9\ns\n1 3-4\n"), output).AskAsync(request, CancellationToken.None);
        Check.True(select.Approved && select.ApprovedRows!.OrderBy(k => k).SequenceEqual(new[] { "m1", "m3", "m4" }), "select keeps rows 1, 3, 4");
        Check.True(output.ToString().Contains("[A]pprove all / [S]elect / [D]eny / [K]ill job", StringComparison.Ordinal), "menu shown");
        Check.True(output.ToString().Contains("Use numbers from 1 to 4.", StringComparison.Ordinal), "out-of-range selection re-asked");
        Check.True(output.ToString().Contains("[0.90]", StringComparison.Ordinal), "confidence shown");

        var kill = await new ConsoleApprovalPrompt(new StringReader("K\n"), new StringWriter()).AskAsync(request, CancellationToken.None);
        Check.True(kill.KillJob && !kill.Approved, "K kills the job");
        var closed = await new ConsoleApprovalPrompt(new StringReader(""), new StringWriter()).AskAsync(request, CancellationToken.None);
        Check.True(!closed.Approved, "closed input denies");
        var notSelectable = request with { Preview = JsonNode.Parse("""{"title":"t","selectable":false,"rows":[{"key":"a","primary":"a"}]}""") };
        var sOnPlain = await new ConsoleApprovalPrompt(new StringReader("s\nd\n"), new StringWriter()).AskAsync(notSelectable, CancellationToken.None);
        Check.True(!sOnPlain.Approved, "S is not offered for a non-selectable preview");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var blockedReader = new BlockingReader();
        var prompt = new ConsoleApprovalPrompt(blockedReader, new StringWriter());
        bool cancelled = false;
        try { await prompt.AskAsync(request, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check.True(cancelled, "approval timeout ends the console wait");
        blockedReader.Lines.Add("a");
        var reused = await prompt.AskAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(reused.Approved, "the abandoned read is reused, so the user's next line answers the next approval");

        Check.True(ConsoleApprovalPrompt.ParseSelection("2-1", 4) is null && ConsoleApprovalPrompt.ParseSelection("0", 4) is null
            && ConsoleApprovalPrompt.ParseSelection("1,,2", 4)!.Count == 2, "selection parsing");
    }

    sealed class BlockingReader : TextReader
    {
        public readonly System.Collections.Concurrent.BlockingCollection<string> Lines = new();
        public override Task<string?> ReadLineAsync() => Task.Run(() => (string?)Lines.Take());
    }

    /// <summary>The console host as a real process: doctor's bridge self-test, then organize --rules and rollback.</summary>
    public static async Task CliHostProcess(string bridgeDll, string cliDll)
    {
        string data = TempDir();
        async Task<(int Code, string Out)> Mediator(params string[] args)
        {
            var psi = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in new[] { cliDll }.Concat(args).Concat(["--data-dir", data, "--bridge", bridgeDll])) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            return (p.ExitCode, await stdout + "\n[stderr]\n" + await stderr);
        }

        var (code, doctor) = await Mediator("doctor", "--simulate", "--json");
        var checks = JsonNode.Parse(doctor[..doctor.IndexOf("\n[stderr]", StringComparison.Ordinal)])!.AsArray();
        string Status(string name) => (string)checks.First(c => (string)c!["name"]! == name)!["status"]!;
        Check.Equal("ok", Status("bridge self-test"), "doctor: bridge self-test passes: " + doctor);
        Check.Equal("ok", Status("SolidWorks"), "doctor: simulated SolidWorks health");

        string folder = TempDir();
        File.WriteAllText(Path.Combine(folder, "scan.pdf"), "pdf");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "txt");
        string rules = Path.Combine(TempDir(), "rules.json");
        File.WriteAllText(rules, """{ "rules": [ { "extensions": [".pdf"], "destination": "Documents/{ext}" } ] }""");

        var (orgCode, org) = await Mediator("organize", folder, "--rules", rules, "--yes", "--job-id", "cli-org");
        Check.Equal(0, orgCode, "organize exit code: " + org);
        Check.True(File.Exists(Path.Combine(folder, "Documents", "pdf", "scan.pdf")) && File.Exists(Path.Combine(folder, "notes.txt")), "pdf moved, txt stayed");

        var (noYes, _) = await Mediator("organize", folder, "--rules", rules, "--job-id", "cli-org-2");
        Check.Equal(0, noYes, "already organized: nothing to move");

        var (rbCode, rb) = await Mediator("rollback", "cli-org", "--yes");
        Check.Equal(0, rbCode, "rollback exit code: " + rb);
        Check.True(File.Exists(Path.Combine(folder, "scan.pdf")) && !Directory.Exists(Path.Combine(folder, "Documents")), "rollback restored the file and removed created folders");

        var (usage, _) = await Mediator("organize", folder, "--frobnicate");
        Check.Equal(2, usage, "unknown option is a usage error");
    }
}
