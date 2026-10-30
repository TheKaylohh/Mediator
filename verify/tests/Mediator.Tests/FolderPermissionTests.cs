using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Connectors.SolidWorks;
using Mediator.UI;

namespace Mediator.Tests;

/// <summary>
/// V10: the user can allow any (non-protected) folder for a chat: from a job's refusal (Allow), with Allow folder, or with
/// Save to. Running jobs get it at once; every later turn starts with it; refusals say what is allowed.
/// </summary>
static class FolderPermissionTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static async Task GrantWhileRunning(string bridgeDll)
    {
        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir("perm-data")),
            Bus = new BusOptions { PipeName = "mediator-perm-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Simulate = true,
        });
        string work = TempDir("perm-work");
        var job = await runtime.StartJobAsync(JobSpecFactory.Organize(work, "by type", LaunchMode.None));
        Check.True(runtime.Registry.TryGet(job.JobId, out var grant) && grant is not null, "the job has a grant");

        string wanted = Path.Combine(TempDir("perm-docs"), "CAD", "F40");
        var refused = grant!.Paths.Check(Path.Combine(wanted, "F40-v1.SLDPRT"), PathAccess.CreateFile);
        Check.True(!refused.Allowed && refused.Code == "outside_roots" && refused.Message.Contains(work, StringComparison.OrdinalIgnoreCase)
            && refused.Message.Contains("Allow", StringComparison.Ordinal), "a refusal lists the allowed folders and how the user allows another: " + refused.Message);
        Check.Equal(wanted, MediatorOverlayViewModel.RefusedFolder("SolidWorks: " + refused.Message), "the view finds the folder to offer (a file's folder)");

        Check.True(await runtime.GrantFolderAsync(job.JobId, wanted) is null && Directory.Exists(wanted), "granted while running; the missing levels are created");
        Check.True(grant.Paths.Check(Path.Combine(wanted, "F40-v1.SLDPRT"), PathAccess.CreateFile).Allowed, "the running job can now write there");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (OperatingSystem.IsWindows())
            Check.True(await runtime.GrantFolderAsync(job.JobId, windows) is { } no && no.Contains("cannot be allowed", StringComparison.Ordinal), "protected places are never granted");
        Check.True(await runtime.GrantFolderAsync("no-such-job", wanted) is { } gone && gone.Contains("not running", StringComparison.Ordinal), "only running jobs");
    }

    public static async Task ChatFoldersAndSaveToReachEveryTurn()
    {
        var rt = new ModelChoiceTests.GatedRuntime();
        var vm = new MediatorOverlayViewModel(rt, new InlineDispatcher(), new MemorySettingsStore());
        string allowed = Path.Combine(TempDir("perm-chat"), "Out");
        Check.True(await vm.AllowFolderAsync(allowed) && vm.ChatFolders.SequenceEqual([allowed]), "Allow folder adds it to the chat (even before it exists)");
        if (OperatingSystem.IsWindows())
            Check.True(!await vm.AllowFolderAsync(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) && vm.ChatFolders.Count == 1, "protected places are refused");

        string save = TempDir("perm-save");
        vm.SetSaveFolder(save);
        vm.PromptText = "what is a determinant";
        vm.SubmitPromptCommand.Execute(null);
        var spec = rt.Started.Single();
        Check.True(spec.Roots.Contains(allowed, StringComparer.OrdinalIgnoreCase) && spec.Roots.Contains(save, StringComparer.OrdinalIgnoreCase),
            "every job the chat starts may use the chat's folders and the Save to folder: " + string.Join("; ", spec.Roots));
        Check.True(spec.Prompt.Contains(save, StringComparison.OrdinalIgnoreCase) || spec.Roots[0] == save, "outputs go to the Save to folder: " + spec.Prompt[..Math.Min(400, spec.Prompt.Length)] + " roots: " + string.Join("; ", spec.Roots));
        rt.Gate.SetResult();
        for (int i = 0; i < 100 && vm.IsChatBusy; i++) await Task.Delay(10);

        var refusal = new ToolCallEvent("job-x", "c1", "swx_build_parametric_part", "tool_error",
            @"SolidWorks: [path:outside_roots] D:\Projects\Car: outside this job's allowed folders (allowed: none). Use an allowed folder…");
        typeof(MediatorOverlayViewModel).GetMethod("OnToolRefusedFolder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [refusal]);
        Check.True(vm.HasFolderRequest && vm.FolderRequest == @"D:\Projects\Car" && vm.AllowFolderRequestCommand.CanExecute(null), "a refusal becomes an Allow request");
        vm.DismissFolderRequestCommand.Execute(null);
        Check.True(!vm.HasFolderRequest, "Dismiss clears it");

        var store = new ChatStore(TempDir("perm-store"));
        store.Save(new SavedChat("20260101-000000-dddddd", "Car", DateTimeOffset.Now, [new SavedTurn("j1", "make a car", "CAD", "ok", [], false)], null, Folders: [allowed]));
        var reopened = new MediatorOverlayViewModel(new ModelChoiceTests.GatedRuntime(), new InlineDispatcher()) { Chats = store };
        Check.True(reopened.ChatFolders.SequenceEqual([allowed]), "a chat's allowed folders come back when it is reopened");
        reopened.ClearConversationCommand.Execute(null);
        Check.True(reopened.ChatFolders.Count == 0, "a new chat starts with none");
    }
}
