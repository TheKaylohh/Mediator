using System.Diagnostics;
using Mediator.Core.Context;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.UI;

namespace Mediator.Tests;

/// <summary>
/// V9: multi-turn session continuation. A finished headless Claude run's session_id becomes a continuation; the
/// follow-up launches `claude -p --resume &lt;id&gt;` in the same folder with a new job and grant; ids are validated;
/// the session never moves to Gemini; the overlay shows, auto-selects, clears and re-offers the chip.
/// </summary>
static class SessionTests
{
    const string Session = "5f1c2d3e-0a1b-4c5d-8e9f-a0b1c2d3e4f5";

    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-sess", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static async Task Until(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception("Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>A successful headless Claude run (subscription login: apiKeySource none) that reports <paramref name="session"/>.</summary>
    static string Stream(string session, string text = "Assembled 3 parts with 2 mates.")
    {
        string path = Path.Combine(TempDir("stream"), "run.jsonl");
        static string Line(System.Text.Json.Nodes.JsonObject o) => o.ToJsonString() + "\n";
        File.WriteAllText(path,
            Line(new() { ["type"] = "system", ["subtype"] = "init", ["session_id"] = session, ["model"] = "claude-opus-5-5", ["apiKeySource"] = "none", ["tools"] = new System.Text.Json.Nodes.JsonArray(), ["mcp_servers"] = new System.Text.Json.Nodes.JsonArray() }) +
            Line(new() { ["type"] = "assistant", ["session_id"] = session, ["message"] = new System.Text.Json.Nodes.JsonObject { ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = text }) } }) +
            Line(new() { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = text, ["num_turns"] = 2, ["duration_ms"] = 40, ["session_id"] = session }));
        return path;
    }

    static string? ResumeArgument(ProcessStartInfo psi)
    {
        var args = psi.ArgumentList.ToList();
        int i = args.IndexOf("--resume");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    public static async Task RuntimeContinuation(string bridgeDll, string testsDll)
    {
        var launcher = new FakeLauncher(testsDll) { EmitFile = Stream(Session) };
        await using var rt = await LaunchTests.Runtime(bridgeDll, launcher);
        string folder = TempDir("parts");
        File.WriteAllText(Path.Combine(folder, "shaft.sldprt"), "p");

        var first = await rt.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Headless) with { JobId = "cont-1" });
        Check.Equal(JobState.Succeeded, await first.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "first run succeeds");
        Check.True(ResumeArgument(launcher.Headless[0]) is null, "a fresh job has no --resume");
        Check.Equal(Session, first.SessionId, "session_id captured from the stream");
        var last = rt.LastContinuableSession;
        Check.True(last is { SessionId: Session, JobId: "cont-1", Provider: AiProvider.ClaudeCode } && last.WorkingDirectory == Path.GetFullPath(folder) && last.ShortId == Session[..8],
            $"runtime offers the session ({last})");

        var spec = JobSpecFactory.Continue(last!, "Flip the concentric mate on the shaft");
        Check.True(spec.Prompt == "Flip the concentric mate on the shaft" && spec.ContinueSessionId == Session && spec.Provider == AiProvider.ClaudeCode && spec.NeverDowngrade
            && spec.Title.StartsWith(JobSpecFactory.FollowUpPrefix, StringComparison.Ordinal) && spec.Tools.SequenceEqual(first.Spec.Tools) && spec.Roots.SequenceEqual(first.Spec.Roots)
            && spec.WorkingDirectory == first.Spec.WorkingDirectory && spec.JobId is null, "follow-up spec: same tools, roots and folder; the follow-up text as the prompt");
        Check.True(JobSpecFactory.Continue(JobSpecFactoryContinueAgain(last!, spec), "again").Title.Split(JobSpecFactory.FollowUpPrefix).Length == 2, "titles do not stack 'Follow-up: ' prefixes");

        launcher.EmitFile = Stream(Session, "Flipped the mate.");
        var second = await rt.StartJobAsync(spec with { JobId = "cont-2" });
        Check.Equal(JobState.Succeeded, await second.Completion.WaitAsync(TimeSpan.FromSeconds(30)), "follow-up succeeds");
        var psi = launcher.Headless[1];
        Check.Equal(Session, ResumeArgument(psi), "claude -p --resume <session id>");
        Check.True(psi.ArgumentList.Contains("-p") && psi.WorkingDirectory == first.Spec.WorkingDirectory, "headless, in the same folder (Claude keeps sessions per folder)");
        Check.True(second.Workspace.Root != first.Workspace.Root && !rt.Registry.TryGet("cont-1", out _) && !psi.ArgumentList.Contains(first.Workspace.ClaudeMcpConfigPath),
            "new job workspace and grant; the first job's revoked token is not reused");
        Check.True(rt.LastContinuableSession?.JobId == "cont-2", "the follow-up becomes the session to continue");

        // Interactive follow-up: `claude --resume '<id>'` in the generated terminal script.
        var inter = await rt.StartJobAsync(JobSpecFactory.Continue(last!, "show me", LaunchMode.Interactive) with { JobId = "cont-i" });
        string script = File.ReadAllText(Path.Combine(launcher.Interactive.Single().Ws.Root, "launch-claude.ps1"));
        Check.True(script.Contains($"--resume '{Session}'", StringComparison.Ordinal), "interactive terminal resumes the session");
        rt.KillJob(inter.JobId, "test");

        // Ids are validated before anything is issued or launched (they reach a command line and, via claude.cmd, cmd.exe).
        int launches = launcher.Headless.Count;
        foreach (var bad in new[] { "", "short", "-resume-me", "abc def ghi jkl", "a&calc.exe&b1", "x\"; rm -rf /", "..\\..\\windows", "%PATH%%PATH%", new string('a', 129) })
        {
            try { await rt.StartJobAsync(spec with { ContinueSessionId = bad, JobId = null }); throw new Exception($"'{bad}' should be refused"); }
            catch (ArgumentException ex) { Check.True(ex.Message.Contains("invalid id", StringComparison.Ordinal), $"'{bad}': {ex.Message}"); }
            Check.True(!SessionIds.IsSafe(bad), $"'{bad}' is not a safe token");
        }
        Check.True(SessionIds.IsSafe(Session) && SessionIds.IsSafe("abc_DEF-123"), "UUIDs and plain tokens are safe");
        try
        {
            new CliLaunchBuilder("/opt/fake/claude", TimeSpan.FromMinutes(1)).ClaudeHeadless(first.Workspace, folder, 5, resumeSessionId: "--dangerously-skip-permissions");
            throw new Exception("the launch builder must refuse an option as a session id");
        }
        catch (ArgumentException) { }
        try { await rt.StartJobAsync(spec with { Provider = AiProvider.GeminiCli, JobId = null }); throw new Exception("Gemini continuation should be refused"); }
        catch (ArgumentException ex) { Check.True(ex.Message.Contains("Only Claude Code", StringComparison.Ordinal), ex.Message); }
        Check.Equal(launches, launcher.Headless.Count, "nothing launched for refused continuations");

        // Claude at its limit: the session is NOT moved to Gemini (which is available); the user is told how to start fresh.
        await rt.Usage.SetCooldownAsync("claude-code", DateTimeOffset.UtcNow.AddHours(2), "limit");
        try { await rt.StartJobAsync(spec with { JobId = "cont-limited" }); throw new Exception("should be refused while Claude is limited"); }
        catch (ProviderUnavailableException ex) { Check.True(ex.Message.Contains("can only continue on Claude Code", StringComparison.Ordinal), ex.Message); }
        Check.Equal(launches, launcher.Headless.Count, "no Gemini launch for a Claude session");

        // Not continuable: a killed run.
        await rt.Usage.SetCooldownAsync("claude-code", DateTimeOffset.UtcNow.AddSeconds(-1), "over");
        launcher.Script.Enqueue([Stream(Session), "gate=" + Path.Combine(TempDir("gate"), "never")]);
        var killed = await rt.StartJobAsync(SolidWorksConnector.Assemble(folder, LaunchMode.Headless) with { JobId = "cont-k" });
        rt.KillJob(killed.JobId, "test");
        Check.True(ContinuableSession.From(killed) is null && rt.LastContinuableSession?.JobId == "cont-2", "a killed run is not offered");
    }

    /// <summary>A continuation of a follow-up (title already prefixed).</summary>
    static ContinuableSession JobSpecFactoryContinueAgain(ContinuableSession s, JobSpec followUp) => s with { Spec = followUp };

    public static async Task OverlayContinuation(string bridgeDll, string testsDll)
    {
        var launcher = new FakeLauncher(testsDll) { EmitFile = Stream(Session) };
        await using var rt = await LaunchTests.Runtime(bridgeDll, launcher);
        string folder = TempDir("parts"), other = TempDir("other");
        File.WriteAllText(Path.Combine(folder, "shaft.sldprt"), "p");
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher());
        Check.True(!vm.HasContinuation && !vm.IsContinuing, "no chip before any run");

        vm.SetContext(new ContextSnapshot("explorer", "parts", folder, [], false));
        var first = await vm.RunActionAsync(vm.Actions.Single(a => a.Id == "assemble"), "");
        await first!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await Until(() => vm.HasContinuation, "chip offered after the run");
        var chip = vm.Continuation!;
        Check.True(chip.Text == $"↩ Continue session ({Session[..8]})" && chip.IsSelected && chip.ToolTipText.Contains(Session, StringComparison.Ordinal), $"chip: {chip.Text}");

        vm.PromptText = "Flip the concentric mate on the shaft";
        Check.True(vm.IsContinuing && vm.SuggestionText.StartsWith("Enter ↵  Continue:", StringComparison.Ordinal), "auto-selected: Enter continues (" + vm.SuggestionText + ")");
        vm.PromptText = "/files";
        Check.True(!vm.IsContinuing, "a / command is never a follow-up");
        chip.IsSelected = false;
        vm.PromptText = "Flip the concentric mate on the shaft";
        Check.True(!vm.IsContinuing && !vm.SuggestionText.Contains("Continue:", StringComparison.Ordinal), "clicking the chip deselects it: start fresh this time");
        chip.IsSelected = true;

        vm.SubmitPromptCommand.Execute(null);
        await Until(() => launcher.Headless.Count == 2, "follow-up launched");
        Check.Equal(Session, ResumeArgument(launcher.Headless[1]), "Enter launched claude --resume <id>");
        var followUp = rt.Jobs.Single(j => j.Spec.ContinueSessionId is not null);
        Check.True(followUp.Spec.Prompt.StartsWith("Flip the concentric mate on the shaft", StringComparison.Ordinal) && followUp.Spec.Title.StartsWith("Follow-up: ", StringComparison.Ordinal), "follow-up prompt and title");
        await followUp.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await Until(() => vm.Continuation is { } c && c.Session.JobId == followUp.JobId, "the chip comes back with the follow-up's session");

        // × removes it.
        vm.ClearContinuationCommand.Execute(null);
        Check.True(!vm.HasContinuation && !vm.ClearContinuationCommand.CanExecute(null), "× removes the chip");

        // Switching folders removes it.
        var again = await vm.RunActionAsync(vm.Actions.Single(a => a.Id == "assemble"), "");
        await again!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await Until(() => vm.HasContinuation, "chip offered again");
        vm.SetContext(new ContextSnapshot("explorer", "parts", folder, [], false));
        Check.True(vm.HasContinuation, "reopening on the same folder keeps it");
        vm.SetContext(new ContextSnapshot("explorer", "other", other, [], false));
        Check.True(!vm.HasContinuation, "opening the panel on another folder removes it");

        // A different folder named in the prompt starts fresh instead of continuing.
        var third = await vm.RunActionAsync(vm.Actions.Single(a => a.Id == "assemble"), folder);
        await third!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await Until(() => vm.HasContinuation, "chip offered for the third run");
        int before = launcher.Headless.Count;
        File.WriteAllText(Path.Combine(other, "plate.sldprt"), "p");
        vm.PromptText = $"assemble the parts in {other}";
        vm.SubmitPromptCommand.Execute(null);
        await Until(() => launcher.Headless.Count == before + 1, "fresh job launched");
        Check.True(ResumeArgument(launcher.Headless[^1]) is null && launcher.Headless[^1].WorkingDirectory == Path.GetFullPath(other), "fresh session in the other folder, no --resume");
    }
}
