using System.Globalization;
using System.Text.Json;
using Mediator.Core.Security;
using Microsoft.Data.Sqlite;

namespace Mediator.Core.Files;

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
    public JournalException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Test-only hooks. <see cref="FaultPoint"/> is called at each crash boundary of the state machine with the
/// boundary name (see <see cref="FileJournalService.Boundaries"/>); a test makes it kill the process. Never set
/// in production code.
/// </summary>
public sealed class JournalOptions
{
    public Action<string>? FaultPoint { get; init; }
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
///
/// V6 additions:
///  - Every path is authorized by a <see cref="PathAuthorizer"/> built from the roots recorded with the batch, at
///    plan time and again for every file at execute and rollback time. Renames are kernel-checked on Linux
///    (renameat2 RENAME_NOREPLACE beneath the root) and handle-verified on Windows.
///  - A file whose size or timestamp changed between approval and execution is skipped, not moved.
///  - One process at a time: the journal holds an exclusive lock file next to the database.
///  - A batch left 'executing' or 'rolling_back' (crash, or a journal write that failed) is reconciled before it
///    is touched again; it is never resumed blindly.
/// State machine and crash boundaries: docs/v6/OPERATION_RECOVERY.md.
/// </summary>
public sealed class FileJournalService : IAsyncDisposable
{
    public const int MaxOpsPerBatch = 50_000;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Crash boundaries, in execution order. Fault-injection tests kill the process at each one.</summary>
    public static readonly IReadOnlyList<string> Boundaries =
    [
        "plan.before_commit", "execute.after_status", "mkdir.after_create", "move.after_fingerprint",
        "move.after_rename", "execute.before_final_status", "rollback.after_status", "undo.after_rename",
    ];

    private readonly SqliteConnection _conn;
    private readonly ProtectedPathPolicy _policy;
    private readonly JournalOptions _options;
    private readonly FileStream _lock;
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

    private FileJournalService(SqliteConnection conn, ProtectedPathPolicy policy, JournalOptions options, FileStream lockFile)
    {
        _conn = conn;
        _policy = policy;
        _options = options;
        _lock = lockFile;
    }

    private void Fault(string boundary) => _options.FaultPoint?.Invoke(boundary);

    public static async Task<FileJournalService> OpenAsync(string databasePath, ProtectedPathPolicy policy, CancellationToken ct = default) =>
        await OpenAsync(databasePath, policy, new JournalOptions(), ct).ConfigureAwait(false);

