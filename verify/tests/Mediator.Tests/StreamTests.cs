using System.Text;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Protocol;

namespace Mediator.Tests;

static class StreamTests
{
    // RECONSTRUCTED fixture, not a capture: it follows the documented `claude -p --verbose --output-format stream-json`
    // shape (system/init, assistant and user messages with content blocks, a final result). Field names can drift
    // between CLI versions; capture a real run on Windows and add it beside this one (see SPEC "Verification on Windows").
    public const string SuccessRun =
        "\uFEFF" +
        """{"type":"system","subtype":"init","session_id":"sess-1","model":"claude-sonnet-4-5","apiKeySource":"none","permissionMode":"default","cwd":"C:\\jobs\\j1","tools":["mcp__mediator__swx_part_cards","mcp__mediator__swx_build_assembly"],"mcp_servers":[{"name":"mediator","status":"connected"}]}""" + "\r\n" +
        """{"type":"assistant","session_id":"sess-1","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"Reading the part cards… ✓ 🔩"},{"type":"tool_use","id":"toolu_1","name":"mcp__mediator__swx_part_cards","input":{"folder":"D:\\Jetta\\parts"}},{"type":"tool_use","id":"toolu_2","name":"mcp__mediator__fs_list","input":{"path":"D:\\Jetta"}}],"usage":{"input_tokens":12,"output_tokens":40}}}""" + "\n" +
        """{"type":"user","session_id":"sess-1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"2 cards"}]},{"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":"denied by user"}]}}""" + "\n" +
        "this line is not json\n" +
        """{"type":"future_event","session_id":"sess-1","event":{"type":"ping"}}""" + "\n" +
        """{"type":"result","subtype":"success","is_error":false,"session_id":"sess-1","num_turns":3,"duration_ms":18234,"duration_api_ms":15002,"result":"Assembly saved.","total_cost_usd":0.0831,"usage":{"input_tokens":1520,"output_tokens":842,"cache_read_input_tokens":20480,"cache_creation_input_tokens":3072}}"""; // no trailing newline

    // RECONSTRUCTED: the subscription usage-limit message carries the reset time as "|<unix seconds>".
    public const long ResetEpoch = 1792699200; // 2026-10-22T20:00:00Z
    public static readonly string RateLimitedRun =
        """{"type":"system","subtype":"init","session_id":"sess-2","model":"claude-sonnet-4-5","apiKeySource":"none","tools":[],"mcp_servers":[]}""" + "\n" +
        $$$"""{"type":"result","subtype":"success","is_error":true,"session_id":"sess-2","num_turns":1,"result":"Claude AI usage limit reached|{{{ResetEpoch}}}","total_cost_usd":0,"usage":{"input_tokens":0,"output_tokens":0}}""" + "\n";

    public static readonly string ApiKeyRun =
        """{"type":"system","subtype":"init","session_id":"sess-3","model":"claude-sonnet-4-5","apiKeySource":"ANTHROPIC_API_KEY","tools":[],"mcp_servers":[]}""" + "\n";

    static List<MediatorStreamEvent> ParseInChunks(string text, int chunk)
    {
        var parser = new ClaudeStreamParser();
        var events = new List<MediatorStreamEvent>();
        for (int i = 0; i < text.Length; i += chunk)
            events.AddRange(parser.Feed(text.AsSpan(i, Math.Min(chunk, text.Length - i))));
        events.AddRange(parser.Complete());
        return events;
    }

