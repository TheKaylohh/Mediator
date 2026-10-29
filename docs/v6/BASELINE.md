# V6 Baseline

The V5 tree as delivered (`mediator-v5.zip`), measured before any V6 change. Commit `72bbe3b`.

## Environment where everything in docs/v6 was measured

| Item | Value |
|---|---|
| OS | Ubuntu 24.04.5 LTS, kernel 6.18.44, x86_64, inside a cloud container |
| User | root (uid 0). Matters for permission tests: root bypasses file mode bits, so permission-failure tests drop to uid 65534 with `setpriv`. |
| CPU / RAM | 2 vCPU, 7.8 GiB |
| .NET | SDK 8.0.132, runtime 8.0.32 |
| Python | 3.13.16, venv with build123d 0.13.0, bpy 5.2.3 LTS (Blender as a Python module), PyMuPDF 2.0, SymPy 1.14.0 |
| ffmpeg / ffprobe | 6.1.1 |
| Claude Code CLI | 2.1.309 is installed, but this environment's own credentials are **not** used for any Mediator test |
| Gemini CLI | not installed at baseline |
| Isolation tooling | `unshare` and `prlimit` present. bubblewrap was absent; installed from apt (0.9.0) for V6. |

**Not available here.** None of these exist in this environment, so nothing that needs them can be run:
- Windows
- WPF
- SolidWorks
- DaVinci Resolve
- the Blender application (only the `bpy` module)
- Windows Sandbox and WSL
- an authenticated Claude or Gemini subscription session that Mediator may use

## Commands and results

```
bash verify/run-tests.sh                                   # offline suite
MEDIATOR_LIVE_PYTHON=/tmp/claude-0/pyv/bin/python bash verify/run-tests.sh   # + live suite
```

| Suite | Result | Wall time | Warnings |
|---|---|---|---|
| Offline | 40 / 40 pass | 41.5 s | 0 (build runs with TreatWarningsAsErrors) |
| Offline + live | 44 / 44 pass | 34.3 s | 0 |

Note on skips: when `MEDIATOR_LIVE_PYTHON` is unset, the four live tests are dropped from the list. The runner prints one line about it, and the count reads "All 40 tests passed". A skip is never reported as a pass, but it isn't counted as a skip either. V6 fixes this (see TEST_RESULTS.md).

## Architecture and trust boundaries (as found)

```
 hotkey overlay (WPF) / mediator CLI
          │ in-process
   MediatorRuntime ──► ProviderRouter ──► claude / gemini CLI (child process or terminal tab)
          │                                     │ stdio MCP
          │                              mediator-mcp-bridge (stateless)
          │                                     │ named pipe (current-user ACL) or loopback TCP (WSL)
   McpBusServer ── JobRegistry (token→grant) ── PermissionGate ── AuditLog (SQLite)
          │
   Tool providers: Files(journal) · SolidWorks(worker, COM STA) · Resolve · Blender · CodeCad · Tutor
          │
   External processes: python / blender / ffprobe via DefaultProcessRunner (job object, output cap, timeout)
```

Trust boundaries:
1. **Model output → bus.** All tool arguments are untrusted. They are validated against a schema, filtered by the job grant and checked by the gate.
2. **Bus → filesystem.** Providers resolve paths and check them against the job's granted roots.
3. **Bus → custom code.** `cad_generate_custom_step` runs model-written Python.
4. **Mediator → CLI.** Environment scrubbing keeps the CLI on subscription billing.
5. **Mediator ↔ terminal.** Interactive sessions coordinate through sentinel files.

## Defects and gaps found at baseline

Found by reading the code; each one is fixed or tracked in V6.

| # | Severity | Finding |
|---|---|---|
| B1 | P0 | `JobGrant.IsInsideRoots` and `FileJournalService.InsideRoots` are lexical prefix checks after `Path.GetFullPath`. A symlink, junction or bind mount *inside* a granted root that points outside passes. The journal's `ProtectedPathPolicy` rejects reparse-point ancestors for moves, but the Resolve, Blender, CodeCad, Tutor and SolidWorks providers have no reparse check at all. |
| B2 | P0 | `cad_generate_custom_step` runs model-written Python on the **host** interpreter (`CadPython ?? Python`) with `python -I` as the only restriction. Approval is the only control: there is no isolation boundary. |
| B3 | P0 | No durable operation record. When a tool call times out (`ExecutionTimeout`), the model sees "timed out" while the provider may still be running (for example a COM call). A retry can then duplicate a committed side effect, and nothing survives a Core restart. |
| B4 | P0 | Journal recovery is tested for one crash point only, and with a simulated "crash" (the service was disposed in-process). There is no fault injection at the other boundaries, no disk-full, locked-file or permission-failure test, and no check that the file is unchanged between plan and execute. |
| B5 | P1 | The bridge receives the job token as a command-line argument (`--scope <token>` in the MCP config). The token is bound to the job and current-user pipe, but argv is visible to other processes. |
| B6 | P1 | The interactive Claude prompt goes on the PowerShell command line (`claude ... '<prompt>'`). |
| B7 | P1 | Interactive sentinels have no session nonce and are not written atomically. A stale `interactive.done` from an earlier run, or a half-written file, can be misread. |
| B8 | P1 | Missing token counts are recorded as 0 (`?? 0` in the Gemini and Claude parsers), so "unknown" looks like "free". |
| B9 | P1 | Failover is not audited: there is no record of the original, attempted and final provider, or of why. |
| B10 | P1 | PDF reader has no resource limits (Flate output size, object count, recursion). A small hostile PDF can exhaust memory. |
| B11 | P1 | Logs, audit and exceptions have no redaction layer, and there is no support bundle. |
| B12 | P1 | SolidWorks STEP import does not check that the result has solid bodies (a surface-only import counts as success). It does not track which documents Mediator opened, or detect a same-named document that is already open. |
| B13 | P2 | Audit log has no tamper evidence. |
| B14 | P2 | No per-connection limit on concurrent in-flight requests on the bus. The frame length *is* checked before allocation (16 MiB cap). |
| B15 | P2 | Job grants are in memory only. A Core restart voids every token (fail-closed, but undocumented). |

Baseline outputs are kept in the session scratch area. They are not shipped because they contain environment paths.
