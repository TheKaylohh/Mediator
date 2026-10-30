using System.Text.Json;
using System.Text.Json.Serialization;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.UI;
using Mediator.Protocol;

namespace Mediator.Tests;

/// <summary>V10: one prompt at a time per chat, Stop, the model / effort picker, and saved chats keeping their tools.</summary>
static class ModelChoiceTests
{
    /// <summary>A runtime whose StartJobAsync waits on a gate, like the real one-second start.</summary>
    internal sealed class GatedRuntime : IMediatorRuntime
    {
        public readonly List<JobSpec> Started = new();
        public readonly List<string> Killed = new();
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<ToolCallEvent>? ToolCallActivity { add { } remove { } }
        public event Action<RunningJob>? JobChanged { add { } remove { } }
        public event Action<string, MediatorStreamEvent>? StreamEvent { add { } remove { } }
        public event Action<string, string>? BillingAlarm { add { } remove { } }
        public event Action<string, string>? Notice { add { } remove { } }
        public SwappableApprovalPrompt Approvals { get; } = new();
        public IReadOnlyList<RunningJob> Jobs => [];
        public async Task<RunningJob> StartJobAsync(JobSpec spec, CancellationToken ct = default)
        {
            Started.Add(spec);
            await Gate.Task;
            throw new InvalidOperationException("fake: not started");
        }
        public bool KillJob(string jobId, string reason) { Killed.Add(jobId); return true; }
        public Task EndJobAsync(string jobId) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetCooldownAsync(string provider) => Task.FromResult<DateTimeOffset?>(null);
        public Task<RouteDecision> RouteAsync(TaskCategory category, AiProvider? provider = null, bool neverDowngrade = false) =>
            Task.FromResult(new RouteDecision(provider ?? AiProvider.ClaudeCode, false, null));
        public Mediator.Core.Attachments.Attachment StageAttachment(string path) => throw new NotSupportedException();
        public Mediator.Core.Attachments.Attachment StagePastedImage(byte[] png) => throw new NotSupportedException();
        public void DiscardAttachment(Mediator.Core.Attachments.Attachment attachment) { }
        public Task<bool> AttachToJobAsync(string jobId, Mediator.Core.Attachments.Attachment attachment) => Task.FromResult(false);
        public Task<IReadOnlyList<CliModels>> DetectModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CliModels>>(
        [
            new(AiProvider.ClaudeCode, "Claude Code", ModelCatalog.ClaudeModels),
            new(AiProvider.GeminiCli, "Antigravity CLI", ["gemini-3.8-flash-high", "gemini-3.8-flash-low"]),
        ]);
    }

    /// <summary>The double click: a second Send while the first is starting is refused; Stop kills the running turn.</summary>
    public static async Task OnePromptAtATimeAndStop()
    {
        var rt = new GatedRuntime();
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher());
        vm.PromptText = "what is a determinant";
        vm.SubmitPromptCommand.Execute(null);
        Check.True(vm.IsChatBusy && !vm.SubmitPromptCommand.CanExecute(null) && vm.StopCommand.CanExecute(null), "while starting: Send off, Stop on");
        vm.SubmitPromptCommand.Execute(null);
        vm.PromptText = "what is a determinant";
        vm.SubmitPromptCommand.Execute(null);
        Check.Equal(1, rt.Started.Count, "the second and third clicks started nothing");
        rt.Gate.SetResult();
        for (int i = 0; i < 100 && vm.IsChatBusy; i++) await Task.Delay(10);
        Check.True(!vm.IsChatBusy && vm.SubmitPromptCommand.CanExecute(null), "a failed start frees the chat");

