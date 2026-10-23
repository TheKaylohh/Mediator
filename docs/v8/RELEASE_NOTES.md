# AI Cockpit V8: Windows stabilized release candidate

V8 removes the five sources of Windows friction listed in the V8 brief. It does not change the security model, the bus, the gate, the journal or any tool's behaviour.

> **Status, plainly.** V8 is a release candidate, not a verified Windows release. Everything below was built and tested on Linux (Ubuntu 24.04, .NET SDK 8.0.132). The WPF overlay itself (`AiCockpit.exe`) has still never been compiled: the WindowsDesktop reference pack is a NuGet download, and NuGet was not reachable. The first run of `scripts\publish-release.ps1` on a Windows 11 PC is the remaining gate; see the checklist at the end.

## What was fixed

### 1. Theme loading (pack URIs)

`ThemeManager.UriFor` now returns `pack://application:,,,/AiCockpit;component/Themes/<theme>.xaml` (`UriKind.Absolute`). The string comes from `Cockpit.UI.ThemeSources`, so it can be tested off Windows. Loading tries three sources in order:

1. **Compiled resource**: loaded by the absolute pack URI.
2. **Loose copy**: if step 1 throws (`IOException: Cannot locate resource`, or a `UriFormatException` if the `pack:` scheme isn't registered yet), `<exe folder>\Themes\<theme>.xaml` is read with `XamlReader`. `Cockpit.Wpf.csproj` copies the three themes there at build and publish time. Only that fixed, known file name is ever read.
3. **Keep the current theme**: if both fail, the current dictionary stays. At startup that is HoloHud, merged by `App.xaml`, which now uses the same absolute pack URI. `ThemeManager.LoadFailed` logs the reason and shows a tray notice.

XamlChecker now also checks:

- `ThemeSources.WpfAssemblyName` equals `<AssemblyName>` in the csproj.
- Every `ThemeSources.PackUri(theme)` points at that theme's ResourceDictionary.
- ThemeManager uses `UriKind.Absolute`, and no code builds a relative `;component/` URI.
- Every `pack://` literal and relative `Source=` in the XAML resolves to a project file.
- The csproj copies the loose themes.
- Each theme can be loaded loose: no `x:Class`, and every `clr-namespace` names its assembly.

The mutation test proves each rule fires.

### 2. Finding SolidWorks, Resolve and Blender

`Cockpit.Core.Runtime.DesktopAppLocator` is reached through `ComponentLocator.FindDesktopApp`. It is read-only and never writes to the registry. Search order:

| App | Order |
| --- | --- |
| all | `COCKPIT_<APP>` override → PATH (not SolidWorks) → the locations below, newest version first |
| DaVinci Resolve | `HKLM\SOFTWARE\Blackmagic Design\DaVinci Resolve` (values and subkeys), Apps & features (`DaVinci Resolve*`, not Panels), `%ProgramW6432%` / `%ProgramFiles%` / `%ProgramFiles(x86)%\Blackmagic Design\DaVinci Resolve\Resolve.exe` |
| Blender | `…\Blender Foundation\Blender*\blender.exe` under Program Files and `%LOCALAPPDATA%\Programs` (version folders such as `Blender 4.2`, `Blender 5.1`; the newest wins), `HKLM` / `HKCU\SOFTWARE\BlenderFoundation`, Apps & features, Steam (`steamapps\common\Blender`) |
| SolidWorks | `HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup` → `SolidWorks Folder` (newest year), the `SldWorks.Application` → `CLSID` → `LocalServer32` COM registration, Apps & features, `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`; reports `api\redist` when it holds the interop |

The registry is read in both the 64-bit and 32-bit views, and from `HKCU` where per-user installers write. Blackmagic and the Blender Foundation do not document their registry value names. So instead of trusting one name, the locator accepts any string value that names a folder containing the exe, the exe itself, or an icon reference like `"…\blender.exe",0`.

The results are used in four places:

- **Connectors tab** (`ConnectorProbe`): shows where each app was found (product, version, path, how it was found) or every place it looked. Resolve Studio is also recognised from its Apps & features name.
- **Doctor**: `cockpit doctor` prints a "SolidWorks / DaVinci Resolve / Blender install" line, and finds the SolidWorks interop through the same search.
- **Resolve script**: `RESOLVE_SCRIPT_LIB` points at `fusionscript.dll` in the install folder actually found.
- **Blender**: `ToolLocator.Blender` (and so `BlenderRunner`) uses the same search.

### 3. Building on a PC without SolidWorks

`Directory.Build.targets` now decides `CockpitSolidWorksInterop`:

- `real`: both interop DLLs are in `$(SolidWorksInteropDir)`.
- `stub`: verify builds.
- `none`: anything else.

With `none`:

- MSBuild emits **warning COCKPIT001**. It is listed in `MSBuildWarningsNotAsErrors`, so `-warnaserror` and `TreatWarningsAsErrors` still succeed.
- `SOLIDWORKS_NOT_INSTALLED` is defined.
- `SwxComHost.cs` and `SwxConnector.cs` drop out, and `SwxHostFactory` returns `SwxUnavailableHost`. It answers `health`, `part_cards`, `import_step` and `build_assembly` with:

```json
{"id":1,"error":{"code":"solidworks_not_installed","message":"SolidWorks assemblies not installed on this machine: this cockpit-swx-worker was built without the SolidWorks interop …"}}
```

`SwxToolProvider` adds "Do not retry this tool; tell the user …" for that code. `AiCockpit.exe`, `cockpit.exe` and `cockpit-mcp-bridge.exe` don't reference the interop at all, so they build the same with or without it.

To make a missing interop fatal again, use `-p:CockpitRequireSolidWorks=true` (**error COCKPIT002**). `publish-release.ps1 -RequireSolidWorks` and `check-windows-build.ps1 -CheckSolidWorks` set it.

### 4. Multi-monitor and DPI placement

The causes of the flicker and jumps, and the fixes:

| Cause in V7 | V8 |
| --- | --- |
| Shown first, moved afterwards: one frame at the old position. | Placed while still hidden (`EnsureHandle` before the first show), then shown. |
| Size taken at the old monitor's DPI, so on a 100 % → 200 % move it was placed for half its final size. | The size is converted to DIP with `GetDpiForWindow`, then to pixels with the target monitor's `GetDpiForMonitor`. |
| SizeToContent growth extended the window downwards for one frame before `SizeChanged` moved it up. | A `WM_WINDOWPOSCHANGING` hook (`MonitorPlacement.KeepAnchored`) rewrites the position in the same message, for content growth and for `WM_DPICHANGED`'s suggested rect. |
| Only the bottom-right corner and only `Max(left/top)` clamps, so the window could run off-screen when taller than the work area. | Corner by the tray (top-right for a top taskbar, bottom-left for a left one). Full clamping, top-left first so the header stays visible. `MaxHeight` is capped to the monitor's usable height. |
| Auto-hide taskbars ignored, so the taskbar popped up over the panel. | `ABM_GETAUTOHIDEBAREX` per edge; that strip is kept clear (its real thickness, or 48 DIP). |
| Monitor or taskbar changes while open were ignored. | `WM_DISPLAYCHANGE` and `WM_SETTINGCHANGE(SPI_SETWORKAREA)` trigger a re-placement. |

The geometry lives in `Cockpit.UI.PanelPlacement` and is tested on Linux. The cases are 1080p at 100 %, a Surface at 200 %, 150 %, a 100 → 200 % move, monitors left of and above the primary (negative coordinates), taskbars on all four edges, auto-hide with known and unknown thickness, a window too tall or too wide, and 5,000 seeded random layouts. `MonitorPlacement.cs` has no WPF types. The Linux verify build compiles it (P/Invoke source generation included), and its `WINDOWPOS` rewrite is tested on a real unmanaged buffer. The tests also check the `WINDOWPOS`, `APPBARDATA` and `MONITORINFO` layouts against the Windows headers.

### 5. One-click package

`scripts\publish-release.ps1` writes `dist\AiCockpit\`:

```
AiCockpit.exe  cockpit.exe  cockpit-mcp-bridge.exe  cockpit-swx-worker.exe   (always all four)
Themes\HoloHud.xaml  Themes\ProDark.xaml  Themes\CleanLight.xaml           (loose fallback copies)
launch.bat              double-click: starts the tray app; "launch.bat doctor" checks the install
install-shortcut.vbs    double-click: Desktop + Start menu shortcuts (per user), offers to start the app
Create-Desktop-Shortcut.ps1  README-FIRST.txt  VERSION.txt  SHA256SUMS.txt  + the .NET runtime files
```

The script refuses to finish if any of the four executables, three themes or two launchers is missing. `.pdb` files are left out unless `-IncludeSymbols` is given. Both launchers are ASCII with CRLF line endings, enforced by `.gitattributes` and a test. `launch.bat` avoids parenthesised blocks, so a folder like `C:\Tools (x86)\AiCockpit` still works. Neither launcher needs PowerShell or administrator rights.

Two caveats for users:

- **SmartScreen**: the executables are unsigned, so SmartScreen may ask once. `README-FIRST.txt` explains this.
- **VBScript**: Microsoft is phasing VBScript out; it is an optional feature, still on by default in Windows 11 24H2. If it is removed, use `launch.bat`, `Create-Desktop-Shortcut.ps1`, or right-click → Send to → Desktop.

**Found on the way:** two `.ps1` scripts contained UTF-8 `→`/`─` without a BOM. Windows PowerShell 5.1 decodes such files as ANSI, which turns the `0x94` byte of `─` into a curly double quote, a string delimiter for PowerShell. All scripts are ASCII now, and a test keeps them so.

## Verification

| Check | Result |
| --- | --- |
| `bash verify/run-tests.sh` (offline) | **102 passed, 0 failed**, 8 skipped: 6 live tests need `COCKPIT_LIVE_PYTHON`, 2 need Windows ([transcript](test-run-linux-offline.txt)) |
| `COCKPIT_LIVE_PYTHON=… bash verify/run-tests.sh` | **108 passed, 0 failed**, 2 skipped: Windows-only NTFS and sharing-violation tests ([transcript](test-run-linux-live.txt)) |
| Warnings | 0 in all six builds of the run (`TreatWarningsAsErrors=true`) |
| V7 baseline on the same machine | 92 offline / 98 live, so all V7 tests still pass unchanged |
| XamlChecker | 6 files, 183 bindings, 303 resource references, 44 keys × 3 themes, 4 pack URIs; every rule's mutation is caught |
| Ship-mode worker build without SolidWorks | Real `CockpitVerify=false` build in a scratch copy (TFM set to `net8.0` off Windows): succeeds with `-warnaserror` and exactly COCKPIT001; `CockpitRequireSolidWorks=true` fails with COCKPIT002; the built worker answers `solidworks_not_installed` |

What these tests do **not** prove, because it needs Windows:

- That `AiCockpit.exe` compiles. `ThemeManager.cs`, `MainWindow.xaml.cs` and `App.xaml.cs` use WPF types and were reviewed by hand, not compiled.
- That the real registry adapter (`WindowsRegistryReader`) reads real installs. The discovery rules are tested against a fake registry.
- That the flicker is gone on real mixed-DPI hardware. The geometry and the `WINDOWPOS` rewrite are tested; the Win32 queries that feed them are compiled but not run.
- That `launch.bat` and `install-shortcut.vbs` run. They are checked statically only.

## Windows acceptance checklist (first run)

1. `powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -RunTests` on Windows 11 without SolidWorks. Expected:
   - `run-tests.ps1` passes, including the NTFS tests.
   - Every publish succeeds; the worker shows only COCKPIT001.
   - `dist\AiCockpit` is written, and `doctor` runs.
2. Double-click `dist\AiCockpit\launch.bat`. The tray icon appears and `Ctrl+Shift+Space` opens the panel above the tray.
3. Cycle the three themes from the header. Then rename `Themes\ProDark.xaml` inside the exe folder: switching must still work, because the compiled resource is used. The loose path is only a fallback.
4. Two monitors at different scales (for example 200 % + 100 %): open the panel on each, type until it grows, switch tabs. It should never flicker, jump, or extend past a screen edge. Repeat with the taskbar on the top or left, and with auto-hide on.
5. Connectors tab on a PC with Blender (Program Files and/or `%LOCALAPPDATA%\Programs`) and Resolve installed but not on PATH. Both should be found, with version and path.
6. A SolidWorks job without SolidWorks. The model should get "SolidWorks assemblies not installed on this machine" and stop retrying.
7. On the SolidWorks PC: `scripts\check-windows-build.ps1 -RunVerifySuite -CheckSolidWorks`, then `scripts\swx-integration.ps1`.
8. Double-click `install-shortcut.vbs`. Shortcuts should appear on the Desktop and in the Start menu, and the app should start from them.
