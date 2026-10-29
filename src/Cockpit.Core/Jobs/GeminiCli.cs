using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Protocol;

namespace Cockpit.Core.Jobs;

/// <summary>
/// Builds Gemini CLI launches. Gemini reads MCP servers from <c>.gemini/settings.json</c> in its working folder, so
/// it runs in the job workspace (which <see cref="JobWorkspaceFactory"/> wires to cockpit-mcp-bridge) and reaches the
/// user's files only through the Cockpit tools. Flags used (verify against `gemini --help` for your version):
/// <c>--output-format stream-json</c>, <c>--allowed-mcp-server-names cockpit</c>, <c>-m</c>, <c>-i</c> (interactive with a first prompt).
/// The prompt goes on stdin (piped input runs Gemini non-interactively), never on a command line.
/// </summary>
public sealed class GeminiLaunchBuilder
{
    private readonly string _geminiExecutable;
    private readonly bool _skipTrust;

    /// <param name="skipTrust">Pass --skip-trust (newer Gemini CLI): with folder trust enabled, Gemini ignores a project's
    /// .gemini/settings.json — and so the Cockpit's MCP server — in untrusted folders. The job workspace is the Cockpit's own
    /// folder. Alternative: trust %LOCALAPPDATA%\AiCockpit\jobs once in Gemini. `cockpit doctor` says which applies.</param>
    public GeminiLaunchBuilder(string geminiExecutable, bool skipTrust = false)
    {
        _geminiExecutable = geminiExecutable;
        _skipTrust = skipTrust;
    }

    /// <summary>Billing variables for Gemini specifically, scrubbed so the CLI uses your Google login (Google AI Pro), not an API key or Vertex.</summary>
    public static readonly string[] GoogleBillingVariables = ["GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_CLOUD_PROJECT", "GOOGLE_APPLICATION_CREDENTIALS"];

    public static IReadOnlyList<string> VariablesToScrub(bool allowMeteredBilling) =>
        allowMeteredBilling ? [] : CliLaunchBuilder.BillingVariables.Concat(GoogleBillingVariables).Distinct(StringComparer.Ordinal).ToList();

