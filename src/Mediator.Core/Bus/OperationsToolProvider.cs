using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mediator.Core.Bus;

/// <summary>
/// mediator_operation_status: lets the model (and the user, through the CLI) see what happened to this job's
/// side-effecting calls, so an unknown outcome is checked instead of retried. Granted to every job; read-only.
/// </summary>
public sealed class OperationsToolProvider(OperationStore store) : IToolProvider
{
    public const string StatusTool = "mediator_operation_status";

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(StatusTool,
            "Status of this job's side-effecting tool calls (operations): committed, failed, outcome_unknown, recovery_required… " +
            "Use it when a call timed out or was refused as a duplicate, instead of retrying. Optional operationId for one operation and its history.",
            (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"operationId":{"type":"string"}},"additionalProperties":false}""")!,
            ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(10)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        if (call.Arguments.TryGetProperty("operationId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            var op = await store.GetAsync(idEl.GetString()!).ConfigureAwait(false);
            // Another job's operation is reported exactly like a missing one.
            if (op is null || op.JobId != call.Job.JobId) return ToolResult.Fail($"No operation {idEl.GetString()} for this job.");
            var history = await store.HistoryAsync(op.OperationId).ConfigureAwait(false);
            var o = Describe(op);
            o["history"] = new JsonArray(history.Select(h => (JsonNode)new JsonObject { ["from"] = h.From, ["to"] = h.To, ["at"] = h.At, ["note"] = h.Note }).ToArray());
            return ToolResult.Ok(o.ToJsonString(), o);
        }
        var list = await store.ListAsync(call.Job.JobId).ConfigureAwait(false);
        var arr = new JsonArray(list.Select(op => (JsonNode)Describe(op)).ToArray());
        var result = new JsonObject { ["operations"] = arr, ["unresolved"] = list.Count(o => OperationStore.IsUnresolved(o.State)) };
        return ToolResult.Ok(result.ToJsonString(), result);
    }

    private static JsonObject Describe(OperationRecord op) => new()
    {
        ["operationId"] = op.OperationId, ["tool"] = op.Tool, ["state"] = OperationStore.Snake(op.State), ["attempt"] = op.Attempt,
        ["created"] = op.CreatedUtc, ["updated"] = op.UpdatedUtc, ["note"] = op.Note,
    };
}
