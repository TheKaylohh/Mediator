# V16.2: the workspace window

The "Full view" (overlay → Full view) is now a full-screen workspace with three regions under a top bar. The quick panel (Ctrl+Shift+Space) is unchanged and stays the way to start something from any app. This version is the layout skeleton from the V16 UI specification, with the six right-sidebar modes wired to what Mediator already tracks. Parts the backend does not have yet say so in the panel instead of showing made-up data.

## Layout

| Region | What it holds |
| --- | --- |
| Top bar | Sidebar toggle, MEDIATOR, current project, search / command palette (Ctrl+P), the six mode buttons, right-sidebar toggle, notifications (approvals waiting, failed jobs), profile menu, window buttons. The bar is the window caption: drag to move, double-click to maximize. |
| Left sidebar | New chat (Ctrl+N), search chats (titles, prompts and answers), Active jobs, Pinned, Recent, Projects, profile button. Sections collapse. |
| Center | Conversation header (title, project, provider · model, session status, model switcher, pin, auto-approve, retry), messages (each answer keeps its provider/model badge), jump to latest, composer (approval and folder banners, context chips, attach, Save to, Allow folder, effort, send/stop). |
| Right sidebar | One of six modes, each a separate control sharing the panel: Cockpit, Artifacts, Access, Activity, Knowledge, Software. |

Rules (`Mediator.UI/Workspace/WorkspaceLayout.cs`, tested on any OS):

- On a window at least 1280 px wide the conversation keeps at least 50% of the width; below that, at least 480 px.
- Sidebars are resizable (left 200–420 px, right 300–640 px). A drag is capped so the conversation keeps its share.
- When the window is too narrow, the right sidebar shrinks to its minimum, then the left, then the right one hides, then the left. Hiding for lack of room never changes your choice: the sidebar returns when the window grows.
- Widths, which sidebars are shown, the right-sidebar mode and pinned software are saved in settings.json.
- Every mode keeps its state (search text, filters, selection) while another one is shown.

## Shortcuts

Ctrl+N new chat · Ctrl+K search chats · Ctrl+P command palette · Ctrl+B left sidebar · Ctrl+Alt+B right sidebar · Ctrl+1…6 modes (pressing the shown mode again hides the sidebar) · Ctrl+Shift+C copy the last answer · Esc closes the palette or a menu · Enter / Shift+Enter / Up / Esc in the composer as before.

## The six modes: what works now

| Mode | Working now | Not built yet (the panel says so) |
| --- | --- | --- |
| Cockpit | Environment (project, folders, Save to, provider · model, routing, session id, host, version), context (turns, attachments, status), connectors (CLIs, desktop apps, MCP servers with status), tool calls of the latest job, outputs, background tasks | Context-window %, token counts per turn, Git changes and diffs |
| Artifacts | Files the answers name that exist, plus attachments; filter by kind; details; Open (default program) and Folder | In-app viewers (PDF pages, sheets, slides, image zoom, code highlighting, sandboxed HTML), inline artifact cards in the chat |
| Access | The waiting request with Approve / Deny / Stop job and its rows; folders this chat may use, with revoke; Save to; auto-approve; decision history | Shell / network / MCP grants as separate rows, expiry dates |
| Activity | Timeline of tool calls, decisions and messages; search; category filter; one job's events (Inspect from the job list); export without secrets (Documents\Mediator) | Date filter, provider events, software launches |
| Knowledge | Exactly what the tools read for the latest answer; the sources a chat has (conversation, folders, attachments, saved chats) | Long-term memory, project instructions, document indexing |
| Software | Desktop programs and CLIs by category with status; New chat (routes the chat's tools and tells the model the program, its status and what Mediator can do with it), Run, Pin (saved), Details, Configure | Version-specific limitations, the current document in the context |

## Also in this version

- **Settings fix**: custom actions (+ New action) and the Save to folder were never written to settings.json, so both were lost at every restart. They are saved now.
- Terminal jobs from V16.1 show as chat cards here too.

## Checking it on Windows

1. Overlay → Full view: the window opens maximized with both sidebars and the Cockpit.
2. Drag both splitters as far as they go: the conversation never gets narrower than half the window.
3. Restore the window and make it narrow: the right sidebar hides first, then the left; widen it and both come back.
4. Ctrl+1…6 switch modes; pressing the shown one hides the panel. Type a search in Activity, switch to Cockpit and back: the search is still there.
5. Quit and restart: sidebar widths, hidden sidebars, the mode and pinned software are as you left them.
6. Software → SolidWorks → New chat, then "what's open?": the chat gets the SolidWorks tools.
7. Profile menu: Usage, Providers, Connections and Settings open the quick panel on that tab; Logs opens the data folder; Appearance switches theme (Holo HUD and Pro Dark are dark; Clean Light is light).
