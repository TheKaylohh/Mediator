# V16: code review and SolidWorks by chat

## What was verified

- Linux, offline verify builds of every project with `TreatWarningsAsErrors=true`: 0 warnings.
- `bash verify/run-tests.sh`: 151 passed, 0 failed, 11 skipped (162 total). The skips are the same environment-dependent ones as before (Windows-only, bubblewrap, live Python).
- The SolidWorks COM calls added in V16 have **not** run against SolidWorks (no Windows here). They are late-bound like the rest of `SwxParametric`, and each section and operation is tried on its own, so a call SolidWorks refuses is reported and the rest still run. Check them with the list below.

## Code review: fixes

| Area | Problem | Fix |
| --- | --- | --- |
| Tests | 2 tests failed on V15 as pushed: `release scripts V8` still looked for `$PSScriptRoot` after the script moved to `$scriptDir` (the PowerShell 5.1 fix), and `assembly name V10` ran a Windows drive path through Linux path rules. | Test accepts both; a typed drive path is kept as typed off Windows. |
| SolidWorks | The lease contract says one job drives SolidWorks at a time, but the parametric, session, document and export tools held no lease, so two jobs could interleave edits. | Every SolidWorks tool holds the `solidworks` lease. |
| SolidWorks | `swx_set_part_globals` and `swx_document_ops save` without a path changed and saved the active document even when it was outside the folders the job may change. | The active document's file is checked for write access first; a document outside is refused with a clear message. |
| SolidWorks | Every call switched off the user's "Input dimension value" preference and never switched it back. | Saved and restored with the other prompt settings. |
| SolidWorks | `swx_export_neutral` overwrote an existing file of the same name. | Existing files are skipped and reported (the app never replaces files). |
| CAD files | The 500 MB mesh limit applied only when loading by path; `cad_convert_mesh` loads through a stream and could read any size into memory. | The limit applies to every load. |
| Office | Reading a Word file copied the whole text so far on every blank paragraph (slow on long documents). | Checks the last two characters instead. |
| Dead code | Unused members: `DedicatedStaQueue.IsWedged`, `StdioJsonLines.Over`, `MonitorPlacement.PlaceBottomRight`, `PathAuthorizer.IsAllowed` / `WriteNewBytes`, `OperationStore.FindLatestAsync`, `PanelPlacement.MaxWidthDip`, `PInvokeRot.MK_E_UNAVAILABLE`; 17 unnecessary `using` lines; a redundant assignment in the bridge. | Removed. Win32 struct fields, `IsolationConfig.RenderDotWslConfig` and `IsolationRequirement` stay (interop layout; described in SPEC/ISOLATION docs). |

Found with an analyzer pass (IDE0051/0052/0059/0060/0005 as warnings) plus a search for public members nothing references, then a manual read of the V12-V15 code (Office, Outlook, CAD files, Chrome bridge, usage meter) and the SolidWorks layer.

## SolidWorks by chat

New tools, available in every CAD job and in chats routed to SolidWorks:

- **`swx_inspect_model`**: feature tree (type, suppressed, rebuild errors), dimensions with their names for editing, global variables and equations, custom properties, mass / volume / area / centre of mass / material, configurations, the user's selection (faces with area and diameter, edges with length, features, components), and for assemblies the BOM, mates with errors and interference detection.
- **`swx_edit_model`**: set dimensions (mm, in or degrees) and globals, suppress / unsuppress / rename features, set or delete custom properties, set the material, activate or add configurations, suppress / unsuppress components. Changes stay open and unsaved unless `save: true`; the approval card lists each operation and unticked ones are not run.
- **`swx_create_drawing`**: a new drawing with the three standard views (first or third angle), an isometric view, the model's dimensions, A4-A0 / A-E sheets, and an optional PDF.
- **`swx_capture_views`**: pictures of the model (what the user sees, or iso / front / top ...) returned to the AI as images.
- **`swx_document_ops`** gains `save_as`; **`swx_export_neutral`** gains 3MF, DXF and DWG (drawings) and PNG.

