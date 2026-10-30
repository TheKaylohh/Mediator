# V17: CAD co-pilot expansion

Seven CAD programs share one tool vocabulary. Each tool is `<prefix>_<verb>` with the same arguments everywhere, so the model learns one language:

| Prefix | Program | How Mediator talks to it | Mode |
| --- | --- | --- | --- |
| `swx` | SolidWorks | COM, inside Mediator | live session |
| `fc` | FreeCAD | Python script run by FreeCADCmd | on files (headless) |
| `acad` | AutoCAD | Windows PowerShell 5.1 → AutoCAD COM | live session |
| `inv` | Autodesk Inventor | Windows PowerShell 5.1 → Inventor COM | live session |
| `nx` | Siemens NX | NX Open Python journal run by run_journal.exe | on files (batch) |
| `creo` | PTC Creo | Windows PowerShell 5.1 → Creo VB API (pfcls) | live session |
| `catia` | CATIA V5 | Windows PowerShell 5.1 → CATIA COM | live session |

## Tools per program

| Tool | swx | fc | acad | inv | nx | creo | catia |
| --- | --- | --- | --- | --- | --- | --- | --- |
| session | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| document_ops (open, save, save_as, close) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ (no save_as) | ✔ |
| export_neutral | STEP IGES Parasolid STL 3MF PDF DXF DWG PNG | STEP IGES BREP STL OBJ 3MF | PDF DXF PNG DWG | STEP IGES STL SAT PDF DWG DXF | STEP Parasolid | script | STEP IGES STL 3DXML PDF DXF |
| inspect_model | ✔ | ✔ | ✔ layers, blocks, entities, attributes | ✔ | ✔ expressions, features, mass | ✔ parameters, features, mass | ✔ parameters, BOM, mass |
| edit_model | ✔ 17 ops | ✔ 11 ops | ✔ layers, transforms, attributes, commands | ✔ 14 ops | ✔ expressions, suppress, attributes | ✔ parameters | ✔ parameters, properties, (de)activate |
| capture_views | ✔ | — (headless) | ✔ (plotted) | ✔ | — (batch) | ✔ current | ✔ |
| create_drawing | ✔ | script | — | ✔ | script | script | script |
| sketch (geometry, constraints, dimensions) | ✔ | ✔ | ✔ 2D drafting | ✔ | script | script | script |
| model_features | ✔ extrude…hole, patterns, mirror, delete | ✔ PartDesign incl. Hole, patterns, booleans | — | ✔ extrude, cut, revolve, fillet, chamfer, shell, circular pattern | script | script | script |
| assembly_ops | ✔ insert, mate, fix, move, replace, delete, copies | script | — | ✔ insert, constraints, ground, move, replace | script | script | script |
| annotate_drawing | ✔ projected/section/detail views, dims, notes, BOM, balloons | script | ✔ dimensions, notes, leaders, tables, blocks, hatches, layers | script | script | script | script |
| simulate | script (SolidWorks Simulation API) | ✔ CalculiX static: material, fixed, force, pressure, mesh, solve | — | script | script | script | script |
| run_script | ✔ PowerShell COM | ✔ Python | ✔ PowerShell COM | ✔ PowerShell COM | ✔ NX Open Python | ✔ PowerShell VB API | ✔ PowerShell COM |

"script" means the program's `run_script` tool: the model writes code in the program's own API (the tool's description says what is in scope), the user reads and approves it every time (Destructive: never auto-approved), and the output or error comes back. That is how anything without a structured tool gets done: CAM toolpaths, Simulation in SolidWorks/Inventor/NX/Creo/CATIA, GD&T frames, sheet metal, weldments, motion studies, iLogic, family tables.

## Beyond the draft spec

- **Standard holes** (`hole`): ISO clearance (ISO 273 medium), tap drill and ISO 4762 counterbore sizes M2–M24.
- **Live document safety**: every change is listed in the approval card (untick any), runs unsaved by default (undo reverts it), and only touches files in folders shared with the chat; the open document's folder is shared automatically (V16.1).
- **Program in front**: the chat gets that program's tools when it is the active window (acad, inventor, ugraf, xtop, cnext, freecad, sldworks).
- **Patching without a rebuild**: every connector script can be replaced by a copy in `%LOCALAPPDATA%\Mediator\cad-scripts\` (autocad.ps1, inventor.ps1, catia.ps1, creo.ps1, solidworks.ps1, freecad.py, nx.py).
- **Hung programs**: each call has a timeout; the script process tree is killed and the model is told to check for a dialog.

## Spec tools → Mediator tools

| Spec tool (SolidWorks names; the other programs map the same way) | Mediator |
| --- | --- |
| swx_session_manager | swx_session |
| swx_document_ops | swx_document_ops |
| swx_sketch_create_2d, swx_sketch_constrain | swx_sketch |
| swx_feature_boss_cut, _sweep_loft, _fillet_chamfer, _pattern_mirror, _hole_wizard | swx_model_features |
| swx_surfacing_tools | swx_model_features (surface extrude/loft/sweep, knit, thicken) + run_script |
| swx_sheet_metal_ops, swx_weldment_structural | swx_run_script |
| swx_inspect_model_deep, swx_edit_model_parametric | swx_inspect_model, swx_edit_model |
| swx_assembly_component_ops, _mate_builder | swx_assembly_ops (+ swx_build_assembly for new assemblies) |
| swx_assembly_motion_kinematics | swx_run_script |
| swx_assembly_interference_bom | swx_inspect_model (components, interference) |
| swx_drawing_generate_views, _annotate_gdt, _table_bom_export | swx_create_drawing, swx_annotate_drawing (GD&T frames via run_script), swx_export_neutral |
| swx_simulation_fea_setup, swx_cam_toolpath_gen | swx_run_script |
| swx_capture_viewport_multimodal | swx_capture_views |

## What was verified, and what was not

- Verified here (Linux): the shared provider, operation catalog, approvals, path checks and the script host end to end (with a stand-in Python program); every connector script compiles (Python) or parses (PowerShell 7 parser) and handles every verb and operation its tools call; detection logic; routing. 161 tests pass.
- **Not run against any real CAD program.** None runs on Linux, and downloading FreeCAD to test it here was not permitted. Each call that fails reports the program's own error message, and the script can be patched without a rebuild.

## Windows checklist

For each program you have: open a document, then in a new chat ask, in order:
1. "what's open in <program>?" (session)
2. "list the parameters / features of the open model" (inspect_model)
3. "change <parameter> to 25 mm" (edit_model), then "show me" (capture_views)
4. "sketch a 60 × 40 rectangle on the top plane and extrude it 10 mm" (sketch + model_features; SolidWorks, FreeCAD, Inventor)
5. "export it as STEP to <folder>" (export_neutral)
6. "use a script to list every body's volume" (run_script)
AutoCAD: "draw a 100 × 50 rectangle with a 10 mm hole in the middle and dimension it", "add a table with ...". FreeCAD: "fix the bottom face, put 1000 N on the top, solve".
