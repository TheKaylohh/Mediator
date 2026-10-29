# Integration Matrix

Each integration is in one of four states:
- **Real**: run against the real application or OS feature, here.
- **Real capture**: real output from the application, recorded once and replayed in tests.
- **Simulated**: fake or stub only.
- **NOT RUN**: needs an environment that was not available.

A simulated pass is never reported as a real one.

| Integration | Version(s) | State | What was exercised | What remains |
|---|---|---|---|---|
| Linux filesystem (ext4): symlinks, bind mounts, openat2, renameat2 | kernel 6.18 | **Real** | containment, swap race, no-overwrite moves, directory fsync | — |
| Linux sandbox (bubblewrap + prlimit + setpriv) | bwrap 0.9.0 | **Real** | files, network, environment, uid, PID namespace, rlimits, cleanup, hostile build123d | seccomp filter (not implemented) |
| SQLite (journal, operations, audit, usage) | system libsqlite3 via the verify shim | **Real** (engine), shim API | crashes, disk full, tampering, migrations | the shipping Microsoft.Data.Sqlite package: Windows CI `windows-ship-build` (NOT RUN) |
| build123d / OpenCascade | 0.13.0 | **Real** | 7 templates; 32 boundary and random parameter sets; custom code in the sandbox; independent re-import | — |
| Blender | bpy 5.2.3 LTS (module) | **Real** (scene build) | car scene `.blend`, engine choice, frame count | `blender -b` app, EEVEE/Cycles preview render, Blender 4.x: **NOT RUN** |
| PyMuPDF / SymPy | 2.0 / 1.14.0 | **Real** | chapter extraction; SymPy cross-check script | — |
| ffmpeg / ffprobe | 6.1.1 | **Real** | built-in MP4/MOV/WAV readers agree with ffprobe | — |
| Built-in PDF reader | — | **Real** | 3000-case fuzz, decompression bomb, nesting, limits | PDFs from more producers (corpus) |
| Claude Code CLI | 2.1.309 | **Real capture** (invalid key) | init event `apiKeySource`, error result with `subtype: success`, exit 1 | signed-in success, tool calls, rate limit, interactive tab: **NOT RUN** |
| Gemini CLI | 0.65.0 | **Real capture** (no auth, untrusted folder, no result) | exit codes 41 and 55, stderr messages, a stream with no result event; trust fix | signed-in success, tool calls, quota: **NOT RUN** |
| Windows: build (NuGet, WPF), job objects, pipe ACLs, `FirstPipeInstance`, junctions, ADS, 8.3, sharing violations | — | **NOT RUN** | code compiles (Windows-targeting reference assemblies) | CI `windows-offline`, `windows-ship-build` |
| WPF overlay | — | **Simulated** (ViewModels and XAML parse only) | approvals, kill, routing, uncertain states | hotkey, focus, DPI, multi-monitor, tray, elevated apps: **NOT RUN** |
| Windows Terminal / PowerShell interactive sessions | — | **Simulated** (stand-in processes; generated `.ps1` inspected) | launcher exits, sentinels, kill, closed tab | real `wt.exe`, PowerShell 5.1 BOM handling, other desktop session: **NOT RUN** |
| SolidWorks (COM, STA worker) | — | **Simulated** (fake COM + interop stub) | worker rules: mates, selection marks, undo group, modal-dialog probe, STEP ownership | everything real: **NOT RUN** (`scripts/swx-integration.ps1`) |
| DaVinci Resolve | — | **Simulated** (FCPXML checked against the DTD; script generated) | timeline math, subset approval, media authorization | import in Resolve, Studio scripting: **NOT RUN** |
| WSL2 restricted distro / `WslBubblewrapSandbox` | — | **Simulated** (command line only) | generated `wsl.conf`, fstab, sandbox arguments | everything real: **NOT RUN** |
| Windows Sandbox (`.wsb`) | — | generator only | XML | not used for automatic execution (ISOLATION.md) |

## Procedures for what did not run

### Windows (no licensed software needed)

1. `powershell -ExecutionPolicy Bypass -File scripts\run-tests.ps1`
   - Expect 0 failures.
   - The two Windows tests (NTFS junctions/ADS/8.3, sharing violation) must show PASS.
2. `scripts\check-windows-build.ps1 -RunVerifySuite`
   - The ship build uses real Microsoft.Data.Sqlite and System.IO.Hashing, with WPF and warnings as errors.
   - Then `mediator-cli doctor` runs. It reports the audit chain head; note it down.
3. Overlay, by hand:
   - Ctrl+Shift+Space on each monitor.
   - 100% / 150% / mixed DPI.
   - Approve a subset.
   - Kill during an approval (the card disappears).
   - Kill while a call is running: the row must say the call *may still finish*.
   - Tray exit with an active job.
   - An elevated app in the foreground: UIPI may block the hotkey; document what you see.
4. Interactive tab:
   - `mediator-cli assemble <folder> --interactive` with a non-ASCII folder name.
   - Close the tab: the job must end "the terminal was closed".
   - `interactive.pid` and `.done` carry the session nonce.

### SolidWorks (supported: the version whose interop you build against; student licences must allow API use)

`scripts\swx-integration.ps1`. It covers:
- valid, surface-only, truncated and empty STEP files;
- an existing target, duplicate names, and a STEP already open;
- an assembly with valid and invalid mates;
- a worker restart that keeps the same SolidWorks PID;
- your own document left open and modified, which must never be saved.

**Confirm `ISldWorks.GetDocuments` exists in your version.** V6 relies on it for document ownership.

### DaVinci Resolve (Free: import only; Studio: scripting)

1. `mediator-cli resolve <folder with clips> --request "60 s teaser"`. Approve 3 of 4 cuts.
2. **Free:** File > Import > Timeline, then pick `edits\timeline.fcpxml`. Check:
   - three clips at the planned in and out points (frame-exact at 29.97);
   - the dissolve;
   - connected audio.
3. Move one clip's source file, then import again: Resolve must report the missing media.
4. **Studio:** enable Preferences > General > External scripting = Local, then run the generated `apply_resolve.py`. A new project and timeline must appear. Your existing projects must be untouched.

### Blender application, 4.x and 5.x

1. `mediator-cli blender <folder> --request "red car"`. The scene build and the preview render are separate steps and are reported separately.
2. Render the preview with EEVEE on a GPU machine. Then interrupt a render: the partial frames must be reported, not presented as a finished render.

### Signed-in CLIs

`scripts\capture-cli.ps1 -Cases success,cancel`, on your machine with your own subscriptions. Commit the captures under `verify/fixtures/real/` and add a parse test next to `FailoverTests.RealCapturesClassified`.

## Supported versions as tested

| Component | Tested | Expected to work | Notes |
|---|---|---|---|
| .NET | 8.0.32 runtime, SDK 8.0.132 | 8.0.x | — |
| Claude Code | 2.1.309 (error path only) | the stream-json shape documented for 2.x | `apiKeySource` is present in 2.1.309 |
| Gemini CLI | 0.65.0 (error paths only) | ≥ 0.65 with `GEMINI_CLI_TRUST_WORKSPACE` | earlier versions: use `--gemini-skip-trust` or trust the jobs folder |
| build123d | 0.13.0 | 0.13.x | — |
| Blender | 5.2.3 (bpy module) | 4.2+ untested | engine names differ between 4.x and 5.x; the script handles both, but 4.x was not run |
| SolidWorks | none | — | minimum version unknown until `swx-integration.ps1` passes |
| Resolve | none | 18+ for FCPXML 1.9 import (vendor documentation; not verified) | — |
