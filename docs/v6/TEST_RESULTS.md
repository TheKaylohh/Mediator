# V6 Test Results

**Verdict for this environment: partial release candidate.** Everything that can run on Linux ran and passed. Nothing that needs Windows, SolidWorks, Resolve, the Blender application or a signed-in Claude or Gemini subscription has run yet; those suites are listed below as **NOT RUN**, never as passed.

## Environment

| Item | Value |
|---|---|
| OS | Ubuntu 24.04.5 LTS, x86-64, cloud container. Runs as root; tests that need an unprivileged user drop to uid 65534. |
| CPU / RAM | 2 vCPU (Intel Xeon @ 2.80 GHz), 7.8 GiB |
| .NET | SDK 8.0.132, runtime 8.0.32 |
| Build | `MediatorVerify=true`, which builds against stand-ins: the SQLite shim over the system libsqlite3, XxHash64 checked against reference vectors, and the SolidWorks interop **stub** |
| Python (live) | 3.13.17. build123d 0.13.0, bpy 5.2.3 LTS (Blender as a Python module, not the app), PyMuPDF 2.0, SymPy 1.14.0 |
| Tools | ffmpeg / ffprobe 6.1.1, bubblewrap 0.9.0, util-linux prlimit and setpriv |
| CLIs (captures only) | Claude Code 2.1.309, Gemini CLI 0.65.0. Neither is signed in to a Mediator account (see verify/fixtures/real/README.md). |

## Commands and results

| # | Command | Exit | Result | Evidence |
|---|---|---|---|---|
| 1 | `bash verify/run-tests.sh` (offline) | 0 | **81 passed, 0 failed, 8 skipped** (89), 56 s | `docs/v6/test-run-linux-offline.txt` |
| 2 | `MEDIATOR_LIVE_PYTHON=<venv>/bin/python bash verify/run-tests.sh` (offline + live) | 0 | **87 passed, 0 failed, 2 skipped** (89), 1 min 44 s | `docs/v6/test-run-linux-live.txt` |
| 3 | `MEDIATOR_REQUIRE_ALL=1 bash verify/run-tests.sh` | **2** | Release gate: any skip counts as a failure. It fails here, as intended, because 8 tests did not run. | — |
| 4 | `dotnet build Mediator.sln -p:MediatorVerify=true -p:TreatWarningsAsErrors=true` | 0 | 0 warnings, 0 errors (the WPF project builds against reference assemblies; the UI is not run) | — |
| 5 | `dotnet Mediator.Tests.dll --bench <bridge.dll> docs/v6/benchmarks.json` | 0 | Measurements | `docs/v6/BENCHMARKS.md` |

### Skipped in run 2, and why

| Test | Reason | Where it must run |
|---|---|---|
| fs-auth (Windows): junctions, junction swap race, real ADS | needs NTFS | Windows CI job `windows-offline` (written, **NOT RUN**) |
| journal faults (Windows): locked file (sharing violation) | mandatory file locks are Windows-only | Windows CI job `windows-offline` (written, **NOT RUN**) |

Run 1 also skips the 6 live tests: no `MEDIATOR_LIVE_PYTHON` was set.

## What the 89 tests cover

Every test name is printed in the evidence files.

| Area | Tests | Real OS primitives or apps used |
|---|---|---|
| Filesystem authorization | 7 (6 run) | Real symlinks, a bind mount (`mount --bind`), a 3 s folder↔symlink swap race (openat2 and fallback modes), provider refusals with audit |
| Custom-code isolation | 4, plus 1 live | Real bubblewrap: no host files, no network, no secrets, uid 65534, PID namespace, rlimits, no surviving grandchildren, a hostile live build123d script |
| Journal recovery | 10 (9 run) | Real process crashes (`FailFast`) at 8 boundaries × first/middle hit, a 1 MiB tmpfs filled to disk-full, real uid-65534 permission failure, concurrent edits, stale rollback, single-process lock |
| Durable operations | 5 | Real SIGKILL of the bridge and of the Core mid-call; restart reconciliation |
| Providers and failover | 6 | Real Claude Code 2.1.309 and Gemini CLI 0.65.0 outputs as fixtures; real child processes as stand-in CLIs |
| Bus and protocol | 8 | Real pipe: hostile frames, invalid UTF-8, in-flight cap, revocation race, 160-call concurrency |
| Interactive sessions | 1 | Real processes for the launcher, the terminal and the bridge; stale, foreign, malformed and half-written sentinels |
| PDF reader | 4 | Child-process stack-overflow guard, 300 MiB decompression bomb, 3000-case mutation fuzz |
| Tutor (exact maths) | 2 | — |
| Audit and support bundle | 5 | Real SIGKILL during appends; tampering with SQLite directly (triggers dropped) |
| SolidWorks worker | 5 | **Fake COM only**: proves the worker's own rules, not SolidWorks' behaviour |
| Resolve / Blender / Code-CAD / media / STEP | 5, plus 4 live | Live: build123d (all templates; 32 boundary and random parameter sets), bpy 5.2.3 scene, PyMuPDF, ffprobe against ffmpeg-made clips; StepValidator on STEP files written by real OpenCascade |
| Overlay ViewModels and XAML | 6 | ViewModels only; the XAML is parsed but WPF is **not** run |
| CLI host | 1 | Real `mediator-cli` process |

