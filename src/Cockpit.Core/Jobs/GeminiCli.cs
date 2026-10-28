using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cockpit.Protocol;

namespace Cockpit.Core.Jobs;

/// <summary>
/// Builds Antigravity CLI (<c>agy</c>) launches for the Google AI Pro provider (<see cref="AiProvider.GeminiCli"/>; Gemini CLI
/// stopped serving AI Pro accounts on 2026-06-18). agy reads workspace MCP servers from <c>.agents/mcp_config.json</c> in its
/// working folder, so it runs in the job workspace (wired to cockpit-mcp-bridge by <see cref="JobWorkspaceFactory"/>) and
/// reaches the user's files only through the Cockpit tools. The user's folder is deliberately NOT added with --add-dir:
/// agy auto-allows writes inside its workspace, which would bypass the Cockpit gate and journal.
/// Verified against agy 1.4.7 (verify/fixtures/real/agy-1.4.7-ok.stream.jsonl). The prompt goes on stdin as one stream-json
/// user event, never on a command line.
/// </summary>
public sealed class GeminiLaunchBuilder
{
    private readonly string _agyExecutable;

    public GeminiLaunchBuilder(string agyExecutable) => _agyExecutable = agyExecutable;

    /// <summary>The executable name on PATH (installed to %LOCALAPPDATA%\agy\bin).</summary>
    public const string ExecutableName = "agy";

    /// <summary>Finds agy on PATH, then in its default install folder (a terminal opened before the install has the old PATH).</summary>
    public static string? Locate()
    {
        if (CliLaunchBuilder.ResolveOnPath(ExecutableName) is string onPath) return onPath;
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        string candidate = Path.Combine(local, "agy", "bin", OperatingSystem.IsWindows() ? "agy.exe" : "agy");
        return local.Length > 0 && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Billing variables for Google, scrubbed so agy uses your Google login (Google AI Pro), not an API key or Vertex.</summary>
    public static readonly string[] GoogleBillingVariables = ["GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_CLOUD_PROJECT", "GOOGLE_APPLICATION_CREDENTIALS"];

    public static IReadOnlyList<string> VariablesToScrub(bool allowMeteredBilling) =>
        allowMeteredBilling ? [] : CliLaunchBuilder.BillingVariables.Concat(GoogleBillingVariables).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>agy has no documented "not signed in" exit code; sign-in failures are recognised from stderr text.</summary>
    public const int ExitNoAuth = int.MinValue;

    /// <summary>One stdin line for --input-format stream-json: {"event":"user","message":{"content":"..."}}.</summary>
    /// <param name="cockpitTools">The job has Cockpit tools: say where they are (<see cref="ToolPreamble"/>). Without
    /// them (V10 Chat and Search) the model is told not to touch local files, so it does not go looking for tools.</param>
    /// <param name="webSearch">V10 Search jobs: search_web and read_url_content are the only tools it should use.</param>
    public static string StdinMessage(string prompt, bool cockpitTools = true, bool webSearch = false) =>
        new JsonObject { ["event"] = "user", ["message"] = new JsonObject { ["content"] = (cockpitTools ? ToolPreamble : NoToolsPreamble(webSearch)) + prompt } }.ToJsonString() + "\n";

    /// <summary>The preamble for jobs without Cockpit tools: no file, command or browser tools; web search only when allowed.</summary>
    public static string NoToolsPreamble(bool webSearch) =>
        "No local tools are granted for this job: do not read, list, search or edit files, run commands or open a browser. "
        + (webSearch ? "To look things up, use only search_web and read_url_content." : "Answer from your own knowledge without calling any tool.")
        + "\n\n";

    /// <summary>agy exposes MCP tools through its generic call_mcp_tool, not by name, and models otherwise reach for
    /// run_command (soft-denied in headless runs). Real run, agy 1.4.7: without this the job ended without a tool call.</summary>
    public static string ToolPreamble =>
        $"Your tools for this task are on the MCP server \"{JobWorkspaceFactory.ServerName}\": list them, then call them with call_mcp_tool " +
        $"(server \"{JobWorkspaceFactory.ServerName}\"). Do not use run_command, file-editing or browser tools; they are disabled for this job.\n\n";

    public ProcessStartInfo Headless(JobWorkspace ws, string? model = null, bool allowMeteredBilling = false, string? effort = null)
    {
        // --disable-slash-commands: a prompt starting with "/" must be text, not an agy command (which ends a stream with exit 2).
        var args = new List<string> { "--input-format", "stream-json", "--output-format", "stream-json", "--disable-slash-commands" };
        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (ModelCatalog.IsEffort(effort)) { args.Add("--effort"); args.Add(effort!); }
        var psi = CliLaunchBuilder.WrapForShim(_agyExecutable, args);
        psi.WorkingDirectory = ws.Root;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardInputEncoding = Utf8.NoBom;
        psi.StandardOutputEncoding = Utf8.NoBom;
        psi.StandardErrorEncoding = Utf8.NoBom;
        foreach (var v in VariablesToScrub(allowMeteredBilling)) psi.Environment.Remove(v);
        return psi;
    }

    /// <param name="hasBrief">The job's task is served by cockpit_job_brief; the command line only says to fetch it.</param>
    public ProcessStartInfo Interactive(JobWorkspace ws, InteractiveSession session, bool hasBrief, bool allowMeteredBilling = false)
    {
        string command = $"& {InteractiveSession.Ps(_agyExecutable)}"
            + (hasBrief ? " -i " + InteractiveSession.Ps(JobBriefToolProvider.Instruction) : "");
        string script = InteractiveSession.WriteScript(Path.Combine(ws.Root, "launch-agy.ps1"), ws.Root, VariablesToScrub(allowMeteredBilling),
            allowMeteredBilling, new Dictionary<string, string>(), ws.Root, command, session.Nonce);
        return CliLaunchBuilder.TerminalStartInfo(script, $"Cockpit {ws.JobId} (Antigravity)", ws.Root);
    }

    /// <summary>The allow rule that lets agy call the Cockpit's MCP tools without its own prompt (headless runs soft-deny
    /// unapproved tools). The Cockpit gate still approves, audits and journals every call.</summary>
    public static string CockpitAllowRule => $"mcp({JobWorkspaceFactory.ServerName}/*)";

    /// <summary>Adds <see cref="CockpitAllowRule"/> to ~/.gemini/antigravity-cli/settings.json permissions.allow, keeping
    /// everything else. Idempotent. Returns false (and changes nothing) when the file exists but is not a JSON object.</summary>
    public static bool EnsureCockpitToolsAllowed(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(home, ".gemini", "antigravity-cli", "settings.json");
        JsonObject root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { return false; }
        }
        else root = new JsonObject();
        var permissions = root["permissions"] as JsonObject ?? new JsonObject();
        var allow = permissions["allow"] as JsonArray ?? new JsonArray();
        if (allow.Any(n => n?.GetValueKind() == JsonValueKind.String && n.GetValue<string>() == CockpitAllowRule)) return true;
        allow.Add(JsonValue.Create(CockpitAllowRule)); // non-generic: reflection-based JSON is off in this app
        permissions["allow"] = allow;
        root["permissions"] = permissions;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}

/// <summary>
/// Parser for `agy --output-format stream-json` (agy 1.4.7, real capture in verify/fixtures/real): NDJSON objects
/// {"event":"init","conversation_id",init:{cwd,tools,permission_mode,model?}}, {"event":"step_update",step_update:{conversation_id,
/// step_index,state ACTIVE|DONE,step_type user_input|agent_response|tool|...,tool_name?,text_delta?,usage?,tool_info?:{name,parameters,
/// output,error?:{type,message}}}} and a final {"event":"result",result:{conversation_id,status SUCCESS|ERROR|...,response,error?,
/// duration_seconds,num_turns,usage:{input_tokens,output_tokens,thinking_tokens,cache_read_tokens}}}. Agent text arrives as deltas
/// and is emitted once per step when the step is DONE. Quota errors (429, RESOURCE_EXHAUSTED) become a <see cref="RateLimitInfo"/>.
/// </summary>
public sealed partial class GeminiStreamParser : ICliStreamParser
{
    private readonly StringBuilder _buffer = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<long, StringBuilder> _text = new();
    private readonly HashSet<long> _toolStarted = new();
    private bool _atStart = true;
    private bool _sawResult;
    private bool _sawText;
    private string? _session;
    private RateLimitInfo? _limit;
    private string? _lastError;

