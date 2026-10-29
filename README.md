# AI Cockpit V5

A personal Windows cockpit that drives your existing subscriptions through their official CLIs — Claude Code (Claude Pro) and Gemini CLI (Google AI Pro), with automatic failover when one is at its limit — and your desktop apps: SolidWorks, DaVinci Resolve, Blender, plus build123d for parametric parts and an exact-arithmetic linear algebra tutor. Every tool call is gated, audited and, for files, reversible.
Start with [SPEC.md](SPEC.md): what was verified and how, V5 changes, the Toolbox-style overlay, the tool providers, topology, protocol, security, the day-1 bug checklist, and what still needs verifying on Windows.

| Project | What it is |
| --- | --- |
| `src/Cockpit.Protocol` | Framing, handshake, JSON-RPC helpers, SolidWorks DTOs (shared, AOT-safe) |
| `src/Cockpit.Core` | Bus, grants, gate, leases, audit, journal; Claude/Gemini launch, stream parsers, usage ledger, provider router; tool providers (`Swx`, `Files`, `Resolve`, `Blender`, `CadCode`, `Tutor`); `CockpitRuntime` |
| `src/Cockpit.Bridge` | `cockpit-mcp-bridge`: the stdio MCP server each CLI session spawns |
| `src/Cockpit.SwxWorker` | `cockpit-swx-worker`: the STA COM host that talks to SolidWorks |
| `src/Cockpit.CliHost` | `cockpit`: doctor, assemble, cad, organize, resolve, blender, tutor, matrix, rollback |
| `src/Cockpit.UI` | Overlay ViewModels (no WPF; tested on any OS) |
| `src/Cockpit.Wpf` | `AiCockpit`: the tray + Ctrl+Shift+Space overlay (Windows only) |
| `scripts/check-windows-build.ps1` | Real build on Windows: finds the SolidWorks interop, publishes to `dist\`, runs `cockpit doctor` |
| `verify/` | Offline stand-ins and the test runner: `bash verify/run-tests.sh` (40 tests, zero warnings); `live-requirements.txt` for the 4 optional live tests |
