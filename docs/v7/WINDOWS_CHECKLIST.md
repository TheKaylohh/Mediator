# V7 on Windows: first compile and manual UI check

The V7 overlay was written without a Windows machine. Its XAML passed the offline checker, but **nothing in `src/Cockpit.Wpf` has been compiled or run**. Follow these steps in order on a Windows 10 or 11 machine. Write down every deviation; the first run will probably need small fixes.

## 0. Build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -RunTests
```

Expected:

- the offline suite passes, with the Windows-only tests that Linux skips now running;
- the four publishes succeed with zero warnings (three if the SolidWorks interop is absent);
- `dist\AiCockpit\` contains `AiCockpit.exe`, `cockpit.exe` and `cockpit-mcp-bridge.exe` (native, about 5–10 MB);
- `cockpit.exe doctor` prints no FAIL line other than missing CLIs or apps.

**If the bridge fails with a linker error:** install "Desktop development with C++", or rerun with `-NoAot` and note it.

**If a XAML error appears at run time:** the exception names the file and line. The most likely culprits are listed in the last section.

## 1. First run

1. Delete `%LOCALAPPDATA%\AiCockpit\settings.json` if it exists, then start `dist\AiCockpit\AiCockpit.exe`.
2. **Expected:** the tray icon appears and the panel opens with **SET UP AI COCKPIT**. Claude Code, Gemini CLI, Python and Git each show a ✓ with their version, or ⚠ with an install hint.
3. Click **Clean Light**, then **Pro Dark**, then **Holo HUD**. The whole panel recolours instantly each time, with no restart.
4. Type `Shift+A` in the hotkey field. **Expected:** an amber error, and Finish disabled. Click the `Ctrl+Alt+J` preset; the error clears.
5. Tick **Run AI Cockpit when I sign in to Windows** and click **Finish**.
   - **Expected:** the card closes.
   - `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v AiCockpit` shows the quoted exe path.
   - Ctrl+Alt+J now toggles the panel, and Ctrl+Shift+Space no longer does.
6. Quit from the tray and start again. **Expected:** no card (unless neither CLI is installed), and the theme you chose.

## 2. Themes and the orb

| Step | Expected |
|---|---|
| Click the palette button in the header three times | Holo HUD → Pro Dark → Clean Light → Holo HUD. Scanlines and corner brackets appear only in Holo HUD. |
| Idle | The orb's three rings turn slowly (outer clockwise, middle counter-clockwise, inner clockwise), the core breathes, and the caption reads STANDBY. Task Manager shows near-zero CPU for AiCockpit while it is idle and visible. |
| Start any job (for example "organize" on a test folder) | The rings speed up (about 1.5 s per turn), the caption reads ACTIVE · 1 JOB, and a light sweep runs back and forth along the bottom edge of the prompt. |
| A job asks for approval | The orb turns amber, slows down and pulses outwards; the caption reads AWAITING APPROVAL; the approval card shows. |
| Make a job fail (for example sign out of the CLI, or point it at a missing folder) | Crimson, no motion, steady glow, caption FAULT. Clicking the orb clears it. A job you kill yourself is not a fault: the orb returns to STANDBY. |
| Settings → Accessibility → Visual effects → Animation effects off | The orb is static but still changes colour per state. |

## 3. Attachments

| Step | Expected |
|---|---|
| Open the panel, click the **pin**, then drag a file from Explorer onto it | While dragging: a dimmed panel with a dashed glowing frame and **DROP ARTIFACTS TO ATTACH**. On drop: a chip with a paperclip and an ×. The status line reads "Attached 1 item(s), read-only." |
| Drag a folder | A folder chip. |
| Drag `%USERPROFILE%`, `%USERPROFILE%\.ssh` or `C:\` | No chip. The status line explains why. |
| Drag a shortcut (`.lnk`) or a junction | A `.lnk` is attached as a file (it is not followed). A junction is refused as a link. |
| Win+Shift+S a region, then Ctrl+V in the panel | An image chip named `pasted-<date>-<time>.png`. Paste a transparent PNG copied from a browser; the snapshot under `%LOCALAPPDATA%\AiCockpit\attachments\` keeps its transparency. |
| Copy text and press Ctrl+V in the prompt | The text pastes normally. |
| Copy files in Explorer, then Ctrl+V in the panel | Chips for each file. |
| Click **Attach** | A file picker opens and the panel stays open behind it. Picked files become chips. |
| Run "Claude Code in a terminal" with an image chip and "describe the attached image" | Claude calls `cockpit_attachments` and `cockpit_read_attachment` and describes the picture. The chips disappear: the job took them. |
| While that terminal job runs, drop another file | The chip shows a small link mark ("also shared with …"). Typing "read the new attachment" in the terminal works. |
| Ask the model to write into an attached folder | It is refused with `[path:read_only]`, and the Activity tab shows it. |

## 4. Connectors tab

| Step | Expected |
|---|---|
| Open the tab | Both CLIs show versions. Sign-in reads "signed in (credential file found; confirmed by the next run)" or "not signed in". Claude shows the usage-limit time while it is cooling down. |
| **Sign In** on a CLI | A console opens with the CLI. `echo %ANTHROPIC_API_KEY%` in it prints nothing, even if the variable is set system-wide. |
| SolidWorks running / closed, then refresh | "running (PID n)" / "not running". |
| Resolve | Installed or not, plus the edition line. Note what the Studio check says on your edition. |
| **+ Add Custom MCP** with name `everything`, command `npx`, arguments `-y @modelcontextprotocol/server-everything` | About 5–15 s, then "Added 'everything': n tool(s)". The row shows its tools. `%LOCALAPPDATA%\AiCockpit\cockpit.mcp.json` has no `env`. |
| A job calls `ext_everything_echo` | A red Destructive approval card naming the server and command. Deny: nothing runs. Approve: the result comes back. |
| Untick the row | New calls fail. Remove: the row disappears. |

## 5. If the first compile or run fails

These are the WPF details most likely to need a fix, because they could not be checked without the real XAML compiler:

- `BeginStoryboard Name="SweepStoryboard"` inside a Style DataTrigger (MainWindow, prompt sweep).
- `<Visibility x:Key="HudDecorVisibility">` and `<sys:Double>` in the theme dictionaries.
- `DynamicResource` on `DropShadowEffect.Opacity` and `Color`.
- `MouseBinding` on the orb (`AcknowledgeFaultCommand`).
- `x:Name` on the `RotateTransform` and `ScaleTransform` elements used by `HudOrbControl.xaml.cs`.
- Implicit usings: WPF projects drop `System.IO`, so the files that need it add `using System.IO;` explicitly.
