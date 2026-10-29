using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Security;
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

/// <summary>
/// Append-only audit trail of every tool call, stored next to the file journal. V6:
/// <list type="bullet">
/// <item>Arguments and details are redacted (<see cref="Redactor"/>) before they are written.</item>
/// <item>Each row carries <c>prev_hash</c> and <c>row_hash</c> = SHA-256(prev_hash ‖ row): a hash chain that
/// <see cref="VerifyAsync"/> checks. It detects edits, deletions, insertions and reordering of rows made by anything
/// that did not also recompute every later hash. It is NOT proof against someone with write access to the file who
/// rewrites the whole chain, and a truncated tail is only detectable against a head recorded elsewhere
/// (<see cref="HeadAsync"/>, shown by doctor and in the support bundle).</item>
/// <item>Each append is one transaction with synchronous=FULL: after a crash a row is either fully present, with a
/// valid hash, or absent.</item>
/// <item>Retention: when the file passes <see cref="AuditOptions.RotateAtBytes"/> it is renamed (never deleted) and a
/// new log starts whose first prev_hash is the old head, so the chain continues across files.</item>
/// </list>
/// </summary>
public sealed record AuditOptions
{
    public long RotateAtBytes { get; init; } = 256L * 1024 * 1024;
}

public sealed record AuditVerification(bool Ok, long RowsChecked, long? FirstBadId, string? Problem, string? Head);

public sealed class AuditLog : IAsyncDisposable
{
    public const int MaxArgumentChars = 64 * 1024;
    public const string Genesis = "genesis";

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AuditLog(SqliteConnection conn) => _conn = conn;

    /// <summary>Path of the archive the last open rotated into, if any.</summary>
    public string? RotatedTo { get; private set; }

    public static async Task<AuditLog> OpenAsync(string databasePath, CancellationToken ct = default) => await OpenAsync(databasePath, new AuditOptions(), ct).ConfigureAwait(false);

