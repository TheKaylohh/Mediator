using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Cockpit.Core.Jobs;

/// <summary>
/// Estimates, not truth: providers do not expose remaining subscription quota, so the ledger records what each
/// run reported (tokens, turns, Claude Code's API-equivalent cost) and any rate-limit cooldown, for routing.
/// </summary>
public sealed class UsageLedger : IAsyncDisposable
{
    public const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;

        CREATE TABLE IF NOT EXISTS usage_run (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_utc                TEXT    NOT NULL,
            job_id                TEXT    NOT NULL,
            provider              TEXT    NOT NULL,       -- 'claude-code', 'gemini-cli', ...
            session_id            TEXT,
            model                 TEXT,
            subtype               TEXT,
            is_error              INTEGER NOT NULL,
            rate_limited          INTEGER NOT NULL,
            num_turns             INTEGER,
            duration_ms           INTEGER,
            input_tokens          INTEGER NOT NULL,
            output_tokens         INTEGER NOT NULL,
            cache_read_tokens     INTEGER NOT NULL,
            cache_creation_tokens INTEGER NOT NULL,
            notional_cost_usd     REAL                    -- API-equivalent estimate; not a charge on a subscription
        );
        CREATE INDEX IF NOT EXISTS ix_usage_provider ON usage_run(provider, ts_utc);

