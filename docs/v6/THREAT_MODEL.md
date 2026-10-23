# Threat Model

## Assets

| Asset | Why it matters |
|---|---|
| The user's files (documents, CAD, media, repositories) | Loss, corruption or exfiltration is the worst outcome |
| The user's open application state (SolidWorks documents, Resolve projects) | Closing or saving the wrong document destroys unsaved work |
| Subscription accounts and any API keys in the environment | Silent metered billing; account abuse |
| Job tokens | The capability to call Cockpit tools for one job |
| Audit trail, file journal, operation store | Evidence, and the only route back after a mistake or a crash |
| The host itself (processes, credentials, network) | Model-written code must not reach it |

## Actors

| Actor | Capability | Trust |
|---|---|---|
| The user | Approves actions in the overlay or CLI | Trusted; can be misled by a preview, so previews are built from Cockpit's data and not from the model's claims |
| The model, via Claude Code or Gemini CLI | Any tool call within its job's grant; chooses arguments | **Untrusted**: prompt injection from web pages, PDFs or file contents can steer it |
| Content the model reads (PDFs, STEP files, media, timelines) | Crafted input to parsers | **Untrusted** |
| Other local processes as the same user | Can read process command lines, connect to loopback TCP, read user-owned files | Partly trusted. A same-user attacker who wants to can read the user's files anyway; the aim is not to hand them extra capability. |
| Another user or a remote host | Network access | Untrusted. The pipe ACL excludes them, and TCP binds to loopback only, for WSL jobs only. |
| A local administrator, or malware running as the user | Everything | Out of scope. Stated where it matters (audit). |

## Trust boundaries and controls

| # | Boundary | Threats | Controls (V6) | Evidence |
|---|---|---|---|---|
| 1 | Model → bus | Ungranted tools; invented arguments; floods; malformed frames | Grant allow-list on every call; schema validation; 8 KiB hello limit; 16 MiB frame limit checked before allocation; UTF-8 validated; 32 in-flight requests per connection; duplicate request ids refused by value | bus tests |
| 2 | Bus → approval | Approving something other than what runs; acting after revocation | Previews built from Cockpit state; subset approval executes only that subset; Destructive is never auto-approved; the grant is checked again just before execution; custom code is bound to a hash of the approved content | gate, overlay, isolation and revocation tests |
| 3 | Bus → filesystem | Links, junctions, mounts or races escaping the roots; overwrite; deletion | `PathAuthorizer`: lexical rules, component walk, reparse and mount refusal, kernel-checked opens (openat2) or handle checks (Windows), no-replace renames, roots themselves protected | fs-auth tests (Linux real; Windows NOT RUN) |
| 4 | Bus → custom code | Code that reads secrets, uses the network, writes outside its folder, forks or exhausts resources | bubblewrap sandbox required (refused, never downgraded); private staging; output inspection; independent re-import | isolation tests (Linux real; WSL NOT RUN) |
| 5 | Parsers ← content | Zip bombs, deep nesting, huge counts, malformed files | PDF limits and depth bound, fuzzed; STEP structural check; FCPXML DTDs ignored and no resolver; exact-maths input limits | pdf, tutor and STEP tests |
| 6 | Cockpit → CLI | Metered billing; leaked secrets | Billing variables scrubbed (headless and the `.ps1`); `apiKeySource` alarm kills the job unless opted in; token in the environment, not argv; prompt on stdin or via `cockpit_job_brief`, never on a command line | billing, failover and launch tests |
| 7 | Cockpit ↔ terminal tab | Stale or forged sentinels; killing the wrong process | Per-session nonce; atomic writes; PID plus start-time check | interactive test |
| 8 | Cockpit → SolidWorks / Resolve | Closing, saving or overwriting the user's documents; hanging | Ownership by COM identity and title; already-open refusal; no overwrite; modal-dialog probe; SolidWorks never killed | swx tests (fake COM only) |
| 9 | Crash or restart | Duplicate or lost side effects; torn records | Durable operation store; outcome-unknown never retried; restart reconciliation; journal state machine; hash-chained audit with synchronous=FULL | operations, journal and audit fault tests |
| 10 | Diagnostics → outside | Secrets or private content in shared logs | Redactor before storage; the support bundle anonymizes paths and excludes databases, configs and prompts | redaction and bundle tests |

## Residual risks, prioritized

| # | Severity | Risk | Why it remains | Mitigation or next step |
|---|---|---|---|---|
| R1 | **High** | None of the Windows-specific controls has run: handle-based containment, job objects, pipe ACL, junction refusal, WPF UI | No Windows host | CI `windows-offline` and the procedures in INTEGRATION_MATRIX.md. Do not rely on Windows containment until these pass. |
| R2 | **High** | The SolidWorks worker has only run against a fake. Real COM behaviour (`GetDocuments`, `LoadFile4` on an open title, `AddMate5` errors, modal dialogs) is unverified. | No SolidWorks | `scripts/swx-integration.ps1`, run with a document of your own open |
| R3 | **High** | On Windows there is no sandbox unless the WSL distro is configured, so custom CAD code is refused there by default. The WSL sandbox path has not run. | No Windows / WSL | Configure as described in ISOLATION.md and verify with the adversarial scripts inside WSL |
| R4 | Medium | The sandbox has no seccomp filter. A kernel bug reachable by an unprivileged user in a namespace could allow an escape. | Not implemented | Add a seccomp filter (bwrap `--seccomp`). For hostile code, prefer a VM. |
| R5 | Medium | Successful-run, tool-call and rate-limit stream shapes come from documentation, not captures | Must not use this machine's account | `scripts/capture-cli.ps1` |
| R6 | Medium | The audit chain is tamper-evident only against partial edits. Someone with write access can rewrite the whole chain or truncate its tail. | A local hash chain cannot prove more | Note the head (doctor, support bundle). An external anchor would be needed for more. |
| R7 | Medium | Power loss is not simulated. Durability rests on SQLite `synchronous=FULL` and directory fsync (Linux); on Windows, `MoveFileEx` without write-through. | No power-cut rig | Add `MOVEFILE_WRITE_THROUGH` on Windows; run power-cut tests on hardware |
| R8 | Medium | A hung COM call keeps its operation `outcome_unknown` until it returns or the worker is killed | COM calls cannot be interrupted | The UI states this; the user resolves it via `cockpit operations` |
| R9 | Low | Path containment on Linux beyond openat2 (fallback mode) has a narrow race window, documented in FILESYSTEM_SECURITY.md R-FS* | Older kernels | openat2 is the default on 5.6+ |
| R10 | Low | Redaction of unknown secret formats is best-effort | Formats are open-ended | Known secrets are registered and removed exactly; patterns cover the common key formats |
| R11 | Low | Deduplication treats an intentional identical repeat of a committed call as a replay | By design | The model must change an argument (for example the output name) |
| R12 | Low | Job grants are in memory: a Core restart ends every job (fail-closed). Operations survive and are reconciled. | By design | Documented in OPERATION_RECOVERY.md |
