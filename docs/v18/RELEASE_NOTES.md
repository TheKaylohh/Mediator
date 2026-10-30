# V18: connectors as plug-ins, the full-screen app, light and dark themes

## 1. The quick panel and the full-screen app can be open together

- Opening the full screen (Open full view, the "Mediator Workspace" shortcut, `Mediator.exe --workspace`) leaves the quick panel as it is. The hotkey and the tray icon still toggle the panel.
- Clicking from the panel into the full screen does not hide the panel; clicking another program's window does (unless pinned).
- Settings, Usage and Connections in the full screen's profile menu open the quick panel on that tab; the full screen stays open.
- Rules live in `MediatorOverlayViewModel` (`IsWorkspaceOpen`, `ShowPanelTab`) and are tested.

## 2. The full screen is its own app

- Its own taskbar button and Alt+Tab entry (the quick panel has neither), and it reopens where you left it (position, size, maximized), saved in settings.json.
- **"Mediator Workspace" shortcut** (Desktop and Start menu, from `install-shortcut.vbs` or `Create-Desktop-Shortcut.ps1`): runs `Mediator.exe --workspace`. Mediator keeps running in the tray when you close the window.
- Starting Mediator again while it runs no longer shows "already running": the running copy opens the panel (or, with `--workspace`, the full screen), like clicking an app's icon.

## 3. One folder per connector

See [CONNECTORS.md](CONNECTORS.md). 13 connectors (SolidWorks, FreeCAD, AutoCAD, Inventor, NX, Creo, CATIA, Parts from code, CAD files, Office, Resolve, Blender, Tutor) moved out of Mediator.Core into `src/Connectors/<Name>`, each its own assembly loaded from `<app>\connectors\<Name>`. The core no longer references any of them. The SolidWorks worker moved to `src/Connectors/SolidWorks/Worker` and ships in the SolidWorks folder. New: `mediator-cli connectors`; `mediator-cli doctor` asks each connector to check itself; the Connectors tab and the Software Library list the installed connectors' programs (the six CAD programs now appear there too).

## 4. New Dark and Light themes

- **Dark** (new default): graphite surfaces, one indigo accent for actions and focus, sky blue for activity, status colours kept for status. **Light**: paper-white surfaces with the same accent, darkened for white. Every text colour meets WCAG AA (≥ 4.5:1) on the background, panel and raised surfaces of its theme.
- A **sun/moon button** in both windows switches light and dark (the full screen's profile menu has it too). A classic theme toggles by lightness (Clean Light → Dark, the dark classics → Light).
- The V7–V17 themes are kept unchanged in `Themes/Legacy` (Holo HUD, Pro Dark, Clean Light) and can still be picked in the setup card. Settings that named one keep it.

## Verification

- Linux, offline: `bash verify/run-tests.sh` (177 tests; 166 run here, all pass; 11 need Windows, bubblewrap or the live Python stack). New tests cover the catalog with any subset of connectors (none, no SolidWorks), loading a connector folder in its own context (and skipping a broken one), a runtime started with one connector (a missing connector's tools are not granted), the panel/full-screen rules, window placement, and the light/dark toggle.
- A real CLI build puts all 13 connectors in `connectors\`; `mediator-cli connectors` and `doctor --simulate` load and check them (SolidWorks simulated).
- **Not run here**: the WPF windows (no Windows), the taskbar and shortcut behaviour, the second-start hand-over (Windows named events), and the themes on screen. Checklist on Windows: start Mediator, open the full screen, press the hotkey (the full screen comes forward, no panel), open Settings from the profile menu (the full screen closes, the panel opens on Settings), toggle light/dark in each window, close and reopen the full screen (same place), run the "Mediator Workspace" shortcut while Mediator runs.
