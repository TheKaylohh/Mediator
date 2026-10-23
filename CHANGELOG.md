# Changelog

Earlier versions: [V7](docs/v7/CHANGELOG.md) (UI/UX), [V6](docs/v6/CHANGELOG.md) (hardening). The full V8 story, with what was and was not verified, is in [docs/v8/RELEASE_NOTES.md](docs/v8/RELEASE_NOTES.md).

## V8: Windows stabilized release candidate (2026-10-23)

V8 builds on V7 (commit `fb0179c`) and targets the Windows friction that V7's offline checks could not see. All 100 V7 tests still pass with their original assertions, and 10 V8 tests were added (110 in total). With `COCKPIT_LIVE_PYTHON` set, 108 run and pass; the 2 skips are the Windows-only NTFS and sharing-violation tests. Without it, 102 pass and the 6 live tests are skipped. The six builds in `verify/run-tests.sh` have 0 warnings with `TreatWarningsAsErrors=true`.

### Fixed

- **Theme loading by absolute pack URI** (`ThemeManager`, new `Cockpit.UI.ThemeSources`). Themes load from `pack://application:,,,/AiCockpit;component/Themes/<name>.xaml` with `UriKind.Absolute` instead of a relative `/AiCockpit;component/…` URI. If the compiled resource still cannot be found, the loose copy next to the exe is used (`Themes\<name>.xaml`, copied at build and publish by `Cockpit.Wpf.csproj`). If both fail, the current theme stays and a tray notice says why. A missing theme no longer stops the app. `App.xaml` merges HoloHud by the same absolute URI.
- **Desktop apps found without PATH** (new `DesktopAppLocator`, through `ComponentLocator.FindDesktopApp`).
  - **DaVinci Resolve**: `HKLM\SOFTWARE\Blackmagic Design\DaVinci Resolve`, Apps & features (Studio detected from its name), and `%ProgramFiles%`, `%ProgramW6432%` or `%ProgramFiles(x86)%\Blackmagic Design\DaVinci Resolve\Resolve.exe`.
  - **Blender**: version folders matched by wildcard (`Blender Foundation\Blender 4.2`, `Blender 5.1`, …; the newest wins) under Program Files and `%LOCALAPPDATA%\Programs`, plus `HKLM`/`HKCU\SOFTWARE\BlenderFoundation`, Apps & features and Steam.
  - **SolidWorks**: `HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup` (newest year wins, with its `api\redist`), the `SldWorks.Application` COM registration, Apps & features and `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`.
  - `COCKPIT_SOLIDWORKS`, `COCKPIT_RESOLVE` and `COCKPIT_BLENDER` override the search. PATH still wins for Blender.
  - Used by `ConnectorProbe` (the Connectors tab now says where each app was found or where it looked), `ToolLocator.Blender`, the Resolve scripting environment (`fusionscript.dll` from the actual install folder) and `cockpit doctor` (new "<app> install" lines).
- **Builds without SolidWorks.** A missing `SolidWorks.Interop.sldworks.dll` is now warning **COCKPIT001** instead of an error. It never fails `-warnaserror` builds. The build defines `SOLIDWORKS_NOT_INSTALLED`, and `cockpit-swx-worker` is built with `SwxUnavailableHost`, which answers every call with `solidworks_not_installed` / "SolidWorks assemblies not installed on this machine…". `SwxToolProvider` tells the model not to retry. `-p:CockpitRequireSolidWorks=true` (or `publish-release.ps1 -RequireSolidWorks`, `check-windows-build.ps1 -CheckSolidWorks`) restores the hard error as **COCKPIT002**. The worker's host surface moved to `ISwxHost` (`SwxHostContracts.cs`); `SwxComHost.cs` and `SwxConnector.cs` compile only when the interop exists.
- **Panel placement on multi-monitor and mixed-DPI setups** (new `Cockpit.UI.PanelPlacement`, rewritten `MonitorPlacement`, `MainWindow`).
  - **Placement**: `MonitorPlacement.PlaceAboveTray(hwnd, anchor)` places the window in the corner next to the tray: bottom-right, top-right for a top taskbar, bottom-left for a left one.
  - **Usable area**: placement uses the work area of the monitor under the mouse. The strip of an auto-hide taskbar is kept clear.
  - **DPI**: sizes are converted at the target monitor's DPI (`GetDpiForMonitor`; `GetDpiForWindow` for the current size).
  - **No flicker**: the window is placed before it is shown. A `WM_WINDOWPOSCHANGING` hook keeps it anchored through SizeToContent and DPI-change resizes, so no frame hangs off the screen.
  - **Height cap**: `MaxHeight` is capped to the monitor's usable height.
  - **Re-placement**: display and work-area changes re-place the window.
- **PowerShell 5.1 encoding trap**: `publish-release.ps1` and `swx-integration.ps1` were UTF-8 without a BOM and contained non-ASCII characters (`→`, `─`). Windows PowerShell 5.1 decodes such a file as ANSI, and the byte `0x94` inside `─` becomes a curly quote that PowerShell treats as a string delimiter. All scripts are now ASCII, and a test keeps them so.

### Added

- `scripts/launch-cockpit.bat`, published as `dist\AiCockpit\launch.bat`. It is plain cmd, so no execution policy applies and no admin rights are needed. `launch.bat doctor` runs the installation check. It uses no parenthesised blocks, so paths such as `C:\Tools (x86)\…` work.
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
- `MonitorPlacement.cs` is compiled by the Linux verify build (`Cockpit.Wpf` in verify mode), so its P/Invoke declarations compile on every run.
- CI (`.github/workflows/v6.yml`):
  - The Windows ship build also builds the worker with no SolidWorks.
  - The package job checks for the launchers and themes, and that the worker answers `solidworks_not_installed`.
