# AI Cockpit V9: Windows release candidate

V9 closes the five gaps found in the V8 review. Every C# file of the overlay is now compiled on every test run. The Windows build no longer depends on NuGet. The hotkey survives conflicts and shows in the tray. Follow-up prompts continue the previous Claude session. And V8 and V9 are committed cleanly.

> **Status, plainly.** Everything below was built and tested on Linux (Ubuntu 24.04, .NET SDK 8.0.132).
>
> **What's new on Linux:** every overlay `.cs` file now goes through csc, against a compile-only WPF stand-in.
>
> **Still Windows-only:**
> - **The XAML markup compiler.** Its input is checked by XamlChecker.
> - **Linking against the real WPF assemblies.** The stand-in follows the .NET 8 API; it is not the real thing.
> - **Real Win32 behaviour:** hotkeys, monitors, the registry and the SolidWorks COM server.
>
> The `windows-release-package` CI job and the checklist below are the remaining gate.

## 1. Every `src/Cockpit.Wpf/*.cs` file is compiled in verify builds

`verify/stubs/WpfCompileStub` is a compile-only stand-in for the WPF and Windows Forms surface the overlay uses. In WPF that covers windows, application, resources, dependency properties, animations, imaging, input, interop, markup, `System.IO.Packaging` and the WPF file dialog. In Windows Forms and System.Drawing it covers the tray icon.

How it stays faithful to the real assemblies:

- **Same API shape**: the same namespaces, base classes, member names and signatures as .NET 8.
- **Nullability as in .NET 8**: WPF members are nullable-oblivious; Windows Forms and System.Drawing are annotated.
- **WPF's implicit usings**: no `System.IO` or `System.Net.Http`.

`XamlPartials.targets` is an MSBuild inline task that does what WPF's markup compiler does for code-behind. It generates, for each `x:Class`:

- the base class from the root element, and `IComponentConnector`;
- `InitializeComponent()` and `Connect()`;
- `Main()` for the application;
- one typed field per `x:Name`/`Name` in the root name scope. Templates and styles are excluded, as in WPF.

In verify mode `Cockpit.Wpf.csproj` compiles all 10 `.cs` files plus the generated partials into `AiCockpit.dll`, with warnings as errors. `dotnet build Cockpit.sln -p:CockpitVerify=true -p:TreatWarningsAsErrors=true` gives 0 warnings and 0 errors. The compile pass found no errors in the V8 code.

Tests:

- **Logic against the stub:** the value converters run against it. So does ThemeManager: its absolute pack URIs, and the compiled → loose file → keep-current fallback, including a corrupt loose file.
- **Mutation build:** a mutated copy must fail to compile. The mutations are a dropped `using System.IO`, an `x:Name` renamed only in the XAML, and a member that `NotifyIcon` does not have. All three were caught before the test was written, too.

## 2. Offline builds: `CockpitUseShims`, decoupled from `CockpitVerify`

- **`CockpitVerify`** still means "build everything as `net8.0` against the stand-ins". **`CockpitUseShims`** now decides where SQLite and xxHash64 come from:

  | | SQLite and xxHash64 from | Restore sources | Native AOT and AOT/trim analyzers |
  | --- | --- | --- | --- |
  | `false` (default for ship builds) | NuGet | NuGet | on |
  | `true` (default when `CockpitVerify=true`) | `SqliteShim` / `HashingShim` | the empty `verify/local-feed` only | off (they download ILCompiler and ILLink) |

