# Changelog

Earlier versions: [V7](docs/v7/CHANGELOG.md) (UI/UX), [V6](docs/v6/CHANGELOG.md) (hardening). The full stories, with what was and was not verified, are in [docs/v9/RELEASE_NOTES.md](docs/v9/RELEASE_NOTES.md) and [docs/v8/RELEASE_NOTES.md](docs/v8/RELEASE_NOTES.md).

## V18.2: patch (2026-10-30)

Details and the feature test list: [docs/v18/V18.2.md](docs/v18/V18.2.md).

- **Quick panel**: the conversation of previous and active jobs now sits in the Jobs tab (above the job list), not on top of the Actions tab; sending a prompt or opening a saved chat switches to Jobs.
- **Full screen**: the model dropdown, Auto-approve and the project / CLI / model / session line moved next to the folder buttons in the composer toolbar; the header is one row, so the conversation is taller.
- **Theme switch**: the chat area follows the instant the new theme is merged (only its colour variables change; no markdown re-render or debounce), so it no longer lags the panels by 0.1-0.2 s.
- **Profile and settings menu**: sits in the body row, scrolls when the window is short, and closes on a click elsewhere.
- **Notifications**: the bell opens a real panel: permission requests, finished and failed jobs, and files the AI created, each with a button (Review, Details, Open). No old jobs; the badge counts unread ones; Clear all.

## V18: connectors as plug-ins, the full-screen app, light and dark themes (2026-10-30)

Details and the Windows checklist: [docs/v18/RELEASE_NOTES.md](docs/v18/RELEASE_NOTES.md); the connector architecture: [docs/v18/CONNECTORS.md](docs/v18/CONNECTORS.md).

- **Connectors are plug-in pieces**: SolidWorks (with its worker and scripts), FreeCAD, AutoCAD, Inventor, NX, Creo, CATIA, Parts from code, CAD files, Office, Resolve, Blender and Tutor each live in `src/Connectors/<Name>`, build to `<app>\connectors\<Name>` and are loaded at start-up; the core references none of them. Tiles, routing, the Connectors tab, the Software Library, attachments, `mediator-cli doctor` and CLI recipes all come from the installed connectors. New `mediator-cli connectors`.
- **The full screen is a standalone app**: own taskbar entry, reopens where it was, "Mediator Workspace" shortcut (`Mediator.exe --workspace`); a second start hands over to the running Mediator.
- **The quick panel and the full screen are never open together**: the hotkey and tray bring the full screen forward while it is open.
- **New Dark (default) and Light themes** with a light/dark toggle in both windows; the old themes are kept in `Themes/Legacy`.
- 9 new tests (177; 166 run here, all pass).
- The SolidWorks worker no longer references Mediator.Protocol (it compiles in the one file it used), which also fixes a `-warnaserror` build without SolidWorks.

## V17: CAD co-pilot expansion (2026-10-30)

Details, coverage per program and the Windows checklist: [docs/v17/CAD_EXPANSION.md](docs/v17/CAD_EXPANSION.md).

- **Six new CAD programs**: FreeCAD (fc_), AutoCAD (acad_), Inventor (inv_), Siemens NX (nx_), Creo (creo_), CATIA V5 (catia_), on the shared CAD layer; each driven by one script in its own automation language (Python, or Windows PowerShell over COM), patchable in `%LOCALAPPDATA%\Mediator\cad-scripts`.
- **New tools**: sketch, model_features, assembly_ops, annotate_drawing, simulate and run_script, with one CAD-neutral operation catalog (units mm/deg/N/MPa); each program lists the operations it supports.
- **SolidWorks**: sketches with constraints and dimensions, features on the open part (standard holes, linear/circular patterns, delete), live assembly work (insert, mate, fix, move, replace, delete, copies), drawing annotation (projected/section/detail views, dimensions, notes, BOM, balloons), more edits (add global, delete feature, colour, undo), run_script.
- **FreeCAD**: PartDesign features incl. Hole and patterns, booleans, and CalculiX static analysis (fixed, force, pressure, mesh, solve).
- **run_script** everywhere: the escape hatch for anything else (CAM, simulation, GD&T, sheet metal); Destructive, shows the code, runs only the approved code.
- Routing: requests naming a program or its file types, or the program in front, get that program's tools; saved chats are upgraded.
- 4 new tests (172; 161 run here, all pass). Not yet run against the real programs.

