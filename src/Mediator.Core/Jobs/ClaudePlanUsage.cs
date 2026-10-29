using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mediator.Core.Jobs;

/// <summary>One plan window: how much of it is used (0-100) and when it resets.</summary>
public sealed record PlanWindow(double Percent, DateTimeOffset? ResetsAt);

/// <summary>Claude's own plan meter (the numbers Claude Code's /usage shows). Problem is set when it could not be read.</summary>
public sealed record ClaudePlanSnapshot(string? Plan, PlanWindow? FiveHour, PlanWindow? Week, DateTimeOffset ReadAt, string? Problem)
{
    public static ClaudePlanSnapshot Failed(string problem, DateTimeOffset at) => new(null, null, null, at, problem);
}

/// <summary>
/// V14: reads the Claude subscription's 5-hour and weekly utilization the way Claude Code's /usage does: the account's
/// usage endpoint, authorized with the OAuth token Claude Code stored at sign-in (~/.claude/.credentials.json).
///
/// No model runs, so it uses no tokens and does not count against the limits it reports. The token is read for each
/// request, sent only to api.anthropic.com, and never logged or kept. Mediator never refreshes it: refreshing rotates
/// the refresh token and would sign Claude Code out; an expired token is renewed the next time Claude Code runs.
/// The endpoint is not a published API, so every failure becomes a readable <see cref="ClaudePlanSnapshot.Problem"/>.
/// Results are cached for <see cref="MinInterval"/> so opening the tab repeatedly sends at most one request a minute.
/// </summary>
public sealed class ClaudePlanUsage(HttpMessageHandler? handler = null, Func<string, string?>? env = null, TimeProvider? time = null)
{
    public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudePlanSnapshot? _last;

    /// <summary>~/.claude/.credentials.json, or under CLAUDE_CONFIG_DIR when set.</summary>
    public string CredentialsPath
    {
        get
        {
            string? dir = _env("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(_env("USERPROFILE") ?? _env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, ".credentials.json");
        }
    }

    public async Task<ClaudePlanSnapshot> GetAsync(bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!force && _last is { } cached && now - cached.ReadAt < MinInterval) return cached;
            _last = await ReadAsync(now, ct).ConfigureAwait(false);
            return _last;
        }
        finally { _gate.Release(); }
    }

    private async Task<ClaudePlanSnapshot> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        string? token, plan;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath, ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object)
                return ClaudePlanSnapshot.Failed("Claude Code is not signed in with a Claude subscription (Connectors > Sign In).", now);
            token = o.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            plan = o.TryGetProperty("subscriptionType", out var s) ? s.GetString() : null;
            if (o.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out long ms) && DateTimeOffset.FromUnixTimeMilliseconds(ms) < now)
                return ClaudePlanSnapshot.Failed("Claude Code's sign-in needs a refresh; it renews on the next Claude job (or open Claude Code once).", now) with { Plan = plan };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClaudePlanSnapshot.Failed("Claude Code is not signed in on this PC (Connectors > Sign In).", now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ClaudePlanSnapshot.Failed("Claude Code's sign-in file could not be read: " + ex.Message, now);
        }
        if (string.IsNullOrEmpty(token)) return ClaudePlanSnapshot.Failed("Claude Code's sign-in has no access token (Connectors > Sign In).", now);

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("Mediator");
        try
        {
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ClaudePlanSnapshot.Failed("Anthropic did not accept Claude Code's sign-in; it renews on the next Claude job.", now) with { Plan = plan };
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return ClaudePlanSnapshot.Failed("Anthropic asked to check less often; try again in a minute.", now) with { Plan = plan };
            if (!res.IsSuccessStatusCode)
                return ClaudePlanSnapshot.Failed($"The usage meter is unavailable right now (HTTP {(int)res.StatusCode}).", now) with { Plan = plan };
            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var five = Window(body.RootElement, "five_hour");
            var week = Window(body.RootElement, "seven_day");
            if (five is null && week is null) return ClaudePlanSnapshot.Failed("The usage meter answered without 5-hour or weekly figures.", now) with { Plan = plan };
            return new ClaudePlanSnapshot(plan, five, week, now, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return ClaudePlanSnapshot.Failed("Could not reach Anthropic to read the usage meter (offline?).", now) with { Plan = plan };
        }
    }

    internal static PlanWindow? Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return null;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return new PlanWindow(Math.Clamp(u.GetDouble(), 0, 100), resets);
    }
}
