# Mediator features (V16)

Everything Mediator can do, what each feature is for, and prompts it handles. Type a prompt in the overlay
(Ctrl+Shift+Space) or Full view. Mediator picks the tools from the prompt, or you can force them with a prefix
(`/cad`, `/office`…). Jobs run on your subscriptions (Claude Pro through Claude Code, Google AI Pro through the
Antigravity CLI) and never on pay-per-token API keys.

## Prefixes

| Prefix | What it forces |
|---|---|
| `/claude …`, `/agy …` (also `/gemini`, `/antigravity`) | Run on that CLI |
| `/ask …` | A plain answer: no tools, no files |
| `/search …` | A web search with sources: no local files |
| `/cad …` | SolidWorks and CAD tools |
| `/office …`, `/outlook …` | Word, Excel, PowerPoint files and classic Outlook |
| `/files …` | Organize files in a folder |
| `/code …` | A coding job in a folder |
| `/tutor …` | Tutoring from PDFs, with exact linear-algebra checks |
| `/resolve …` | DaVinci Resolve rough cut |
| `/blender …` | Blender scene and render |
| `/chrome …`, `/browser …` | Work in your Chrome tabs |

## 1. Chat, questions and search
- **For:** general questions, explanations and writing. Current information comes from a web search that names its sources.
- **Tools:** none. Search jobs never see your files.
- **Prompts:** "explain what a fillet is in CAD", "/search latest SolidWorks 2026 release notes", "the links in my PowerPoint go blank on a shared drive, how do I fix it?"
- **Also:**
  - Follow-ups continue the same session. A Claude session resumes in full; switching CLI carries the recent turns over.
  - Chats are saved and can be resumed from Full view → Chats.

## 2. SolidWorks (parts, assemblies, drawings, any open model)
- **For:** native SolidWorks parts with design intent. Every size is a global variable linked to the sketches.
- **Assemblies:** built from the parts in a folder. Mediator proposes the mates and you approve them.
- **Any model, by chat (V16):** read the open part or assembly (feature tree, dimensions, equations, custom properties, mass and material, configurations, what you selected, BOM, mates, interference), change it (dimensions, globals, suppress/unsuppress, rename, properties, material, configurations, components), make a drawing (3 views + iso + dimensions, PDF), and let the AI look at it (pictures). Changes stay unsaved for your review unless you say save; each one is listed in the approval card and can be unticked.
- **Files:** open, save, save as, close; export STEP/IGES/Parasolid/STL/3MF (PDF/DXF/DWG for drawings, PNG for both). Nothing is ever replaced.
- **Tools:** `swx_session`, `swx_inspect_model`, `swx_edit_model`, `swx_create_drawing`, `swx_capture_views`, `swx_document_ops`, `swx_export_neutral`, `swx_build_parametric_part`, `swx_set_part_globals`, `swx_part_cards`, `swx_build_assembly`, `swx_import_step`.
- **Prompts:**
  - "make a 120 × 60 × 8 mm plate with four M6 holes 10 mm from the edges"
  - "change the base to 150 × 80"
  - "what does the open part weigh, and what material is it?"
  - "make the selected hole 8 mm and set the material to 6061 Alloy"
  - "suppress the fillets and show me the part"
  - "set PartNo to BR-100 and Description to Mounting bracket"
  - "make an A3 drawing of bracket.SLDPRT with dimensions and a PDF"
  - "check this assembly for interferences and list the BOM"
  - "assemble the parts in C:\work\bracket and mate the bolts to the holes"
  - "import housing.step into SolidWorks and export it as STL"
- **Note:** SolidWorks starts only for SolidWorks work. Inspecting an STL or asking a CAD question doesn't start it. One job drives SolidWorks at a time.

## 3. Parametric parts from code (build123d → STEP)
- **For:** standard parts (flanges, bushings, springs, bolt circles…) or custom geometry generated as STEP, checked against size limits, then opened in SolidWorks.
- **Tools:** `cad_generate_parametric_step`, `cad_generate_custom_step`.
- **Prompts:** "design a flange, 80 mm outer diameter, 6 bolts on a 60 mm circle", "generate part: a bushing 20/12 mm, 15 long".

