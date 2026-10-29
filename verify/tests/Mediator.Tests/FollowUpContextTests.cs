using Mediator.Core.Bus;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Core.Swx;
using Mediator.UI;

namespace Mediator.Tests;

/// <summary>
/// V10 fixes: (1) parts are saved INTO an allowed folder (the folder itself, or an existing / new folder below it) instead of
/// the folder being checked as a file to create (root_itself / exists); (2) every follow-up turn carries the whole
/// conversation, so a short "now create the parts and assemble them" after a failed turn keeps the first request's spec.
/// </summary>
static class FollowUpContextTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void OutputFolderInsideAllowedFolder()
    {
        string train = Path.Combine(TempDir("out-root"), "CAD", "Train");
        Directory.CreateDirectory(Path.Combine(train, "Bogies"));
        var paths = new PathAuthorizer([train]);
        Check.True(Mediator.Core.Cad.CadAppToolProvider.OutputFolder(paths, train) is { Allowed: true } v && v.FullPath == train, "the allowed folder itself is an output folder (no root_itself)");
        Check.True(Mediator.Core.Cad.CadAppToolProvider.OutputFolder(paths, Path.Combine(train, "Bogies")).Allowed, "an existing folder below it is used as it is (no 'exists')");
        Check.True(Mediator.Core.Cad.CadAppToolProvider.OutputFolder(paths, Path.Combine(train, "Wagons", "v2")).Allowed, "a new folder below it may be created");
        Check.True(paths.Check(Path.Combine(train, "part.SLDPRT"), PathAccess.CreateFile).Allowed, "a part file inside the allowed folder may be created");
        Check.True(Mediator.Core.Cad.CadAppToolProvider.OutputFolder(paths, TempDir("out-else")) is { Allowed: false, Code: "outside_roots" }, "folders outside stay refused");
        Check.True(paths.Check(train, PathAccess.Modify) is { Allowed: false, Code: "root_itself" }, "the allowed folder itself still cannot be replaced or deleted");
    }

    public static async Task FollowUpKeepsTheFirstRequest()
    {
        string train = Path.Combine(TempDir("follow"), "CAD", "Train");
        Directory.CreateDirectory(train);
        string spec = $"Build a 1:87 train: locomotive 220 mm long, 2 bogies with 4 wheels of 11 mm, couplers at both ends, save in {train} . "
                      + new string('x', 5000) + " END-OF-SPEC";
        var turns = new List<JobSpecFactory.ConversationTurn>
        {
            new(spec, "SolidWorks: [path:root_itself] ...", Failed: true),
            new("try again", new string('a', 9000)),
        };
        string block = JobSpecFactory.ConversationContext(turns);
        Check.True(block.Contains("END-OF-SPEC", StringComparison.Ordinal) && block.Contains("220 mm", StringComparison.Ordinal), "the first request is kept whole");
        Check.True(block.Contains("FAILED", StringComparison.Ordinal) && block.Contains("[Turn 2] User: try again", StringComparison.Ordinal), "a failed turn is marked; every turn is listed");
        Check.True(block.Length < spec.Length + JobSpecFactory.LastAnswerChars + 1000, "answers are trimmed");
        var once = JobSpecFactory.WithConversation(new JobSpec { Kind = "chat", Title = "t", Tools = [], Roots = [], WorkingDirectory = ".", Prompt = "go" }, turns);
        Check.True(JobSpecFactory.WithConversation(once, turns).Prompt == once.Prompt && once.Prompt.EndsWith("Follow-up (now): go", StringComparison.Ordinal), "added once, follow-up last");

        // In the app: a failed first turn (no Continue chip), then a short follow-up.
        var rt = new ModelChoiceTests.GatedRuntime();
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher(), new MemorySettingsStore());
        vm.Conversation.Add(new ChatTurnViewModel("job-1", spec, "Claude") { ResponseText = "SolidWorks: [path:root_itself] ...", IsError = true, IsRunning = false });
        vm.PromptText = "you are to create the parts then assemble them";
        vm.SubmitPromptCommand.Execute(null);
        for (int i = 0; i < 100 && rt.Started.Count == 0; i++) await Task.Delay(10);
        Check.True(rt.Started.Count == 1, "a job started: " + vm.StatusText + " tile=" + vm.Route(vm.PromptText)?.Id + " busy=" + vm.IsChatBusy + " prompt=" + vm.PromptText);
        var started = rt.Started.Single();
        Check.True(started.Prompt.Contains("END-OF-SPEC", StringComparison.Ordinal) && started.Prompt.Contains("locomotive 220 mm", StringComparison.Ordinal)
            && started.Prompt.Contains("create the parts then assemble them", StringComparison.Ordinal), "the follow-up job gets the first request's specification: " + started.Prompt[..Math.Min(300, started.Prompt.Length)] + " launch=" + started.Launch);
        Check.True(started.Roots.Contains(train, StringComparer.OrdinalIgnoreCase), "and works in the folder the first request named: " + string.Join("; ", started.Roots));
        rt.Gate.SetResult();
        for (int i = 0; i < 100 && vm.IsChatBusy; i++) await Task.Delay(10);
    }

    /// <summary>A first message that routes to an action whose template has no request slot (Assemble) still reaches the model whole.</summary>
    public static async Task ActionTemplatesKeepTheRequest(string bridgeDll)
    {
        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir("req-data")),
            Bus = new Mediator.Core.Bus.BusOptions { PipeName = "mediator-req-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxBackend = new SimulatedSwxBackend(),
        });
        string folder = TempDir("req-train");
        string request = $"Save all parts to {folder}. Locomotive_Boiler: cylinder 30 mm x 80 mm. Axle reused 4 times. Fix the Locomotive_Chassis at the origin.";
        var job = await runtime.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.None) with { UserText = request });
        Check.True(job.Spec.Prompt.Contains(request, StringComparison.Ordinal) && job.Spec.Prompt.Contains("swx_part_cards", StringComparison.Ordinal),
            "the job's prompt holds the user's request AND the action's steps: " + job.Spec.Prompt[..Math.Min(300, job.Spec.Prompt.Length)]);
        var plain = JobSpecFactory.CodeCad(folder, "a bracket 40 mm wide", LaunchMode.None) with { UserText = "a bracket 40 mm wide" };
        Check.True(JobSpecFactory.WithUserText(plain) == plain.Prompt, "a template that already has the request is left as it is");
    }
}