    public static async Task<AuditLog> OpenAsync(string databasePath, AuditOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        string? carriedHead = null, rotatedTo = null;
        if (File.Exists(databasePath) && new FileInfo(databasePath).Length >= options.RotateAtBytes)
        {
            await using (var old = await OpenCoreAsync(databasePath, null, ct).ConfigureAwait(false))
                carriedHead = await old.HeadAsync().ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            rotatedTo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
                Path.GetFileNameWithoutExtension(databasePath) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(databasePath));
            File.Move(databasePath, rotatedTo);
            foreach (var side in new[] { "-wal", "-shm" })
                if (File.Exists(databasePath + side)) File.Move(databasePath + side, rotatedTo + side);
        }
        var log = await OpenCoreAsync(databasePath, carriedHead, ct).ConfigureAwait(false);
        log.RotatedTo = rotatedTo;
        return log;
    }

    private static async Task<AuditLog> OpenCoreAsync(string databasePath, string? carriedHead, CancellationToken ct)
    {
        var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
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
            CREATE TABLE IF NOT EXISTS audit_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // V5 → V6: hash columns (V5 rows keep NULL hashes and are reported as unchained, before the first hashed row).
        foreach (var col in new[] { "prev_hash", "row_hash" })
        {
            using var has = conn.CreateCommand();
            has.CommandText = "SELECT COUNT(*) FROM pragma_table_info('audit') WHERE name = $c;";
            has.Parameters.AddWithValue("$c", col);
            if ((long)(await has.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE audit ADD COLUMN {col} TEXT;";
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        if (carriedHead is not null)
        {
            using var meta = conn.CreateCommand();
            meta.CommandText = "INSERT OR IGNORE INTO audit_meta (key, value) VALUES ('genesis', $h);";
            meta.Parameters.AddWithValue("$h", carriedHead);
            await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return new AuditLog(conn);
    }

    private static string RowHash(string prev, string ts, string job, string tool, string call, string risk, string args, string decision, string outcome, string? detail, long ms)
    {
        var sb = new StringBuilder(prev).Append('\u001e');
        foreach (var f in new[] { ts, job, tool, call, risk, args, decision, outcome, detail ?? "\u0000", ms.ToString(CultureInfo.InvariantCulture) })
            sb.Append(f).Append('\u001f');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private async Task<string> GenesisAsync(SqliteTransaction? tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM audit_meta WHERE key = 'genesis';";
        return await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string ?? Genesis;
    }

    /// <summary>The newest row's hash (the chain head), or the genesis value for an empty log.</summary>
    public async Task<string> HeadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await HeadCoreAsync(null).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string> HeadCoreAsync(SqliteTransaction? tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT row_hash FROM audit WHERE row_hash IS NOT NULL ORDER BY id DESC LIMIT 1;";
        return await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string ?? await GenesisAsync(tx).ConfigureAwait(false);
    }

    /// <summary>Walks the chain from the first hashed row. Unhashed V5 rows before it are counted but not verifiable.</summary>
    public async Task<AuditVerification> VerifyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string expectedPrev = await GenesisAsync(null).ConfigureAwait(false);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms, prev_hash, row_hash FROM audit ORDER BY id;";
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            long n = 0;
            bool chained = false;
            string? head = null;
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                n++;
                long id = r.GetInt64(0);
                string? prev = r.IsDBNull(11) ? null : r.GetString(11), hash = r.IsDBNull(12) ? null : r.GetString(12);
                if (prev is null || hash is null)
                {
                    if (chained) return new AuditVerification(false, n, id, "a row without a hash after the chain started", head);
                    continue; // V5 row
                }
                if (!chained) { chained = true; if (prev != expectedPrev && expectedPrev != Genesis) return new AuditVerification(false, n, id, "the first row does not continue the rotated log's head", head); }
                else if (prev != expectedPrev) return new AuditVerification(false, n, id, "prev_hash does not match the previous row: a row was removed, inserted or reordered", head);
                string actual = RowHash(prev, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10));
                if (actual != hash) return new AuditVerification(false, n, id, "the row's content does not match its hash: it was edited", head);
                expectedPrev = hash;
                head = hash;
            }
            return new AuditVerification(true, n, null, null, head ?? expectedPrev);
        }
        finally { _gate.Release(); }
    }

    public async Task RecordAsync(AuditEntry e)
    {
        string args = Redactor.Redact(e.ArgumentsJson);
        if (args.Length > MaxArgumentChars) args = args[..MaxArgumentChars] + "…[truncated]";
        string? detail = e.Detail is null ? null : Redactor.Redact(e.Detail);
        // Audit writes must not be skipped because a caller was cancelled.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var tx = _conn.BeginTransaction();
            string ts = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            string prev = await HeadCoreAsync(tx).ConfigureAwait(false);
            string hash = RowHash(prev, ts, e.JobId, e.Tool, e.CallId, e.Risk.ToString(), args, e.Decision, e.Outcome, detail, e.DurationMs);
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO audit (ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms, prev_hash, row_hash)
                VALUES ($ts, $job, $tool, $call, $risk, $args, $decision, $outcome, $detail, $ms, $prev, $hash);
                """;
            cmd.Parameters.AddWithValue("$prev", prev);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$job", e.JobId);
            cmd.Parameters.AddWithValue("$tool", e.Tool);
            cmd.Parameters.AddWithValue("$call", e.CallId);
            cmd.Parameters.AddWithValue("$risk", e.Risk.ToString());
            cmd.Parameters.AddWithValue("$args", args);
            cmd.Parameters.AddWithValue("$decision", e.Decision);
            cmd.Parameters.AddWithValue("$outcome", e.Outcome);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", e.DurationMs);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            tx.Commit();
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

    /// <summary>The newest <paramref name="limit"/> entries across all jobs, oldest first, and the total row count.</summary>
    public async Task<(IReadOnlyList<AuditRow> Rows, long Total)> RecentAsync(int limit)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var count = _conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM audit;";
            long total = (long)(await count.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms FROM audit ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<AuditRow>();
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
                rows.Add(new AuditRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10)));
            rows.Reverse();
            return (rows, total);
        }
        finally
        {
            _gate.Release();
        }
    }

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
