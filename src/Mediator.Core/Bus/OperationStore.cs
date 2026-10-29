using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mediator.Core.Bus;

/// <summary>
/// States of a side-effecting tool call. The provider is only ever invoked after <see cref="Executing"/> is durable,
/// so anything before it is known not to have run, and anything at or after it may have.
/// </summary>
public enum OperationState
{
    Pending,
    Validated,
    Approved,
    Executing,
    Committed,
    Failed,
    Rejected,
    Denied,
    Cancelled,
    CancellationRequested,
    OutcomeUnknown,
    RecoveryRequired,
}

public sealed record OperationRecord(
    string OperationId, string JobId, string Tool, string RequestKey, OperationState State, int Attempt,
    string CreatedUtc, string UpdatedUtc, string? Note, string? ResultJson, string? ApprovalJson, string ArgumentsJson = "{}");

public sealed class OperationStateException(string message) : InvalidOperationException(message);

/// <summary>
/// Durable record of every Write/Destructive tool call (V6). It is what makes a retry safe: an identical call
/// (same job, tool and canonical arguments) after a commit returns the stored result instead of running again, and
/// an identical call while an earlier one is executing or has an unknown outcome is refused. On start, calls that
/// were mid-flight when the Core stopped are reconciled: never-started ones become Cancelled (not run), started
/// ones become OutcomeUnknown and are handed to their provider's reconciler, else RecoveryRequired for the user.
/// </summary>
public sealed class OperationStore : IAsyncDisposable
{
    private static readonly Dictionary<OperationState, OperationState[]> Allowed = new()
    {
        [OperationState.Pending] = [OperationState.Validated, OperationState.Rejected, OperationState.Cancelled],
        [OperationState.Validated] = [OperationState.Approved, OperationState.Denied, OperationState.Cancelled, OperationState.Rejected],
        [OperationState.Approved] = [OperationState.Executing, OperationState.Cancelled, OperationState.Rejected],
        [OperationState.Executing] = [OperationState.Committed, OperationState.Failed, OperationState.OutcomeUnknown, OperationState.CancellationRequested],
        [OperationState.CancellationRequested] = [OperationState.Committed, OperationState.Failed, OperationState.OutcomeUnknown],
        [OperationState.OutcomeUnknown] = [OperationState.Committed, OperationState.Failed, OperationState.RecoveryRequired],
        [OperationState.RecoveryRequired] = [OperationState.Committed, OperationState.Failed],
    };

    /// <summary>States in which the side effect may have happened and the outcome is not known: never retry blindly.</summary>
    public static bool IsUnresolved(OperationState s) => s is OperationState.Executing or OperationState.CancellationRequested
        or OperationState.OutcomeUnknown or OperationState.RecoveryRequired;

