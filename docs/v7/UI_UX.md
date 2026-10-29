# V7 overlay: themes, HUD orb, attachments, Connectors, first run

V7 rebuilds the WPF overlay's look and adds four features. Behaviour lives in `src/Cockpit.UI` (plain .NET, unit-tested on Linux). `src/Cockpit.Wpf` is a thin view. **The WPF project has not been compiled yet**: it cannot be built on Linux, and NuGet is blocked in the environment that wrote it. XamlChecker checks the XAML offline (see [TEST_RESULTS](TEST_RESULTS.md)). The first real compile is the `windows-release-package` CI job or `scripts/publish-release.ps1`, followed by [WINDOWS_CHECKLIST](WINDOWS_CHECKLIST.md).

## Names

The request called the default theme `JarvisHud` and the control `JarvisReactorControl`. They ship as `HoloHud` (shown as "Holo HUD") and `HudOrbControl`. The look is an original holographic HUD built from generic vector shapes: segmented rings, a reticle, scanlines and corner brackets. It does not copy a film prop or use a trademarked name. The requested colours are used exactly.

## 1. Theme engine

| | Holo HUD (default) | Pro Dark | Clean Light |
|---|---|---|---|
| Canvas | `#E6080D14` glass, lit-visor gradient | `#FF1E1F22` | `#FFF8FAFC` |
| Panels | `#CC0E1724` | `#FF2B2D30` | `#FFFFFFFF` cards, `#FFE2E8F0` borders |
| Accent | cyan `#FF00E5FF`, reactor blue `#FF0088FF` | `#FF3574F0` | `#FF2563EB` |
| Warning / critical | amber `#FFFFB300` / crimson `#FFFF2A55` | `#FFF2C55C` / `#FFE55765` | `#FFD97706` / `#FFDC2626` |
| Text | `#FFE6FBFF` | `#FFDFE1E5` | `#FF0F172A` |
| Type | Bahnschrift headers, Cascadia Mono telemetry | Segoe UI, JetBrains Mono | Segoe UI, Cascadia Mono |
| Extras | scanlines, corner brackets, cyan glow, 4 px corners | none, 10 px corners | soft shadow, 12 px corners |

How it works:

- **Three dictionaries.** `Themes/HoloHud.xaml`, `Themes/ProDark.xaml` and `Themes/CleanLight.xaml` define the same 44 keys: colours, brushes, orb state brushes, fonts, corner radii, shadow opacity, and whether the HUD decoration is visible.
- **App.xaml** merges the default theme at `MergedDictionaries[0]`. Its styles (buttons, chips, tabs, fields, cards) use `{DynamicResource}` for every theme key.
- **`ThemeManager.Apply`** replaces that one dictionary using the pack URI `/AiCockpit;component/Themes/<name>.xaml`. Because every reference is dynamic, the window updates at once.
- **`CockpitOverlayViewModel.CurrentTheme`** is saved in `settings.json` in the data folder. Writes are atomic. A malformed file is kept as `settings.json.bad` and defaults are used.
- **The header button** (colour-palette icon) cycles Holo HUD → Pro Dark → Clean Light. The first-run card has the same choice.
- **Known limit.** The job-state dots in the Jobs tab use a converter, so they take the new theme's colour at their next state change, not immediately.

## 2. HUD orb (`Controls/HudOrbControl.xaml`)

The orb is a 44 × 44 Canvas containing a glow, a reticle (four ticks), three rings and a core:

- an outer ring of dashes;
- a middle ring of three arcs;
- an inner ring of dots;
- a core in the centre.

Only `RenderTransform` and `Opacity` are animated. WPF composites these on the GPU without layout passes. When Windows' "Show animations" is off, the orb shows the state colour without motion.

| `ReactorState` | Colour (theme key) | Motion |
|---|---|---|
| Idle | `OrbIdleBrush` (cyan) | rings counter-rotate at 10 s per turn, the core breathes (3 s) |
| Active | `OrbActiveBrush` (reactor blue) | 1.5 s per turn, fast core pulse; a light sweep oscillates along the prompt's bottom edge |
| AwaitingApproval | `OrbApprovalBrush` (amber) | slows to 16 s per turn; a radial pulse repeats from the core |
| Error | `OrbErrorBrush` (crimson) | no motion, steady glow |

**Priority.** AwaitingApproval wins (the user must act). Then Active (any job running). Then Error. Then Idle.

**What sets Error, and what clears it.** Error is set by:

- a failed job;
- a run that ended "not signed in" or "usage limit";
- a rate-limit event;
- the billing alarm.

It stays until the user clicks the orb, or until a new job starts. The caption under the title reads one of:

- STANDBY
- ACTIVE · n JOBS
- AWAITING APPROVAL
- FAULT

## 3. Attachments: drag-and-drop, Ctrl+V, `[ + Attach ]`

| Input | What happens |
|---|---|
| Files or folders dragged onto the panel | A glowing "DROP ARTIFACTS TO ATTACH" overlay appears. On drop, each path becomes a removable chip. |
| Ctrl+V with files on the clipboard | Same as a drop. |
| Ctrl+V with an image | The clipboard's `PNG` stream (which keeps transparency) or the bitmap, encoded as PNG, is staged as `pasted-<yyyyMMdd-HHmmss>.png` and becomes an image chip. |
| Ctrl+V with text | It pastes into the prompt as before. |
| `[ + Attach ]` | A file picker opens. Shortcuts are attached as themselves, not followed. |

