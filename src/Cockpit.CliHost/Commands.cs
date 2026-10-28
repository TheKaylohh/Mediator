using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Files;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;
using Cockpit.Core.Swx;
using Cockpit.Core.Tools;

namespace Cockpit.CliHost;

public static class Commands
{
    private static readonly string[] RollbackableStates = ["committed", "partially_committed", "partially_rolled_back"];

    public static int Help()
    {
        Console.WriteLine("""
            AI Cockpit, console host

              cockpit doctor   [--check-swx] [--json]
              cockpit assemble <folder> [--headless|--interactive] [--output <file.SLDASM>] [--model <m>] [--max-turns <n>] [--dry-run]
              cockpit organize <folder> --rules <rules.json> [--recursive]
              cockpit organize <folder> --criteria "<what you want>" [--headless|--interactive]
              cockpit rollback <jobId> [--batch <batchId>] [--yes]
              cockpit operations [--unresolved] [--job-id <id>]              (side-effecting calls and their state)
              cockpit operations resolve <operationId> --done|--not-done --note "<what you checked>"
              cockpit support-bundle [--output <file.zip>]                   (redacted diagnostics to share)
              cockpit cad      <folder> --request "front suspension: hub, disc, spring"   (build123d → STEP → SolidWorks)
              cockpit resolve  <folder> --request "60 s teaser, music under"            (FCPXML + EDL for DaVinci Resolve)
              cockpit blender  <folder> --request "red car through a neon city"        (scene .blend + preview frames)
              cockpit tutor    <folder> --request "chapter 3, pages 41-58"              (study pack + exact matrix checks)
              cockpit matrix   "[[2,1,1],[1,2,1],[1,1,2]]" [--b "<matrix>"]            (exact det/inverse/RREF/eigenvalues; no AI)

            Common options
              --simulate         use a simulated SolidWorks (no COM; builds write <output>.simulated.json)
              --yes              approve Write-risk tool calls without asking (Destructive is still denied)
              --allow-metered    let this job's CLI keep API-key variables (pay-per-token). Off by default.
              --provider claude|agy      run on this subscription first (default: by task; failover when one is at its limit)
              --keep-subscription        never fail over: refuse if the chosen subscription is at its limit
              --data-dir <dir>   state folder (default %LOCALAPPDATA%\AiCockpit, or COCKPIT_DATA_DIR)
              --bridge <path>    cockpit-mcp-bridge (.exe or .dll); --worker <path> cockpit-swx-worker; --claude <path> claude

            Approvals appear in this window: [A]pprove all / [S]elect / [D]eny / [K]ill job. Ctrl+C kills the running job.
            """);
        return 0;
    }

    public static int Usage(string message)
    {
        Console.Error.WriteLine("cockpit: " + message);
        Console.Error.WriteLine("Run `cockpit help` for usage.");
        return 2;
    }

    // ── Composition ────────────────────────────────────────────────────────────────────────────────────

    private static RuntimeOptions Options(CliArgs cli)
    {
        var paths = cli.Get("data-dir") is { } dir ? new CockpitPaths(Path.GetFullPath(dir)) : CockpitPaths.Default();
        ComponentCommand? Locate(string name, string? path) =>
            path is null ? null : ComponentLocator.Find(name, path) ?? throw new UsageException($"{path} not found.");
        return new RuntimeOptions
        {
            Paths = paths,
            // A private pipe per CLI process, so `cockpit ...` works while the overlay owns \\.\pipe\ai-cockpit-bus.
            // Each job's MCP config carries the endpoint, so nothing else needs to know the name.
            Bus = new BusOptions { PipeName = string.Create(CultureInfo.InvariantCulture, $"ai-cockpit-cli-{Environment.ProcessId}") },
            Bridge = Locate(ComponentLocator.Bridge, cli.Get("bridge")),
            SwxWorker = Locate(ComponentLocator.SwxWorker, cli.Get("worker")),
            ClaudeExecutable = cli.Get("claude"),
            GeminiExecutable = cli.Get("agy"),
            SwxBackend = cli.Has("simulate") ? new SimulatedSwxBackend() : null,
            Log = new StderrLog("cockpit"),
        };
    }

