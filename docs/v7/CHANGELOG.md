# V7 changelog (UI/UX overhaul)

V7 builds on V6 (commit `c7f1a6e`). All 89 V6 tests still pass with their original assertions, and 11 V7 tests were added (100 in total). Builds have zero warnings with `TreatWarningsAsErrors=true`. Details are in [TEST_RESULTS](TEST_RESULTS.md), [UI_UX](UI_UX.md), [SECURITY](SECURITY.md) and [WINDOWS_CHECKLIST](WINDOWS_CHECKLIST.md).

## Added

**Theme engine**

- Three dictionaries under `src/Mediator.Wpf/Themes/`: **Holo HUD** (the default), **Pro Dark** and **Clean Light**. All three define the same 44 keys.
- Every brush, colour, font and radius in `App.xaml` styles and `MainWindow.xaml` is now a `{DynamicResource}`.
- `ThemeManager` swaps the dictionary at run time.
- `MediatorOverlayViewModel.CurrentTheme`, `CycleThemeCommand` and `ThemeChanged`. A header button cycles the themes, and the choice is saved in `settings.json` (`JsonSettingsStore`, written atomically).

**HUD orb**

- `Controls/HudOrbControl.xaml`: three counter-rotating segmented rings, a reticle and a pulsing core. It is bound to the new `MediatorOverlayViewModel.ReactorState` (Idle, Active, AwaitingApproval, Error), with a caption and a tooltip.
- Clicking the orb acknowledges a fault.
- Scanlines and corner brackets appear in the HUD theme, and an active-job sweep runs along the prompt.

**Attachments**

- Files and folders can be dragged onto the panel, behind a glowing drop overlay.
- Ctrl+V attaches files, or a screenshot as PNG; plain text still pastes into the prompt.
- An `[ + Attach ]` button opens a file picker.
- Attachments show as removable chips. A pin button keeps the panel open while dragging from Explorer.
- Core side:
  - `Mediator.Core.Attachments.AttachmentStore` stages snapshots, folders and pasted images under strict rules.
  - `AttachmentToolProvider` adds `mediator_attachments` and `mediator_read_attachment`; images are returned as MCP image content.
  - `JobSpec.Attachments` carries attachments into a job.
  - `MediatorRuntime.AttachToJobAsync` gives a running job read-only access, audited as `mediator/attach`.
  - `PathAuthorizer` gains read-only roots (`read_only` denial code) and `AddReadOnlyRoot`.
  - `ToolResult.Images` is added, and `BridgeMcpClient` parses image content.

**Connectors tab** (`ConnectorsViewModel`, 4th tab)

- Claude Code and Gemini CLI: version, honest sign-in state, usage-limit cooldown, and `[ Sign In ]`.
- SolidWorks PID, Resolve edition and running state, and the Blender location.
- `[ + Add Custom MCP ]` with a 15 s health check. Servers are saved to `mediator.mcp.json` and their tools proxied through the gate as Destructive `ext_<server>_<tool>` (`CustomMcpService`, `ExternalMcpToolProvider`, `ConnectorProbe`).

**First-run card** (`OnboardingViewModel`)

- Checks for claude, gemini, python and git.
- Offers a theme picker, a hotkey picker (`HotkeyGesture`, validated), and **Run on Windows startup** (the per-user `HKCU…\Run` value, through `StartupRegistration`).
- Opens on the first launch, or whenever neither CLI is found. The hotkey is re-registered when it changes.

**Packaging**

- `scripts/publish-release.ps1`: Release builds, the bridge with `-p:PublishAot=true` (the MSVC linker is checked first), self-contained win-x64 by default, merged into `dist\Mediator\` with conflict detection, checksums and a doctor smoke test.
- `scripts/Create-Desktop-Shortcut.ps1`.
- CI job `windows-release-package`: the first real WPF compile, published as a build artifact.

**Tests**

- 11 new tests: attachments ×3, custom MCP ×2, connector probe, overlay V7 ×4 (themes and persistence, reactor transitions, drag-drop and clipboard, Connectors and onboarding), and a XamlChecker self-test.
- `XamlChecker` now:
  - walks `Themes/` and `Controls/`;
  - checks theme parity (same keys, same kind of value);
  - checks that every `DynamicResource` exists in every theme;
  - forbids `StaticResource` to theme keys and colour literals outside `Themes/`;
  - catches a property set twice;
  - resolves `ElementName` bindings to dependency properties.

## Changed

- `ToolRegistry` is now thread-safe and can unregister providers (custom MCP servers come and go). A provider is registered all-or-nothing.
- `BridgeMcpClient` can start any stdio MCP server, lists full tool definitions, and caps captured stderr at 64 KiB.
- `MediatorPaths` gains `AttachmentsDirectory`, `SettingsFile` and `CustomMcpFile`.
- Hard-coded colours (`#33E55765`, `#22F2C55C`, `White`, `Black`) are replaced by theme brushes (`BannerBackgroundBrush`, `LowConfidenceRowBrush`, `OnAccentBrush`, `GlowColor`).
- Approval cards have an amber border (red for Destructive), matching the orb.
- `App.xaml.cs` and `MainWindow.xaml.cs` add `using System.IO;` explicitly: WPF's implicit usings do not include it.

## Deviations from the request

- **Naming.** "JarvisHud" and "JarvisReactorControl" ship as **HoloHud** and **HudOrbControl**: an original HUD look, not a copy of a film prop or a trademarked name. The requested colours are used exactly.
- **Where pasted images go.** They are stored in `<data>/attachments/<id>/`, not `<Workspace>/pasted/`. Each attachment is its own read-only root, and nothing is written into the user's project folders.
- **Live attachment to a running job.** It works for jobs that started with attachments, and for interactive sessions. A headless job started without attachments cannot take new input mid-run, so V7 tells the user the file goes to the next job instead. This also keeps every V6 tool list unchanged.
- **Sign-in state.** It is shown as "credential file found (confirmed by the next run)", not as a verified OAuth state. The Mediator never opens the credential files, so it cannot claim more.

## Not done or not verified

- **WPF compile and run.** Nothing in `src/Mediator.Wpf` has been compiled. Follow [WINDOWS_CHECKLIST](WINDOWS_CHECKLIST.md).
- **Packaging scripts.** `publish-release.ps1` and `Create-Desktop-Shortcut.ps1` have not run. PowerShell is not available in the authoring environment.
- **Resolve Studio detection.** It relies on the executable's version info naming "Studio", which is unconfirmed on a real install.