        CREATE TABLE IF NOT EXISTS provider_cooldown (
            provider   TEXT PRIMARY KEY,
            until_utc  TEXT NOT NULL,
            reason     TEXT
        );
        """;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;

    private UsageLedger(SqliteConnection conn, TimeProvider time) { _conn = conn; _time = time; }

    public static async Task<UsageLedger> OpenAsync(string databasePath, TimeProvider? time = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return new UsageLedger(conn, time ?? TimeProvider.System);
    }

    public async Task RecordRunAsync(string jobId, string provider, string? model, RunResultEvent run)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO usage_run (ts_utc, job_id, provider, session_id, model, subtype, is_error, rate_limited, num_turns, duration_ms,
                                       input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, notional_cost_usd)
                VALUES ($ts, $job, $prov, $sess, $model, $sub, $err, $rl, $turns, $dur, $in, $out, $cr, $cc, $cost);
                """;
            cmd.Parameters.AddWithValue("$ts", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$prov", provider);
            cmd.Parameters.AddWithValue("$sess", (object?)run.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sub", run.Subtype);
            cmd.Parameters.AddWithValue("$err", run.IsError);
            cmd.Parameters.AddWithValue("$rl", run.RateLimit is not null);
            cmd.Parameters.AddWithValue("$turns", run.NumTurns is int n ? n : DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", run.DurationMs is long d ? d : DBNull.Value);
            cmd.Parameters.AddWithValue("$in", run.Usage.InputTokens);
            cmd.Parameters.AddWithValue("$out", run.Usage.OutputTokens);
            cmd.Parameters.AddWithValue("$cr", run.Usage.CacheReadInputTokens);
            cmd.Parameters.AddWithValue("$cc", run.Usage.CacheCreationInputTokens);
            cmd.Parameters.AddWithValue("$cost", run.TotalCostUsd is double c ? c : DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetCooldownAsync(string provider, DateTimeOffset until, string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO provider_cooldown (provider, until_utc, reason) VALUES ($p, $u, $r)
                ON CONFLICT(provider) DO UPDATE SET until_utc = excluded.until_utc, reason = excluded.reason;
                """;
            cmd.Parameters.AddWithValue("$p", provider);
            cmd.Parameters.AddWithValue("$u", until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$r", reason);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The time until which a provider should not be used, or null if it is available now.</summary>
    public async Task<DateTimeOffset?> GetCooldownAsync(string provider)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT until_utc FROM provider_cooldown WHERE provider = $p;";
            cmd.Parameters.AddWithValue("$p", provider);
            if (await cmd.ExecuteScalarAsync().ConfigureAwait(false) is not string s) return null;
            var until = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return until > _time.GetUtcNow() ? until : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record ProviderTotals(int Runs, UsageTotals Tokens, double NotionalCostUsd, int RateLimitedRuns);

    public async Task<ProviderTotals> TotalsAsync(string provider, DateTimeOffset since)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(cache_read_tokens),0),
                       COALESCE(SUM(cache_creation_tokens),0), COALESCE(SUM(notional_cost_usd),0.0), COALESCE(SUM(rate_limited),0)
                FROM usage_run WHERE provider = $p AND ts_utc >= $since;
                """;
            cmd.Parameters.AddWithValue("$p", provider);
            cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            await r.ReadAsync().ConfigureAwait(false);
            return new ProviderTotals(r.GetInt32(0), new UsageTotals(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)), r.GetDouble(5), r.GetInt32(6));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Reads a headless CLI run's stdout, turns it into events for the overlay, records usage when the run ends, sets
/// a provider cooldown on a usage limit, and raises a billing alarm if the run authenticated with an API key
/// (Claude Code reports this in system/init; Gemini CLI does not, so for Gemini the environment scrub is the guard).
/// </summary>
public class CliRunMonitor
{
    private readonly string _jobId;
    private readonly UsageLedger _ledger;
    private readonly TimeProvider _time;
    private readonly ICliStreamParser _parser;
    private string? _model;

    public string Provider { get; }
    public event Action<string, CockpitStreamEvent>? EventReceived;
    /// <summary>Raised with the apiKeySource when a run is billed to an API key instead of the subscription.</summary>
    public event Action<string, string>? BillingAlarm;

    public RunResultEvent? LastResult { get; private set; }

    public CliRunMonitor(string jobId, string provider, UsageLedger ledger, ICliStreamParser parser, TimeProvider? time = null)
    {
        _jobId = jobId;
        Provider = provider;
        _ledger = ledger;
        _parser = parser;
        _time = time ?? TimeProvider.System;
    }

    public static CliRunMonitor For(AiProvider provider, string jobId, UsageLedger ledger, TimeProvider? time = null) => provider == AiProvider.ClaudeCode
        ? new ClaudeRunMonitor(jobId, ledger, time)
        : new CliRunMonitor(jobId, ProviderRouter.Id(AiProvider.GeminiCli), ledger, new GeminiStreamParser(time), time);

    public async Task ObserveAsync(TextReader output, CancellationToken ct)
    {
        var buffer = new char[8192];
        int n;
        while ((n = await output.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            foreach (var e in _parser.Feed(buffer.AsSpan(0, n))) await HandleAsync(e).ConfigureAwait(false);
        foreach (var e in _parser.Complete()) await HandleAsync(e).ConfigureAwait(false);
    }

    public async Task HandleAsync(CockpitStreamEvent e)
    {
        switch (e)
        {
            case SessionInitEvent init:
                _model = init.Model;
                if (init.UsesApiKey) BillingAlarm?.Invoke(_jobId, init.ApiKeySource!);
                break;
            case RunResultEvent result:
                LastResult = result;
                await _ledger.RecordRunAsync(_jobId, Provider, _model, result).ConfigureAwait(false);
                if (result.RateLimit is { } limit)
                    await _ledger.SetCooldownAsync(Provider, limit.ResetsAt ?? _time.GetUtcNow().AddHours(1), limit.Message).ConfigureAwait(false);
                break;
        }
        EventReceived?.Invoke(_jobId, e);
    }
}

/// <summary>Claude Code: the stream-json parser and the "claude-code" ledger key.</summary>
public sealed class ClaudeRunMonitor : CliRunMonitor
{
    public new const string Provider = "claude-code";

    public ClaudeRunMonitor(string jobId, UsageLedger ledger, TimeProvider? time = null)
        : base(jobId, Provider, ledger, new ClaudeStreamParser(), time) { }
}