**The Core checks every path.** The view only hands over strings. `AttachmentStore` then applies these rules:

- **Files are copied.** Each file is snapshotted into its own folder, `<data>/attachments/<id>/`, and the job gets only that folder, read-only. The copy reads through a handle opened without following a final link. That handle must resolve to the path the user gave; a link swapped in during the copy is refused.
- **Folders are shared in place, read-only.** The new `read_only` rule in `PathAuthorizer` allows Read and List, and denies Create, Modify and Mkdir.
- **These are refused:**
  - links;
  - FIFOs, sockets and devices;
  - `/proc`, `/sys`, `/dev` and `/run`;
  - a drive root;
  - the whole user folder;
  - the Cockpit's data folder;
  - any credential folder, or a folder containing one: `~/.claude`, `~/.gemini`, `~/.ssh`, `~/.aws`, `~/.azure`, `~/.gnupg`, `~/.kube`, `~/.docker`, `~/.config/gcloud`. This is checked on the real path too, so a linked parent folder does not get around it.
- **Size limits:** 256 MiB per copied file, 20 MiB per pasted image, 20 attachments per prompt.
- **Pasted images** must start with a valid PNG header. Snapshots older than 7 days are deleted at start-up.

**Who gets an attachment:**

- **The next job.** Its prompt lists the attachments, and they become read-only roots. The model reads them with `cockpit_attachments` and `cockpit_read_attachment`. Images come back as MCP image content, so vision models see them.
- **The running job, when exactly one is running and it can take attachments.** That means it started with attachments, or it is an interactive terminal session. The read-only root is added to its grant and audited as `cockpit/attach`; its tool list does not change. A headless job that started without attachments refuses, because it cannot take new input mid-run. The chip then stays for the next job and the status line says so.

**Removing a chip.** The × deletes the snapshot if no job received it. A snapshot a job received is kept until pruned, because the job may be reading it.

**Pin.** The panel hides when another window is clicked, which would prevent dragging from Explorer. The pin button in the header keeps it open. Pressing the hotkey during a drag also works.

**Deviation from the request.** Pasted images go to `<data>/attachments/<id>/`, not `<Workspace>/pasted/`. Each attachment gets its own read-only root, so a job never gains write access to a shared `pasted` folder. Nothing is written into the user's project folder either.

## 4. Connectors tab

**Subscription CLIs.** Claude Code CLI and Gemini CLI each show:

- the version, from `--version`;
- the sign-in state;
- the usage-limit cooldown, from the ledger;
- a `[ Sign In ]` button.

**Sign-in state is reported honestly.** The Cockpit checks that the CLI's own credential file exists (`~/.claude/.credentials.json`, `~/.gemini/oauth_creds.json`) and never opens it. If a run in this session ended "not signed in", it says so. Only a real run proves a sign-in works, so the text reads "signed in (credential file found; confirmed by the next run)".

`[ Sign In ]` opens a terminal running the CLI with API-key variables removed, so it signs in with the subscription.

**Desktop apps:**

| App | What the tab shows |
|---|---|
| SolidWorks | the running PID, or "not running" |
| DaVinci Resolve | installed, running PID, and the edition from the executable's version info. It says "Studio" only when the version info says so; otherwise it explains that FCPXML/EDL import works in both and the scripting bridge needs Studio. |
| Blender | the executable found on PATH or in Program Files |

**`[ + Add Custom MCP ]`.** The form takes a name, a command (npx, uvx, python or a full path) and arguments. The Cockpit:

1. Starts the server once for a health check (initialize + tools/list, 15 s limit).
2. Registers its tools as `ext_<server>_<tool>`.
3. Saves it to `cockpit.mcp.json` in the data folder.

Rows can be:

- enabled or disabled, which registers or unregisters the tools immediately;
- re-checked;
- removed.

The security rules are in [SECURITY.md](SECURITY.md#custom-mcp-servers).

## 5. First-run card and packaging

**When the card opens.** On the first launch, and on any launch where neither CLI is found.

**What it does:**

- checks `claude`, `gemini`, `python` and `git` (version, and the sign-in hint for the CLIs);
- offers the three themes (applied live);
- offers the hotkey (typed or picked from presets, validated by `HotkeyGesture`; Shift-only and Alt+Space are refused);
- offers **Run AI Cockpit when I sign in to Windows**, which writes or removes the per-user value `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AiCockpit`.

**Finish** saves everything and re-registers the hotkey. If the registry write fails, the card stays open with the reason. **Later** closes the card for this session only.

**`scripts/publish-release.ps1`:**

- publishes Wpf, CliHost and the SolidWorks worker (when its interop is found), self-contained win-x64 by default, plus the bridge with `-p:PublishAot=true`. It checks the MSVC linker first; `-NoAot` is the escape hatch;
- merges them into `dist\AiCockpit\`, refusing any file two projects publish with different bytes;
- adds `Create-Desktop-Shortcut.ps1`, `README-FIRST.txt`, `VERSION.txt` and `SHA256SUMS.txt`;
- runs `cockpit.exe doctor` from the package.

`Create-Desktop-Shortcut.ps1` creates a per-user desktop shortcut, and a Start-menu one with `-StartMenu`.
