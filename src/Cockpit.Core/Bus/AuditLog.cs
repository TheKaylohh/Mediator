using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Cockpit.Core.Bus;

public sealed record AuditEntry(
    string JobId,
    string Tool,
    string CallId,
    ToolRisk Risk,
    string ArgumentsJson,
    string Decision,      // approved | denied | timed_out | cancelled | auto | rejected
    string Outcome,       // ok | tool_error | exception | timeout | not_run
    string? Detail,
    long DurationMs);

/// <summary>Append-only audit trail of every tool call, stored next to the file journal.</summary>
public sealed class AuditLog : IAsyncDisposable
{
    public const int MaxArgumentChars = 64 * 1024;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AuditLog(SqliteConnection conn) => _conn = conn;

    public static async Task<AuditLog> OpenAsync(string databasePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS audit (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_utc       TEXT    NOT NULL,
                job_id       TEXT    NOT NULL,
                tool         TEXT    NOT NULL,
                call_id      TEXT    NOT NULL,
                risk         TEXT    NOT NULL,
                arguments    TEXT    NOT NULL,
                decision     TEXT    NOT NULL,
                outcome      TEXT    NOT NULL,
                detail       TEXT,
                duration_ms  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_job ON audit(job_id, id);
            -- Append-only: refuse edits and deletes at the database level.
            CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return new AuditLog(conn);
    }

    public async Task RecordAsync(AuditEntry e)
    {
        string args = e.ArgumentsJson.Length > MaxArgumentChars ? e.ArgumentsJson[..MaxArgumentChars] + "…[truncated]" : e.ArgumentsJson;
        // Audit writes must not be skipped because a caller was cancelled.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit (ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms)
                VALUES ($ts, $job, $tool, $call, $risk, $args, $decision, $outcome, $detail, $ms);
                """;
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$job", e.JobId);
            cmd.Parameters.AddWithValue("$tool", e.Tool);
            cmd.Parameters.AddWithValue("$call", e.CallId);
            cmd.Parameters.AddWithValue("$risk", e.Risk.ToString());
            cmd.Parameters.AddWithValue("$args", args);
            cmd.Parameters.AddWithValue("$decision", e.Decision);
            cmd.Parameters.AddWithValue("$outcome", e.Outcome);
            cmd.Parameters.AddWithValue("$detail", (object?)e.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", e.DurationMs);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountAsync(string jobId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM audit WHERE job_id = $job;";
            cmd.Parameters.AddWithValue("$job", jobId);
            return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record AuditRow(long Id, string TimestampUtc, string JobId, string Tool, string CallId, string Risk,
        string ArgumentsJson, string Decision, string Outcome, string? Detail, long DurationMs);

    /// <summary>Every entry for a job, oldest first.</summary>
    public async Task<IReadOnlyList<AuditRow>> ListAsync(string jobId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms FROM audit WHERE job_id = $job ORDER BY id;";
            cmd.Parameters.AddWithValue("$job", jobId);
            var rows = new List<AuditRow>();
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
                rows.Add(new AuditRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10)));
            return rows;
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