The tools come from a CAD-neutral layer, so Inventor, Fusion, Revit or AutoCAD can be added later with the same tools under their own prefix. See [CAD_CONNECTORS.md](CAD_CONNECTORS.md).

## Windows checklist for V16

Run each with a part, an assembly and a drawing open in SolidWorks:

1. "what's open in SolidWorks?" → `swx_session` lists them.
2. "what does this part weigh and what's it made of?" → `swx_inspect_model` mass shows kg and the material.
3. "list the dimensions of the part" → names like `D1@Sketch1` with mm / deg values; angles in degrees.
4. Select a hole face, ask "how big is the selected hole?" → selection shows a cylinder with its diameter.
5. "make D1@Boss-Extrude1 25 mm" → the part rebuilds, stays unsaved; Ctrl+Z undoes it.
6. "set the material to 6061 Alloy" and "set the material to Unobtainium" → the first works, the second is reported as not found.
7. "suppress Fillet1", "set PartNo to BR-100" → done; `swx_inspect_model` shows the change.
8. Assembly: "list the BOM and check for interferences" → quantities per part; interferences with volumes (or none).
9. "make an A3 drawing of this part with a PDF" → drawing saved, views placed, dimensions inserted, PDF written. Check that no Model View panel or dialog blocks the call.
10. "show me the part from the front" → the AI receives the picture.
11. "save this as bracket-v2" → a new file; the old one is unchanged.
12. With two jobs at once, the second job's SolidWorks call is refused (naming the job that holds SolidWorks) instead of interleaving.
13. After any call, Tools > Options > "Input dimension value" is as it was.

## V16.1: fixes from the Windows test battery

| Test | What happened | Cause | Fix |
| --- | --- | --- | --- |
| SWX-04 to 11, 14 | "No swx_inspect_model tool"; save_as and PNG missing | The tool descriptions the model quoted are V15's. An older Mediator was still in the tray: starting V16 only showed "already running" and quit, so every chat ran on V15. Chats saved by V15 also kept their V15 tool list forever. | A new build now offers to close an older running one and start itself; the tray shows the version; `mediator-cli doctor` prints the version, checks all 12 swx_ tools are registered and warns if the tray runs another build. Chats saved by older versions (reopened or continued) get today's SolidWorks and Office tools. |
| SWX-02, 04, 14 | Active-document requests did not reach SolidWorks; a turn ran on Haiku with no CAD tools | Only prompts containing "SolidWorks"/.SLDPRT (or SolidWorks in front) were routed to SolidWorks. | While SolidWorks is running, a request about a part, assembly, drawing, feature, dimension, material, mass, BOM, selection, export, save... gets the whole SolidWorks toolset. |
| SWX-05 to 07 | Edits refused for the open part | A chat with no folder could read the open document but not change it. | A SolidWorks job gets the open document's folder (like a folder selected in File Explorer) and is told which file is open. Every change still shows its approval card. |
| SWX-08, 14 | `Win32 error 32` | Path checks opened the file allowing only other readers; SolidWorks holds open documents with write access. | Path checks open for attributes only; reads also share with writers. |
| SWX-12 | PNG export refused | V15 (see first row). | V16's export has PNG. |
| SWX-13 | PowerShell windows running Claude; jobs missing from the chat | The chat runs one background job at a time, so the second job ran in terminal mode (its own PowerShell window, by design) and terminal jobs had no chat card. | Terminal jobs get a chat card saying where they run and how to stop them, updated when they end. The lease guard itself works: the second job's SolidWorks call is refused while the first holds SolidWorks. |

### Re-running the battery

1. Quit every Mediator from the tray (or start the new build and answer Yes to "close it and start this one").
2. Rebuild: `powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1`, then run `dist\Mediator\launch.bat`.
3. `dist\Mediator\mediator-cli.exe doctor`: "Mediator V16.1 (build …)" and "SolidWorks tools ok, 12 swx_ tools registered".
4. Start a **new** chat (not one from before) with SolidWorks open, and run the tests.
