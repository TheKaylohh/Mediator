# V7 test results

**Verdict for this environment: partial release candidate.** Everything that can run on Linux ran and passed: all 89 V6 tests with their original assertions, plus the 11 V7 tests. The V7 WPF overlay has **not been compiled or run**, because there is no Windows here and NuGet is blocked. Its XAML is checked offline by XamlChecker; the Windows steps are in [WINDOWS_CHECKLIST](WINDOWS_CHECKLIST.md). The suites listed as NOT RUN in [docs/v6/TEST_RESULTS.md](../v6/TEST_RESULTS.md) are still not run.

The environment is the same as V6: Ubuntu 24.04.5, .NET SDK 8.0.132, `CockpitVerify=true` stand-ins, and a live Python with build123d 0.13.0, bpy 5.2.3, PyMuPDF 2.0 and SymPy 1.14.

## Commands and results

| # | Command | Exit | Result | Evidence |
|---|---|---|---|---|
| 1 | `COCKPIT_LIVE_PYTHON=<venv>/bin/python bash verify/run-tests.sh` | 0 | **98 passed, 0 failed, 2 skipped** (100) | `test-run-linux-live.txt` |
| 2 | `bash verify/run-tests.sh` (offline) | 0 | **92 passed, 0 failed, 8 skipped** (100) | `test-run-linux-offline.txt` |
| 3 | `dotnet build Cockpit.sln -p:CockpitVerify=true -p:TreatWarningsAsErrors=true` | 0 | 0 warnings, 0 errors | — |

**Skipped in run 1 (Windows only):**

- fs-auth: junctions, junction swap race and real ADS (needs NTFS);
- journal faults: locked file / sharing violation.

Run 2 also skips the 6 live tests, because `COCKPIT_LIVE_PYTHON` is not set.

## V6 tests kept unchanged

The 89 V6 tests run with their V6 assertions. Two of them assert exact tool lists: `runtime: assemble …` and the doctor bridge self-test in `cli host process`. They failed in an early V7 draft, which granted the attachment tools to every job. The design was changed rather than the tests: attachment tools now go only to jobs that start with attachments or run interactively.

## New V7 tests (11)

| Test | What it proves |
|---|---|
| attachments: staging | Files are snapshotted into their own read-only folder; folders are shared in place; PNG headers are checked. Refused: links (final component, folder), FIFO, `/proc`, `/dev`, credential files and folders (also through a linked parent), the home folder, `..` paths, the data folder, a drive root, oversize files, fake PNGs. A refused copy leaves nothing behind. Discard never touches user files. Prune after 7 days. |
| attachments: read-only roots | Read and List are allowed. Create, Move and Mkdir are denied with `read_only`. A read-only root inside a writable one never removes write access. A root added live is read-only at once, and links inside it are still refused. |
| attachments through the bus | Real bridge. Listed; text read; image returned as MCP image content, byte-identical. The files tool cannot move an attachment. A job without attachments has no attachment tools and cannot be attached to. Live attach to a running job: folder listed and read; `..` and a sideways absolute path refused; read-only; audited; tool list unchanged. A finished job cannot be attached to. |
| custom MCP: registry | Bad name, unknown command, missing or relative path refused. 15 s health check. Tools registered as Destructive `ext_fake_*` (an invalid tool name is sanitised). `cockpit.mcp.json` has no env and no temp file is left. Reload from disk. Disable and remove unregister. A dead server and a hung server are reported (the hung one gives up after about 15 s) with nothing registered. Run folders are cleaned. A malformed file is reported and never overwritten. |
| custom MCP through the gate | Real bridge and a real stdio MCP server process. The grant asks Auto, but the call is still asked as Destructive. The preview says it runs outside the file rules. Image passed through. `COCKPIT_*`/TOKEN and `ANTHROPIC_API_KEY` scrubbed; an ordinary variable kept; own empty folder. A denied call never starts the server (marker file absent). Audited, with no secret in the audit. A removed server's tool no longer runs. |
| connector probe (Linux) | `--version` parsed from stand-in CLIs; a failing or missing CLI reported. The credential file is detected while unreadable (existence only). Windows-only apps are reported as such. The sign-in terminal has no API key. |
| overlay: themes | Cycle order, `ThemeChanged`, notifications. Saved to `settings.json`, reloaded by a new ViewModel. Undefined themes ignored. Unknown values fall back. A malformed file is kept as `.bad`. Hotkey parser (round trip, F13, refusals: Shift-only, Alt+Space, duplicate, unknown modifier, unsupported key; every preset valid). |
| overlay: reactor | Exact sequence Idle → Active → AwaitingApproval → Active → Error (failed job) → Idle (acknowledged) → Error (rate limit) → Active (new job clears it) → Idle → Error (billing alarm), with captions. |
| overlay: drag-drop and clipboard | Drop/pick → chips; refusals explained; duplicates ignored; links refused. Pasted PNG → image chip; non-PNG refused. × deletes an unshared snapshot only. Shared with the single running job, or the user is told why not. Included chips go to the next job (excluded ones stay) and are consumed on success, kept on failure. 20-item cap. Attach button, drop flag, pin, our own dialog does not hide the panel. |
| overlay: Connectors and first run | Versions, honest sign-in text, cooldown, install hints. Sign In only when installed, launches a terminal. App statuses. + Add Custom MCP through the form (bad name, quoted arguments, health check, register, untick, remove). Refresh on tab open. First-run card: opens on first run; prerequisites; hotkey validation and presets; live theme preview; a registry failure keeps it open; Finish saves and re-registers the hotkey; not shown again; shown again when no CLI is found. |
| XAML checker self-test | On a mutated copy of the real XAML, the checker reports each planted mistake: a missing theme key, a DynamicResource one theme lacks, a StaticResource to a theme key, a colour literal, a property set twice, a non-resolving binding, an ElementName binding to a missing dependency property, and a misspelt key in App.xaml. The unmodified copy is clean. |

The `overlay XAML` test (V6, extended) checks 6 files, 183 bindings and 303 resource references, with 44 keys compared across the 3 themes.

## Mutation checks (manual, this session)

| Protection disabled | Test that failed |
|---|---|
| Custom MCP tools Destructive → Write | custom MCP through the gate |
| Environment scrub for custom MCP servers | custom MCP through the gate |
| Folder containment in `cockpit_read_attachment` | attachments through the bus |
| Refusing live attach to a job without the attachment tools | attachments through the bus |
| `read_only` denial in `PathAuthorizer` | attachments: read-only roots, and attachments through the bus |
| Credential/home/data/drive check (`SensitiveReason`) | attachments: staging |

Disabling only the first, lexical credential check did **not** fail the test. The real-path checks still caught every case: the protection is layered, so the mutation had to remove both layers.

## NOT RUN

| What | Why | Procedure |
|---|---|---|
| WPF compile and run of the V7 overlay (themes, orb animations, drop overlay, clipboard, file picker, Connectors tab, first-run card, Run key) | no Windows; NuGet blocked | [WINDOWS_CHECKLIST](WINDOWS_CHECKLIST.md); CI job `windows-release-package` |
| `scripts/publish-release.ps1`, `Create-Desktop-Shortcut.ps1` | no PowerShell or Windows | same |
| Native AOT build of the bridge for win-x64 | needs Windows and the MSVC linker | same |
| Resolve Studio vs Free detection on a real install | no Resolve | WINDOWS_CHECKLIST §4 |
| Everything listed as NOT RUN in V6 | unchanged | docs/v6/TEST_RESULTS.md |
