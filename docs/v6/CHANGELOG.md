# Changelog: V5 → V6

V6 is a hardening release. It adds no new end-user features beyond `cockpit operations` and `cockpit support-bundle`.

## Security

- **Filesystem containment**: every provider and the journal now go through `PathAuthorizer`. It applies lexical rules, walks each path component, refuses reparse points and mounts, and protects the roots themselves. It opens files through kernel-checked calls (openat2 on Linux, handle checks on Windows) and moves them without replacing anything. This fixes B1: a link inside a granted root reached outside it. See FILESYSTEM_SECURITY.md.
- **Custom CAD code** now runs only in a sandbox. On Linux and WSL that is bubblewrap with prlimit, plus setpriv when Cockpit runs as root. If no sandbox is available the call is refused; it never falls back to the host interpreter. The approval is bound to the script hash, the arguments, the folder, the policy and the limits. Outputs are staged, inspected, structurally validated and re-imported in a fresh sandbox. See ISOLATION.md.
- **Revocation race fixed**: a Write call that was approved after its job was revoked used to run. It no longer does.
- **Bus hardening**:
  - an 8 KiB limit on the pre-authentication hello frame;
  - UTF-8 validated up front (invalid bytes used to drop the connection);
  - 32 requests in flight per connection.
- **Job token** is passed to the bridge in its environment. It is no longer in `--scope` on the command line. MCP config files are owner-only.
- **Interactive prompts** are never placed on a command line. The CLI fetches the task with the `cockpit_job_brief` tool.
- **PDF reader**:
  - a nesting bound (a crafted PDF used to overflow the stack and kill the process);
  - decompression caps (per stream and per document);
  - time, operand, page and text budgets.
- **Redaction** before anything is stored, and a support bundle with anonymized paths.

## Reliability and recovery

- **Durable operation store** (`operations.db`) for every Write or Destructive call:
  - a state machine that records each call's progress;
  - an identical call after a commit gets the stored result back instead of running again;
  - calls with an unknown outcome are never retried;
  - late results are recorded;
  - after a restart, interrupted calls are reconciled.
- **Journal**: fault injection at 8 crash boundaries with real process crashes, disk-full and permission faults, and a check that a file has not changed since approval. Rollback conflicts are now retried, and only one process may hold the journal.
- **Interactive sessions**: sentinels carry a session nonce and are written atomically. Stale files are cleared at launch.
- **Provider outcomes**: succeeded, result error, rate limited, auth failed, no result, or result followed by an abnormal exit.
- **Failover**:
  - mid-job only before any side effect, with the same grant, and at most once;
  - every decision is audited as `cockpit/route`.
- **Usage**: tokens a CLI did not report are recorded as unknown, never 0. A duplicate result counts once, and a run with no result is still recorded.
- **SolidWorks STEP import**:
  - the worker never saves or closes a document it did not create;
  - it refuses surface-only imports, duplicate target names and files that are already open;
  - each saved file is checked on disk.
- **Gemini CLI 0.65**: headless runs need a trusted folder. Cockpit trusts only its own job folder (`GEMINI_CLI_TRUST_WORKSPACE`), and only for that process.

## Observability

- **Audit**:
  - hash chain (`prev_hash`, `row_hash`) and `VerifyAsync`;
  - `synchronous=FULL`;
  - rotation by renaming the file, never deleting it;
  - `doctor` reports the chain head.
- **Overlay**:
  - a call whose outcome is unknown is shown in amber;
  - a killed job says a call may still finish in the app;
  - the routing history and the run outcome are shown on each job row.
- New CLI commands: `cockpit operations [--unresolved]`, `cockpit operations resolve <id> --done|--not-done --note …` and `cockpit support-bundle`.
- New MCP tools: `cockpit_operation_status` (granted to every job) and `cockpit_job_brief` (interactive jobs).

## Migrations (automatic, on first open)

| Store | Change | Data |
|---|---|---|
| `usage.db` | token columns become nullable; `outcome` column added | V5 rows kept; their zeros stay zeros (they cannot be told apart from real zeros) |
| `audit.db` | `prev_hash` and `row_hash` columns and an `audit_meta` table | V5 rows kept, unhashed; the chain starts at the first V6 row |
| `journal.db` | `roots_json` and `plan_mtime_ticks` columns | — |
| `operations.db` | new | — |

## Compatibility notes

- **Bridge arguments.** `cockpit-mcp-bridge` no longer accepts `--scope`. It reads `COCKPIT_JOB_TOKEN` from its environment instead. MCP configs written by V5 jobs will not work with a V6 bridge, but jobs are short-lived, so nothing persistent breaks.
- **Code-CAD.**
  - `cad_generate_custom_step` is refused on Windows unless the WSL sandbox is configured.
  - On Linux it needs bubblewrap.
  - Template parts use the sandbox when it is available and the host Python otherwise. Templates are fixed code; the model supplies only numbers.
- **API changes.**
  - `UsageTotals` fields are nullable, and `RunResultEvent.Usage` may be null.
  - `ProviderTotals` has `UnknownUsageRuns`.
  - `RouteDecision` has `Original` and `Reason`.
  - `ToolCallContext` has `ApprovedPreview`.
- **Tests.**
  - The test runner now reports PASS, FAIL and SKIP.
  - `COCKPIT_ONLY` accepts a comma-separated list.
  - With `COCKPIT_REQUIRE_ALL=1`, any skip counts as a failure.