## Mutation checks (manual, this session)

Each protection below was disabled by hand, the named test was run and failed, then the protection was restored. This shows the test really guards the behaviour.

| Protection disabled | Test that failed |
|---|---|
| Kernel-checked open (openat2) | fs-auth swap race (Phase 2a) |
| bubblewrap network namespace (`--share-net`) | isolation: no network |
| Sandbox requirement + approval binding | code-cad and isolation binding tests |
| Journal move reconciliation | journal faults: crash at every boundary (`move.after_rename#1`) |
| Refusal of retries while unresolved | operations: replay, timeout, Core-killed |
| Side-effect check before a mid-job switch | failover: mid-job switch |
| UTF-8 validation on the bus | bus: hostile frames |
| Grant re-check before execution | bus: revoked while awaiting approval |
| STEP import ownership check | swx: STEP import ownership |
| PDF nesting bound | pdf: nested arrays (the child process died of stack overflow) |

## Defects found by V6 tests (all fixed; each has a regression test)

1. **P0, V5 → V6.** A symlink, junction or bind mount inside a granted root let providers read or write outside it.
2. **P0.** Model-written CAD code ran on the host interpreter.
3. **P0.** A **revoked job could still run a Write call** if the approval arrived after the revocation.
4. **P0.** A crafted PDF with deeply nested arrays **overflowed the stack and killed the whole Mediator process**.
5. **P1.** Invalid UTF-8 inside a JSON string parsed, then threw on the bus read loop and dropped the connection.
6. **P1.** Gemini CLI 0.65 refuses headless runs in untrusted folders (exit 55), so V5's headless Gemini path would fail by default. This was found from a real capture.
7. **P1.** STEP import could close or save a document that the user had open.
8. **P1.** Unknown token usage was recorded as 0.
9. **P1.** A time-out was reported as a failure while the call could still complete, and a retry could repeat it.
10. **P2.** A rollback conflict was final even after the newer batch that caused it was undone. Conflicts are now retried.

## NOT RUN: required before a full release

| Suite | Why it did not run | Procedure |
|---|---|---|
| Windows build with real dependencies (`check-windows-build.ps1`) and WPF UI | no Windows | INTEGRATION_MATRIX.md §Windows |
| Windows offline suite (junctions, ADS, 8.3 names, sharing violations, job objects, named-pipe ACLs, `FirstPipeInstance`) | no Windows | CI `windows-offline` |
| Windows handle-based containment (`FILE_FLAG_OPEN_REPARSE_POINT`, `GetFinalPathNameByHandle`, delete-by-handle) and the WSL sandbox (`WslBubblewrapSandbox`) | no Windows | INTEGRATION_MATRIX.md |
| Real SolidWorks: part cards, STEP import (valid, surface-only, malformed, existing target, already open), assembly mates, modal dialogs, worker restart | no SolidWorks | INTEGRATION_MATRIX.md §SolidWorks |
| Real DaVinci Resolve import of the FCPXML/EDL and Studio script | no Resolve | INTEGRATION_MATRIX.md §Resolve |
| Blender application (`blender -b`) and EEVEE preview renders | only the bpy module is available; EEVEE needs a GPU or display | INTEGRATION_MATRIX.md §Blender |
| Signed-in Claude Code and Gemini runs: success, tool calls, rate limit, quota, interactive tab | must not use this environment's account | `scripts/capture-cli.ps1` |
| Windows Sandbox | not used for automatic execution (see ISOLATION.md) | — |