    private static async Task<CockpitRuntime> StartRuntimeAsync(CliArgs cli)
    {
        var runtime = await CockpitRuntime.CreateAsync(Options(cli));
        if (cli.Has("yes"))
        {
            runtime.Approvals.Target = new AutoApprovalPrompt(ToolRisk.Write);
            Console.WriteLine("--yes: Write-risk calls are approved automatically and audited; Destructive calls are denied.");
        }
        else
        {
            runtime.Approvals.Target = new ConsoleApprovalPrompt();
        }
        runtime.ToolCallActivity += e => Console.WriteLine($"  · {e.Tool} {e.Phase}{(e.Detail is null ? "" : ": " + Trim(e.Detail, 160))}");
        runtime.StreamEvent += (_, e) =>
        {
            switch (e)
            {
                case AssistantTextEvent t: Console.WriteLine("  claude: " + Trim(t.Text.ReplaceLineEndings(" "), 400)); break;
                case SessionInitEvent init:
                    Console.WriteLine($"  session {init.SessionId} · model {init.Model} · auth {(init.UsesApiKey ? init.ApiKeySource : "subscription")}");
                    foreach (var s in init.McpServers.Where(s => s.Status != "connected")) Console.WriteLine($"  ! MCP server {s.Name}: {s.Status}");
                    break;
                case ParseErrorEvent p: Console.Error.WriteLine("  ! unparsed CLI output: " + Trim(p.Line, 160)); break;
            }
        };
        runtime.BillingAlarm += (job, source) =>
            Console.Error.WriteLine($"  !! job {job} is authenticating with {source} (pay-per-token), not your subscription.");
        return runtime;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static async Task<int> RunModelJobAsync(CliArgs cli, JobSpec spec)
    {
        spec = spec with
        {
            Model = cli.Get("model") ?? spec.Model,
            MaxTurns = cli.GetInt("max-turns", spec.MaxTurns),
            AllowMeteredBilling = cli.Has("allow-metered"),
            NeverDowngrade = cli.Has("keep-subscription"),
            Provider = cli.Get("provider") switch
            {
                null => spec.Provider,
                "claude" => AiProvider.ClaudeCode,
                "agy" or "antigravity" or "gemini" => AiProvider.GeminiCli,
                var other => throw new UsageException($"--provider must be claude or agy, not '{other}'."),
            },
        };
        await using var runtime = await StartRuntimeAsync(cli);

        if (cli.Has("dry-run"))
        {
            RouteDecision route;
            try { route = await runtime.RouteAsync(spec.Category, spec.Provider, spec.NeverDowngrade); }
            catch (ProviderUnavailableException ex) { Console.Error.WriteLine("cockpit: " + ex.Message); return 1; }
            var dry = await runtime.StartJobAsync(spec with { Launch = LaunchMode.None });
            var psi = route.Provider == AiProvider.GeminiCli
                ? new GeminiLaunchBuilder(cli.Get("agy") ?? GeminiLaunchBuilder.Locate() ?? "agy").Headless(dry.Workspace, spec.Model, spec.AllowMeteredBilling)
                : new CliLaunchBuilder(cli.Get("claude") ?? CliLaunchBuilder.ResolveOnPath("claude") ?? "claude", TimeSpan.FromMinutes(21))
                    .ClaudeHeadless(dry.Workspace, spec.WorkingDirectory, spec.MaxTurns, spec.Model, spec.AllowMeteredBilling);
            Console.WriteLine($"Job {dry.JobId} (dry run; nothing launched)\nSubscription: {ProviderRouter.DisplayName(route.Provider)}{(route.Notice is null ? "" : " — " + route.Notice)}\nTools: {string.Join(", ", spec.Tools)}\nRoots: {string.Join(", ", dry.Grant.FileRoots)}");
            Console.WriteLine($"Command: {psi.FileName} {string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a))}");
            Console.WriteLine($"Working folder: {psi.WorkingDirectory}");
            Console.WriteLine($"Scrubbed: {string.Join(", ", (route.Provider == AiProvider.GeminiCli ? GeminiLaunchBuilder.VariablesToScrub(spec.AllowMeteredBilling) : CliLaunchBuilder.VariablesToScrub(spec.AllowMeteredBilling)))}");
            Console.WriteLine($"MCP config ({dry.Workspace.ClaudeMcpConfigPath}):\n{File.ReadAllText(dry.Workspace.ClaudeMcpConfigPath)}");
            Console.WriteLine($"Prompt:\n{spec.Prompt}");
            await runtime.EndJobAsync(dry.JobId);
            return 0;
        }

        RunningJob? job = null;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            if (job is not null) runtime.KillJob(job.JobId, "Ctrl+C");
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            runtime.Notice += (_, message) => Console.WriteLine("  » " + message);
            job = await runtime.StartJobAsync(spec);
            Console.WriteLine($"Job {job.JobId}: {spec.Title} ({spec.Launch}, {(job.Provider is { } p ? ProviderRouter.DisplayName(p) : "no CLI")})");
            if (spec.Launch == LaunchMode.Interactive)
                Console.WriteLine("Claude Code opened in a new terminal tab. Approvals appear HERE. Ctrl+C ends the job.");
            var state = await job.Completion;
            Console.WriteLine($"Job {job.JobId} {state.ToString().ToLowerInvariant()}{(job.Detail is null ? "" : ": " + job.Detail)}");
            if (job.LastResult is { } r)
            {
                string cost = r.TotalCostUsd is double c
                    ? string.Create(CultureInfo.InvariantCulture, $", API-equivalent ${c:0.000} (not charged on a subscription)") : "";
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Usage: {r.NumTurns?.ToString(CultureInfo.InvariantCulture) ?? "?"} turns, {r.Usage?.Describe() ?? "token usage not reported by the CLI (unknown)"}{cost}"));
            }
            Console.WriteLine($"Undo file changes with: cockpit rollback {job.JobId}");
            return state switch { JobState.Succeeded or JobState.Ended => 0, JobState.Killed => 130, _ => 1 };
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("cockpit: " + ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    // ── assemble ──────────────────────────────────────────────────────────────────────────────────────

    public static Task<int> AssembleAsync(CliArgs cli)
    {
        string folder = Path.GetFullPath(cli.RequirePositional(0, "<folder>"));
        if (!Directory.Exists(folder)) throw new UsageException($"{folder} is not a folder.");
        if (cli.Has("headless") && cli.Has("interactive")) throw new UsageException("Choose --headless or --interactive, not both.");
        var launch = cli.Has("interactive") ? LaunchMode.Interactive : LaunchMode.Headless;
        return RunModelJobAsync(cli, JobSpecFactory.Assemble(folder, launch, cli.Get("output")));
    }

    // ── cad / resolve / blender / tutor ──────────────────────────────────────────────────────────────

    public static Task<int> TaskAsync(CliArgs cli)
    {
        string folder = Path.GetFullPath(cli.RequirePositional(0, "<folder>"));
        if (!Directory.Exists(folder)) throw new UsageException($"{folder} is not a folder.");
        string request = cli.Get("request") ?? throw new UsageException("Say what you want with --request \"...\".");
        if (cli.Has("headless") && cli.Has("interactive")) throw new UsageException("Choose --headless or --interactive, not both.");
        var launch = cli.Has("interactive") ? LaunchMode.Interactive : LaunchMode.Headless;
        var spec = cli.Command switch
        {
            "cad" => JobSpecFactory.CodeCad(folder, request, launch),
            "resolve" => JobSpecFactory.ResolveEdit(folder, request, launch),
            "blender" => JobSpecFactory.BlenderScene(folder, request, launch),
            _ => JobSpecFactory.Tutor(folder, request, launch),
        };
        return RunModelJobAsync(cli, spec);
    }

    // ── matrix (deterministic) ───────────────────────────────────────────────────────────────────────

    public static int Matrix(CliArgs cli)
    {
        string a = cli.RequirePositional(0, "a matrix, e.g. \"[[2,1],[1,2]]\"");
        try
        {
            using var docA = JsonDocument.Parse(a);
            var A = Cockpit.Core.Tutor.ExactMatrix.From(docA.RootElement);
            Console.WriteLine($"A is {A.Rows}×{A.Cols}, rank {A.Rank}");
            var (rref, pivots, det) = A.Reduce();
            if (det is { } d) Console.WriteLine($"det(A) = {d}");
            Console.WriteLine("rref(A) = " + rref.ToJson().ToJsonString());
            if (A.IsSquare)
            {
                Console.WriteLine(A.Inverse() is { } inv ? "A⁻¹ = " + inv.ToJson().ToJsonString() : "A is singular (no inverse).");
                if (A.Rows <= 8)
                {
                    var cp = A.CharacteristicPolynomial();
                    Console.WriteLine("det(λI − A) coefficients = " + string.Join(", ", cp));
                    Console.WriteLine("eigenvalues ≈ " + string.Join(", ", Cockpit.Core.Tutor.ExactMatrix.NumericRoots(cp).Select(r =>
                        Math.Abs(r.Imaginary) < 1e-9 ? r.Real.ToString("0.######", CultureInfo.InvariantCulture)
                            : string.Create(CultureInfo.InvariantCulture, $"{r.Real:0.######}{(r.Imaginary < 0 ? "-" : "+")}{Math.Abs(r.Imaginary):0.######}i"))));
                }
            }
            if (cli.Get("b") is { } b)
            {
                using var docB = JsonDocument.Parse(b);
                Console.WriteLine("A·B = " + A.Multiply(Cockpit.Core.Tutor.ExactMatrix.From(docB.RootElement)).ToJson().ToJsonString());
            }
            return 0;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new UsageException(ex.Message);
        }
    }

    // ── organize ──────────────────────────────────────────────────────────────────────────────────────

    public static async Task<int> OrganizeAsync(CliArgs cli)
    {
        string folder = Path.GetFullPath(cli.RequirePositional(0, "<folder>"));
        if (!Directory.Exists(folder)) throw new UsageException($"{folder} is not a folder.");
        string? rulesPath = cli.Get("rules");
        string? criteria = cli.Get("criteria");
        if ((rulesPath is null) == (criteria is null)) throw new UsageException("Pass exactly one of --rules <file> or --criteria \"...\".");

        if (criteria is not null)
        {
            var launch = cli.Has("interactive") ? LaunchMode.Interactive : LaunchMode.Headless;
            return await RunModelJobAsync(cli, JobSpecFactory.Organize(folder, criteria, launch));
        }

        // Deterministic: no model. The CLI is the MCP client, through the real bridge, so the gate, journal and audit all apply.
        JsonNode rulesFile;
        try { rulesFile = JsonNode.Parse(File.ReadAllText(rulesPath!)) ?? throw new UsageException("Empty rules file."); }
        catch (Exception ex) when (ex is IOException or JsonException) { throw new UsageException($"Cannot read {rulesPath}: {ex.Message}"); }
        var rules = rulesFile is JsonArray arr ? arr : rulesFile["rules"] as JsonArray ?? throw new UsageException("Rules file must be an array or {\"rules\": [...]}.");
        bool recursive = cli.Has("recursive") || ((bool?)rulesFile["recursive"] ?? false);

        await using var runtime = await StartRuntimeAsync(cli);
        RunningJob job;
        try { job = await runtime.StartJobAsync(JobSpecFactory.OrganizeWithRules(folder) with { JobId = cli.Get("job-id") }); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine("cockpit: " + ex.Message); return 1; }

        Console.WriteLine($"Job {job.JobId}: organize {folder} by {rules.Count} rule(s)");
        try
        {
            await using var client = await runtime.ConnectClientAsync(job);
            var plan = await client.CallToolAsync(FilesToolProvider.PlanRules,
                new JsonObject { ["sourceFolder"] = folder, ["rules"] = rules.DeepClone(), ["recursive"] = recursive, ["note"] = "cockpit organize --rules" },
                TimeSpan.FromMinutes(10));
            Console.WriteLine(plan.Text);
            if (plan.IsError) return 1;
            string? batchId = (string?)plan.Structured?["batchId"];
            int moves = (int?)plan.Structured?["moves"] ?? -1;
            if (batchId is null) return 1;
            if (moves == 0) { Console.WriteLine("Nothing to move."); return 0; }

            var exec = await client.CallToolAsync(FilesToolProvider.Execute, new JsonObject { ["batchId"] = batchId }, TimeSpan.FromHours(2));
            Console.WriteLine(exec.Text);
            if (!exec.IsError) Console.WriteLine($"Undo with: cockpit rollback {job.JobId}");
            return exec.IsError ? 1 : 0;
        }
        catch (McpClientException ex)
        {
            Console.Error.WriteLine("cockpit: " + ex.Message);
            return 1;
        }
        finally
        {
            await runtime.EndJobAsync(job.JobId);
        }
    }

    // ── rollback ──────────────────────────────────────────────────────────────────────────────────────

    public static async Task<int> RollbackAsync(CliArgs cli)
    {
        string jobId = cli.RequirePositional(0, "<jobId>");
        var options = Options(cli) with { SwxBackend = new SimulatedSwxBackend() }; // rollback never needs SolidWorks
        await using var runtime = await CockpitRuntime.CreateAsync(options);

        var batches = await runtime.Journal.ListBatchesAsync(jobId);
        var targets = cli.Get("batch") is { } only
            ? batches.Where(b => b.BatchId == only).ToList()
            : batches.Where(b => RollbackableStates.Contains(b.Status)).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine(batches.Count == 0 ? $"No file batches recorded for job {jobId}." : $"Nothing to roll back for job {jobId} ({string.Join(", ", batches.Select(b => $"{b.BatchId}: {b.Status}"))}).");
            return batches.Count == 0 ? 1 : 0;
        }

        foreach (var b in targets) Console.WriteLine($"  {b.BatchId}  {b.Status}  {b.OpCount} ops  {b.CreatedUtc}  {b.Note}");
        if (!cli.Has("yes"))
        {
            Console.Write($"Roll back {targets.Count} batch(es), newest first? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Cancelled."); return 0; }
        }

        int conflicts = 0;
        foreach (var b in targets)
        {
            var sw = Stopwatch.StartNew();
            var r = await runtime.Journal.RollbackAsync(b.BatchId, jobId, CancellationToken.None);
            conflicts += r.Conflicts.Count;
            Console.WriteLine($"{b.BatchId}: {r.Status}, {r.Undone} undone, {r.Conflicts.Count} conflict(s)");
            foreach (var c in r.Conflicts.Take(20)) Console.WriteLine("  ! " + c);
            await runtime.Audit.RecordAsync(new AuditEntry(jobId, "cockpit/rollback", b.BatchId, ToolRisk.Write,
                new JsonObject { ["batchId"] = b.BatchId }.ToJsonString(), "approved", r.Conflicts.Count == 0 ? "ok" : "tool_error",
                $"{r.Status}: {r.Undone} undone, {r.Conflicts.Count} conflicts", sw.ElapsedMilliseconds));
        }
        return conflicts == 0 ? 0 : 1;
    }

    // ── support bundle ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes a shareable zip: secrets redacted, paths anonymized, no databases, configs, prompts or user files.</summary>
    public static async Task<int> SupportBundleAsync(CliArgs cli)
    {
        await using var runtime = await CockpitRuntime.CreateAsync(Options(cli));
        string output = cli.Get("output") ?? Path.Combine(Environment.CurrentDirectory,
            "cockpit-support-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");
        string written = await Cockpit.Core.Diagnostics.SupportBundle.CreateAsync(runtime, output);
        Console.WriteLine($"Support bundle: {written}");
        Console.WriteLine("It contains no secrets, prompts, file names or file contents; paths are replaced by <path#hash>. Open it to check before sharing.");
        return 0;
    }

    // ── operations ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lists side-effecting calls, or records the user's verdict on one whose outcome was unknown.</summary>
    public static async Task<int> OperationsAsync(CliArgs cli)
    {
        await using var runtime = await CockpitRuntime.CreateAsync(Options(cli));
        if (cli.Positional.Count > 0 && cli.Positional[0] == "resolve")
        {
            string id = cli.RequirePositional(1, "operation id");
            if (cli.Has("done") == cli.Has("not-done")) throw new UsageException("Say what you found: --done (it took effect) or --not-done.");
            string note = cli.Get("note") ?? throw new UsageException("--note is required: say what you checked.");
            var op = await runtime.Operations.GetAsync(id) ?? throw new UsageException($"No operation {id}.");
            if (op.State is not (OperationState.RecoveryRequired or OperationState.OutcomeUnknown))
                throw new UsageException($"{id} is {OperationStore.Snake(op.State)}; only outcome_unknown or recovery_required operations need a verdict.");
            if (op.State == OperationState.OutcomeUnknown) op = await runtime.Operations.TransitionAsync(id, OperationState.RecoveryRequired, "user is reviewing it");
            var resolved = await runtime.Operations.ResolveAsync(id, cli.Has("done"), note);
            await runtime.Audit.RecordAsync(new AuditEntry(op.JobId, "cockpit/operations-resolve", id, ToolRisk.Write,
                new JsonObject { ["operationId"] = id, ["tookEffect"] = cli.Has("done") }.ToJsonString(), "approved", "ok", note, 0));
            Console.WriteLine($"{id}: {OperationStore.Snake(resolved.State)}");
            return 0;
        }
        var list = await runtime.Operations.ListAsync(cli.Get("job-id"), cli.Has("unresolved"));
        if (cli.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(list.Select(o => new { o.OperationId, o.JobId, o.Tool, State = OperationStore.Snake(o.State), o.Attempt, o.CreatedUtc, o.UpdatedUtc, o.Note }),
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (list.Count == 0) { Console.WriteLine(cli.Has("unresolved") ? "No operations need a check." : "No operations recorded."); return 0; }
        foreach (var o in list)
            Console.WriteLine($"{o.OperationId}  {OperationStore.Snake(o.State),-22} {o.Tool,-30} {o.JobId}  {o.UpdatedUtc}  {o.Note}");
        return 0;
    }

    // ── doctor ────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Check(string Name, string Status, string Detail);

    public static async Task<int> DoctorAsync(CliArgs cli)
    {
        var checks = new List<Check>();
        void Add(string name, string status, string detail) => checks.Add(new Check(name, status, detail));

        Add(".NET runtime", "ok", Environment.Version.ToString());
        Add("OS", "ok", System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        CockpitRuntime? runtime = null;
        try
        {
            runtime = await CockpitRuntime.CreateAsync(Options(cli));
            Add("state folder", "ok", runtime.Paths.DataDirectory);
            Add("bus", "ok", runtime.Bus.PipeEndpoint.ToString());
            var chain = await runtime.Audit.VerifyAsync();
            Add("audit chain", chain.Ok ? "ok" : "fail", chain.Ok
                ? $"{chain.RowsChecked} rows verified; head {chain.Head?[..Math.Min(16, chain.Head.Length)]}… (note it down to detect a truncated log later)"
                : $"row {chain.FirstBadId}: {chain.Problem}");
            if (runtime.Recovery.NeedsUser > 0) Add("operations", "warn", $"{runtime.Recovery.NeedsUser} operation(s) need your check: cockpit operations --unresolved");
        }
        catch (Exception ex)
        {
            Add("state folder / bus", "fail", ex.Message);
        }

        if (runtime is not null)
        {
            await using (runtime)
            {
                if (runtime.Bridge is null) Add("bridge", "fail", "cockpit-mcp-bridge not found next to cockpit or in src/Cockpit.Bridge/bin. Build it, or pass --bridge.");
                else
                {
                    Add("bridge", "ok", runtime.Bridge.Display);
                    await BridgeSelfTestAsync(runtime, Add);
                }

                if (cli.Has("simulate") || cli.Has("check-swx"))
                {
                    try
                    {
                        var health = await runtime.Swx.CallAsync("health", new JsonObject(), TimeSpan.FromSeconds(90), CancellationToken.None);
                        Add("SolidWorks", "ok", health?.ToString() ?? "?");
                    }
                    catch (Exception ex) when (ex is SwxWorkerException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        Add("SolidWorks", "fail", ex.Message);
                    }
                }
                else
                {
                    var worker = cli.Get("worker") is { } w ? ComponentLocator.Find(ComponentLocator.SwxWorker, w) : ComponentLocator.Find(ComponentLocator.SwxWorker);
                    Add("SolidWorks worker", worker is null ? "warn" : "ok", worker?.Display ?? "cockpit-swx-worker not found (SolidWorks jobs will fail). Build src/Cockpit.SwxWorker on Windows.");
                }
            }
        }

        if (OperatingSystem.IsWindows()) WindowsChecks(Add);
        else Add("SolidWorks install", "warn", "not Windows: SolidWorks, WSL and Windows Sandbox checks skipped");

        string? claude = cli.Get("claude") ?? CliLaunchBuilder.ResolveOnPath("claude");
        if (claude is null) Add("Claude Code", "fail", "claude not on PATH. Install Claude Code, then run `claude` once and /login with your Pro account.");
        else Add("Claude Code", "ok", $"{claude} {await VersionAsync(claude)}".Trim());
        string? agy = cli.Get("agy") ?? GeminiLaunchBuilder.Locate();
        Add("Antigravity CLI", agy is null ? "warn" : "ok", agy is null
            ? "agy not installed: tutor/files/resolve jobs fall back to Claude (irm https://antigravity.google/cli/install.ps1 | iex, then run agy and sign in with Google)"
            : $"{agy} {await VersionAsync(agy)}".Trim());
        if (agy is not null)
        {
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "settings.json");
            bool allowed = File.Exists(settings) && File.ReadAllText(settings).Contains(GeminiLaunchBuilder.CockpitAllowRule, StringComparison.Ordinal);
            Add("  cockpit tools", allowed ? "ok" : "warn", allowed
                ? $"{GeminiLaunchBuilder.CockpitAllowRule} allowed in {settings}"
                : $"{GeminiLaunchBuilder.CockpitAllowRule} is added to {settings} on the first Antigravity job (headless runs skip unapproved tools)");
        }

        await HelperChecksAsync(Add);

        var billing = CliLaunchBuilder.FindBillingVariables(Environment.GetEnvironmentVariables());
        Add("billing variables", billing.Count == 0 ? "ok" : "warn", billing.Count == 0
            ? "none set"
            : $"{string.Join(", ", billing)} set in this environment. Cockpit jobs scrub them unless --allow-metered; other tools you run may still bill per token.");

        if (cli.Has("json"))
        {
            Console.WriteLine(new JsonArray(checks.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["status"] = c.Status, ["detail"] = c.Detail }).ToArray())
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var c in checks) Console.WriteLine($"{c.Status.ToUpperInvariant(),-5} {c.Name,-20} {c.Detail}");
        }
        return checks.Any(c => c.Status == "fail") ? 1 : 0;
    }

    /// <summary>The programs the V5 tools call: Python modules, Blender, ffmpeg, Resolve's scripting folder.</summary>
    private static async Task HelperChecksAsync(Action<string, string, string> add)
    {
        var python = LocalPythonRunner.Detect();
        if (!python.IsAvailable) add("Python", "warn", python.Describe + " — needed for Code-CAD (build123d), PyMuPDF and the Resolve script");
        else
        {
            string probe = Path.Combine(Path.GetTempPath(), $"cockpit-doctor-{Environment.ProcessId}.py");
            await File.WriteAllTextAsync(probe, "import importlib.util as u, json\nprint(json.dumps({m: u.find_spec(m) is not None for m in ['build123d', 'pymupdf', 'sympy']}))\n");
            try
            {
                var r = await python.RunScriptAsync(probe, [], null, TimeSpan.FromSeconds(30), CancellationToken.None);
                var found = r.Ok ? JsonNode.Parse(r.Stdout.Trim().Split('\n')[^1]) : null;
                add("Python", "ok", python.Describe);
                foreach (var (module, purpose) in new[] { ("build123d", "Code-CAD parts (pip install build123d)"), ("pymupdf", "PDF chapters (pip install pymupdf; a built-in reader is the fallback)"), ("sympy", "optional SymPy cross-checks") })
                    add("  " + module, (bool?)found?[module] == true ? "ok" : "warn", (bool?)found?[module] == true ? "installed" : "missing: " + purpose);
            }
            finally { File.Delete(probe); }
        }
        var blender = BlenderRunner.Detect();
        add("Blender", blender.IsAvailable ? "ok" : "warn", blender.IsAvailable ? blender.Describe : blender.Describe + " — scenes are written as scripts only");
        add("ffprobe / ffmpeg", ToolLocator.Ffprobe() is not null ? "ok" : "warn", ToolLocator.Ffprobe() is not null
            ? $"{ToolLocator.Ffprobe()} / {ToolLocator.Ffmpeg() ?? "(no ffmpeg: no preview MP4)"}"
            : "not on PATH — clip probing uses the built-in MP4/MOV/WAV reader; no preview MP4");
        if (OperatingSystem.IsWindows())
        {
            string api = Cockpit.Core.Resolve.ResolveScript.Environment().GetValueOrDefault("RESOLVE_SCRIPT_API") ?? "";
            add("Resolve scripting", Directory.Exists(api) ? "ok" : "warn", Directory.Exists(api)
                ? api + " (external scripting needs Resolve Studio with Preferences > General > External scripting: Local)"
                : "DaVinci Resolve not found — timelines are still exported as FCPXML/EDL");
        }
    }

    /// <summary>Issues a throwaway job and drives the real bridge: initialize, tools/list, one tool call, audit row.</summary>
    private static async Task BridgeSelfTestAsync(CockpitRuntime runtime, Action<string, string, string> add)
    {
        var sw = Stopwatch.StartNew();
        RunningJob? job = null;
        try
        {
            job = await runtime.StartJobAsync(new JobSpec
            {
                Kind = "doctor", Title = "doctor self-test", Tools = [FilesToolProvider.Status], Roots = [],
                WorkingDirectory = runtime.Paths.DataDirectory, Launch = LaunchMode.None, Lifetime = TimeSpan.FromMinutes(2),
            });
            await using var client = await runtime.ConnectClientAsync(job);
            var tools = await client.ListToolsAsync();
            var call = await client.CallToolAsync(FilesToolProvider.Status, new JsonObject { ["batchId"] = "doctor-probe" }, TimeSpan.FromSeconds(20));
            long audited = await runtime.Audit.CountAsync(job.JobId);
            bool ok = tools.Order(StringComparer.Ordinal).SequenceEqual([OperationsToolProvider.StatusTool, FilesToolProvider.Status]) && call.IsError && audited >= 1;
            add("bridge self-test", ok ? "ok" : "fail", ok
                ? $"initialize, tools/list, tools/call and audit through the bridge in {sw.ElapsedMilliseconds} ms"
                : $"unexpected: tools [{string.Join(",", tools)}], call error {call.IsError}, audit rows {audited}");
        }
        catch (Exception ex) when (ex is McpClientException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            add("bridge self-test", "fail", ex.Message);
        }
        finally
        {
            if (job is not null) await runtime.EndJobAsync(job.JobId);
        }
    }

    private static async Task<string> HelpAsync(string executable)
    {
        string v = await VersionAsync(executable, "--help");
        return v;
    }

    private static async Task<string> VersionAsync(string executable, string flag = "--version")
    {
        try
        {
            ProcessStartInfo psi;
            if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
                foreach (var a in new[] { "/d", "/s", "/c", executable, flag }) psi.ArgumentList.Add(a);
            }
            else
            {
                psi = new ProcessStartInfo(executable);
                psi.ArgumentList.Add(flag);
            }
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            foreach (var v in CliLaunchBuilder.BillingVariables) psi.Environment.Remove(v);
            using var p = Process.Start(psi);
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { p.Kill(true); return "(--version timed out)"; }
            return "(" + (await output).Trim().ReplaceLineEndings(" ") + ")";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"(could not run: {ex.Message})";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsChecks(Action<string, string, string> add)
    {
        using (var clsid = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"SldWorks.Application\CLSID"))
            add("SolidWorks COM", clsid is null ? "warn" : "ok", clsid is null ? "SldWorks.Application is not registered (SolidWorks not installed?)" : $"SldWorks.Application → {clsid.GetValue(null)}");

        // V8: installations found through the registry and the standard folders, not just PATH.
        foreach (var app in new[] { DesktopApp.SolidWorks, DesktopApp.Resolve, DesktopApp.Blender })
        {
            var install = ComponentLocator.FindDesktopApp(app);
            add($"{DesktopAppLocator.DisplayName(app)} install", install is null ? "warn" : "ok", DesktopAppLocator.Default.Summary(app));
        }

        string? redist = SolidWorksInteropDirectory();
        add("SolidWorks interop", redist is null ? "warn" : "ok", redist ?? "api\\redist not found: cockpit-swx-worker is built without SolidWorks support and reports \"SolidWorks assemblies not installed on this machine\"");

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        add("WSL", File.Exists(Path.Combine(system32, "wsl.exe")) ? "ok" : "warn", File.Exists(Path.Combine(system32, "wsl.exe")) ? "wsl.exe present" : "wsl.exe not found (Linux isolation unavailable)");
        add("Windows Sandbox", File.Exists(Path.Combine(system32, "WindowsSandbox.exe")) ? "ok" : "warn",
            File.Exists(Path.Combine(system32, "WindowsSandbox.exe")) ? "available" : "not enabled (Pro/Enterprise feature 'Windows Sandbox')");
        string? wt = CliLaunchBuilder.ResolveOnPath("wt");
        add("Windows Terminal", wt is null ? "warn" : "ok", wt ?? "wt.exe not found: interactive jobs open a plain PowerShell window");
    }

    /// <summary>
    /// &lt;SolidWorks folder&gt;\api\redist with the interop assemblies, newest install first (HKLM\SOFTWARE\SolidWorks\SOLIDWORKS
    /// &lt;year&gt;\Setup, the COM registration, Apps &amp; features, the default folder; see DesktopAppLocator).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? SolidWorksInteropDirectory() =>
        DesktopAppLocator.Default.FindAll(DesktopApp.SolidWorks).Select(i => i.InteropDirectory).FirstOrDefault(d => d is not null);
}
