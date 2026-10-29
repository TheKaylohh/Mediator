# Operation Recovery

Two durable state machines make side effects safe to retry, safe to crash, and safe to cancel:
- the **operation store**, which covers every Write or Destructive tool call;
- the **file journal**, which covers every individual file move inside a batch.

Both are SQLite in WAL mode with `synchronous=FULL`. Every state write completes before the action it describes.

## 1. Operation store (`Mediator.Core.Bus.OperationStore`, `operations.db`)

```
pending ─► validated ─► approved ─► executing ─► committed
   │           │            │           │  ├────► failed            (the tool reported failure)
   │           │            │           │  ├────► outcome_unknown ─► committed | failed (late result) | recovery_required
   │           │            │           │  └────► cancellation_requested ─► committed | failed | outcome_unknown
   ▼           ▼            ▼
rejected    denied       cancelled                       recovery_required ─► committed | failed (user's verdict)
cancelled   cancelled
```

- **Illegal transitions throw.** The current state is re-read inside the transaction, so a stale writer cannot overwrite a newer state. Examples: `committed → executing` is a replay; `pending → executing` would skip approval.
- **`executing` is written before the provider is invoked.** So:
  - every state before `executing` means the call is known not to have run;
  - `executing` and every state after it mean the call may have run.
- **Request identity** is SHA-256 of the job, the tool, and the canonical arguments. Canonical means object keys are sorted and numbers normalized, so `1.0` equals `1`.

| An identical call arrives while the latest attempt is… | Result |
|---|---|
| `committed` | The stored result is returned with "already completed (operation X); not run again". Audit decision: `replayed`. |
| `pending` / `validated` / `approved` / `executing` / `cancellation_requested` / `outcome_unknown` / `recovery_required` | Refused: "may already have taken effect… do not retry; check with `mediator_operation_status`". Audited. |
| `failed` / `denied` / `rejected` / `cancelled` | A new attempt starts. These outcomes are known, and nothing was committed. |

**Timeouts and cancellation never claim the tool stopped:**

| Situation | What Mediator records | What the caller gets |
|---|---|---|
| Time limit exceeded | `outcome_unknown`. The provider is asked to stop and keeps being observed; a late result moves the operation to `committed` or `failed` and is audited as `late`. | "Outcome unknown, do not retry" |
| Client cancels or the bridge disconnects | `cancellation_requested`, then whatever really happened. If nothing is known after 5 s, `outcome_unknown` and observed late. | No response (MCP) |
| Provider throws | `outcome_unknown`, because partial effects are possible | "May have partly taken effect" |

**Restart reconciliation** runs in `MediatorRuntime.CreateAsync`, before the bus accepts connections:
- `pending`, `validated` and `approved` become `cancelled` with the note "Mediator stopped before this call ran; it was not run". This is safe because `executing` had not been written.
- `executing` and `cancellation_requested` become `outcome_unknown`. The provider's `IOperationReconciler` then gets a chance to settle them.
  - The Files provider settles `files_execute_batch` and `files_rollback_batch` from the journal.
  - Anything still unsettled becomes `recovery_required`. The runtime logs a warning, `mediator-cli operations --unresolved` lists it, and `mediator-cli operations resolve <id> --done|--not-done --note …` records the user's verdict (audited).

Job grants are in memory, so a Core restart invalidates every token (fail-closed). The operation records survive the restart. A job started again with the same id cannot replay an unresolved call.

## 2. File journal (`Mediator.Core.Files.FileJournalService`, `journal.db`)

**Batch states:**
- `planned` → `executing` → `committed` | `partially_committed`
- `committed`, `partially_committed` or `partially_rolled_back` → `rolling_back` → `rolled_back` | `partially_rolled_back`

**Op states:** `pending` → `done` | `skipped` | `failed`. During rollback, `done` → `undone` | `conflict`. A conflict is retried on the next rollback.

Crash boundaries are listed in `FileJournalService.Boundaries`, and each one has a fault point:

| Boundary | State on disk if the process dies here | Recovery |
|---|---|---|
| `plan.before_commit` | Plan transaction not committed | No batch exists; files untouched |
| `execute.after_status` | Batch `executing`, nothing moved | → `partially_committed`; resume runs every op |
| `mkdir.after_create` | Folder exists, op `pending` | Folder exists → op `done` |
| `move.after_fingerprint` | Fingerprint durable, file still at source | Op stays `pending`; resume moves it |
| `move.after_rename` | File at destination, op `pending` | Source gone, destination present, hash matches → `done` |
| `execute.before_final_status` | All ops recorded, batch `executing` | → `partially_committed`, then resume → `committed` |
| `rollback.after_status` | Batch `rolling_back` | → `partially_rolled_back`; rollback again |
| `undo.after_rename` | File back at source, op `done` | Source present, destination gone → `undone` |

