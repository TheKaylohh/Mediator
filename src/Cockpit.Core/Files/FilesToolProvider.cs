using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;

namespace Cockpit.Core.Files;

/// <summary>
/// MCP tools for reversible file organization. Planning is read-only and automatic; executing and rolling
/// back a batch always ask the user, with a preview built from the journal (not from the model's claims).
/// </summary>
public sealed class FilesToolProvider : IToolProvider
{
    public const string PlanRules = "files_plan_by_rules";
    public const string PlanMoves = "files_plan_moves";
    public const string Execute = "files_execute_batch";
    public const string Rollback = "files_rollback_batch";
    public const string Status = "files_batch_status";

    private readonly FileJournalService _journal;
    private readonly TimeProvider _time;

    public FilesToolProvider(FileJournalService journal, TimeProvider? time = null)
    {
        _journal = journal;
        _time = time ?? TimeProvider.System;
    }

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(PlanRules,
            "Plan moving files under a folder according to rules (first matching rule wins; unmatched files stay). " +
            "Destination templates may use {ext} {yyyy} {mm} {dd} {parent}; relative destinations are under sourceFolder. Nothing moves yet: returns a batchId and a summary. " +
            "Git repositories, SolidWorks project folders, system and cloud-sync folders are always excluded.",
            Schema("""
            {
              "type": "object",
              "required": ["sourceFolder", "rules"],
              "properties": {
                "sourceFolder": { "type": "string" },
                "recursive":    { "type": "boolean" },
                "note":         { "type": "string" },
                "rules": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["destination"],
                    "properties": {
                      "extensions": { "type": "array", "items": { "type": "string" } },
                      "nameGlob": { "type": "string" }, "olderThanDays": { "type": "integer" }, "newerThanDays": { "type": "integer" },
                      "minBytes": { "type": "integer" }, "maxBytes": { "type": "integer" }, "destination": { "type": "string" }
                    }
                  }
                }
              },
              "additionalProperties": false
            }
            """), ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(10)),

        new(PlanMoves,
            "Plan an explicit list of file moves (for small, hand-picked sets). Nothing moves yet: returns a batchId and a summary.",
            Schema("""
            {
              "type": "object",
              "required": ["moves"],
              "properties": {
                "note":  { "type": "string" },
                "moves": { "type": "array", "items": { "type": "object", "required": ["from", "to"],
                           "properties": { "from": { "type": "string" }, "to": { "type": "string" } } } }
              },
              "additionalProperties": false
            }
            """), ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(5)),