        var turn = new ChatTurnViewModel("job-7", "long task", "CAD") { IsRunning = true };
        vm.Conversation.Add(turn);
        Check.True(vm.IsChatBusy && vm.StopCommand.CanExecute(null), "a running turn keeps the chat busy");
        vm.StopCommand.Execute(null);
        Check.True(rt.Killed.SequenceEqual(["job-7"]) && !turn.IsRunning && turn.ThinkingSummary == "Stopped" && !vm.IsChatBusy, "Stop kills the turn's job and frees the chat");
    }

    /// <summary>The picker lists every installed CLI's models; the pick fixes CLI, model and effort; Auto leaves them alone.</summary>
    public static async Task PickerAppliesModelAndEffort()
    {
        var rt = new GatedRuntime();
        var store = new MemorySettingsStore();
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher(), store);
        await vm.LoadModelOptionsAsync();
        Check.True(vm.ModelOptions.Count == 6 && vm.ModelOptions[0] == ModelOption.Auto, "Auto + 3 Claude + 2 Gemini: " + string.Join(", ", vm.ModelOptions.Select(o => o.Label)));
        Check.True(vm.EffortOptions.SequenceEqual(["Auto", "low", "medium", "high", "xhigh", "max"]), "every effort level");

        var spec = JobSpecFactory.Assistant(null, "hi", LaunchMode.Headless, [], TaskCategory.Chat);
        var auto = vm.ApplyModelPick(spec);
        Check.True(auto.Provider is null && auto.Model is null && auto.Effort is null, "Auto: routing rules decide");

        vm.SelectedModel = vm.ModelOptions.First(o => o.Model == "gemini-3.8-flash-high");
        vm.SelectedEffort = "xhigh";
        var picked = vm.ApplyModelPick(spec);
        Check.True(picked is { Provider: AiProvider.GeminiCli, Model: "gemini-3.8-flash-high", Effort: "xhigh", ModelFor: AiProvider.GeminiCli }, "pick reaches the job");

        var again = new MediatorOverlayViewModel(rt, new InlineDispatcher(), store);
        await again.LoadModelOptionsAsync();
        Check.True(again.SelectedModel.Model == "gemini-3.8-flash-high" && again.SelectedEffort == "xhigh", "the pick is remembered");

        Check.Equal("sonnet", ProviderRouter.SelectModel(AiProvider.ClaudeCode, TaskCategory.Chat, "make it", 3), "a short follow-up with tools is not haiku");
        Check.Equal("haiku", ProviderRouter.SelectModel(AiProvider.ClaudeCode, TaskCategory.Chat, "hi", 0), "plain short chat stays haiku");
        var agy = ModelCatalog.ParseAgyModels("Fetching available models...\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\r\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\n\n");
        Check.True(agy.SequenceEqual(["gemini-3.8-flash-high", "claude-sonnet-4-6"]), "agy models parsed: " + string.Join(",", agy));
    }

    /// <summary>--effort reaches both CLIs' command lines; nothing invalid is passed.</summary>
    public static Task EffortOnCommandLine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mediator-tests", "eff-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var grant = new JobRegistry().Issue("eff-1", [], [dir], TimeSpan.FromMinutes(5));
        var ws = JobWorkspaceFactory.Create(dir, grant, "bridge.exe", BusEndpoint.Default, TimeSpan.FromMinutes(1));
        string claude = string.Join(" ", new CliLaunchBuilder("claude.exe", TimeSpan.FromMinutes(1)).ClaudeHeadless(ws, dir, 5, "opus", effort: "max").ArgumentList);
        Check.True(claude.Contains("--include-partial-messages", StringComparison.Ordinal), "claude streams partial messages (live progress on long answers and tool calls)");
        string agy = string.Join(" ", new GeminiLaunchBuilder("agy.exe").Headless(ws, "gemini-3.8-flash-low", effort: "low").ArgumentList);
        string bad = string.Join(" ", new CliLaunchBuilder("claude.exe", TimeSpan.FromMinutes(1)).ClaudeHeadless(ws, dir, 5, "opus", effort: "turbo; rm").ArgumentList);
        Check.True(claude.Contains("--model opus --effort max", StringComparison.Ordinal), "claude: " + claude);
        Check.True(agy.Contains("--model gemini-3.8-flash-low --effort low", StringComparison.Ordinal), "agy: " + agy);
        Check.True(!bad.Contains("--effort", StringComparison.Ordinal), "an unknown effort is never passed");
        return Task.CompletedTask;
    }

    /// <summary>The F40 prompt end to end: folder from the prompt, CAD tools, and a capable model on either CLI.</summary>
    public static Task F40Pipeline()
    {
        string docs = Path.Combine(Path.GetTempPath(), "mediator-tests", "docs-" + Guid.NewGuid().ToString("N")[..6]);
        string target = Path.Combine(docs, "Mediator-Test");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Old.SLDASM"), "x"); // a project folder is still a valid job folder

        string prompt = $"Create a ferrari F40 in solidworks that I can 3d print in a 4 cubic inch box. output the file to {target}";
        Check.Equal(target, PromptPaths.TargetFolder(prompt), "existing folder, sentence-final period trimmed");
        Check.Equal(target, PromptPaths.TargetFolder($"save it as {Path.Combine(target, "F40.SLDPRT")} please"), "a file path gives its folder");
        string fresh = Path.Combine(docs, "NewOut");
        Check.Equal(fresh, PromptPaths.TargetFolder($"put it in {fresh} when done"), "a new folder whose parent exists");
        string nested = Path.Combine(docs, "CAD", "F40");
        Check.Equal(nested, PromptPaths.TargetFolder($"Save it to {nested} and name versions F40-v1, F40-v2… Start with"),
            "two new levels below an existing folder, the sentence after it cut off");
        Check.True(PromptPaths.TargetFolder($"Save it to {Path.Combine(docs, "a", "b", "c", "d")} now") is null, "more than three new levels is refused");
        Check.True(PromptPaths.TargetFolder("put it in " + Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is null || !OperatingSystem.IsWindows(), "the Windows folder is refused");
        Check.True(PromptPaths.TargetFolder("no path here") is null, "no path → null");

        var (tools, category) = JobSpecFactory.InferToolsAndCategory(prompt, null);
        Check.True(category == TaskCategory.Cad && SolidWorksConnector.Tools.All(tools.Contains), "F40 prompt → Cad with every CAD tool: " + string.Join(",", tools));
        var (cadTools, cadCat) = JobSpecFactory.InferToolsAndCategory("make me a CAD model of a bracket", null);
        Check.True(cadCat == TaskCategory.Cad && cadTools.Contains("swx_build_parametric_part"), "\"cad\" → Cad");
        Check.True(JobSpecFactory.InferToolsAndCategory("a decade of cadence", null).Category != TaskCategory.Cad, "cadence / decade are not CAD");

        var claude = ProviderRouter.ResolveRun(AiProvider.ClaudeCode, "haiku", "low", category, prompt, tools.Count);
        Check.True(claude is { Model: "sonnet", Effort: "medium" } && claude.Note is not null, "haiku + low on a tool job → sonnet + medium, with a note: " + claude.Note);
        var gemini = ProviderRouter.ResolveRun(AiProvider.GeminiCli, "gemini-3.8-flash-low", null, category, prompt, tools.Count);
        Check.Equal(ModelCatalog.StrongAgy, gemini.Model, "Gemini low on a tool job → the -high model");
        var wrong = ProviderRouter.ResolveRun(AiProvider.ClaudeCode, "gemini-3.8-flash-high", "max", category, prompt, tools.Count);
        Check.True(wrong.Model == "sonnet" && wrong.Effort == "max", "a Gemini id never goes to claude --model");
        var keep = ProviderRouter.ResolveRun(AiProvider.ClaudeCode, "opus", "xhigh", category, prompt, tools.Count);
        Check.True(keep is { Model: "opus", Effort: "xhigh", Note: null }, "a capable pick is kept as is");
        var chat = ProviderRouter.ResolveRun(AiProvider.ClaudeCode, "haiku", "low", TaskCategory.Chat, "hi", 0);
        Check.True(chat is { Model: "haiku", Effort: "low" }, "plain chat may still use haiku / low");
        return Task.CompletedTask;
    }

    /// <summary>The F40 bug: a chat reopened after a restart came back with no tools and the Chat category.</summary>
    public static Task SavedChatKeepsTools()
    {
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string folder = Path.Combine(Path.GetTempPath(), "mediator-tests", "f40-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var spec = JobSpecFactory.Assistant(folder, "Create a ferrari F40 in solidworks", LaunchMode.Headless, SolidWorksConnector.Tools, TaskCategory.Cad);
        var session = new ContinuableSession("abc123", AiProvider.ClaudeCode, folder, "assistant-1", spec, DateTimeOffset.Now) { NativeResume = true };

        var saved = JsonSerializer.Deserialize<SavedResume>(JsonSerializer.Serialize(SavedResume.From(session), json), json)!;
        var back = saved.ToSession(DateTimeOffset.Now);
        Check.True(back.Spec.Tools.SequenceEqual(SolidWorksConnector.Tools) && back.Spec.Category == TaskCategory.Cad && back.Spec.Roots.SequenceEqual([folder]),
            "tools, category and folder survive a save and reopen");

        var old = new SavedResume("abc123", AiProvider.ClaudeCode, folder, "assistant-1", true, "Create a ferrari F40 in solidworks that I can 3d print", null);
        var oldBack = old.ToSession(DateTimeOffset.Now);
        Check.True(oldBack.Spec.Tools.Contains("swx_build_parametric_part") && oldBack.Spec.Category == TaskCategory.Cad, "old chat: tools inferred from its prompt");

        var asm = new ContinuableSession("abc123", AiProvider.ClaudeCode, folder, "assemble-1", SolidWorksConnector.Assemble(folder, LaunchMode.Headless), DateTimeOffset.Now) { NativeResume = true };
        var next = JobSpecFactory.Continue(asm, "now create an F40 in solidworks I can 3d print");
        Check.True(next.Tools.Contains("swx_build_parametric_part") && next.Tools.Contains("swx_build_assembly"), "follow-up adds the part-building tools");
        return Task.CompletedTask;
    }
}
