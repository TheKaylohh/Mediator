using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;

namespace Cockpit.Core.Jobs;

/// <summary>
/// cockpit_job_brief: the task of an interactive job. A terminal tab's command line is readable by other
/// processes, so the user's prompt is not put there; the CLI starts with a fixed instruction to call this tool,
/// which answers over the authenticated bus. Briefs live in memory only and are dropped when the job ends.
/// </summary>
public sealed class JobBriefToolProvider : IToolProvider
{
    public const string BriefTool = "cockpit_job_brief";
    public const string Instruction = "Call the cockpit_job_brief tool first: it holds your task from the AI Cockpit.";

    private readonly ConcurrentDictionary<string, string> _briefs = new(StringComparer.Ordinal);

    public void Set(string jobId, string brief) => _briefs[jobId] = brief;
    public void Remove(string jobId) => _briefs.TryRemove(jobId, out _);

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(BriefTool, "Your task for this job, as written by the user in the AI Cockpit. Call it once at the start.",
            (JsonObject)JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(5)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);

    public Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct) =>
        Task.FromResult(_briefs.TryGetValue(call.Job.JobId, out var brief) ? ToolResult.Ok(brief) : ToolResult.Fail("This job has no brief."));
}