        new(Execute,
            "Execute a planned batch. The user approves it first. Every move is journaled and can be rolled back.",
            Schema("""{ "type": "object", "required": ["batchId"], "properties": { "batchId": { "type": "string" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromHours(2)),

        new(Rollback,
            "Undo an executed batch, newest move first. Files that changed or whose original path is now taken are left alone and reported.",
            Schema("""{ "type": "object", "required": ["batchId"], "properties": { "batchId": { "type": "string" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromHours(2)),

        new(Status,
            "Show a batch's status and per-state operation counts.",
            Schema("""{ "type": "object", "required": ["batchId"], "properties": { "batchId": { "type": "string" } }, "additionalProperties": false }"""),
            ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(30)),
    ];

    public async Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        if (call.Tool.Name is not (Execute or Rollback)) return null;
        string batchId = call.Arguments.GetProperty("batchId").GetString()!;
        try
        {
            var status = await _journal.GetStatusAsync(batchId, call.Job.JobId, ct).ConfigureAwait(false);
            var sample = await _journal.SampleOpsAsync(batchId, call.Job.JobId, 25, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["kind"] = call.Tool.Name == Execute ? "files_execute" : "files_rollback",
                ["title"] = call.Tool.Name == Execute ? "Move files" : "Undo file moves",
                ["summary"] = $"{status.OpStates.Values.Sum()} operations, {status.TotalBytes / (1024.0 * 1024):0.#} MB" + (status.Note is { Length: > 0 } nt ? $" · {nt}" : ""),
                ["selectable"] = false, // a batch is approved or denied as a whole; the journal makes it reversible
                ["rows"] = new JsonArray(sample.Select(o => (JsonNode)new JsonObject
                {
                    ["key"] = o.Seq.ToString(CultureInfo.InvariantCulture),
                    ["primary"] = Path.GetFileName(o.Src ?? o.Dst),
                    ["detail"] = $"{Path.GetDirectoryName(o.Src)} → {Path.GetDirectoryName(o.Dst)}",
                }).ToArray()),
                ["batchId"] = batchId,
                ["status"] = status.Status,
                ["note"] = status.Note,
                ["totalBytes"] = status.TotalBytes,
                ["opStates"] = new JsonObject(status.OpStates.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["sample"] = new JsonArray(sample.Select(o => (JsonNode)new JsonObject { ["from"] = o.Src, ["to"] = o.Dst, ["bytes"] = o.SizeBytes }).ToArray()),
            };
        }
        catch (JournalException ex)
        {
            return new JsonObject { ["kind"] = "error", ["error"] = ex.Message };
        }
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            switch (call.Tool.Name)
            {
                case PlanRules: return await PlanByRulesAsync(call, ct).ConfigureAwait(false);
                case PlanMoves: return await PlanExplicitAsync(call, ct).ConfigureAwait(false);
                case Execute:
                {
                    var r = await _journal.ExecuteAsync(Batch(call), call.Job.JobId, null, ct).ConfigureAwait(false);
                    var node = JsonSerializer.SerializeToNode(r, FilesJsonContext.Default.ExecuteResult)!.AsObject();
                    return new ToolResult(r.Failed > 0, $"Batch {r.BatchId}: {r.Status}. done {r.Done}, skipped {r.Skipped}, failed {r.Failed}, pending {r.Pending}." +
                        (r.Errors.Count > 0 ? " First errors: " + string.Join(" | ", r.Errors.Take(10)) : ""), node);
                }
                case Rollback:
                {
                    var r = await _journal.RollbackAsync(Batch(call), call.Job.JobId, ct).ConfigureAwait(false);
                    var node = JsonSerializer.SerializeToNode(r, FilesJsonContext.Default.RollbackResult)!.AsObject();
                    return new ToolResult(r.Conflicts.Count > 0, $"Batch {r.BatchId}: {r.Status}. undone {r.Undone}, conflicts {r.Conflicts.Count}." +
                        (r.Conflicts.Count > 0 ? " " + string.Join(" | ", r.Conflicts.Take(10)) : ""), node);
                }
                case Status:
                {
                    var s = await _journal.GetStatusAsync(Batch(call), call.Job.JobId, ct).ConfigureAwait(false);
                    var node = JsonSerializer.SerializeToNode(s, FilesJsonContext.Default.BatchStatus)!.AsObject();
                    return ToolResult.Ok(node.ToJsonString(), node);
                }
                default:
                    return ToolResult.Fail($"Unknown tool {call.Tool.Name}.");
            }
        }
        catch (JournalException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<ToolResult> PlanByRulesAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        string source = Path.GetFullPath(a.GetProperty("sourceFolder").GetString()!);
        if (!call.Job.IsInsideRoots(source)) return ToolResult.Fail($"{source} is outside this job's allowed folders.");
        if (!Directory.Exists(source)) return ToolResult.Fail($"{source} does not exist.");
        bool recursive = a.TryGetProperty("recursive", out var rec) && rec.ValueKind == JsonValueKind.True;
        var rules = FileRules.Parse(a.GetProperty("rules"));
        if (rules.Count == 0) return ToolResult.Fail("Give at least one rule.");

        var expansion = await Task.Run(() => FileRules.Expand(source, recursive, rules, FileJournalService.MaxOpsPerBatch,
            _time.GetUtcNow().UtcDateTime, ct), ct).ConfigureAwait(false);
        string? note = a.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var plan = await _journal.PlanAsync(call.Job.JobId, expansion.Moves, call.Job.FileRoots, note, ct).ConfigureAwait(false);

        var summary = Summarize(plan);
        summary["scanned"] = expansion.Scanned;
        summary["unmatched"] = expansion.Unmatched;
        summary["truncated"] = expansion.Truncated;
        summary["matchesPerRule"] = new JsonObject(expansion.MatchesPerRule.OrderBy(kv => kv.Key)
            .Select(kv => KeyValuePair.Create(kv.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)kv.Value)));
        return ToolResult.Ok(summary.ToJsonString(), summary);
    }

    private async Task<ToolResult> PlanExplicitAsync(ToolCallContext call, CancellationToken ct)
    {
        var moves = call.Arguments.GetProperty("moves").EnumerateArray()
            .Select(m => new MoveRequest(m.GetProperty("from").GetString() ?? "", m.GetProperty("to").GetString() ?? ""))
            .ToList();
        string? note = call.Arguments.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var plan = await _journal.PlanAsync(call.Job.JobId, moves, call.Job.FileRoots, note, ct).ConfigureAwait(false);
        var summary = Summarize(plan);
        return ToolResult.Ok(summary.ToJsonString(), summary);
    }

    /// <summary>Compact summary for the model: counts and samples, never the full list.</summary>
    private static JsonObject Summarize(PlanResult plan)
    {
        var moves = plan.Ops.Where(o => o.Kind == "move").ToList();
        var byFolder = moves.GroupBy(o => Path.GetDirectoryName(o.Dst) ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(15)
            .Select(g => (JsonNode)new JsonObject { ["folder"] = g.Key, ["files"] = g.Count() }).ToArray();
        var reasons = plan.Rejected.GroupBy(r => r.Reason.Split(" (")[0]).OrderByDescending(g => g.Count())
            .Select(g => (JsonNode)new JsonObject { ["reason"] = g.Key, ["count"] = g.Count(), ["example"] = g.First().From }).ToArray();
        return new JsonObject
        {
            ["batchId"] = plan.BatchId,
            ["moves"] = moves.Count,
            ["foldersToCreate"] = plan.Ops.Count(o => o.Kind == "mkdir"),
            ["totalBytes"] = plan.TotalBytes,
            ["topDestinations"] = new JsonArray(byFolder),
            ["sample"] = new JsonArray(moves.Take(10).Select(o => (JsonNode)new JsonObject { ["from"] = o.Src, ["to"] = o.Dst }).ToArray()),
            ["rejected"] = plan.Rejected.Count,
            ["rejectedByReason"] = new JsonArray(reasons),
            ["next"] = moves.Count == 0 ? "Nothing to move." : $"Call {Execute} with this batchId; the user will review it first.",
        };
    }

    private static string Batch(ToolCallContext call) => call.Arguments.GetProperty("batchId").GetString()!;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ExecuteResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RollbackResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BatchStatus))]
internal sealed partial class FilesJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