    public GeminiStreamParser(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    [GeneratedRegex(@"\b429\b|RESOURCE_EXHAUSTED|quota|rate.?limit|usage limit", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaText();

    [GeneratedRegex(@"retry (?:in|after) (\d+(?:\.\d+)?)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex RetryIn();

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
            if (c == '\n') Flush(events);
            else _buffer.Append(c);
        }
        return events;
    }

    public IReadOnlyList<CockpitStreamEvent> Complete()
    {
        var events = new List<CockpitStreamEvent>();
        Flush(events);
        foreach (var step in _text.Keys.ToList()) EmitText(step, events);
        if (!_sawResult && (_limit is not null || _lastError is not null))
        {
            _sawResult = true;
            events.Add(new RunResultEvent(_session, "error", true, _limit?.Message ?? _lastError, null, null, null, null, _limit));
        }
        return events;
    }

    private void Flush(List<CockpitStreamEvent> sink)
    {
        string line = _buffer.ToString().TrimEnd('\r').Trim();
        _buffer.Clear();
        if (line.Length > 0) ParseLine(line, sink);
    }

    private void EmitText(long step, List<CockpitStreamEvent> sink)
    {
        if (!_text.Remove(step, out var sb)) return;
        string text = sb.ToString().TrimEnd();
        if (text.Length == 0) return;
        _sawText = true;
        sink.Add(new AssistantTextEvent(_session, text));
    }

    private void ParseLine(string line, List<CockpitStreamEvent> sink)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex)
        {
            // Plain-text failures on stdout: still notice a quota message.
            NoteError(line);
            sink.Add(new ParseErrorEvent(line.Length > 300 ? line[..300] + "…" : line, ex.Message));
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { sink.Add(new ParseErrorEvent(line, "not a JSON object")); return; }
            _session = Str(root, "conversation_id") ?? _session;
            string? type = Str(root, "event");
            switch (type)
            {
                case "init":
                    var init = Obj(root, "init");
                    sink.Add(new SessionInitEvent(_session, init is { } i ? Str(i, "model") : null, null, init is { } i2 ? Str(i2, "permission_mode") : null, [], []));
                    return;
                case "step_update" when Obj(root, "step_update") is { } s:
                    Step(s, sink);
                    return;
                case "result" when Obj(root, "result") is { } r:
                    Result(r, sink);
                    return;
                default:
                    sink.Add(new UnknownEvent(_session, type ?? "", line.Length > 500 ? line[..500] + "…" : line));
                    return;
            }
        }
    }

    private void Step(JsonElement s, List<CockpitStreamEvent> sink)
    {
        _session = Str(s, "conversation_id") ?? _session;
        long index = Long(s, "step_index") ?? -1;
        bool done = Str(s, "state") == "DONE";
        switch (Str(s, "step_type"))
        {
            case "agent_response":
                if (Str(s, "text_delta") is { Length: > 0 } delta)
                {
                    if (!_text.TryGetValue(index, out var sb)) _text[index] = sb = new StringBuilder();
                    sb.Append(delta);
                }
                if (done) EmitText(index, sink);
                return;
            case "tool":
                var info = Obj(s, "tool_info");
                string id = $"{_session}:{index}";
                string name = (info is { } n ? Str(n, "name") : null) ?? Str(s, "tool_name") ?? "";
                if (_toolStarted.Add(index))
                    sink.Add(new ToolUseEvent(_session, id, name, info is { } p && p.TryGetProperty("parameters", out var prm) ? prm.Clone() : default));
                if (done)
                {
                    string output = "";
                    bool isError = false;
                    if (info is { } o)
                    {
                        if (o.TryGetProperty("output", out var ov)) output = ov.ValueKind == JsonValueKind.String ? ov.GetString()! : ov.GetRawText();
                        if (Obj(o, "error") is { } err) { isError = true; output = Str(err, "message") ?? output; NoteError(output); }
                    }
                    sink.Add(new ToolResultEvent(_session, id, isError, output));
                }
                return;
        }
    }

    private void Result(JsonElement r, List<CockpitStreamEvent> sink)
    {
        foreach (var step in _text.Keys.ToList()) EmitText(step, sink);
        _sawResult = true;
        string status = Str(r, "status") ?? "";
        bool isError = !status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
        string? response = Str(r, "response")?.TrimEnd();
        if (!_sawText && !isError && response is { Length: > 0 }) sink.Add(new AssistantTextEvent(_session, response));
        if (isError)
        {
            string? message = r.TryGetProperty("error", out var e)
                ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Object ? Str(e, "message") ?? e.GetRawText() : null)
                : null;
            NoteError(message ?? $"Antigravity run ended with status {status}");
        }
        double? seconds = r.TryGetProperty("duration_seconds", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
        sink.Add(new RunResultEvent(_session, status.ToLowerInvariant(), isError, isError ? _lastError : response,
            (int?)Long(r, "num_turns"), seconds is { } sec ? (long)Math.Round(sec * 1000) : null, null, Usage(r), _limit));
    }

    private void NoteError(string message)
    {
        _lastError = message;
        if (_limit is null && QuotaText().IsMatch(message)) _limit = new RateLimitInfo(message, ResetFrom(message));
    }

    /// <summary>"retry in 23.5s" → now + delay; "daily" quota → next midnight Pacific time (when Google's daily quotas reset); else unknown.</summary>
    private DateTimeOffset? ResetFrom(string message)
    {
        var now = _time.GetUtcNow();
        var m = RetryIn().Match(message);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return now.AddSeconds(Math.Ceiling(seconds));
        if (message.Contains("daily", StringComparison.OrdinalIgnoreCase) || message.Contains("per day", StringComparison.OrdinalIgnoreCase))
            return NextPacificMidnight(now);
        return null;
    }

    public static DateTimeOffset NextPacificMidnight(DateTimeOffset nowUtc)
    {
        TimeZoneInfo? pacific = null;
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { pacific = TimeZoneInfo.FindSystemTimeZoneById(id); break; }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        var offset = pacific?.GetUtcOffset(nowUtc) ?? TimeSpan.FromHours(-8);
        var local = nowUtc.ToOffset(offset);
        var midnight = new DateTimeOffset(local.Date.AddDays(1), offset);
        return midnight.ToUniversalTime();
    }

    /// <summary>Null when the result has no usage; a field agy did not report stays null (unknown).</summary>
    private static UsageTotals? Usage(JsonElement root)
    {
        if (Obj(root, "usage") is not { } u) return null;
        long? input = Long(u, "input_tokens"), output = Long(u, "output_tokens"), cached = Long(u, "cache_read_tokens");
        if (output is not null && Long(u, "thinking_tokens") is { } thinking) output += thinking;
        return input is null && output is null && cached is null ? null : new UsageTotals(input, output, cached, null);
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;
}