    public static async Task<FileJournalService> OpenAsync(string databasePath, ProtectedPathPolicy policy, JournalOptions options, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        FileStream lockFile;
        try
        {
            // Exclusive for the life of the service: a second Mediator process (the CLI while the overlay runs, say)
            // must not interleave its own state transitions with ours.
            lockFile = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new JournalException($"The file journal {full} is in use by another Mediator process. Close it and try again.", ex);
        }
        try
        {
            var conn = new SqliteConnection($"Data Source={full}");
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = Schema;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await MigrateAsync(conn, ct).ConfigureAwait(false);
            var journal = new FileJournalService(conn, policy, options, lockFile);
            await journal.RecoverAsync(ct).ConfigureAwait(false);
            return journal;
        }
        catch
        {
            await lockFile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>V6 columns, added in place to a V5 database.</summary>
    private static async Task MigrateAsync(SqliteConnection conn, CancellationToken ct)
    {
        async Task<bool> HasColumn(string table, string column)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
            cmd.Parameters.AddWithValue("$c", column);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        }
        async Task Exec(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (!await HasColumn("batch", "roots_json").ConfigureAwait(false)) await Exec("ALTER TABLE batch ADD COLUMN roots_json TEXT;").ConfigureAwait(false);
        if (!await HasColumn("op", "plan_mtime_ticks").ConfigureAwait(false)) await Exec("ALTER TABLE op ADD COLUMN plan_mtime_ticks INTEGER;").ConfigureAwait(false);
    }

    /// <summary>The authorizer for a batch, rebuilt from the roots recorded when it was planned (fail-closed if they are gone).</summary>
    private async Task<PathAuthorizer> AuthorizerForAsync(string batchId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT roots_json FROM batch WHERE batch_id = $b;";
        cmd.Parameters.AddWithValue("$b", batchId);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (json is null) throw new JournalException($"Batch {batchId} was planned by an older version without recorded folder roots; plan it again.");
        var roots = JsonSerializer.Deserialize<string[]>(json) ?? [];
        try { return new PathAuthorizer(roots); }
        catch (ArgumentException ex) { throw new JournalException($"A folder this batch was planned for is no longer usable: {ex.Message}"); }
    }

    // ── Plan ──────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="paths">The job's path authorizer. Source and destination must both pass it.</param>
    public async Task<PlanResult> PlanAsync(string jobId, IReadOnlyList<MoveRequest> moves, PathAuthorizer paths,
        string? note, CancellationToken ct)
    {
        if (moves.Count > MaxOpsPerBatch) throw new JournalException($"A batch may hold at most {MaxOpsPerBatch} moves.");
        _policy.ClearCache();

        var rejected = new List<RejectedMove>();
        var accepted = new List<(string Src, string Dst, long Size, long Mtime)>();
        var dstSeen = new HashSet<string>(PathComparer);
        var srcSeen = new HashSet<string>(PathComparer);

        foreach (var m in moves)
        {
            ct.ThrowIfCancellationRequested();
            var sv = paths.Check(m.From, PathAccess.Modify);
            var dv = paths.Check(m.To, PathAccess.CreateFile);
            string src = sv.FullPath, dst = dv.FullPath;

            string? reason =
                !sv.Allowed ? "source: " + sv.Message :
                !dv.Allowed ? "destination: " + (dv.Code == "exists" ? "destination already exists (the journal never overwrites)" : dv.Message) :
                !File.Exists(src) ? "source is not a file" :
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
            var fi = new FileInfo(src);
            accepted.Add((src, dst, fi.Length, fi.LastWriteTimeUtc.Ticks));
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
        var mtimes = new Dictionary<int, long>();
        var created = new HashSet<string>(PathComparer);
        int seq = 0;
        foreach (var a in accepted)
        {
            foreach (var dir in MissingAncestors(Path.GetDirectoryName(a.Dst)!))
                if (created.Add(dir)) ops.Add(new PlannedOp(++seq, "mkdir", null, dir, null));
            ops.Add(new PlannedOp(++seq, "move", a.Src, a.Dst, a.Size));
            mtimes[seq] = a.Mtime;
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
                cmd.CommandText = "INSERT INTO batch (batch_id, job_id, created_utc, status, note, op_count, total_bytes, roots_json) VALUES ($b, $j, $t, 'planned', $n, $c, $bytes, $roots);";
                cmd.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(paths.Roots));
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
                cmd.CommandText = "INSERT INTO op (batch_id, seq, kind, src_path, dst_path, size_bytes, plan_mtime_ticks, state) VALUES ($b, $s, $k, $src, $dst, $size, $pmt, 'pending');";
                cmd.Parameters.AddWithValue("$pmt", mtimes.TryGetValue(op.Seq, out long pm) ? pm : DBNull.Value);
                cmd.Parameters.AddWithValue("$b", batchId);
                cmd.Parameters.AddWithValue("$s", op.Seq);
                cmd.Parameters.AddWithValue("$k", op.Kind);
                cmd.Parameters.AddWithValue("$src", (object?)op.Src ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dst", op.Dst);
                cmd.Parameters.AddWithValue("$size", (object?)op.SizeBytes ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            Fault("plan.before_commit");
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
            return await ExecuteLockedAsync(batchId, jobId, progress, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // The journal itself cannot be written (disk full, I/O error). Stop at once: continuing would move
            // files the journal cannot record. The batch stays 'executing' on disk and is reconciled from the
            // file system on the next attempt or the next start.
            throw new JournalException($"The file journal could not record progress ({ex.Message}). No further files were moved; " +
                "the batch will be reconciled from the disk before it is touched again.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ExecuteResult> ExecuteLockedAsync(string batchId, string jobId, IProgress<int>? progress, CancellationToken ct)
    {
        {
            string status = await GetOwnedStatusAsync(batchId, jobId, ct).ConfigureAwait(false);
            if (status is "executing" or "rolling_back")
            {
                // We hold the gate and the process lock, so nothing is running this batch: the state is stale
                // (a crash, or a journal write that failed). Reconcile from the file system before going on.
                status = await ReconcileBatchAsync(batchId, status, ct).ConfigureAwait(false);
            }
            if (status is not ("planned" or "partially_committed"))
                throw new JournalException($"Batch {batchId} is '{status}' and cannot be executed.");
            var paths = await AuthorizerForAsync(batchId, ct).ConfigureAwait(false);
            await SetStatusAsync(batchId, "executing").ConfigureAwait(false);
            Fault("execute.after_status");
            _policy.ClearCache();

            var errors = new List<string>();
            int processed = 0;
            {
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
                                paths.EnsureDirectory(op.Dst);
                                Fault("mkdir.after_create");
                                await SetOpStateAsync(batchId, op.Seq, "done", null).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await ExecuteMoveAsync(batchId, op, paths, ct).ConfigureAwait(false);
                        }
                    }
                    catch (SkipException skip)
                    {
                        errors.Add($"{op.Src}: {skip.Message}");
                        await SetOpStateAsync(batchId, op.Seq, "skipped", skip.Message).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JournalException)
                    {
                        errors.Add($"{op.Src ?? op.Dst}: {ex.Message}");
                        await SetOpStateAsync(batchId, op.Seq, "failed", ex.Message).ConfigureAwait(false);
                    }
                    progress?.Report(++processed);
                }
            }

            Fault("execute.before_final_status");
            var counts = await CountStatesAsync(batchId).ConfigureAwait(false);
            string final = counts.GetValueOrDefault("pending") == 0 && counts.GetValueOrDefault("failed") == 0 ? "committed" : "partially_committed";
            await SetStatusAsync(batchId, final).ConfigureAwait(false);
            return new ExecuteResult(batchId, final, counts.GetValueOrDefault("done"), counts.GetValueOrDefault("skipped"),
                counts.GetValueOrDefault("failed"), counts.GetValueOrDefault("pending"), errors);
        }
    }

    private sealed class SkipException(string message) : Exception(message);

    private async Task ExecuteMoveAsync(string batchId, OpRow op, PathAuthorizer paths, CancellationToken ct)
    {
        string src = op.Src!;
        // Re-validate at execution time: the file system may have changed since planning.
        var sv = paths.Check(src, PathAccess.Modify);
        if (!sv.Allowed) throw new JournalException("no longer movable: " + sv.Message);
        var dv = paths.Check(op.Dst, PathAccess.CreateFile);
        if (!dv.Allowed) throw new JournalException(dv.Code == "exists" ? "destination now exists" : "destination: " + dv.Message);
        if (_policy.CheckSource(src) is string reason) throw new JournalException($"no longer movable: {reason}");
        if (!SameVolume(src, op.Dst)) throw new JournalException("cross-volume move refused");

        // The user approved moving the file as it was when planned. If it changed since, it is not the approved file.
        var info = new FileInfo(src);
        if (op.SizeBytes is long plannedSize && op.PlanMtimeTicks is long plannedMtime
            && (info.Length != plannedSize || info.LastWriteTimeUtc.Ticks != plannedMtime))
            throw new SkipException("changed since the move was approved (size or timestamp differs); not moved");

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
        Fault("move.after_fingerprint");

        paths.MoveNoReplace(src, op.Dst); // same volume: an atomic, no-overwrite rename
        Fault("move.after_rename");
        await SetOpStateAsync(batchId, op.Seq, "done", null).ConfigureAwait(false);
    }

    // ── Rollback ──────────────────────────────────────────────────────────────────────────────────────

    public async Task<RollbackResult> RollbackAsync(string batchId, string jobId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RollbackLockedAsync(batchId, jobId, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw new JournalException($"The file journal could not record the rollback ({ex.Message}). It stopped; the batch will be reconciled before it is touched again.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RollbackResult> RollbackLockedAsync(string batchId, string jobId, CancellationToken ct)
    {
        {
            string status = await GetOwnedStatusAsync(batchId, jobId, ct).ConfigureAwait(false);
            if (status is "executing" or "rolling_back") status = await ReconcileBatchAsync(batchId, status, ct).ConfigureAwait(false);
            if (status is not ("committed" or "partially_committed" or "partially_rolled_back"))
                throw new JournalException($"Batch {batchId} is '{status}'; only executed batches can be rolled back.");
            var paths = await AuthorizerForAsync(batchId, ct).ConfigureAwait(false);
            await SetStatusAsync(batchId, "rolling_back").ConfigureAwait(false);
            Fault("rollback.after_status");

            var conflicts = new List<string>();
            int undone = 0;
            // Earlier conflicts are retried: a conflict means "not safe now" (for example a later batch had moved the
            // file on), and rolling that later batch back first can make it safe.
            var toUndo = (await LoadOpsAsync(batchId, "done", descending: true, ct).ConfigureAwait(false))
                .Concat(await LoadOpsAsync(batchId, "conflict", descending: true, ct).ConfigureAwait(false))
                .Where(o => o.Fingerprint is not null || o.Kind == "mkdir")
                .OrderByDescending(o => o.Seq).ToList();
            foreach (var op in toUndo)
            {
                if (ct.IsCancellationRequested) break;
                string? conflict = op.Kind == "mkdir" ? UndoMkdir(op, paths) : await UndoMoveAsync(op, paths, ct).ConfigureAwait(false);
                if (conflict is null && op.Kind == "move") Fault("undo.after_rename");
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
    }

    private static string? UndoMkdir(OpRow op, PathAuthorizer paths)
    {
        if (!Directory.Exists(op.Dst) && !File.Exists(op.Dst)) return null; // already gone
        try
        {
            return paths.RemoveEmptyDirectory(op.Dst) ? null : "folder is not empty (new files were added); left in place";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> UndoMoveAsync(OpRow op, PathAuthorizer paths, CancellationToken ct)
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
            string parent = Path.GetDirectoryName(src)!;
            if (!Directory.Exists(parent)) paths.EnsureDirectory(parent); // the original folder may have been removed
            paths.MoveNoReplace(op.Dst, src);
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

        foreach (var (id, status) in interrupted) await ReconcileBatchAsync(id, status, ct).ConfigureAwait(false);
        return interrupted.Count;
    }

    /// <summary>Reconciles one interrupted batch from the file system and returns its new status.</summary>
    private async Task<string> ReconcileBatchAsync(string id, string status, CancellationToken ct)
    {
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
                return "partially_committed";
            }
            else
            {
                foreach (var op in await LoadOpsAsync(id, "done", true, ct).ConfigureAwait(false))
                {
                    bool undone = op.Kind == "mkdir" ? !Directory.Exists(op.Dst) : File.Exists(op.Src!) && !File.Exists(op.Dst);
                    if (undone) await SetOpStateAsync(id, op.Seq, "undone", "reconciled after interruption", undone: true).ConfigureAwait(false);
                }
                await SetStatusAsync(id, "partially_rolled_back").ConfigureAwait(false);
                return "partially_rolled_back";
            }
        }
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

    /// <summary>A job's batches, newest first (for `mediator-cli rollback` and the overlay's job history).</summary>
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

    /// <summary>Every job's batches, newest first, at most <paramref name="limit"/> (support bundle; no paths).</summary>
    public async Task<IReadOnlyList<(string JobId, BatchSummary Batch)>> ListAllBatchesAsync(int limit, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT job_id, batch_id, status, created_utc, note, op_count, total_bytes FROM batch ORDER BY created_utc DESC, rowid DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", limit);
            var list = new List<(string, BatchSummary)>();
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add((r.GetString(0), new BatchSummary(r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt64(6))));
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

    private sealed record OpRow(int Seq, string Kind, string? Src, string Dst, long? SizeBytes, long? MtimeTicks, string? Fingerprint, long? PlanMtimeTicks);

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
        cmd.CommandText = $"SELECT seq, kind, src_path, dst_path, size_bytes, mtime_ticks, fingerprint, plan_mtime_ticks FROM op WHERE batch_id = $b AND state = $st ORDER BY seq {(descending ? "DESC" : "ASC")};";
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$st", state);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<OpRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new OpRow(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7)));
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
        finally
        {
            await _lock.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }
}