- **The SQLite shim on Windows** P/Invokes `System32\winsqlite3.dll`, which ships with Windows 10 and 11. V9 hardens that load: Windows is searched in System32, then the shim's own folder, and nowhere else, so a `sqlite3.dll` planted in a PATH folder is never loaded. The shim assemblies are renamed `*.CockpitShim.dll`, since they can now ship.
- **`publish-release.ps1`** restores every project from NuGet first. If that fails (no network, or a proxy blocking nuget.org), it falls back to `-p:CockpitUseShims=true`. The fallback build is framework-dependent, because runtime packs are NuGet downloads; the .NET 8 Desktop Runtime installed with the SDK runs it. The bridge is a normal .NET app in that build.
  - `-UseShims` builds offline from the start; `-NoShimFallback` fails instead.
  - A missing MSVC toolchain no longer stops the script: it warns and drops Native AOT.
  - `VERSION.txt` records which dependencies the package used.
- **Tests:**
  - **MSBuild evaluation:** the switch is checked with `dotnet msbuild -getProperty/-getItem`, not as text.
  - **Real offline build:** the console host and bridge are built in ship mode (`CockpitVerify=false`, `CockpitUseShims=true`) from an empty feed. The restore graph contains 0 packages and the builds have 0 warnings. `cockpit doctor --json` from that build opens real `audit.db`/`journal.db`/`operations.db` through the shim, and the bridge self-test passes.
  - **CI:** the Windows ship job also builds the real WPF overlay and every executable with `CockpitUseShims=true`.

## 3. Hotkeys: parser, live rebind with fallback, tray text

The review said the hotkey was hard-coded. That was not quite right: V7 already registered the hotkey parsed from settings and re-registered it when the setup card changed it. But three gaps were real, and V9 fixes them:

- **A taken combination left no hotkey at all.** `HotkeyBinding` (Cockpit.UI, tested off Windows) now handles this:
  - **At start:** the configured hotkey, else Ctrl+Shift+Space, else none (the tray icon still works). The user's setting is not overwritten.
  - **On rebind:** the old hotkey is released, the new one registered at once. If the new one is taken (ERROR_HOTKEY_ALREADY_REGISTERED), the previous one is registered again and stays active.
  - **In the setup card:** it rebinds before saving. A conflict keeps the card open with the reason, and nothing is saved.
- **The tray menu always said Ctrl+Shift+Space.** It now shows the active hotkey, as `Open AI Cockpit  (Ctrl+Alt+J)`, and the tooltip follows it.
- **Keys:** `HotkeyParser.TryParse(gesture, out modifiers, out virtualKey, out normalizedDisplay)` sits over the one existing parser, and now also accepts Tab, Enter and `~` (OemTilde). Tab and Enter need two modifiers. Alt+Tab, Win+Tab and Ctrl+Alt+Tab are refused, since they belong to the task switcher.

`HotkeyManager` is now only the Win32 registrar and exposes `Start` and `Rebind(string)`.

## 4. Multi-turn sessions (`claude -p --resume`)

**When a session can be continued.** A headless Claude run that produced a result records its `session_id` from the stream (`RunningJob.SessionId`), and becomes the runtime's `LastContinuableSession`. Interactive, killed, rate-limited, sign-in-failed and Gemini runs are not offered. Gemini sessions are not continued because the resume flag is not verified for the tested Gemini CLI version.

**The chip.** The overlay shows **↩ Continue session (5f1c2d3e)**, selected by default, so pressing Enter sends the prompt as a follow-up.

- Clicking the chip deselects it: start fresh this time.
- **×** removes it.
- So does opening the panel on another folder, or naming another folder in the prompt; that last case starts a fresh job there.
- A `/` command is never treated as a follow-up.

**What the follow-up job gets.** `JobSpecFactory.Continue` builds it:

- the same kind, tools, roots, folder and limits;
- the user's text as the prompt;
- the previous attachments plus any new ones;
- `ContinueSessionId` set.

The runtime passes `--resume <id>` to `claude -p`, and to the interactive terminal when "terminal" is on.

**Pinned to Claude Code.** A continued job never switches subscriptions. If Claude is at its limit, the follow-up is refused with "a Claude Code session can only continue on Claude Code; clear the chip to start fresh".

