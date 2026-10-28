using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Jobs;

/// <summary>Incremental parser of a CLI's newline-delimited JSON output (Claude Code or Antigravity CLI).</summary>
public interface ICliStreamParser
{
    IReadOnlyList<CockpitStreamEvent> Feed(ReadOnlySpan<char> chunk);
    IReadOnlyList<CockpitStreamEvent> Complete();
}

/// <summary>One event from a CLI's stream-json output, normalized for the Cockpit.</summary>
public abstract record CockpitStreamEvent(string? SessionId);

/// <summary>
/// First event of a run. <see cref="ApiKeySource"/> is the billing tripwire: anything other than "none" means
/// Claude Code is authenticating with an API key (pay-per-token) instead of the subscription login.
/// </summary>
public sealed record SessionInitEvent(string? SessionId, string? Model, string? ApiKeySource, string? PermissionMode,
    IReadOnlyList<string> Tools, IReadOnlyList<McpServerStatus> McpServers) : CockpitStreamEvent(SessionId)
{
    public bool UsesApiKey => ApiKeySource is { Length: > 0 } s && !s.Equals("none", StringComparison.OrdinalIgnoreCase);
}

public sealed record McpServerStatus(string Name, string Status);
public sealed record AssistantTextEvent(string? SessionId, string Text) : CockpitStreamEvent(SessionId);
/// <summary>
/// Live progress while a reply is being written (--include-partial-messages): Kind "text", "thinking" or "tool"; Tool is set
/// when a tool call starts; Chars is the size of this piece. The finished text and tool call still arrive as their own events.
/// </summary>
public sealed record ProgressEvent(string? SessionId, string Kind, string? Tool, int Chars) : CockpitStreamEvent(SessionId);
public sealed record ToolUseEvent(string? SessionId, string ToolUseId, string Name, JsonElement Input) : CockpitStreamEvent(SessionId);
public sealed record ToolResultEvent(string? SessionId, string ToolUseId, bool IsError, string Text) : CockpitStreamEvent(SessionId);
public sealed record SystemNoticeEvent(string? SessionId, string Subtype, string Raw) : CockpitStreamEvent(SessionId);

/// <summary>A subscription usage limit or API rate limit, with the reset time when the CLI reports one.</summary>
public sealed record RateLimitInfo(string Message, DateTimeOffset? ResetsAt);

/// <summary>
/// Token counts a run reported. V6: a field the CLI did not report is null (unknown), never 0, and a run whose
/// result carried no usage at all has <c>RunResultEvent.Usage == null</c>. Quota estimates count unknown runs
/// separately instead of treating them as free.
/// </summary>
public sealed record UsageTotals(long? InputTokens, long? OutputTokens, long? CacheReadInputTokens, long? CacheCreationInputTokens)
{
    public bool Complete => InputTokens is not null && OutputTokens is not null;

    public string Describe() => Complete
        ? string.Create(CultureInfo.InvariantCulture, $"{InputTokens} in / {OutputTokens} out / {Fmt(CacheReadInputTokens)} cache-read tokens")
        : $"token usage partly unknown ({Fmt(InputTokens)} in / {Fmt(OutputTokens)} out)";

    private static string Fmt(long? v) => v is long x ? x.ToString(CultureInfo.InvariantCulture) : "?";
}

/// <summary>
/// Last event of a run. <see cref="TotalCostUsd"/> is Claude Code's API-equivalent estimate; on a subscription
/// it is a gauge for comparing runs, not money charged.
/// </summary>
/// <param name="Usage">Null when the CLI reported no usage for the run (unknown, not zero).</param>
public sealed record RunResultEvent(string? SessionId, string Subtype, bool IsError, string? ResultText, int? NumTurns, long? DurationMs,
    double? TotalCostUsd, UsageTotals? Usage, RateLimitInfo? RateLimit) : CockpitStreamEvent(SessionId);

public sealed record UnknownEvent(string? SessionId, string Type, string Raw) : CockpitStreamEvent(SessionId);

/// <summary>A line that was not valid JSON. Reported, never thrown: one bad line must not stop the monitor.</summary>
public sealed record ParseErrorEvent(string Line, string Error) : CockpitStreamEvent((string?)null);

/// <summary>
/// Incremental parser for Claude Code's newline-delimited stream-json output. Accepts arbitrary chunks (partial
/// lines are buffered), strips a leading BOM and CR line endings, and turns each JSON line into zero or more
/// typed events (an assistant message with text and two tool calls yields three events).
/// Field names follow Claude Code's documented stream-json shape; verify against your CLI version.
/// </summary>
public sealed partial class ClaudeStreamParser : ICliStreamParser
{
    private readonly StringBuilder _buffer = new();
    private bool _atStart = true;

    // "Claude AI usage limit reached|1761235200" style messages carry the reset time as Unix seconds.
    [GeneratedRegex(@"\|(\d{9,11})\b")]
    private static partial Regex ResetEpoch();

    [GeneratedRegex(@"usage limit|rate[ _-]?limit|\b429\b|overloaded", RegexOptions.IgnoreCase)]
    private static partial Regex LimitText();

    /// <summary>Feeds a chunk of output; returns events for every line completed by it.</summary>
    public IReadOnlyList<CockpitStreamEvent> Feed(ReadOnlySpan<char> chunk)
    {
        if (_atStart && chunk.Length > 0)
        {
            if (chunk[0] == '﻿') chunk = chunk[1..];
            _atStart = false;
        }
        var events = new List<CockpitStreamEvent>();
        foreach (char c in chunk)
        {
            if (c == '\n')
            {
                FlushLine(events);
            }
            else
            {
                _buffer.Append(c);
            }
        }
        return events;
    }