/// <summary>V10: the Auto-approve toggle approves every request without a card, and every decision is logged in the chat turn.</summary>
static class AutoApproveTests
{
    public static async Task ToggleApprovesAndLogs()
    {
        var vm = new MediatorOverlayViewModel(new ModelChoiceTests.GatedRuntime(), new InlineDispatcher(), new MemorySettingsStore());
        var turn = new ChatTurnViewModel("job-a", "build it", "CAD");
        typeof(MediatorOverlayViewModel).GetField("_turns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm)!.GetType().GetMethod("Add")!.Invoke(typeof(MediatorOverlayViewModel).GetField("_turns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm), ["job-a", turn]);
        var args = System.Text.Json.JsonDocument.Parse("""{"name":"Axle"}""").RootElement;
        ApprovalRequest Req(string call) => new("job-a", call, "swx_build_parametric_part", Mediator.Core.Bus.ToolRisk.Write, "Build", args, null);

        // Off: a card waits; the user's answer is logged.
        var asked = vm.AskAsync(Req("c1"), CancellationToken.None);
        Check.True(vm.PendingApproval is not null && !asked.IsCompleted, "off: the request waits for the user");
        vm.PendingApproval!.ApproveAllCommand.Execute(null);
        Check.True((await asked).Approved && turn.Steps.Any(s => s.Contains("Approved: swx_build_parametric_part", StringComparison.Ordinal)), "the user's approval is logged: " + string.Join(" | ", turn.Steps));

        // A card waiting when the toggle goes on is approved at once; later ones never show a card.
        var waiting = vm.AskAsync(Req("c2"), CancellationToken.None);
        vm.AutoApproveAll = true;
        Check.True((await waiting.WaitAsync(TimeSpan.FromSeconds(2))).Approved, "turning it on approves the waiting card");
        var auto = await vm.AskAsync(Req("c3"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(auto.Approved && vm.PendingApproval is null, "on: approved without a card");
        Check.True(turn.Steps.Count(s => s.Contains("Auto-approved: swx_build_parametric_part", StringComparison.Ordinal) && s.Contains("Axle", StringComparison.Ordinal)) == 2,
            "auto-approved requests are logged with their arguments: " + string.Join(" | ", turn.Steps));
        Check.True(new MediatorOverlayViewModel(new ModelChoiceTests.GatedRuntime(), new InlineDispatcher(), new MemorySettingsStore()).AutoApproveAll == false, "off at every start");
    }
}