    /// <summary>States of a call that is still being prepared in this process.</summary>
    public static bool IsInFlight(OperationState s) => s is OperationState.Pending or OperationState.Validated or OperationState.Approved;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string Snake(OperationState s) => s switch
    {
        OperationState.CancellationRequested => "cancellation_requested",
        OperationState.OutcomeUnknown => "outcome_unknown",
        OperationState.RecoveryRequired => "recovery_required",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static OperationState Parse(string s) => Enum.Parse<OperationState>(s.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);

    private OperationStore(SqliteConnection conn) => _conn = conn;

    /// <summary>Opens (or creates) the store. <paramref name="databasePath"/> = ":memory:" gives a non-durable store (bus unit tests only).</summary>
    public static async Task<OperationStore> OpenAsync(string databasePath, CancellationToken ct = default)
    {
        if (databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var conn = new SqliteConnection($"Data Source={databasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS operation (
                    op_id        TEXT PRIMARY KEY,
                    job_id       TEXT NOT NULL,
                    tool         TEXT NOT NULL,
                    request_key  TEXT NOT NULL,
                    state        TEXT NOT NULL,
                    attempt      INTEGER NOT NULL,
                    created_utc  TEXT NOT NULL,
                    updated_utc  TEXT NOT NULL,
                    note         TEXT,
                    result_json  TEXT,
                    approval_json TEXT,
                    arguments    TEXT NOT NULL DEFAULT '{}'
                );
                CREATE INDEX IF NOT EXISTS ix_operation_key ON operation(job_id, request_key, attempt);
                CREATE TABLE IF NOT EXISTS operation_transition (
                    op_id      TEXT NOT NULL,
                    seq        INTEGER NOT NULL,
                    from_state TEXT,
                    to_state   TEXT NOT NULL,
                    at_utc     TEXT NOT NULL,
                    note       TEXT,
                    PRIMARY KEY (op_id, seq)
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return new OperationStore(conn);
    }

    /// <summary>Identity of a request for de-duplication: job, tool and arguments with object keys sorted.</summary>
    public static string RequestKey(string jobId, string tool, JsonElement arguments)
    {
        var sb = new StringBuilder();
        sb.Append(jobId).Append('\n').Append(tool).Append('\n');
        Canonical(arguments, sb);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static void Canonical(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Canonical(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                int i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (i++ > 0) sb.Append(',');
                    Canonical(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.Number:
                sb.Append(e.TryGetDecimal(out var d) ? d.ToString("G29", CultureInfo.InvariantCulture) : e.GetRawText()); // G29 drops trailing zeros: 1.0 == 1
                break;
            default:
                sb.Append(e.GetRawText());
                break;
        }
    }

    /// <summary>
    /// Creates a new attempt in Pending, atomically refusing if an earlier attempt of the same request is still
    /// in flight or unresolved, or returning it if it committed. Exactly one of the results is non-null.
    /// </summary>
    public async Task<(OperationRecord? Created, OperationRecord? Existing)> BeginAsync(string jobId, string tool, string requestKey, string argumentsJson = "{}")
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var tx = _conn.BeginTransaction();
            OperationRecord? latest;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Select + " WHERE job_id = $j AND request_key = $k ORDER BY attempt DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.Parameters.AddWithValue("$k", requestKey);
                latest = (await ReadAsync(cmd).ConfigureAwait(false)).FirstOrDefault();
            }
            if (latest is not null && (latest.State == OperationState.Committed || IsUnresolved(latest.State) || IsInFlight(latest.State)))
            {
                tx.Rollback();
                return (null, latest);
            }
            string id = "op-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string now = Now();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO operation (op_id, job_id, tool, request_key, state, attempt, created_utc, updated_utc, arguments) VALUES ($id, $j, $t, $k, 'pending', $a, $now, $now, $args);";
                cmd.Parameters.AddWithValue("$args", Mediator.Core.Security.Redactor.Redact(argumentsJson));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$j", jobId);
                cmd.Parameters.AddWithValue("$t", tool);
                cmd.Parameters.AddWithValue("$k", requestKey);
                cmd.Parameters.AddWithValue("$a", (latest?.Attempt ?? 0) + 1);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await InsertTransitionAsync(tx, id, null, OperationState.Pending, null).ConfigureAwait(false);
            tx.Commit();
            return (new OperationRecord(id, jobId, tool, requestKey, OperationState.Pending, (latest?.Attempt ?? 0) + 1, now, now, null, null, null, argumentsJson), null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Moves an operation to a new state; throws <see cref="OperationStateException"/> on an illegal transition
    /// (including a stale one: the current state is re-read inside the transaction). Never cancelled: a skipped
    /// state write is exactly what recovery could not reconstruct.
    /// </summary>
    public async Task<OperationRecord> TransitionAsync(string opId, OperationState to, string? note = null, string? resultJson = null, string? approvalJson = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var tx = _conn.BeginTransaction();
            OperationRecord current;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Select + " WHERE op_id = $id;";
                cmd.Parameters.AddWithValue("$id", opId);
                current = (await ReadAsync(cmd).ConfigureAwait(false)).FirstOrDefault() ?? throw new OperationStateException($"No operation {opId}.");
            }
            if (!Allowed.TryGetValue(current.State, out var next) || !next.Contains(to))
            {
                tx.Rollback();
                throw new OperationStateException($"Operation {opId}: {Snake(current.State)} → {Snake(to)} is not allowed.");
            }
            string now = Now();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE operation SET state = $s, updated_utc = $now, note = COALESCE($note, note),
                        result_json = COALESCE($r, result_json), approval_json = COALESCE($ap, approval_json)
                    WHERE op_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$s", Snake(to));
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$r", (object?)resultJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ap", (object?)approvalJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", opId);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await InsertTransitionAsync(tx, opId, current.State, to, note).ConfigureAwait(false);
            tx.Commit();
            return current with { State = to, UpdatedUtc = now, Note = note ?? current.Note, ResultJson = resultJson ?? current.ResultJson };
        }
        finally { _gate.Release(); }
    }

    private async Task InsertTransitionAsync(SqliteTransaction tx, string opId, OperationState? from, OperationState to, string? note)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO operation_transition (op_id, seq, from_state, to_state, at_utc, note)
            VALUES ($id, (SELECT COALESCE(MAX(seq), 0) + 1 FROM operation_transition WHERE op_id = $id), $f, $t, $now, $note);
            """;
        cmd.Parameters.AddWithValue("$id", opId);
        cmd.Parameters.AddWithValue("$f", from is OperationState f ? Snake(f) : DBNull.Value);
        cmd.Parameters.AddWithValue("$t", Snake(to));
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<OperationRecord?> GetAsync(string opId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = Select + " WHERE op_id = $id;";
            cmd.Parameters.AddWithValue("$id", opId);
            return (await ReadAsync(cmd).ConfigureAwait(false)).FirstOrDefault();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<OperationRecord>> ListAsync(string? jobId = null, bool unresolvedOnly = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            var where = new List<string>();
            if (jobId is not null) { where.Add("job_id = $j"); cmd.Parameters.AddWithValue("$j", jobId); }
            if (unresolvedOnly) where.Add("state IN ('executing','cancellation_requested','outcome_unknown','recovery_required')");
            cmd.CommandText = Select + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY created_utc, rowid;";
            return await ReadAsync(cmd).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The user's verdict on an operation that needed a check (RecoveryRequired / OutcomeUnknown).</summary>
    public Task<OperationRecord> ResolveAsync(string opId, bool tookEffect, string note) =>
        TransitionAsync(opId, tookEffect ? OperationState.Committed : OperationState.Failed, "resolved by the user: " + note);

    public async Task<IReadOnlyList<(string? From, string To, string At, string? Note)>> HistoryAsync(string opId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT from_state, to_state, at_utc, note FROM operation_transition WHERE op_id = $id ORDER BY seq;";
            cmd.Parameters.AddWithValue("$id", opId);
            using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            var list = new List<(string?, string, string, string?)>();
            while (await r.ReadAsync().ConfigureAwait(false))
                list.Add((r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            return list;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Restart reconciliation (call once at Core start, before the bus accepts connections). Calls that never
    /// reached Executing are Cancelled ("not run"); calls that did become OutcomeUnknown, then each provider
    /// reconciler may settle them; whatever stays unknown becomes RecoveryRequired for the user.
    /// </summary>
    public async Task<(int NotRun, int Settled, int NeedsUser)> ReconcileAfterRestartAsync(Func<OperationRecord, Task<(OperationState State, string Note)?>>? reconcile)
    {
        int notRun = 0, settled = 0, needsUser = 0;
        foreach (var op in await ListAsync().ConfigureAwait(false))
        {
            if (IsInFlight(op.State))
            {
                await TransitionAsync(op.OperationId, OperationState.Cancelled, "Mediator stopped before this call ran; it was not run.").ConfigureAwait(false);
                notRun++;
                continue;
            }
            if (op.State is not (OperationState.Executing or OperationState.CancellationRequested or OperationState.OutcomeUnknown)) continue;
            var current = op;
            if (op.State != OperationState.OutcomeUnknown)
                current = await TransitionAsync(op.OperationId, OperationState.OutcomeUnknown, "Mediator stopped while this call was running; it may or may not have taken effect.").ConfigureAwait(false);
            var verdict = reconcile is null ? null : await reconcile(current).ConfigureAwait(false);
            if (verdict is { } v && v.State is OperationState.Committed or OperationState.Failed)
            {
                await TransitionAsync(op.OperationId, v.State, "reconciled after restart: " + v.Note).ConfigureAwait(false);
                settled++;
            }
            else
            {
                await TransitionAsync(op.OperationId, OperationState.RecoveryRequired,
                    "Needs your check: it may have taken effect. Inspect the result, then mark it done or not done (mediator-cli operations).").ConfigureAwait(false);
                needsUser++;
            }
        }
        return (notRun, settled, needsUser);
    }

    private const string Select = "SELECT op_id, job_id, tool, request_key, state, attempt, created_utc, updated_utc, note, result_json, approval_json, arguments FROM operation";

    private static async Task<List<OperationRecord>> ReadAsync(SqliteCommand cmd)
    {
        var list = new List<OperationRecord>();
        using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await r.ReadAsync().ConfigureAwait(false))
            list.Add(new OperationRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), r.GetInt32(5),
                r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11)));
        return list;
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _conn.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

/// <summary>Optional: a provider that can tell, after a restart, whether one of its interrupted operations took effect.</summary>
public interface IOperationReconciler
{
    Task<(OperationState State, string Note)?> ReconcileAsync(OperationRecord operation);
}
