using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Swx;
using Mediator.Protocol;

namespace Mediator.Tests;

/// <summary>V10: SolidWorks is started for the job (no need to open it first), early, and only once.</summary>
static class AppSessionTests
{
    sealed class CountingSession : IDesktopAppSession
    {
        public int WarmUps, Ensures;
        public Exception? Fail;
        public Task EnsureReadyAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Ensures);
            return Fail is { } e ? Task.FromException(e) : Task.CompletedTask;
        }
        public void WarmUp() => Interlocked.Increment(ref WarmUps);
    }

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "app-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static async Task SolidWorksStartedForCadJobs(string bridgeDll)
    {
        var session = new CountingSession();
        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir()),
            Bus = new BusOptions { PipeName = "mediator-app-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxBackend = new SimulatedSwxBackend(),
            SolidWorksSession = session,
        });
        runtime.Approvals.Target = new AutoApprovalPrompt();
        string parts = TempDir();
        File.WriteAllText(Path.Combine(parts, "a.SLDPRT"), "p");

        await runtime.StartJobAsync(JobSpecFactory.Organize(TempDir(), "by type", LaunchMode.None));
        Check.Equal(0, session.WarmUps, "a job without SolidWorks tools does not start SolidWorks");

        var job = await runtime.StartJobAsync(JobSpecFactory.Assemble(parts, LaunchMode.None, Path.Combine(parts, "x.SLDASM")));
        Check.Equal(1, session.WarmUps, "a CAD job starts SolidWorks as it begins, before any tool call");

        await using var client = await runtime.ConnectClientAsync(job);
        var cards = await client.CallToolAsync(SwxToolProvider.PartCardsTool, new JsonObject { ["folder"] = parts }, TimeSpan.FromSeconds(30));
        Check.True(!cards.IsError && session.Ensures == 1, "the tool waits for SolidWorks to be ready, then runs: " + cards.Text);

        session.Fail = new TimeoutException("a start-up dialog is waiting");
        var failed = await client.CallToolAsync(SwxToolProvider.PartCardsTool, new JsonObject { ["folder"] = parts }, TimeSpan.FromSeconds(30));
        Check.True(failed.IsError && failed.Text.Contains("SolidWorks could not be started", StringComparison.Ordinal) && failed.Text.Contains("dialog", StringComparison.Ordinal),
            "a failed start is a clear tool error: " + failed.Text);

        Check.True(SwxToolProvider.ToolNames.Contains(SwxToolProvider.ImportStepTool) && SwxPartToolProvider.ToolNames.Contains(SwxPartToolProvider.BuildTool),
            "every SolidWorks tool is known to need SolidWorks");
        Check.True(NoDesktopAppSession.Instance.EnsureReadyAsync(CancellationToken.None).IsCompletedSuccessfully, "simulated / no-app session starts nothing");
    }

    /// <summary>The part tool advertises every op the builder runs, and the fields organic recipes use.</summary>
    public static void PartToolSchemaCoversSurfacing()
    {
        var tool = new SwxPartToolProvider().Tools.Single(t => t.Name == SwxPartToolProvider.BuildTool);
        var ops = tool.InputSchema["properties"]!["features"]!["items"]!["properties"]!["op"]!["enum"]!.AsArray().Select(o => (string)o!).ToArray();
        Check.True(ops.SequenceEqual(SwxParametric.Ops), "op enum = the builder's ops: " + string.Join(",", ops));
        foreach (string op in new[] { "loft", "sweep", "revolve", "knit", "thicken", "shell", "chamfer", "mirror", "plane", "sketch" })
            Check.True(ops.Contains(op), "surfacing op offered: " + op);
        Check.True(tool.InputSchema["properties"]!["fitBoxMm"] is not null, "fitBoxMm is accepted (top level has additionalProperties: false)");
        var fields = tool.InputSchema["properties"]!["features"]!["items"]!["properties"]!.AsObject();
        foreach (string field in new[] { "profiles", "guides", "path", "surface", "thickness", "edgesNear", "base", "offset" })
            Check.True(fields.ContainsKey(field), "feature field in schema: " + field);
        Check.True(fields["profile"]!.AsObject().Count == 0, "profile is a shape list or a sketch name (sweep), so it is untyped");
        Check.True(tool.Description.Contains("spline", StringComparison.Ordinal) && tool.Description.Contains("guides", StringComparison.Ordinal),
            "the description teaches splines and guided lofts");
    }

    /// <summary>"Make the parts and assemble them": one job with part building AND assembly tools, and room for every call.</summary>
    public static void PartsAndAssemblyInOneJob()
    {
        string[] both = [SwxPartToolProvider.BuildTool, SwxToolProvider.PartCardsTool, SwxToolProvider.BuildTool];
        foreach (string prompt in new[]
        {
            "Create a gearbox in SolidWorks: make the housing and two shafts, then assemble and mate them",
            "assemble the parts in this folder",
            "design a hinge in CAD with two leaves and a pin",
            "mate the bracket to the plate in solidworks",
        })
        {
            var (tools, category) = JobSpecFactory.InferToolsAndCategory(prompt, null);
            Check.True(category == TaskCategory.Cad && both.All(tools.Contains), $"'{prompt}': parts and assembly tools together: {string.Join(", ", tools)}");
        }
        Check.True(JobSpecFactory.InferToolsAndCategory("explain what a fillet is in CAD", null).Category == TaskCategory.Chat, "a CAD question stays a chat");

        string dir = Path.Combine(Path.GetTempPath(), "mediator-tests", "asm-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var assemble = JobSpecFactory.Assemble(dir, LaunchMode.None);
        Check.True(both.All(assemble.Tools.Contains) && assemble.MaxTurns == JobSpecFactory.CadMaxTurns && assemble.Prompt.Contains("swx_build_parametric_part", StringComparison.Ordinal),
            "the Assemble action can build missing parts first, with the CAD turn budget");
        var codeCad = JobSpecFactory.CodeCad(dir, "a gearbox", LaunchMode.None);
        Check.True(codeCad.Prompt.Contains("swx_part_cards on", StringComparison.Ordinal) && codeCad.MaxTurns == JobSpecFactory.CadMaxTurns, "the CAD prompt chains parts → cards → assembly");
        var asked = JobSpecFactory.Assistant(dir, "make two parts and mate them", LaunchMode.None, JobSpecFactory.CadTools, TaskCategory.Cad);
        Check.True(asked.MaxTurns == JobSpecFactory.CadMaxTurns && JobSpecFactory.Assistant(null, "hi", LaunchMode.None, [], TaskCategory.Chat).MaxTurns == 20,
            "CAD chat jobs get the CAD turn budget; plain answers keep theirs");
    }
}
