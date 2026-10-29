using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Cockpit.Core.Files;

public sealed record MoveRequest(string From, string To);

public sealed record RejectedMove(string From, string To, string Reason);

public sealed record PlannedOp(int Seq, string Kind, string? Src, string Dst, long? SizeBytes);

public sealed record PlanResult(string BatchId, IReadOnlyList<PlannedOp> Ops, IReadOnlyList<RejectedMove> Rejected, long TotalBytes);

public sealed record ExecuteResult(string BatchId, string Status, int Done, int Skipped, int Failed, int Pending, IReadOnlyList<string> Errors);

public sealed record RollbackResult(string BatchId, string Status, int Undone, IReadOnlyList<string> Conflicts);

public sealed record BatchStatus(string BatchId, string JobId, string Status, string CreatedUtc, string? Note,
    IReadOnlyDictionary<string, int> OpStates, long TotalBytes);

public sealed class JournalException : Exception
{
    public JournalException(string message) : base(message) { }
}

/// <summary>
/// Durable, reversible file moves for the D-drive organizer (Prompt 4).
///
/// Guarantees:
///  - Every move is recorded (with a content fingerprint) BEFORE it happens and marked done AFTER, in a SQLite
///    database with synchronous=FULL, so a crash at any point can be reconciled by <see cref="RecoverAsync"/>.
///  - Moves are same-volume renames only (atomic per file). Cross-volume moves, which would be copy+delete,
///    are refused at planning time. Nothing is ever overwritten and nothing is ever deleted except empty
///    folders the batch itself created.
///  - Rollback undoes completed operations in reverse order and refuses to overwrite: if a file changed or its
///    original path is now occupied, that operation is reported as a conflict and left alone.
/// A batch as a whole cannot be atomic on a file system; it is per-file atomic, resumable and idempotent.
/// </summary>
public sealed class FileJournalService : IAsyncDisposable
{
    public const int MaxOpsPerBatch = 50_000;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly SqliteConnection _conn;
    private readonly ProtectedPathPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1); // one batch operation at a time per journal

    public const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS batch (
            batch_id     TEXT    PRIMARY KEY,
            job_id       TEXT    NOT NULL,
            created_utc  TEXT    NOT NULL,
            status       TEXT    NOT NULL CHECK (status IN ('planned','executing','committed','partially_committed',
                                                            'rolling_back','rolled_back','partially_rolled_back')),
            note         TEXT,
            op_count     INTEGER NOT NULL,
            total_bytes  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS op (
            batch_id     TEXT    NOT NULL REFERENCES batch(batch_id),
            seq          INTEGER NOT NULL,
            kind         TEXT    NOT NULL CHECK (kind IN ('mkdir','move')),
            src_path     TEXT,              -- move: original location; NULL for mkdir
            dst_path     TEXT    NOT NULL,  -- move: new location; mkdir: folder created by this batch
            size_bytes   INTEGER,           -- recorded just before the move
            mtime_ticks  INTEGER,           -- last-write time (UTC ticks) just before the move
            fingerprint  TEXT,              -- 'xxh64:<hex>' (full) or 'xxh64p:<hex>' (size + first/last 4 MiB)
            state        TEXT    NOT NULL CHECK (state IN ('pending','done','undone','skipped','conflict','failed')),
            error        TEXT,
            done_utc     TEXT,
            undone_utc   TEXT,
            PRIMARY KEY (batch_id, seq)
        );

        CREATE INDEX IF NOT EXISTS ix_batch_job   ON batch(job_id, created_utc);
        CREATE INDEX IF NOT EXISTS ix_op_state    ON op(batch_id, state);
        """;

    private FileJournalService(SqliteConnection conn, ProtectedPathPolicy policy)
    {
        _conn = conn;
        _policy = policy;
    }

    public static async Task<FileJournalService> OpenAsync(string databasePath, ProtectedPathPolicy policy, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        var journal = new FileJournalService(conn, policy);
        await journal.RecoverAsync(ct).ConfigureAwait(false);
        return journal;
    }

    // ── Plan ──────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="allowedRoots">The job's folder roots (full paths ending in a separator). Source and destination must be inside.</param>
    public async Task<PlanResult> PlanAsync(string jobId, IReadOnlyList<MoveRequest> moves, IReadOnlyList<string> allowedRoots,
        string? note, CancellationToken ct)
    {
        if (moves.Count > MaxOpsPerBatch) throw new JournalException($"A batch may hold at most {MaxOpsPerBatch} moves.");
        _policy.ClearCache();

        var rejected = new List<RejectedMove>();
        var accepted = new List<(string Src, string Dst, long Size)>();
        var dstSeen = new HashSet<string>(PathComparer);
        var srcSeen = new HashSet<string>(PathComparer);

        foreach (var m in moves)
        {
            ct.ThrowIfCancellationRequested();
            string src, dst;
            try
            {
                src = Path.GetFullPath(m.From);
                dst = Path.GetFullPath(m.To);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejected.Add(new RejectedMove(m.From, m.To, $"invalid path: {ex.Message}"));
                continue;
            }

            string? reason =
                !InsideRoots(src, allowedRoots) ? "source is outside the job's allowed folders" :
                !InsideRoots(dst, allowedRoots) ? "destination is outside the job's allowed folders" :
                PathComparer.Equals(src, dst) ? "source and destination are the same" :
                !SameVolume(src, dst) ? "different drive: cross-volume moves are copy+delete and are refused" :
                File.Exists(dst) || Directory.Exists(dst) ? "destination already exists (the journal never overwrites)" :
                !dstSeen.Add(dst) ? "another move in this batch has the same destination" :
                !srcSeen.Add(src) ? "this file is moved twice in one batch" :
                _policy.CheckSource(src) is string s ? s :
                _policy.CheckDestination(dst) is string d ? $"destination: {d}" :
                null;

            if (reason is not null)
            {
                rejected.Add(new RejectedMove(src, dst, reason));
                continue;
            }
            accepted.Add((src, dst, new FileInfo(src).Length));
        }

        // A destination that is also some source in the batch would make order matter; refuse such chains.
        var srcSet = new HashSet<string>(accepted.Select(a => a.Src), PathComparer);
        var chained = accepted.Where(a => srcSet.Contains(a.Dst)).ToList();
        foreach (var c in chained)
        {
            accepted.Remove(c);
            rejected.Add(new RejectedMove(c.Src, c.Dst, "destination is another file's source in this batch (chained move)"));
        }

        // Folders to create, parent first, each recorded so rollback can remove it if it is still empty.
        var ops = new List<PlannedOp>();
        var created = new HashSet<string>(PathComparer);
        int seq = 0;
        foreach (var a in accepted)
        {
            foreach (var dir in MissingAncestors(Path.GetDirectoryName(a.Dst)!))
                if (created.Add(dir)) ops.Add(new PlannedOp(++seq, "mkdir", null, dir, null));
            ops.Add(new PlannedOp(++seq, "move", a.Src, a.Dst, a.Size));
        }

        string batchId = $"b{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28];
        long totalBytes = accepted.Sum(a => a.Size);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO batch (batch_id, job_id, created_utc, status, note, op_count, total_bytes) VALUES ($b, $j, $t, 'planned', $n, $c, $bytes);";
                cmd.Parameters.AddWithValue("$b", batchId);
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.Parameters.AddWithValue("$t", Now());
                cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", ops.Count);
                cmd.Parameters.AddWithValue("$bytes", totalBytes);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach (var op in ops)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO op (batch_id, seq, kind, src_path, dst_path, size_bytes, state) VALUES ($b, $s, $k, $src, $dst, $size, 'pending');";
                cmd.Parameters.AddWithValue("$b", batchId);
                cmd.Parameters.AddWithValue("$s", op.Seq);
                cmd.Parameters.AddWithValue("$k", op.Kind);
                cmd.Parameters.AddWithValue("$src", (object?)op.Src ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dst", op.Dst);
                cmd.Parameters.AddWithValue("$size", (object?)op.SizeBytes ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
        return new PlanResult(batchId, ops, rejected, totalBytes);
    }

    // ── Execute ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs pending operations of a planned (or partially committed) batch. Safe to call again to resume.</summary>
    public async Task<ExecuteResult> ExecuteAsync(string batchId, string jobId, IProgress<int>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string status = await GetOwnedStatusAsync(batchId, jobId, ct).ConfigureAwait(false);
            if (status is not ("planned" or "partially_committed"))
                throw new JournalException($"Batch {batchId} is '{status}' and cannot be executed.");
            await SetStatusAsync(batchId, "executing").ConfigureAwait(false);
            _policy.ClearCache();

            var errors = new List<string>();
            int processed = 0;
            foreach (var op in await LoadOpsAsync(batchId, "pending", descending: false, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break; // stop between files, never inside one
                try
                {
                    if (op.Kind == "mkdir")
                    {
                        if (Directory.Exists(op.Dst))
                        {
                            // Someone else created it since planning: not ours, so rollback must not remove it.
                            await SetOpStateAsync(batchId, op.Seq, "skipped", "folder already existed").ConfigureAwait(false);
                        }
                        else
                        {
                            Directory.CreateDirectory(op.Dst);
                            await SetOpStateAsync(batchId, op.Seq, "done", null).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await ExecuteMoveAsync(batchId, op, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JournalException)
                {
                    errors.Add($"{op.Src ?? op.Dst}: {ex.Message}");
                    await SetOpStateAsync(batchId, op.Seq, "failed", ex.Message).ConfigureAwait(false);
                }
                progress?.Report(++processed);
            }

            var counts = await CountStatesAsync(batchId).ConfigureAwait(false);
            string final = counts.GetValueOrDefault("pending") == 0 && counts.GetValueOrDefault("failed") == 0 ? "committed" : "partially_committed";
            await SetStatusAsync(batchId, final).ConfigureAwait(false);
            return new ExecuteResult(batchId, final, counts.GetValueOrDefault("done"), counts.GetValueOrDefault("skipped"),
                counts.GetValueOrDefault("failed"), counts.GetValueOrDefault("pending"), errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExecuteMoveAsync(string batchId, OpRow op, CancellationToken ct)
    {
        string src = op.Src!;
        // Re-validate at execution time: the file system may have changed since planning.
        if (_policy.CheckSource(src) is string reason) throw new JournalException($"no longer movable: {reason}");
        if (File.Exists(op.Dst) || Directory.Exists(op.Dst)) throw new JournalException("destination now exists");
        if (!SameVolume(src, op.Dst)) throw new JournalException("cross-volume move refused");

        var fp = await FileFingerprint.ComputeAsync(src, ct).ConfigureAwait(false);

        // Write-ahead: the fingerprint is durable before the rename, so recovery can tell whether it happened.
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE op SET size_bytes = $size, mtime_ticks = $mt, fingerprint = $fp WHERE batch_id = $b AND seq = $s;";
            cmd.Parameters.AddWithValue("$size", fp.Size);
            cmd.Parameters.AddWithValue("$mt", fp.LastWriteUtcTicks);
            cmd.Parameters.AddWithValue("$fp", fp.Hash);
            cmd.Parameters.AddWithValue("$b", batchId);
            cmd.Parameters.AddWithValue("$s", op.Seq);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        File.Move(src, op.Dst, overwrite: false); // same volume: an atomic rename
        await SetOpStateAsync(batchId, op.Seq, "done", null).ConfigureAwait(false);
    }

    // ── Rollback ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<RollbackResult> RollbackAsync(string batchId, string jobId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string status = await GetOwnedStatusAsync(batchId, jobId, ct).ConfigureAwait(false);
            if (status is not ("committed" or "partially_committed" or "partially_rolled_back"))
                throw new JournalException($"Batch {batchId} is '{status}'; only executed batches can be rolled back.");
            await SetStatusAsync(batchId, "rolling_back").ConfigureAwait(false);

            var conflicts = new List<string>();
            int undone = 0;
            foreach (var op in await LoadOpsAsync(batchId, "done", descending: true, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                string? conflict = op.Kind == "mkdir" ? UndoMkdir(op) : await UndoMoveAsync(op, ct).ConfigureAwait(false);
                if (conflict is null)
                {
                    await SetOpStateAsync(batchId, op.Seq, "undone", null, undone: true).ConfigureAwait(false);
                    undone++;
                }
                else
                {
                    conflicts.Add($"{op.Dst}: {conflict}");
                    await SetOpStateAsync(batchId, op.Seq, "conflict", conflict).ConfigureAwait(false);
                }
            }

            var counts = await CountStatesAsync(batchId).ConfigureAwait(false);
            string final = counts.GetValueOrDefault("done") == 0 && counts.GetValueOrDefault("conflict") == 0 ? "rolled_back" : "partially_rolled_back";
            await SetStatusAsync(batchId, final).ConfigureAwait(false);
            return new RollbackResult(batchId, final, undone, conflicts);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? UndoMkdir(OpRow op)
    {
        if (!Directory.Exists(op.Dst)) return null; // already gone
        if (Directory.EnumerateFileSystemEntries(op.Dst).Any()) return "folder is not empty (new files were added); left in place";
        try
        {
            Directory.Delete(op.Dst, recursive: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> UndoMoveAsync(OpRow op, CancellationToken ct)
    {
        string src = op.Src!;
        var current = new FileInfo(op.Dst);
        if (!current.Exists) return "file is no longer at the destination";
        if (File.Exists(src) || Directory.Exists(src)) return "something now occupies the original path; not overwritten";
        if (op.Fingerprint is null || op.SizeBytes is null || op.MtimeTicks is null) return "no fingerprint recorded; refusing to move blind";

        var recorded = new FileFingerprint(op.SizeBytes.Value, op.MtimeTicks.Value, op.Fingerprint);
        if (!recorded.QuickMatches(current)) return "file changed since it was moved (size or timestamp differs); left in place";
        var now = await FileFingerprint.ComputeAsync(op.Dst, ct).ConfigureAwait(false);
        if (now.Hash != recorded.Hash) return "file content changed since it was moved; left in place";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(src)!); // the original folder may have been removed
            File.Move(op.Dst, src, overwrite: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // ── Recovery and status ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reconciles batches interrupted by a crash or power loss. A pending move whose file is gone from the
    /// source and present at the destination with the recorded fingerprint did happen: mark it done. A done
    /// move in a rollback whose file is back at the source did get undone: mark it undone.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        var interrupted = new List<(string Id, string Status)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT batch_id, status FROM batch WHERE status IN ('executing','rolling_back');";
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) interrupted.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (id, status) in interrupted)
        {
            if (status == "executing")
            {
                foreach (var op in await LoadOpsAsync(id, "pending", false, ct).ConfigureAwait(false))
                {
                    bool happened = op.Kind == "mkdir"
                        ? Directory.Exists(op.Dst)
                        : op.Fingerprint is not null && !File.Exists(op.Src!) && File.Exists(op.Dst)
                          && (await FileFingerprint.ComputeAsync(op.Dst, ct).ConfigureAwait(false)).Hash == op.Fingerprint;
                    if (happened) await SetOpStateAsync(id, op.Seq, "done", "reconciled after interruption").ConfigureAwait(false);
                }
                await SetStatusAsync(id, "partially_committed").ConfigureAwait(false);
            }
            else
            {
                foreach (var op in await LoadOpsAsync(id, "done", true, ct).ConfigureAwait(false))
                {
                    bool undone = op.Kind == "mkdir" ? !Directory.Exists(op.Dst) : File.Exists(op.Src!) && !File.Exists(op.Dst);
                    if (undone) await SetOpStateAsync(id, op.Seq, "undone", "reconciled after interruption", undone: true).ConfigureAwait(false);
                }
                await SetStatusAsync(id, "partially_rolled_back").ConfigureAwait(false);
            }
        }
        return interrupted.Count;
    }

    public async Task<BatchStatus> GetStatusAsync(string batchId, string jobId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT job_id, status, created_utc, note, total_bytes FROM batch WHERE batch_id = $b;";
            cmd.Parameters.AddWithValue("$b", batchId);
            string owner, status, created;
            string? note;
            long bytes;
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.GetString(0) != jobId)
                    throw new JournalException($"No batch {batchId} for this job.");
                (owner, status, created, note, bytes) = (r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4));
            }
            return new BatchStatus(batchId, owner, status, created, note, await CountStatesAsync(batchId).ConfigureAwait(false), bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed record BatchSummary(string BatchId, string Status, string CreatedUtc, string? Note, int OpCount, long TotalBytes);

    /// <summary>A job's batches, newest first (for `cockpit rollback` and the overlay's job history).</summary>
    public async Task<IReadOnlyList<BatchSummary>> ListBatchesAsync(string jobId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT batch_id, status, created_utc, note, op_count, total_bytes FROM batch WHERE job_id = $j ORDER BY created_utc DESC, rowid DESC;";
            cmd.Parameters.AddWithValue("$j", jobId);
            var list = new List<BatchSummary>();
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new BatchSummary(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetInt64(5)));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A sample of a batch's moves for the approval preview.</summary>
    public async Task<IReadOnlyList<PlannedOp>> SampleOpsAsync(string batchId, string jobId, int max, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await GetOwnedStatusAsync(batchId, jobId, ct).ConfigureAwait(false);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT seq, kind, src_path, dst_path, size_bytes FROM op WHERE batch_id = $b AND kind = 'move' ORDER BY seq LIMIT $max;";
            cmd.Parameters.AddWithValue("$b", batchId);
            cmd.Parameters.AddWithValue("$max", max);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<PlannedOp>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new PlannedOp(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Data access helpers (callers hold _gate, except RecoverAsync which runs before any caller) ──────

    private sealed record OpRow(int Seq, string Kind, string? Src, string Dst, long? SizeBytes, long? MtimeTicks, string? Fingerprint);

    private async Task<string> GetOwnedStatusAsync(string batchId, string jobId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT job_id, status FROM batch WHERE batch_id = $b;";
        cmd.Parameters.AddWithValue("$b", batchId);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // Another job's batch is reported exactly like a missing one.
        if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.GetString(0) != jobId)
            throw new JournalException($"No batch {batchId} for this job.");
        return r.GetString(1);
    }

    private async Task<List<OpRow>> LoadOpsAsync(string batchId, string state, bool descending, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, kind, src_path, dst_path, size_bytes, mtime_ticks, fingerprint FROM op WHERE batch_id = $b AND state = $st ORDER BY seq {(descending ? "DESC" : "ASC")};";
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$st", state);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<OpRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new OpRow(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return rows;
    }

    private async Task SetOpStateAsync(string batchId, int seq, string state, string? error, bool undone = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = undone
            ? "UPDATE op SET state = $st, error = $e, undone_utc = $t WHERE batch_id = $b AND seq = $s;"
            : "UPDATE op SET state = $st, error = $e, done_utc = CASE WHEN $st = 'done' THEN $t ELSE done_utc END WHERE batch_id = $b AND seq = $s;";
        cmd.Parameters.AddWithValue("$st", state);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$s", seq);
        // Journal writes are never cancelled: a skipped write is exactly what recovery cannot reconstruct.
        await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SetStatusAsync(string batchId, string status)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE batch SET status = $st WHERE batch_id = $b;";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$b", batchId);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, int>> CountStatesAsync(string batchId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT state, COUNT(*) FROM op WHERE batch_id = $b GROUP BY state;";
        cmd.Parameters.AddWithValue("$b", batchId);
        using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await r.ReadAsync().ConfigureAwait(false)) counts[r.GetString(0)] = r.GetInt32(1);
        return counts;
    }

    private static IEnumerable<string> MissingAncestors(string dir)
    {
        var stack = new Stack<string>();
        for (string? d = dir; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) stack.Push(d);
        return stack; // parent first
    }

    private static bool InsideRoots(string fullPath, IReadOnlyList<string> roots)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string withSep = fullPath + Path.DirectorySeparatorChar;
        return roots.Any(r => withSep.StartsWith(Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar, cmp));
    }

    /// <summary>
    /// True only if both paths are on the same volume, so File.Move is a rename. On Windows this compares
    /// real volume mount paths (GetVolumePathNameW), because a folder like D:\Archive can be a mount point
    /// for another disk and a drive-letter comparison would let File.Move silently copy and delete.
    /// </summary>
    private static bool SameVolume(string a, string b)
    {
        if (IsUnc(a) || IsUnc(b)) return false;
        if (!PathComparer.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b))) return false;
        if (!OperatingSystem.IsWindows()) return true;
        string? va = Platform.Win32.VolumeOf(NearestExisting(a));
        string? vb = Platform.Win32.VolumeOf(NearestExisting(b));
        return va is not null && vb is not null && PathComparer.Equals(va, vb);
    }

    private static string NearestExisting(string path)
    {
        string? p = path;
        while (p is not null && !File.Exists(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
        return p ?? Path.GetPathRoot(path) ?? path;
    }

    private static bool IsUnc(string p) => p.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