**Safety.** The session id came from a previous run's output and goes onto a command line, and through `claude.cmd` also into cmd.exe. So it must be a plain token: `[A-Za-z0-9][A-Za-z0-9_-]{7,127}`, never starting with `-`. The runtime and the launch builder both check it.

**Why the old workspace is not reused.** Claude Code stores sessions per working folder, and Claude jobs already run in the user's folder. So `--resume` works with a new job workspace. That workspace has a new grant and token; the old job's token was revoked when it ended and is never revived. The tools keep the same names (`mcp__cockpit__…`), so the resumed conversation's history still matches.

Tests:

- **Runtime:** a real run through the fake CLI launcher is offered. The follow-up launches with `--resume <id>` in the same folder, with a new workspace and grant. The interactive script resumes too. Nine unsafe ids, `--dangerously-skip-permissions` as an id, Gemini, and a limited Claude are all refused with nothing launched. A killed run is not offered.
- **Overlay**, on the real runtime:
  - the chip appears selected;
  - Enter continues; click deselects;
  - **×** and a folder switch remove it;
  - another folder named in the prompt starts fresh;
  - the chip returns with the follow-up's session.

The full suite also surfaced a race in my own new code: `Completion` completed before the continuation was recorded. `RunningJob.TryFinish` now records it first.

## 5. Clean git history

- **V8 commit:** V8 is its own commit (`a033d7f`) on top of V7 (`fb0179c`).
- **V9 commit:** V9 is a second commit, and `git status --porcelain` is empty.
- **No open TODOs:** `git grep -n TODO src/` finds nothing.
- **Clean zip:** `cockpit-v9.zip` is made from a clean tree, with no `bin/`, `obj/` or `.vs/`.

## Verification

| Check | Result |
| --- | --- |
| `bash verify/run-tests.sh` (offline) | **111 passed, 0 failed**, 8 skipped: 6 live tests need `COCKPIT_LIVE_PYTHON`, 2 need Windows ([transcript](test-run-linux-offline.txt)) |
| `COCKPIT_LIVE_PYTHON=… bash verify/run-tests.sh` | **117 passed, 0 failed**, 2 skipped (Windows-only NTFS and sharing-violation tests) ([transcript](test-run-linux-live.txt)) |
| Warnings | 0 in all six builds of the run, including `src/Cockpit.Wpf` (all 10 `.cs` files) |
| `dotnet build Cockpit.sln -p:CockpitVerify=true -p:TreatWarningsAsErrors=true` | 0 warnings, 0 errors |
| V8 baseline | all 110 V8 tests still pass. Two assertion texts changed on purpose: Ctrl+Enter is now refused with "add a second modifier" instead of "not supported" (Enter became a supported key), and the release-script test finds the project table in its new place |

## Windows acceptance checklist (first run)

1. **Online publish.** Run `powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -RunTests` online. Expected: NuGet build, Native AOT bridge, `VERSION.txt` saying NuGet.
2. **Offline publish.** Turn Wi-Fi off, clear `src\*\obj` and `verify\*\obj`, and run the script again. Expected: "NuGet restore failed … Falling back to the offline shims". The package is written, and `VERSION.txt` says "offline shims". `launch.bat` starts it, and settings, audit and journal work: SQLite comes from `winsqlite3.dll`.
3. **Hotkey conflict.** In the setup card, pick a hotkey another program owns. The card should say "already used by another program" and the old hotkey should keep working. Pick a free one: it should work immediately, and the tray menu should show it.
4. **Session continuation.** In a folder with parts, run "Assemble". When it finishes, the ↩ chip appears. Type a follow-up and press Enter. Claude should answer with the previous turn in mind, and the Jobs list should show "Follow-up: …". Open the panel on another folder: the chip should be gone.
5. **V8 checklist.** Run steps 2–8 of the [V8 checklist](../v8/RELEASE_NOTES.md#windows-acceptance-checklist-first-run): themes, mixed DPI, Connectors discovery, no-SolidWorks message, SolidWorks PC, shortcuts.
