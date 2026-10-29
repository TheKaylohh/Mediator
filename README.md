# AI Cockpit V9 (Windows release candidate)

A personal Windows cockpit that drives your existing subscriptions through their official CLIs — Claude Code (Claude Pro) and Gemini CLI (Google AI Pro), with automatic failover when one is at its limit — and your desktop apps: SolidWorks, DaVinci Resolve, Blender, plus build123d for parametric parts and an exact-arithmetic linear algebra tutor. Every tool call is gated, audited and, for files, reversible.
**V9 closes the V8 review**:
- every C# file of the WPF overlay is compiled on every test run (against `verify/stubs/WpfCompileStub`);
- `-p:CockpitUseShims=true` builds the real Windows app with no NuGet download, and `publish-release.ps1` falls back to it offline;
- the hotkey rebinds live, keeps the old one on a conflict, and shows in the tray;
- follow-up prompts continue the last Claude session with `claude -p --resume` (the ↩ chip).

Start with [CHANGELOG.md](CHANGELOG.md) and [docs/v9/RELEASE_NOTES.md](docs/v9/RELEASE_NOTES.md).

**V8 is the Windows stabilization release**:
- themes load by absolute pack URI, with a loose-file fallback;
- SolidWorks, DaVinci Resolve and Blender are found through the registry and Program Files (version folders included), not just PATH;
- the solution builds and runs on PCs without SolidWorks (warning COCKPIT001; the worker says "SolidWorks assemblies not installed on this machine");
- the panel is placed correctly on multi-monitor, mixed-DPI and any-edge or auto-hide taskbar setups;
- `dist\AiCockpit\` ships with a double-click `launch.bat` and `install-shortcut.vbs`.

Start with [CHANGELOG.md](CHANGELOG.md) and [docs/v8/RELEASE_NOTES.md](docs/v8/RELEASE_NOTES.md), including its Windows acceptance checklist. The WPF overlay still has to be compiled on Windows for the first time.

**V7 is the UI/UX release**: three run-time themes (Holo HUD, Pro Dark, Clean Light), an animated HUD orb that shows what the agents are doing, drag-and-drop / Ctrl+V / Attach for files and screenshots (read-only to jobs), a Connectors tab (CLI sign-in and limits, desktop apps, custom MCP servers behind the approval gate), a first-run setup card, and `scripts/publish-release.ps1`. Start with [docs/v7/CHANGELOG.md](docs/v7/CHANGELOG.md), [UI_UX](docs/v7/UI_UX.md), [SECURITY](docs/v7/SECURITY.md), [TEST_RESULTS](docs/v7/TEST_RESULTS.md) and [WINDOWS_CHECKLIST](docs/v7/WINDOWS_CHECKLIST.md): the WPF overlay has not been compiled yet (no Windows here); its XAML is checked offline.

**V6 was the hardening release.** Start with [docs/v6/TEST_RESULTS.md](docs/v6/TEST_RESULTS.md) (what ran, what did not), then [THREAT_MODEL](docs/v6/THREAT_MODEL.md), [INTEGRATION_MATRIX](docs/v6/INTEGRATION_MATRIX.md), [FILESYSTEM_SECURITY](docs/v6/FILESYSTEM_SECURITY.md), [ISOLATION](docs/v6/ISOLATION.md), [OPERATION_RECOVERY](docs/v6/OPERATION_RECOVERY.md), [BENCHMARKS](docs/v6/BENCHMARKS.md), [RELEASE_CHECKLIST](docs/v6/RELEASE_CHECKLIST.md) and [CHANGELOG](docs/v6/CHANGELOG.md). Nothing Windows-, SolidWorks-, Resolve- or account-specific has run yet; those suites are written and listed as NOT RUN.
[SPEC.md](SPEC.md) is the V5 design document: what was verified and how, V5 changes, the Toolbox-style overlay, the tool providers, topology, protocol, security, the day-1 bug checklist, and what still needs verifying on Windows.

| Project | What it is |
| --- | --- |
| `src/Cockpit.Protocol` | Framing, handshake, JSON-RPC helpers, SolidWorks DTOs (shared, AOT-safe) |
| `src/Cockpit.Core` | Bus, grants, gate, leases, audit, journal; Claude/Gemini launch, stream parsers, usage ledger, provider router; tool providers (`Swx`, `Files`, `Resolve`, `Blender`, `CadCode`, `Tutor`); `CockpitRuntime` |
| `src/Cockpit.Bridge` | `cockpit-mcp-bridge`: the stdio MCP server each CLI session spawns |
| `src/Cockpit.SwxWorker` | `cockpit-swx-worker`: the STA COM host that talks to SolidWorks |
| `src/Cockpit.CliHost` | `cockpit`: doctor, assemble, cad, organize, resolve, blender, tutor, matrix, rollback |
| `src/Cockpit.UI` | Overlay ViewModels (no WPF; tested on any OS): overlay, approvals, Connectors, first-run card, settings, hotkey parser |
| `src/Cockpit.Wpf` | `AiCockpit`: the tray + Ctrl+Shift+Space overlay (Windows only); `Themes/` (3 dictionaries), `Controls/HudOrbControl` |
| `scripts/publish-release.ps1` | Release package (V8): Release builds of all four executables (the worker with or without SolidWorks), Native AOT bridge, loose themes, `dist\AiCockpit\` with `launch.bat`, `install-shortcut.vbs`, `Create-Desktop-Shortcut.ps1`, checksums, a required-files check and a doctor smoke test |
| `scripts/launch-cockpit.bat`, `scripts/install-shortcut.vbs` | V8 double-click launcher and shortcut installer (published as `launch.bat` / `install-shortcut.vbs`; no PowerShell, no admin) |
| `scripts/check-windows-build.ps1` | Real build on Windows: finds the SolidWorks interop, publishes to `dist\`, runs `cockpit doctor` |
| `scripts/run-tests.ps1` | Windows twin of `verify/run-tests.sh` (runs the NTFS and sharing-violation tests that Linux skips) |
| `scripts/swx-integration.ps1` | Real-SolidWorks procedure (STEP import cases, ownership, worker restart) |
| `scripts/capture-cli.ps1` | Captures real Claude/Gemini CLI output with your own subscriptions, for parser fixtures |
| `.github/workflows/v6.yml` | CI layers: Linux offline + live, Windows offline + ship build, self-hosted licensed jobs |
| `verify/` | Offline stand-ins and the test runner: `bash verify/run-tests.sh` (119 tests; PASS/FAIL/SKIP; zero warnings); `stubs/SwxWorkerNoInterop` is the worker built with no SolidWorks interop; `stubs/WpfCompileStub` lets the overlay's C# compile on Linux; `stubs/SqliteShim` + `HashingShim` are also the offline (`CockpitUseShims`) dependencies; `live-requirements.txt` for the 6 live tests; `fixtures/real` holds real CLI captures, `fixtures/swx` real STEP files |