    /// <param name="workingDirectory">V10: the user's folder. Gemini runs in the job workspace (for its .gemini/settings.json),
    /// so the folder is added with --include-directories; Gemini's own read tools can then see it directly.</param>
    public ProcessStartInfo Headless(JobWorkspace ws, string? model = null, bool allowMeteredBilling = false, string? workingDirectory = null)
    {
        var args = new List<string> { "--output-format", "stream-json", "--allowed-mcp-server-names", JobWorkspaceFactory.ServerName };
        if (!string.IsNullOrEmpty(workingDirectory) && !string.Equals(Path.GetFullPath(workingDirectory).TrimEnd('\\', '/'), Path.GetFullPath(ws.Root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        { args.Add("--include-directories"); args.Add(Path.GetFullPath(workingDirectory)); }
        if (_skipTrust) args.Add("--skip-trust");
        if (model is not null) { args.Add("-m"); args.Add(model); }
        var psi = CliLaunchBuilder.WrapForShim(_geminiExecutable, args);
        psi.WorkingDirectory = ws.Root;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardInputEncoding = Utf8.NoBom;
        psi.StandardOutputEncoding = Utf8.NoBom;
        psi.StandardErrorEncoding = Utf8.NoBom;
        foreach (var v in VariablesToScrub(allowMeteredBilling)) psi.Environment.Remove(v);
        // Real capture (Gemini CLI 0.65.0, verify/fixtures/real): headless runs in an untrusted folder exit 55 before
        // doing anything. The job workspace is the Cockpit's own folder holding only its generated settings, so it is
        // trusted for this process only; the user's folders are never marked trusted.
        psi.Environment[TrustWorkspaceVariable] = "true";
        return psi;
    }

    public const string TrustWorkspaceVariable = "GEMINI_CLI_TRUST_WORKSPACE";

    /// <summary>Gemini CLI exit codes observed in real captures (0.65.0): 41 = no auth configured, 55 = untrusted folder.</summary>
    public const int ExitNoAuth = 41, ExitUntrustedFolder = 55;

    /// <param name="hasBrief">The job's task is served by cockpit_job_brief; the command line only says to fetch it.</param>
    public ProcessStartInfo Interactive(JobWorkspace ws, InteractiveSession session, bool hasBrief, bool allowMeteredBilling = false)
    {
        string command = $"& {InteractiveSession.Ps(_geminiExecutable)} --allowed-mcp-server-names {JobWorkspaceFactory.ServerName}" + (_skipTrust ? " --skip-trust" : "")
            + (hasBrief ? " -i " + InteractiveSession.Ps(JobBriefToolProvider.Instruction) : "");
        string script = InteractiveSession.WriteScript(Path.Combine(ws.Root, "launch-gemini.ps1"), ws.Root, VariablesToScrub(allowMeteredBilling),
            allowMeteredBilling, new Dictionary<string, string> { [TrustWorkspaceVariable] = "true" }, ws.Root, command, session.Nonce);
        return CliLaunchBuilder.TerminalStartInfo(script, $"Cockpit {ws.JobId} (Gemini)", ws.Root);
    }
}

/// <summary>
/// Parser for `gemini --output-format stream-json`: one JSON object per line with <c>type</c> init, message
/// (role, content, delta), tool_use (tool_name, tool_id, parameters), tool_result (tool_id, status, output),
/// error (severity, message) and result (status, stats). Also accepts the single-object `--output-format json`
/// shape ({response, stats, error}). Field names are from Gemini CLI's headless documentation as remembered when
/// this was written: capture a real run and add it as a fixture. Quota errors (429, RESOURCE_EXHAUSTED) become a
/// <see cref="RateLimitInfo"/> on the final <see cref="RunResultEvent"/>, synthesized if the stream ends without one.
/// </summary>
public sealed partial class GeminiStreamParser : ICliStreamParser
{
    private readonly StringBuilder _buffer = new();
    private readonly TimeProvider _time;
    private bool _atStart = true;
    private bool _sawResult;
    private string? _session;
    private RateLimitInfo? _limit;
    private string? _lastError;

    public GeminiStreamParser(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    [GeneratedRegex(@"\b429\b|RESOURCE_EXHAUSTED|quota|rate.?limit", RegexOptions.IgnoreCase)]
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
        if (line.Length > 0) sink.AddRange(ParseLine(line));
    }

    private IReadOnlyList<CockpitStreamEvent> ParseLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException ex)
        {
            // Gemini prints some failures as plain text on stdout; still notice a quota message.
            NoteError(line);
            return [new ParseErrorEvent(line.Length > 300 ? line[..300] + "…" : line, ex.Message)];
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [new ParseErrorEvent(line, "not a JSON object")];
            _session = Str(root, "session_id") ?? _session;
            string? type = Str(root, "type");
            if (type is null && root.TryGetProperty("response", out _)) return SingleObject(root);
            switch (type)
            {
                case "init":
                    return [new SessionInitEvent(_session, Str(root, "model"), null, null, [], [])];
                case "message":
                    return Str(root, "role") == "assistant" && Str(root, "content") is { Length: > 0 } text
                        ? [new AssistantTextEvent(_session, text)]
                        : [];
                case "tool_use":
                    return [new ToolUseEvent(_session, Str(root, "tool_id") ?? "", Str(root, "tool_name") ?? "",
                        root.TryGetProperty("parameters", out var p) ? p.Clone() : default)];
                case "tool_result":
                    string status = Str(root, "status") ?? "";
                    string output = root.TryGetProperty("output", out var o) ? (o.ValueKind == JsonValueKind.String ? o.GetString()! : o.GetRawText()) : "";
                    if (root.TryGetProperty("error", out var te) && te.ValueKind == JsonValueKind.Object) output = Str(te, "message") ?? output;
                    return [new ToolResultEvent(_session, Str(root, "tool_id") ?? "", !status.Equals("success", StringComparison.OrdinalIgnoreCase), output)];
                case "error":
                    string message = Str(root, "message") ?? line;
                    NoteError(message);
                    return [new SystemNoticeEvent(_session, "error:" + (Str(root, "severity") ?? "error"), message)];
                case "result":
                    _sawResult = true;
                    string resultStatus = Str(root, "status") ?? "";
                    bool isError = !resultStatus.Equals("success", StringComparison.OrdinalIgnoreCase);
                    if (isError && root.TryGetProperty("error", out var re) && re.ValueKind == JsonValueKind.Object) NoteError(Str(re, "message") ?? "");
                    return [new RunResultEvent(_session, resultStatus, isError, isError ? _lastError : null,
                        root.TryGetProperty("stats", out var st) ? (int?)Long(st, "tool_calls") : null,
                        root.TryGetProperty("stats", out var st2) ? Long(st2, "duration_ms") : null,
                        null, Usage(root), _limit)];
                default:
                    return [new UnknownEvent(_session, type ?? "", line.Length > 500 ? line[..500] + "…" : line)];
            }
        }
    }

    /// <summary>`--output-format json`: {"response": "...", "stats": {...}, "error": {"type","message","code"}}.</summary>
    private IReadOnlyList<CockpitStreamEvent> SingleObject(JsonElement root)
    {
        _sawResult = true;
        var events = new List<CockpitStreamEvent>();
        if (Str(root, "response") is { Length: > 0 } text) events.Add(new AssistantTextEvent(_session, text));
        bool isError = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object;
        if (isError) NoteError($"{Str(e, "message")} {(e.TryGetProperty("code", out var code) ? code.GetRawText() : "")}");
        events.Add(new RunResultEvent(_session, isError ? "error" : "success", isError, isError ? _lastError : null, null, null, null, Usage(root), _limit));
        return events;
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

    /// <summary>Null when the result has no stats; a field Gemini did not report stays null (unknown).</summary>
    private static UsageTotals? Usage(JsonElement root)
    {
        if (!root.TryGetProperty("stats", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        long? input = Long(s, "input_tokens"), output = Long(s, "output_tokens"), cached = Long(s, "cached") ?? Long(s, "cached_tokens");
        // json mode nests per-model token counts: stats.models.<model>.tokens.{prompt,candidates,cached}
        if (input is null && output is null && s.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
            foreach (var model in models.EnumerateObject())
                if (model.Value.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    input = Add(input, Long(t, "prompt"));
                    output = Add(output, Long(t, "candidates"));
                    cached = Add(cached, Long(t, "cached"));
                }
        return input is null && output is null && cached is null ? null : new UsageTotals(input, output, cached, null);
    }

    private static long? Add(long? a, long? b) => a is null ? b : b is null ? a : a + b;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;
}