## 4. CAD files, no CAD program needed (new in V15)
- **For:** checking a file before printing, importing or converting it.
- **Meshes:** STL, OBJ, PLY and 3MF.
  - Reports size in mm, triangle count, area, volume, closed or open edges, separate shells, inward-pointing faces and likely units.
  - Converts between these formats, scales, changes units (inch to mm) and centres the part. Always into a new file.
- **STEP and IGES:** reports which program wrote the file, its units, part names, solids or surfaces and approximate size.
- **DXF and SVG:** reports outlines, circles and size.
- **Tools:** `cad_inspect_file`, `cad_convert_mesh`. Converting STEP to STL goes through SolidWorks (`swx_import_step` + `swx_export_neutral`).
- **Prompts:**
  - "how big is bracket.stl and is it watertight?"
  - "this STL was modelled in inches, convert it to mm and centre it"
  - "convert the obj file to 3mf"
  - "what units is this step file in and who exported it?"

## 5. Microsoft Office (new in V15)
- **Files:** Word, Excel and PowerPoint (`.docx`, `.xlsx`, `.pptx`), read and written without Office installed.
  - Word: headings, paragraphs, lists, tables.
  - Excel: every sheet's cells and formulas.
  - PowerPoint: slide titles, bullets and speaker notes.
- **New documents** go to the job's folder, or to `Documents\Mediator`. Mediator never overwrites a file and can open the result in its app.
- **Tools:** `office_read_document`, `office_create_document`, `office_open_document`.
- **Prompts:**
  - "/office make a budget spreadsheet for a 5-day trip with a total row"
  - "read report.docx and list the action items"
  - "turn these notes into a PowerPoint with speaker notes"
  - "summarize the Q3 sheet in sales.xlsx"
- **Outlook (classic desktop Outlook):** searches and reads mail, reads the calendar and writes drafts.
  - Drafts go to Drafts and ask for your approval first.
  - Mediator never sends mail.
  - **Tools:** `outlook_search_mail`, `outlook_read_mail`, `outlook_create_draft`, `outlook_calendar`.
  - **Prompts:** "/outlook summarize my unread emails from today", "draft a reply to Ana's budget email saying I'll send numbers Friday", "what's on my calendar this week?"
- **Limits:**
  - Old `.doc`/`.xls`/`.ppt` files must be re-saved in the new format first.
  - The new Outlook app and Outlook on the web can't be automated.

## 6. Organize files
- **For:** tidying a folder from a description or from rules.
- **Safety:** you review the plan before anything moves, and every batch can be rolled back.
- **Tools:** `files_plan_moves`, `files_plan_by_rules`, `files_execute_batch`, `files_batch_status`, `files_rollback_batch`.
- **Prompts:** "organize my Downloads by type and year", "sort my invoices into folders by client", "undo the last move".
- **Also from the console:** `mediator-cli organize --rules … --yes`, `mediator-cli rollback`.

## 7. Tutor (Ask & learn)
- **For:** learning from course PDFs: chapter extraction, explanations and exact checking of matrix results.
- **Tools:** `tutor_extract_pdf_chapter`, `tutor_verify_matrix`.
- **Prompts:** "/tutor explain chapter 3 of the attached PDF", "check my determinant for this 3×3 matrix", "quiz me on eigenvalues".

## 8. DaVinci Resolve rough cut
- **For:** turning a folder of clips into a timeline.
- **How:** Mediator probes the clips, plans a cut and exports FCPXML + EDL. With Studio, it can also create the timeline through scripting.
- **Tools:** `resolve_probe_clips`, `resolve_export_timeline`, `resolve_apply_studio`.
- **Prompts:** "make a 60-second rough cut from the footage in D:\trip", "build a timeline in DaVinci with the best takes".

## 9. Blender
- **For:** a procedural city car scene with a chase camera and a preview render.
- **Tools:** `blender_build_car_scene`, `blender_render_preview`.
- **Prompts:** "/blender build a night city scene with a red car and render a preview".

