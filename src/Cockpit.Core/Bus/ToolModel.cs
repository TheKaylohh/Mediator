using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Bus;

/// <summary>
/// Risk drives the default approval mode: ReadOnly runs automatically, Write asks, Destructive always asks
/// (a job grant can tighten but never loosen Destructive).
/// </summary>
public enum ToolRisk { ReadOnly, Write, Destructive }

public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolRisk Risk,
    string? LeaseResource,
    TimeSpan ExecutionTimeout);

/// <param name="ApprovedRows">After approval: the preview rows the user kept (null = all). Providers that offer selectable previews must honour it.</param>
public sealed record ToolCallContext(JobGrant Job, ToolDescriptor Tool, JsonElement Arguments, string CallId, IReadOnlySet<string>? ApprovedRows = null);

/// <summary>Result of a tool. IsError results are returned to the model as content, not as protocol errors.</summary>
public sealed record ToolResult(bool IsError, string Text, JsonNode? Structured = null)
{
    public static ToolResult Ok(string text, JsonNode? structured = null) => new(false, text, structured);
    public static ToolResult Fail(string text) => new(true, text);
}

public interface IToolProvider
{
    IReadOnlyList<ToolDescriptor> Tools { get; }

    /// <summary>
    /// Structured preview for the approval UI. Contract: {"kind", "title", "summary", "selectable": bool,
    /// "rows": [{"key", "primary", "detail", "confidence"?}]}. When selectable, the user may approve a subset
    /// of rows by key and the provider receives it as <see cref="ToolCallContext.ApprovedRows"/>. Null if none.
    /// </summary>
    Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct);

    Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct);
}

public sealed partial class ToolRegistry
{
    private readonly Dictionary<string, (IToolProvider Provider, ToolDescriptor Tool)> _tools = new(StringComparer.Ordinal);

    // Claude Code and other MCP clients require this shape for tool names.
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNamePattern();

    public void Register(IToolProvider provider)
    {
        foreach (var tool in provider.Tools)
        {
            if (!ToolNamePattern().IsMatch(tool.Name))
                throw new ArgumentException($"Tool name '{tool.Name}' must match ^[a-zA-Z0-9_-]{{1,64}}$.");
            if (!_tools.TryAdd(tool.Name, (provider, tool)))
                throw new InvalidOperationException($"Tool '{tool.Name}' is registered twice.");
        }
    }

    public bool TryGet(string name, out IToolProvider provider, out ToolDescriptor tool)
    {
        if (_tools.TryGetValue(name, out var entry))
        {
            (provider, tool) = entry;
            return true;
        }
        provider = null!;
        tool = null!;
        return false;
    }

    /// <summary>Tools visible to a job: the intersection of registered tools and the job's grant.</summary>
    public IEnumerable<ToolDescriptor> VisibleTo(JobGrant job) =>
        _tools.Values.Select(v => v.Tool).Where(t => job.AllowedTools.Contains(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal);
}

/// <summary>
/// Checks the subset of JSON Schema the Cockpit's own tools use: object type, required properties,
/// and the primitive type of each declared property. Enough to bounce malformed calls back to the model.
/// </summary>
public static class ArgumentValidator
{
    public static string? Validate(JsonObject schema, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "Arguments must be a JSON object.";
        var props = schema["properties"] as JsonObject;
        if (schema["required"] is JsonArray required)
        {
            foreach (var r in required)
            {
                string name = (string?)r ?? "";
                if (!args.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                    return $"Missing required argument '{name}'.";
            }
        }
        if (props is null) return null;
        foreach (var arg in args.EnumerateObject())
        {
            if (props[arg.Name] is not JsonObject propSchema)
            {
                if (schema["additionalProperties"] is JsonValue ap && ap.TryGetValue(out bool allowed) && !allowed)
                    return $"Unknown argument '{arg.Name}'.";
                continue;
            }
            string? type = (string?)propSchema["type"];
            if (type is null || arg.Value.ValueKind == JsonValueKind.Null) continue;
            bool ok = type switch
            {
                "string" => arg.Value.ValueKind == JsonValueKind.String,
                "integer" => arg.Value.ValueKind == JsonValueKind.Number && arg.Value.TryGetInt64(out _),
                "number" => arg.Value.ValueKind == JsonValueKind.Number,
                "boolean" => arg.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => arg.Value.ValueKind == JsonValueKind.Array,
                "object" => arg.Value.ValueKind == JsonValueKind.Object,
                _ => true,
            };
            if (!ok) return $"Argument '{arg.Name}' must be of type {type}.";
        }
        return null;
    }
}
