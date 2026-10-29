# CAD connectors: the shared tool list

V16 splits the SolidWorks chat tools into two layers so other CAD programs can be added without rewriting them:

- **`src/Mediator.Core/Cad/CadAppToolProvider.cs`** holds everything that does not depend on the program: the tool schemas the model sees, approval cards, path checks (only inside the job's folders, only the program's file types, never replacing a file, the active document's folder checked before a change), the app lease (one job drives the program at a time) and error handling.
- **`ICadApp`** (`src/Mediator.Core/Cad/ICadApp.cs`) is what a program implements. SolidWorks is `SolidWorksCadApp`.

A connector picks a tool prefix (`swx`, later e.g. `inv`, `fus`, `rvt`, `acad`) and the capabilities it supports. Each capability becomes one tool named `<prefix>_<verb>`, with the same arguments for every program.

## The tools

| Capability | Tool (SolidWorks) | Risk | What it does |
| --- | --- | --- | --- |
| Session | `swx_session` | read | Program version, active document, every open document (path, type, unsaved). |
| Documents | `swx_document_ops` | write | `open`, `save`, `save_as` (new file only), `close` (with `save` or `discard`). |
| Export | `swx_export_neutral` | write | Neutral files into a folder. Formats come from the connector (`ICadApp.ExportFormats`), each marked for models, drawings or both. |
| Inspect | `swx_inspect_model` | read | Sections: `features`, `dimensions`, `equations`, `properties`, `mass`, `configurations`, `selection`, `components`, `mates`, `interference`. Lengths in mm, angles in degrees, mass in kg. |
| Edit | `swx_edit_model` | write | Ordered ops: `set_dimension`, `set_global`, `suppress`, `unsuppress`, `rename_feature`, `set_property`, `delete_property`, `set_material`, `activate_configuration`, `add_configuration`, `suppress_component`, `unsuppress_component`, `rebuild`. Unsaved by default; each op is a row the user can untick. |
| Drawing | `swx_create_drawing` | write | New drawing: standard views (first or third angle), isometric, model dimensions, sheet size, optional PDF. |
| Views | `swx_capture_views` | read | Pictures of the model (current, iso, front, ...) returned to the model as images. |

SolidWorks also keeps its own recipe tools, which are not part of the shared list: `swx_build_parametric_part`, `swx_set_part_globals`, `swx_part_cards`, `swx_build_assembly`, `swx_import_step`.

## Adding a CAD program

1. Write a class implementing `ICadApp`: prefix, display name, lease name, capabilities, native extensions, drawing extension, export formats, and one method per capability returning a JSON report. Throw `CadAppException`, `ArgumentException` or `InvalidOperationException` with a sentence the user can read; the provider turns it into a tool error.
2. Register it in `MediatorRuntime`: `Tools.Register(new Cad.CadAppToolProvider(new MyCadApp(...)))`.
3. Add its tool names to the CAD tool set in `JobSpecFactory` (or a set of its own) and its keywords / process name to `InferAppTools` so chats about it get the tools.
4. Add a Connectors card and a `DesktopAppLocator` entry if the program should be found and started.
5. Test the shared behaviour with the fake in `verify/tests/Mediator.Tests/CadAppTests.cs`; only the program-specific calls need the real program.

Paths reaching `ICadApp` are already checked. A capability the program lacks is simply left out of `Capabilities`, and its tool does not exist.

## How each program can be reached

| Program | How Mediator would talk to it | Notes |
| --- | --- | --- |
| SolidWorks | COM, out of process (`SldWorks.Application`) | Done. |
| Autodesk Inventor | COM, out of process (`Inventor.Application`) | Closest to SolidWorks: documents, parameters (`Parameters`), iProperties, mass (`MassProperties`), drawings (`DrawingDocument`), `SaveAs` translators. Every capability maps. |
| AutoCAD | COM, out of process (`AutoCAD.Application`) | 2D drawings: session, documents, export (DWG/DXF/PDF), inspect (layers, blocks, attributes), edit (attributes, layers). No mass or features. |
| Fusion | No out-of-process API: a small Fusion add-in (Python) that connects to Mediator over 127.0.0.1, like the Chrome extension does | Parameters (`userParameters`), timeline features, physical properties, export (STEP/IGES/STL/3MF/F3D), drawings are limited in the API. |
| Revit | No out-of-process API: a Revit add-in (.NET) using `ExternalEvent` to run requests on Revit's thread, connected over 127.0.0.1 | BIM, not mechanical: inspect = elements, parameters, schedules; edit = parameters, types; export = IFC/DWG/PDF. Mass, mates and drawings in the mechanical sense do not apply. |

For the add-in programs (Fusion, Revit), reuse the pairing pattern of `ChromeBridgeServer`: loopback only, a per-user secret, mutual proof before any call.