## V16.2: workspace window (2026-10-30)

Details, what works now and what is still a skeleton: [docs/v16/WORKSPACE.md](docs/v16/WORKSPACE.md).

- **Full view is now a three-region workspace**: top bar (project, search and command palette, six mode buttons, notifications, profile menu), left sidebar (new chat, search, active jobs, pinned and recent chats, projects), the conversation, and a right sidebar with six modes (Cockpit, Artifacts, Access, Activity, Knowledge, Software) as separate controls sharing one panel.
- **Layout rules** in a tested view model: the conversation keeps ≥ 50% of a large window, sidebars resize within limits, shrink and hide on narrow windows (right first) and come back; sizes, visibility, mode and pins are saved.
- **Settings fix**: custom actions and the Save to folder are saved (they were lost at every restart).
- Verify builds: the XAML partial generator handles views with no named elements; the WPF stub gained the members the window uses.
- 3 new tests (168; 157 run here, all pass; XAML checker: 13 files, 583 bindings).

## V16.1: SolidWorks test battery fixes (2026-10-30)

Diagnosis per test: [docs/v16/RELEASE_NOTES.md](docs/v16/RELEASE_NOTES.md#v161-fixes-from-the-windows-test-battery).

- **An old Mediator in the tray answered the tests.** A newer build now offers to close an older running one; the tray and `doctor` show the version; `doctor` checks all SolidWorks tools are registered and warns when the tray runs another build.
- **Chats saved by V15 kept V15's tools**: reopened and continued chats get today's SolidWorks and Office tools.
- **Requests about the open model reach SolidWorks** without the word "SolidWorks" while SolidWorks is running.
- **The open document's folder is shared** with SolidWorks jobs, so the open part can be changed, exported and saved as.
- **Win32 error 32** on files open in SolidWorks fixed (path checks no longer conflict with SolidWorks' handle).
- **Terminal jobs** get a chat card instead of disappearing.
- 3 new tests (165; 154 run here, all pass).

## V16: code review and SolidWorks by chat (2026-10-29)

Full notes, review findings and the Windows checklist: [docs/v16/RELEASE_NOTES.md](docs/v16/RELEASE_NOTES.md).

- **SolidWorks by chat**: `swx_inspect_model` (feature tree, dimensions, equations, properties, mass and material, configurations, selection, BOM, mates, interference), `swx_edit_model` (dimensions, globals, suppression, renames, properties, material, configurations, components; unsaved for review by default, each operation untickable), `swx_create_drawing` (standard views, isometric, dimensions, PDF), `swx_capture_views` (pictures for the AI). `swx_document_ops` gains `save_as`; `swx_export_neutral` gains 3MF, DXF/DWG and PNG.
- **CAD-neutral layer** (`Mediator.Core.Cad`): `ICadApp` + `CadAppToolProvider`, so Inventor, Fusion, Revit or AutoCAD get the same tools under their own prefix ([docs/v16/CAD_CONNECTORS.md](docs/v16/CAD_CONNECTORS.md)). Tool names used so far are unchanged.
- **Fixes**: every SolidWorks tool now holds the `solidworks` lease; changes to the active document check its folder first; the "Input dimension value" preference is restored after each call; exports never overwrite; the mesh size limit applies to streams; faster Word reading; the 2 tests failing on V15 fixed.
- **Removed**: unused members and 17 unnecessary `using` lines.
- 4 new tests (162 in total; 151 run here, all pass).

## V14: Usage tab (2026-10-29)

- **Usage tab** in the overlay: one card per subscription. **Claude Pro** shows the 5-hour and weekly bars with their reset times, the same figures as Claude Code's `/usage` (claude.ai plus every Claude Code session). They come from Anthropic's account usage endpoint, using the OAuth token Claude Code saved at sign-in. **Google AI Pro** has no published meter, so its card shows a rate-limit cooldown if one is active and what Mediator's own Antigravity runs reported. Both cards list Mediator's own runs and tokens for the last 5 hours and 7 days, from the usage ledger.
- **No tokens**: no model runs. Results are cached for a minute; the tab refreshes when opened and with its refresh button. The token is read per request, sent only to api.anthropic.com, never logged, and never refreshed by Mediator (refreshing would sign Claude Code out). The endpoint is not a published API, so every failure is shown as a sentence instead of a wrong 0%.
- 2 new tests (150).

## V13: Mediator for Chrome (2026-10-29)

Mediator's own Chrome extension, shipped with the app. Claude Code or Antigravity can read and operate the user's Chrome tabs through `browser_*` tools on the bus ("/chrome open … and …"). Before this, Browser jobs needed Anthropic's Claude in Chrome extension and only worked with Claude.

- **`chrome-extension/`** (Manifest V3): `background.js` connects to `ws://127.0.0.1:<port>/chrome-bridge`, with a 20 s heartbeat and an alarm that reconnects the worker. It handles `get_tabs`, `navigate`, `read_page`, `click` / `click_coordinates`, `type_text`, `press_key`, `scroll`, `screenshot`, `read_console`, `eval_js`, `close_tab` and `release`. Clicks and keys are real CDP input sent through `chrome.debugger`, attached on demand and detached after 60 s idle. `serializePageForAssistant` lists text and controls as `[ref_N] role "name" (state) @(x,y)`, with refs stable across reads. It understands labels in front of hidden inputs, pointer-cursor answer cards, shadow DOM and frames. `read_page` takes `query` and `offset`, so a quiz page costs hundreds of tokens, not a screenshot. The popup shows the connection state.
- **`ChromeBridgeServer`**: a `TcpListener` on loopback plus `WebSocket.CreateFromStream`, so no admin or URL ACL is needed. It takes the first free port from 19845, splits large messages into multiple frames (up to 32 MB), tracks requests by id with a `TaskCompletionSource` each, and fails pending calls on disconnect. **Security:** the upgrade must come from `chrome-extension://dlgomnkpogggoofnllecblchidkecpfp` (fixed by the manifest `key`) with a loopback Host. Both sides then prove the per-user pairing secret (HMAC-SHA256 over both nonces, server first), so web pages, other extensions and port squatters are refused.
- **Install with one Chrome step**: at start the overlay copies the bundled extension to `%LOCALAPPDATA%\Mediator\chrome-extension` and writes `pairing.json` (port and secret). Connectors > Mediator for Chrome > Set up copies that path and opens `chrome://extensions`; the user turns on Developer mode, clicks Load unpacked and pastes the path. App updates refresh the folder.
- **`browser_*` tools**: tabs, navigate, read, click, type, key, scroll, screenshot (MCP image), console, close_tab and eval. A Browser job gets them, minus `browser_eval`, and runs input without prompts. In any other job they ask. `browser_eval` is Destructive and always asks, with the script shown. New `ToolDescriptor.Repeatable` keeps the bus from de-duplicating identical clicks.
- **Routing**: a Browser job uses the extension when it is connected or has paired before, with any CLI, local file tools off, and the browser debugger released at job end. Otherwise it falls back to V11 `claude --chrome`.
- 4 new tests (148): pairing and refusals, framing, concurrency, disconnect, upgrade checks, manifest key = admitted id, install/update/pairing files, and tools through the real bridge and bus. The serializer was also run in Chrome on a local quiz-style test page.

## V9: Windows release candidate (2026-10-23)

V9 builds on V8 (commit `a033d7f`). All 110 V8 tests still pass, and 9 V9 tests were added (119 in total). With `MEDIATOR_LIVE_PYTHON` set, 117 run and pass, plus 2 Windows-only skips. Without it, 111 pass. The six builds have 0 warnings with `TreatWarningsAsErrors=true`, including the overlay project, which now compiles all of its C#.

### Added

- **The whole overlay is compiled in verify builds.** `verify/stubs/WpfCompileStub` is a compile-only WPF and Windows Forms stand-in that follows the .NET 8 API, including its nullability. `XamlPartials.targets` generates the `InitializeComponent()` and named-field partials the markup compiler would. Verify-mode `Mediator.Wpf.csproj` now builds every `.cs` file into `Mediator.dll`, with WPF's implicit usings.
- **`MediatorUseShims`**, independent of `MediatorVerify`:
  - It builds the real Windows products with `SqliteShim` (System32 `winsqlite3.dll`) and `HashingShim`, from an empty local feed, without Native AOT. No NuGet download at all.
  - `publish-release.ps1` falls back to it when NuGet restore fails (`-UseShims`, `-NoShimFallback`). A missing MSVC toolchain now only drops Native AOT.
- **Hotkeys:**
  - `HotkeyParser` adds Tab, Enter and `~` (Tab and Enter need two modifiers; task-switcher combinations are refused).
  - `HotkeyBinding`: start falls back to the default, then to none. A live rebind keeps the previous hotkey on a conflict.
  - The setup card rebinds before it saves (`OnboardingViewModel.Rebinder`).
  - `HotkeyManager.Start` and `Rebind`.
  - The tray menu and tooltip show the active hotkey.
- **Session continuation:**
  - `RunningJob.SessionId`, `ContinuableSession`, `MediatorRuntime.LastContinuableSession` and `JobSpecFactory.Continue`.
  - `JobSpec.ContinueSessionId` makes the runtime run `claude -p --resume <id>`, or `claude --resume` in the terminal, in the same folder with a new job and grant. Only safe-token ids are accepted, and the session is pinned to Claude Code.
  - The overlay shows **↩ Continue session (id)**: selected by default, click to deselect, × or a folder switch removes it. `MainWindow.xaml` has the new chip.
- **Tests:** 9 new ones. 2 compile WPF code against the stub, 2 cover hotkeys, 2 cover sessions, and 3 cover the offline build: the script fallback, MSBuild evaluation, and a real ship build plus `doctor` from an empty feed. The CI Windows job also builds everything with `MediatorUseShims=true`.

### Changed

- **The shims** are renamed `Microsoft.Data.Sqlite.MediatorShim` and `System.IO.Hashing.MediatorShim`, and are no longer marked verify-only.
- **SQLite loading on Windows:** System32 first, then the shim's own folder, never PATH.
- **`publish-release.ps1`:** builds come from a publish plan (`Get-PublishPlan`). `VERSION.txt` says V9 and which dependencies the package used.
- **A test assertion:** one V7 assertion text changed, because Ctrl+Enter is now refused for needing a second modifier, not as unsupported.

### Fixed

- `RunningJob.TryFinish` records the finished job's continuation before `Completion` completes. The full suite caught this race in the new V9 code.

### Release hygiene

- V8 is committed as `a033d7f` and V9 as the commit after it, and the working tree is clean.
- `mediator-v9.zip` is made from the clean tree.

## V8: Windows stabilized release candidate (2026-10-23)

V8 builds on V7 (commit `fb0179c`) and targets the Windows friction that V7's offline checks could not see. All 100 V7 tests still pass with their original assertions, and 10 V8 tests were added (110 in total). With `MEDIATOR_LIVE_PYTHON` set, 108 run and pass; the 2 skips are the Windows-only NTFS and sharing-violation tests. Without it, 102 pass and the 6 live tests are skipped. The six builds in `verify/run-tests.sh` have 0 warnings with `TreatWarningsAsErrors=true`.

### Fixed

- **Theme loading by absolute pack URI** (`ThemeManager`, new `Mediator.UI.ThemeSources`). Themes load from `pack://application:,,,/Mediator;component/Themes/<name>.xaml` with `UriKind.Absolute` instead of a relative `/Mediator;component/…` URI. If the compiled resource still cannot be found, the loose copy next to the exe is used (`Themes\<name>.xaml`, copied at build and publish by `Mediator.Wpf.csproj`). If both fail, the current theme stays and a tray notice says why. A missing theme no longer stops the app. `App.xaml` merges HoloHud by the same absolute URI.
- **Desktop apps found without PATH** (new `DesktopAppLocator`, through `ComponentLocator.FindDesktopApp`).
  - **DaVinci Resolve**: `HKLM\SOFTWARE\Blackmagic Design\DaVinci Resolve`, Apps & features (Studio detected from its name), and `%ProgramFiles%`, `%ProgramW6432%` or `%ProgramFiles(x86)%\Blackmagic Design\DaVinci Resolve\Resolve.exe`.
  - **Blender**: version folders matched by wildcard (`Blender Foundation\Blender 4.2`, `Blender 5.1`, …; the newest wins) under Program Files and `%LOCALAPPDATA%\Programs`, plus `HKLM`/`HKCU\SOFTWARE\BlenderFoundation`, Apps & features and Steam.
  - **SolidWorks**: `HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup` (newest year wins, with its `api\redist`), the `SldWorks.Application` COM registration, Apps & features and `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`.
  - `MEDIATOR_SOLIDWORKS`, `MEDIATOR_RESOLVE` and `MEDIATOR_BLENDER` override the search. PATH still wins for Blender.
  - Used by `ConnectorProbe` (the Connectors tab now says where each app was found or where it looked), `ToolLocator.Blender`, the Resolve scripting environment (`fusionscript.dll` from the actual install folder) and `mediator-cli doctor` (new "<app> install" lines).
- **Builds without SolidWorks.** A missing `SolidWorks.Interop.sldworks.dll` is now warning **MEDIATOR001** instead of an error. It never fails `-warnaserror` builds. The build defines `SOLIDWORKS_NOT_INSTALLED`, and `mediator-swx-worker` is built with `SwxUnavailableHost`, which answers every call with `solidworks_not_installed` / "SolidWorks assemblies not installed on this machine…". `SwxToolProvider` tells the model not to retry. `-p:MediatorRequireSolidWorks=true` (or `publish-release.ps1 -RequireSolidWorks`, `check-windows-build.ps1 -CheckSolidWorks`) restores the hard error as **MEDIATOR002**. The worker's host surface moved to `ISwxHost` (`SwxHostContracts.cs`); `SwxComHost.cs` and `SwxConnector.cs` compile only when the interop exists.
- **Panel placement on multi-monitor and mixed-DPI setups** (new `Mediator.UI.PanelPlacement`, rewritten `MonitorPlacement`, `MainWindow`).
  - **Placement**: `MonitorPlacement.PlaceAboveTray(hwnd, anchor)` places the window in the corner next to the tray: bottom-right, top-right for a top taskbar, bottom-left for a left one.
  - **Usable area**: placement uses the work area of the monitor under the mouse. The strip of an auto-hide taskbar is kept clear.
  - **DPI**: sizes are converted at the target monitor's DPI (`GetDpiForMonitor`; `GetDpiForWindow` for the current size).
  - **No flicker**: the window is placed before it is shown. A `WM_WINDOWPOSCHANGING` hook keeps it anchored through SizeToContent and DPI-change resizes, so no frame hangs off the screen.
  - **Height cap**: `MaxHeight` is capped to the monitor's usable height.
  - **Re-placement**: display and work-area changes re-place the window.
- **PowerShell 5.1 encoding trap**: `publish-release.ps1` and `swx-integration.ps1` were UTF-8 without a BOM and contained non-ASCII characters (`→`, `─`). Windows PowerShell 5.1 decodes such a file as ANSI, and the byte `0x94` inside `─` becomes a curly quote that PowerShell treats as a string delimiter. All scripts are now ASCII, and a test keeps them so.

### Added

- `scripts/launch-mediator.bat`, published as `dist\Mediator\launch.bat`. It is plain cmd, so no execution policy applies and no admin rights are needed. `launch.bat doctor` runs the installation check. It uses no parenthesised blocks, so paths such as `C:\Tools (x86)\…` work.
- `scripts/install-shortcut.vbs`: Desktop and Start-menu shortcuts, per user, offering to start the app (`/nostartmenu`, `/quiet`).
- `.gitattributes` keeps CRLF line endings for `.bat`, `.cmd` and `.vbs`.
- `verify/stubs/SwxWorkerNoInterop`: the worker's own sources built with no interop at all, run as a real child process by the tests.
- The 2 V7 XAML tests now also check pack URIs, with new mutations for a relative URI, a wrong assembly, a theme that can't be loaded loose and a missing loose-theme copy.
- 10 new tests:
  - Desktop-app discovery: 3 tests, using a fake registry and a fake Program Files tree.
  - No-SolidWorks: 3 tests, in-process, through the bus, and a real ship-mode build with `-warnaserror`.
  - Placement geometry and `WINDOWPOS` rewrite on an unmanaged buffer: 2 tests, including 5,000 random layouts.
  - Release scripts: 2 static tests.

### Changed

- `publish-release.ps1` always publishes all four executables. It checks that the three loose themes and both launchers are present and leaves `.pdb` files out unless `-IncludeSymbols` is given. It also writes an ASCII `README-FIRST.txt` that covers SmartScreen and the launchers.
- `MonitorPlacement.cs` is compiled by the Linux verify build (`Mediator.Wpf` in verify mode), so its P/Invoke declarations compile on every run.
- CI (`.github/workflows/v6.yml`):
  - The Windows ship build also builds the worker with no SolidWorks.
  - The package job checks for the launchers and themes, and that the worker answers `solidworks_not_installed`.