## 10. Chrome: Mediator for Chrome and Claude in Chrome
- **For:** reading and using your real Chrome tabs: navigate, read pages, click, type, scroll, take screenshots, see console logs.
- **Safety:** page scripts (`browser_eval`) always ask first.
- **Tools:** `browser_tabs`, `browser_navigate`, `browser_read`, `browser_click`, `browser_type`, `browser_key`, `browser_scroll`, `browser_screenshot`, `browser_console`, `browser_eval`, `browser_close_tab`.
- **Prompts:**
  - "/chrome open news.ycombinator.com and summarize the top 5 posts"
  - "go to khan academy and find the linear algebra unit"
  - "help me with this quiz on w3schools"
- **Side panel chat** (in Chrome):
  - Chat about the page you're on with the chips *Help me answer*, *Teach me this* and *Answer for me*.
  - Stop and New chat buttons.
  - The chat survives switching tabs.
- **Fallback:** if the extension isn't connected, browser jobs use Claude in Chrome.

## 11. Attachments and folders
- **Adding files:** attach, drag in or paste (Ctrl+V) files, folders, images or screenshots. Jobs can read them and never change them.
- **What jobs see:**
  - Images are sent as images.
  - PDFs are read by the tutor.
  - V15: Office files are read as their text and CAD files as their description.
- **Which folder a job uses:** a path typed in the prompt, then the *Save to* folder, then what's selected in File Explorer when you press the hotkey.
- **Tools:** `mediator_attachments`, `mediator_read_attachment`.

## 12. Actions (tiles) and your own actions
- **Built-in tiles:** Ask & learn, Assemble SolidWorks parts, Design parametric parts, Organize a folder, Rough cut for DaVinci Resolve, Blender car scene, Claude Code in a terminal.
- **Your own:** + New action with name, instructions and *Needs a folder*. It becomes a tile that runs your instructions on whatever is in the prompt box.

## 13. Coding (Claude Code / Antigravity)
- **For:** code work in a folder, headless or in an interactive terminal (Terminal checkbox, or the *Claude Code in a terminal* tile).
- **Prompts:** "/code add email validation to the signup form in C:\src\shop", "fix the failing tests in this repo".

## 14. Jobs, approvals, safety
- **Jobs tab:** state, CLI, elapsed time and outcome. Kill one job, or Kill all to end every job at once.
- **Approval cards:** risky calls pause for Approve/Deny. These include moves, assemblies, new files, Outlook drafts, custom MCP tools and page scripts.
- **Activity tab:** every tool call with its result, for auditing.
- **Rules:**
  - Mediator never sends mail and never overwrites files.
  - Jobs only reach the folders they were given.

## 15. Routing and usage
- **Routing pill:** Auto Best (per-task defaults), Quota Saver (Antigravity first) or Claude First.
- **Usage limits:** if a subscription hits its limit, the job moves to the other one.
- **Usage tab:**
  - Claude Pro 5-hour and weekly meters (the same figures as Claude Code `/usage`).
  - Google AI Pro cooldown.
  - Mediator's own runs and tokens per subscription.
  - Checking uses no model calls.

## 16. Connectors (Connection center)
- **What it lists:** every connector, live or not, with the app's icon, a one-line summary and a status. Details open with the chevron.
- **Subscription CLIs:** Claude Code CLI and Antigravity CLI, with version, sign-in (Sign In opens a terminal without API keys) and usage-limit status.
- **Desktop apps:** SolidWorks, DaVinci Resolve, Blender, Microsoft Word, Excel, PowerPoint and Outlook.
- **Chrome extension:** Mediator for Chrome. Set up takes one step in `chrome://extensions`, and the page warns if Chrome loaded the wrong folder.
- **Built-in file formats:** Office documents, 3D meshes, STEP/IGES, DXF/SVG, PDFs and images.
- **Custom MCP servers:** add any MCP server. It gets a 15-second health check, and its tools ask before every call.
- **Full check from the console:** `mediator-cli doctor` checks every prerequisite.

## 17. App
- **Overlay and Full view:**
  - The overlay opens with a hotkey from the tray. You can rebind the hotkey.
  - Full view is a maximised chat window. Its bar of window buttons hides itself until you move the mouse near it.
- **Themes:** Holo HUD, Pro Dark and Clean Light.
- **Run on startup.**
- **Data:** everything is stored in `%LOCALAPPDATA%\Mediator`.