**Further guarantees, V6:**
- **Same files at execute time.** A file whose size or timestamp changed after approval is `skipped` ("changed since the move was approved"); it is not moved.
- **Rollback never moves blind.** If the file at the destination changed since the move (checked by size, timestamp and hash), rollback leaves it in place and reports a conflict.
- **Moves never overwrite** (`renameat2 RENAME_NOREPLACE` on Linux), and both parent folders are fsynced.
- **The journal itself failing** (`SqliteException`, for example disk full) stops the batch immediately. It is reported as `JournalException: could not record progress`. The batch stays `executing` and is reconciled before it is touched again, whether in this process (a stale `executing` while the gate is held) or at the next start.
- **One process at a time:** an exclusive `journal.db.lock`.
- **Every path is re-authorized from the roots recorded with the batch** (see FILESYSTEM_SECURITY.md).

## 3. Tests (all run here; results in TEST_RESULTS.md)

| Test | Faults |
|---|---|
| `journal faults: real crash … every boundary` | 12 scenarios. A child process calls `Environment.FailFast` (SIGABRT, no finally blocks) at each of the 8 boundaries, at the 1st and at a middle hit where a boundary repeats. The parent checks that every file exists exactly once with its original content, that the status is stable, that resume completes, and that rollback restores the exact start, including removing created folders. |
| `concurrent modification` | A file edited between approval and execute is skipped. A file edited between the write-ahead fingerprint and the rename is not rolled back over. |
| `stale rollback / second process` | Rolling back the older of two chained batches conflicts; newest first works. A second open of the journal is refused. |
| `permission failure` | **Real**: the child runs as uid 65534 against a root-owned read-only folder. One op fails, the rest commit, rollback restores. |
| `disk full` | **Real**: the journal sits on a 1 MiB tmpfs that is then filled. Execute stops with "could not record progress", files stay consistent, and after space is freed it reconciles and completes. |
| `locked file (Windows)` | **SKIP**: mandatory sharing-violation locks are Windows-only. Written; not run. |
| `operations: state machine` | Illegal transitions refused; canonical keys; full history. |
| `operations: replay / duplicates` | Replay after commit; concurrent duplicate refused; exactly one side effect. |
| `operations: timeout` | A provider that ignores cancellation (a stand-in for a blocked COM call). Results: `outcome_unknown`, retry refused, late commit recorded, then replay. One side effect. |
| `operations: bridge killed` | The real bridge process is killed mid-call. The operation shows `cancellation_requested` (never `cancelled`); the real outcome is recorded; a reconnect replays. One side effect. |
| `operations: Core killed` | **Real**: a child Core process is SIGKILLed while executing. The store shows `executing`. Restart turns it into `outcome_unknown` and then `recovery_required`, keeping the full history. The identical call is refused, and the user's verdict is honoured. The journal reconciler settles a files op as committed. |

**Mutation checks**, run by hand:
- Disabling the reconciliation of moves makes the crash test fail at `move.after_rename#1`.
- Letting unresolved operations start new attempts makes the duplicate, timeout and Core-kill tests fail.

## 4. Limits (honest)

- **Power loss is not simulated.** Process crashes keep the OS page cache, so they don't test whether `fsync` actually reached the disk. The design relies on SQLite `synchronous=FULL` and on directory fsync after renames on Linux. On Windows, `MoveFileEx` isn't given `MOVEFILE_WRITE_THROUGH`, and NTFS metadata journaling is relied on instead. Not verified.
- **COM calls cannot be interrupted.** A SolidWorks call that hangs keeps its operation `outcome_unknown` until it returns or the worker is killed. The modal-dialog watchdog from V5 shortens the common case.
- **One-shot deduplication.** An identical, intentional repeat of a committed call (the same export twice) returns the first result. The model must change something, such as the name, to get a new one. This is deliberate: replaying a side effect without the user's knowledge is worse than refusing.
- **Arguments are stored** in `operations.db` for reconciliation. Redaction is in Phase 5 (THREAT_MODEL.md).
