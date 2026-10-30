# V18: connectors are plug-in pieces

Every program Mediator drives is now a **connector**: its own project and folder, built on its own, loaded at start-up if its folder is there. The core app (chat, routing between Claude Code and Antigravity, approvals, audit, files, the browser extension) knows none of them. Remove a connector's folder and that feature is gone; add one and it appears. This is the base for a connector center that downloads only the connectors you use.

## Layout

```
src/
  Mediator.Core/          the app's engine: bus, approvals, audit, jobs, routing, files, browser
    Connectors/           the contract (IMediatorConnector), ConnectorCatalog, ConnectorLoader
    Cad/                  the shared CAD layer every CAD connector uses (tool vocabulary, op catalog, script host)
  Connectors/
    SolidWorks/           everything SolidWorks
      Scripts/            solidworks.ps1 (run_script host)
      Contracts/          messages between the connector and its worker (part cards, assembly plans)
      Worker/             mediator-swx-worker (the STA COM host)
    FreeCAD/ AutoCAD/ Inventor/ NX/ Creo/ CATIA/    one class + its automation script each
    PartsFromCode/        build123d templates and sandboxed custom code → STEP
    CadFiles/             STL/OBJ/PLY/3MF, STEP, IGES, DXF/SVG without a CAD program
    Office/               Word, Excel, PowerPoint files; classic Outlook
    Resolve/ Blender/ Tutor/
    Connectors.targets    builds them all into <app>\connectors\<Name>
```

Installed, each connector is a folder next to the app:

```
Mediator.exe
mediator-cli.exe
connectors\
  SolidWorks\   Mediator.Connector.SolidWorks.dll, ...Contracts.dll, mediator-swx-worker.exe (+ its runtime)
  Office\       Mediator.Connector.Office.dll
  ...
```

## What a connector provides

`IMediatorConnector` (src/Mediator.Core/Connectors/MediatorConnector.cs):

| Member | Used for |
| --- | --- |
| `Id`, `DisplayName`, `Category`, `Summary` | Connectors tab, Software Library, `mediator-cli connectors` |
| `Start(context)` → `ConnectorInstance` | its tool providers for one runtime; `PrepareJobAsync` (SolidWorks shares the open document's folder and starts SolidWorks early); disposal (the worker) |
| `ChatTools`, `OwnsTool` | the tools a chat about it gets; a saved chat with one of its tools gets today's set |
| `Routes` | how free text reaches it (names, file types, the program in front), with an order: SolidWorks 10, other CAD 20–25, parts from code 31, CAD files 40, Office 50, Files 60 (built in), Resolve 70, Blender 80, Tutor 90 |
| `Recipes` | action tiles and CLI recipes (Assemble, Design parametric parts, Rough cut, Blender scene, Ask & learn) |
| `Programs`, `ProbeProgram` | the desktop programs it drives: Connectors tab rows, Software Library entries, installed/running checks |
| `ReadsAttachment`, `DescribeAttachment` | attached files it can read (Office as text, CAD files as a description) |
| `DoctorAsync`, `Commands` | `mediator-cli doctor` lines; CLI commands (the Tutor's `matrix`) |

A connector only depends on Mediator.Core. Connectors never reference each other: SolidWorks lists the code-to-STEP and CAD-file tools by name, and a job is granted only tools that are installed, so SolidWorks works without them (and they work without SolidWorks).

## Loading

`ConnectorLoader.LoadAll` reads `<app>\connectors\<Name>\Mediator.Connector.<Name>.dll` (in a source checkout, the connector projects' build output). Each loads in its own load context: its own dependencies stay private, while Mediator.Core and the framework come from the app, so every connector shares one copy of the contract. A folder that fails to load is skipped and reported (`mediator-cli doctor`, the log); it never stops the app. Connector scripts are patched in `%LOCALAPPDATA%\Mediator\connectors\<id>\` (V17's `cad-scripts` folder moved there).

## Adding a connector

1. `src/Connectors/<Name>/Mediator.Connector.<Name>.csproj` with `<MediatorConnector>true</MediatorConnector>` and `<EnableDynamicLoading>true</EnableDynamicLoading>` (copy any existing one).
2. A public class implementing `IMediatorConnector` with a parameterless constructor (a CAD program: derive from `CadAppConnector` and write one script).
3. Add it to `Mediator.sln` and to the test suite's `TestConnectors`. The hosts build and copy it automatically.

## Not moved (yet)

- **Browser / Chrome** stays in the core: Chrome jobs change how the CLI itself is launched (`--chrome`, the extension bridge started by the app), not just which tools exist.
- **DesktopAppLocator** (finds SolidWorks, Resolve, Blender in the registry and Program Files) stays shared infrastructure; connectors call it.
- **Task categories** (Cad, Office, Resolve, Blender, Tutor) stay a core enum: they choose which subscription a job runs on.
- A connector center (download, update, remove) is the next step: the folder layout and the loader are ready for it.