    /// <summary>Call at end of stream: parses a final line that had no trailing newline.</summary>
    public IReadOnlyList<CockpitStreamEvent> Complete()
    {
        var events = new List<CockpitStreamEvent>();
        FlushLine(events);
        return events;
    }

    private void FlushLine(List<CockpitStreamEvent> sink)
    {
        string line = _buffer.ToString().TrimEnd('\r').Trim();
        _buffer.Clear();
        if (line.Length > 0) sink.AddRange(ParseLine(line));
    }

    public static IReadOnlyList<CockpitStreamEvent> ParseLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex) { return [new ParseErrorEvent(Truncate(line, 300), ex.Message)]; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [new ParseErrorEvent(Truncate(line, 300), "not a JSON object")];
            string type = Str(root, "type") ?? "";
            string? session = Str(root, "session_id");
            return type switch
            {
                "system" => [ParseSystem(root, session, line)],
                "assistant" => ParseAssistant(root, session),
                "user" => ParseUser(root, session),
                "result" => [ParseResult(root, session)],
                "stream_event" => ParseProgress(root, session),
                _ => [new UnknownEvent(session, type, Truncate(line, 500))],
            };
        }
    }

    private static CockpitStreamEvent ParseSystem(JsonElement root, string? session, string line)
    {
        string subtype = Str(root, "subtype") ?? "";
        if (subtype != "init") return new SystemNoticeEvent(session, subtype, Truncate(line, 500));

        var tools = root.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var servers = root.TryGetProperty("mcp_servers", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new McpServerStatus(Str(x, "name") ?? "?", Str(x, "status") ?? "?")).ToList()
            : [];
        return new SessionInitEvent(session, Str(root, "model"), Str(root, "apiKeySource"), Str(root, "permissionMode"), tools, servers);
    }

    private static IReadOnlyList<CockpitStreamEvent> ParseAssistant(JsonElement root, string? session)
    {
        var events = new List<CockpitStreamEvent>();
        foreach (var block in ContentBlocks(root))
        {
            switch (Str(block, "type"))
            {
                case "text" when Str(block, "text") is { } text:
                    events.Add(new AssistantTextEvent(session, text));
                    break;
                case "tool_use":
                    events.Add(new ToolUseEvent(session, Str(block, "id") ?? "", Str(block, "name") ?? "",
                        block.TryGetProperty("input", out var input) ? input.Clone() : default));
                    break;
            }
        }
        return events;
    }

    /// <summary>Partial-message deltas as progress; anything else in them is ignored (the full message follows).</summary>
    private static IReadOnlyList<CockpitStreamEvent> ParseProgress(JsonElement root, string? session)
    {
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return [];
        switch (Str(ev, "type"))
        {
            case "content_block_start" when ev.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object:
                return Str(block, "type") switch
                {
                    "tool_use" => [new ProgressEvent(session, "tool", Str(block, "name"), 0)],
                    "thinking" => [new ProgressEvent(session, "thinking", null, 0)],
                    _ => [],
                };
            case "content_block_delta" when ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object:
                return Str(delta, "type") switch
                {
                    "text_delta" => [new ProgressEvent(session, "text", null, Str(delta, "text")?.Length ?? 0)],
                    "input_json_delta" => [new ProgressEvent(session, "tool", null, Str(delta, "partial_json")?.Length ?? 0)],
                    "thinking_delta" => [new ProgressEvent(session, "thinking", null, Str(delta, "thinking")?.Length ?? 0)],
                    _ => [],
                };
            default:
                return [];
        }
    }

    private static IReadOnlyList<CockpitStreamEvent> ParseUser(JsonElement root, string? session)
    {
        var events = new List<CockpitStreamEvent>();
        foreach (var block in ContentBlocks(root))
        {
            if (Str(block, "type") != "tool_result") continue;
            bool isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            events.Add(new ToolResultEvent(session, Str(block, "tool_use_id") ?? "", isError, ResultText(block)));
        }
        return events;
    }

    private static RunResultEvent ParseResult(JsonElement root, string? session)
    {
        bool isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        string subtype = Str(root, "subtype") ?? "";
        string? text = Str(root, "result");
        UsageTotals? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new UsageTotals(Long(u, "input_tokens"), Long(u, "output_tokens"),
                Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));

        RateLimitInfo? limit = null;
        if ((isError || subtype.StartsWith("error", StringComparison.Ordinal)) && text is not null && LimitText().IsMatch(text))
        {
            DateTimeOffset? resets = null;
            var match = ResetEpoch().Match(text);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long epoch))
                resets = DateTimeOffset.FromUnixTimeSeconds(epoch);
            limit = new RateLimitInfo(text, resets);
        }

        return new RunResultEvent(session, subtype, isError, text, (int?)Long(root, "num_turns"), Long(root, "duration_ms"),
            root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
            usage, limit);
    }

    private static IEnumerable<JsonElement> ContentBlocks(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            return content.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object).ToList();
        return [];
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString()!;
        if (c.ValueKind == JsonValueKind.Array)
            return string.Join("\n", c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Str(x, "type") == "text").Select(x => Str(x, "text")));
        return "";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