    public static Task ParserFixture()
    {
        var whole = ParseInChunks(SuccessRun, SuccessRun.Length);
        string Shape(IEnumerable<MediatorStreamEvent> es) => string.Join(",", es.Select(e => e.GetType().Name));
        foreach (int size in new[] { 1, 2, 3, 7, 64 })
            Check.Equal(Shape(whole), Shape(ParseInChunks(SuccessRun, size)), $"same events when fed in {size}-char chunks");

        Check.Equal("SessionInitEvent,AssistantTextEvent,ToolUseEvent,ToolUseEvent,ToolResultEvent,ToolResultEvent,ParseErrorEvent,UnknownEvent,RunResultEvent",
            Shape(whole), "event sequence");

        var init = (SessionInitEvent)whole[0];
        Check.Equal("sess-1", init.SessionId, "BOM stripped before the first line, so the session id parses");
        Check.Equal(false, init.UsesApiKey, "apiKeySource none = subscription login");
        Check.Equal("connected", init.McpServers.Single().Status, "MCP server status");
        Check.Equal(2, init.Tools.Count, "tools");

        Check.Equal("Reading the part cards… ✓ 🔩", ((AssistantTextEvent)whole[1]).Text, "non-ASCII text survives 1-char chunks (surrogate pairs split)");
        var use = (ToolUseEvent)whole[2];
        Check.Equal("mcp__mediator__swx_part_cards", use.Name, "tool name");
        Check.Equal(@"D:\Jetta\parts", use.Input.GetProperty("folder").GetString(), "tool input outlives the parsed document");

        var ok = (ToolResultEvent)whole[4];
        var denied = (ToolResultEvent)whole[5];
        Check.True(!ok.IsError && ok.Text == "2 cards" && ok.ToolUseId == "toolu_1", "tool result from text blocks");
        Check.True(denied.IsError && denied.Text == "denied by user", "tool error from string content");

        var result = (RunResultEvent)whole[^1];
        Check.True(!result.IsError && result.RateLimit is null, "success result");
        Check.Equal(new UsageTotals(1520, 842, 20480, 3072), result.Usage, "usage parsed");
        Check.Equal(0.0831, result.TotalCostUsd, "total_cost_usd");
        Check.Equal(3, result.NumTurns, "num_turns");
        Check.Equal("Assembly saved.", result.ResultText, "final line without newline parsed by Complete()");

        var limited = (RunResultEvent)ParseInChunks(RateLimitedRun, 5)[^1];
        Check.True(limited.IsError && limited.RateLimit is not null, "usage limit recognised");
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(ResetEpoch), limited.RateLimit!.ResetsAt, "reset time parsed from |epoch");
        return Task.CompletedTask;
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public static async Task LedgerAndMonitor()
    {
        string db = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8], "usage.db");
        var now = new FixedTime(DateTimeOffset.FromUnixTimeSeconds(ResetEpoch).AddHours(-2));
        await using var ledger = await UsageLedger.OpenAsync(db, now);

        var seen = new List<MediatorStreamEvent>();
        var alarms = new List<string>();
        var m1 = new ClaudeRunMonitor("job-a", ledger, now);
        m1.EventReceived += (_, e) => seen.Add(e);
        await m1.ObserveAsync(new StringReader(SuccessRun), CancellationToken.None);
        Check.Equal(9, seen.Count, "every event forwarded to the overlay");

        var totals = await ledger.TotalsAsync(ClaudeRunMonitor.Provider, DateTimeOffset.MinValue);
        Check.Equal(1, totals.Runs, "one run recorded");
        Check.Equal(20480L, totals.Tokens.CacheReadInputTokens, "cache reads recorded");
        Check.Equal(null, await ledger.GetCooldownAsync(ClaudeRunMonitor.Provider), "no cooldown after success");

        var m2 = new ClaudeRunMonitor("job-b", ledger, now);
        await m2.ObserveAsync(new StringReader(RateLimitedRun), CancellationToken.None);
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(ResetEpoch), await ledger.GetCooldownAsync(ClaudeRunMonitor.Provider), "cooldown until the reported reset");
        Check.Equal(1, (await ledger.TotalsAsync(ClaudeRunMonitor.Provider, DateTimeOffset.MinValue)).RateLimitedRuns, "rate-limited run counted");

        var m3 = new ClaudeRunMonitor("job-c", ledger, now);
        m3.BillingAlarm += (job, source) => alarms.Add($"{job}:{source}");
        await m3.ObserveAsync(new StringReader(ApiKeyRun), CancellationToken.None);
        Check.True(alarms.SequenceEqual(new[] { "job-c:ANTHROPIC_API_KEY" }), "billing alarm when a run authenticates with an API key");

        await using (var later = await UsageLedger.OpenAsync(db, new FixedTime(DateTimeOffset.FromUnixTimeSeconds(ResetEpoch).AddMinutes(1))))
            Check.Equal(null, await later.GetCooldownAsync(ClaudeRunMonitor.Provider), "cooldown expires at the reset time");
    }

    public static Task BillingScrub()
    {
        string[] vars = ["ANTHROPIC_API_KEY", "CLAUDE_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI"];
        var saved = vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in vars) Environment.SetEnvironmentVariable(v, "set-by-test");
            var jobs = new JobRegistry();
            var grant = jobs.Issue("job-bill", [], [], TimeSpan.FromMinutes(5));
            string root = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
            var ws = JobWorkspaceFactory.Create(root, grant, "/opt/mediator/mediator-mcp-bridge", BusEndpoint.Default, TimeSpan.FromMinutes(20));
            var builder = new CliLaunchBuilder("/usr/bin/claude", TimeSpan.FromMinutes(21));

            var scrubbed = builder.ClaudeHeadless(ws, root, 10);
            Check.True(vars.All(v => !scrubbed.Environment.ContainsKey(v)), "headless: every billing variable removed");
            string[] expectedArgs = ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--mcp-config", ws.ClaudeMcpConfigPath, "--strict-mcp-config"];
            Check.True(scrubbed.ArgumentList.Take(expectedArgs.Length).SequenceEqual(expectedArgs), "headless flags: " + string.Join(" ", scrubbed.ArgumentList));

            var metered = builder.ClaudeHeadless(ws, root, 10, allowMeteredBilling: true);
            Check.True(vars.All(v => metered.Environment.TryGetValue(v, out var val) && val == "set-by-test"), "headless: opt-in keeps the variables");

            var session = new InteractiveSession(ws.Root);
            builder.ClaudeInteractive(ws, root, session, hasBrief: true);
            byte[] script = File.ReadAllBytes(Path.Combine(ws.Root, "launch-claude.ps1"));
            Check.True(script.AsSpan().StartsWith(Utf8.WithBom.GetPreamble()), ".ps1 has a UTF-8 BOM");
            string text = Encoding.UTF8.GetString(script);
            Check.True(vars.All(v => text.Contains($"Remove-Item Env:{v} ", StringComparison.Ordinal)), ".ps1 removes every billing variable");
            Check.True(text.Contains(InteractiveSession.Ps(JobBriefToolProvider.Instruction), StringComparison.Ordinal), ".ps1 carries only the fixed brief instruction, never the user's prompt");
            Check.True(text.Contains(session.Nonce, StringComparison.Ordinal) && text.Contains("Move-Item", StringComparison.Ordinal), ".ps1 binds sentinels to this session's nonce and writes them atomically");

            builder.ClaudeInteractive(ws, root, session, hasBrief: true, allowMeteredBilling: true);
            text = File.ReadAllText(Path.Combine(ws.Root, "launch-claude.ps1"));
            Check.True(!text.Contains("Remove-Item Env:", StringComparison.Ordinal) && text.Contains("Metered billing was opted in", StringComparison.Ordinal), ".ps1 opt-in keeps the variables");
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
        return Task.CompletedTask;
    }
}
