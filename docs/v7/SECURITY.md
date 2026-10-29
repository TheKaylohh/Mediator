# V7 security: what the new UI features may and may not do

V7 adds three ways for data to reach a model: attachments (drop, paste, pick), custom MCP servers, and sign-in launchers. None of them loosens a V6 invariant. This page lists the new rules, the V6 invariants they preserve, and the test that would fail if each rule were removed.

## Attachments

| Rule | Where | Test (fails if removed) |
|---|---|---|
| A dropped or picked file is **copied** into `<data>/attachments/<id>/`. The job gets only that folder, **read-only**. The original file and its folder are never granted. | `AttachmentStore.AddPath` | attachments: snapshot/folder/PNG staging |
| The copy opens the file without following a final link (`O_NOFOLLOW`; `FILE_FLAG_OPEN_REPARSE_POINT` on Windows). The handle's real path must equal the parent's real path plus the name, so a link swapped in during the copy is refused. Pipes, sockets and devices are refused (`O_NONBLOCK` open, then a seekability check). | `AttachmentStore.OpenSnapshotSource` | same (symlink, FIFO, `/dev`, `/proc` cases) |
| A folder is shared **in place, read-only**: the new `read_only` root kind allows Read and List and denies Create, Modify and Mkdir. A read-only root at or inside a writable root is dropped, so attaching never takes write access away from a running job. | `PathAuthorizer` (`RootEntry.ReadOnly`, `Order`) | attachments: read-only roots |
| **Credentials and whole areas are refused**, on the lexical and the real path: drive roots, the user folder itself, the Mediator data folder, and `~/.claude`, `~/.gemini`, `~/.ssh`, `~/.aws`, `~/.azure`, `~/.gnupg`, `~/.kube`, `~/.docker`, `~/.config/gcloud`, `~/.claude.json`, plus any folder containing one of them. `..` segments are refused before normalisation. | `AttachmentStore.SensitiveReason` | attachments: snapshot… (credential file, `.ssh`, home, data folder, drive root, a linked parent leading into `.ssh`). Mutation: disabling the check fails the test. |
| Size caps: 256 MiB per copied file, 20 MiB per pasted image, 20 attachments per prompt. A refused copy leaves no folder behind. | `AttachmentStore`, overlay VM | attachments: snapshot…; overlay V7: drop/paste/pick |
| Pasted bytes must be a PNG (signature and IHDR). | `AttachmentStore.AddPng` | both |
| The model reads attachments only through `mediator_attachments` and `mediator_read_attachment`. Both are `ReadOnly` built-ins. They see only the calling job's attachments. Every read goes through the job's own `PathAuthorizer.OpenRead`, and a folder attachment's `path` must stay inside that folder. | `AttachmentToolProvider` | attachments through the bus (another job refused; `..` and an absolute path to another attachment refused). Mutation: removing the folder containment fails it. |
| The attachment tools are granted only to jobs that **start with attachments or run interactively**. Every other job's tool list is exactly as in V6, so the V6 tests asserting exact tool lists still pass unchanged. | `MediatorRuntime.StartJobAsync` | runtime: assemble…; cli host process (doctor self-test) |
| Attaching to a **running** job adds one read-only root and nothing else: the tool list and approval modes are unchanged. It is refused for a job without the attachment tools. It is audited as `mediator/attach` with `access: read_only`. | `MediatorRuntime.AttachToJobAsync` | attachments through the bus. Mutation: allowing attach to a job without the tools fails it. |
| The × on a chip deletes a snapshot only if no job received it. Folders attached in place are never touched. Snapshots are pruned after 7 days. | `AttachmentStore.Discard` / `Prune`, overlay VM | attachments: snapshot…; overlay V7: drop/paste/pick |

## Custom MCP servers

| Rule | Where | Test |
|---|---|---|
| Every custom tool is **Destructive**: an approval card on every call, never auto-approved, even if a grant asks for Auto (the V6 gate clamp). The card names the server and the command line, and says the tool runs outside the Mediator's file rules. | `ExternalMcpToolProvider` | custom MCP through the bus. Mutation: making them Write fails the test. |
| A denied call never starts the server. | bus + provider | same (`touch` marker never written) |
| Each call starts the server fresh with **credential-like variables removed**: the billing keys, anything `MEDIATOR_*`, and names containing TOKEN, SECRET, PASSWORD, API_KEY, CREDENTIAL or PRIVATE_KEY. It runs in an empty working folder inside the Mediator's kill-on-close process group, and stops when the call ends. | `CustomMcpService.StartInfo` | same (`env` tool). Mutation: removing the scrub fails it. |
| `mediator.mcp.json` stores the command and arguments only, **never environment variables**. A malformed file is reported and never overwritten. Writes are atomic. | `CustomMcpService.Save` / `Load`, `AtomicFile` | custom MCP: validate… |
| Only **enabled** servers whose **last health check passed** have tools registered. Disabling or removing a server unregisters its tools at once, and a running job's next call to them fails. | `CustomMcpService.Sync` | both custom MCP tests |
| Names are validated: server `^[a-z0-9][a-z0-9-]{0,15}$`; tools are exposed as `ext_<server>_<tool>`, sanitised to the MCP name pattern. The command must be a full path that exists or a name found on PATH. Arguments are capped. The 15 s health check gives up on a hung server. | `CustomMcpService.AddAsync` / `PingAsync` | custom MCP: validate… (bad name, unknown command, relative path, dead server, hung server) |
| The model's input reaches the server only as JSON on stdin, never on a command line, so an npm `.cmd` shim run through `cmd /c` cannot be injected through model text. | `CliLaunchBuilder.WrapForShim` (V6) | — (by construction) |
| Images from a custom server are passed through only if they are `image/*` and at most 5 MiB. | `ExternalMcpToolProvider.ExecuteAsync` | custom MCP through the bus |

A custom server is the user's own program running with the user's rights, like an MCP server configured directly in Claude Code. The Mediator cannot sandbox it, so it requires approval for every call. It does not pretend to contain the server.

## Sign-in and status probes

- **Credential files are only checked for existence**, never opened. The test makes the file unreadable to prove it.
- **Sign-in terminals** run the CLI with the billing API-key variables removed, so the CLI signs in with the subscription. This is the same rule as V6 headless runs.
- **`--version` probes** run through `DefaultProcessRunner` (kill-on-close group, output capped, 10 s limit) with the same variables removed.
- **Run on startup** writes only the per-user value `HKCU\…\Run\Mediator` (no admin rights, no machine-wide change), and only when the user ticks it in the first-run card.

## V6 invariants

| Invariant | Status in V7 |
|---|---|
| Destructive actions always require approval | Kept. Custom MCP tools are Destructive, and the V6 gate clamp (unchanged) still applies; the test asks for Auto and is still asked. |
| Approving a subset runs only that subset | Unchanged code path (overlay VM: untick 1 of 4 mates). |
| Grants can't reach unauthorized tools or roots | Kept. Attachments add **read-only** roots only, by the user's action, audited. Tool lists are fixed at issue and never grow. |
| Secrets never enter logs or bundles | Kept. Custom MCP env is scrubbed, credential folders cannot be attached, and the audit is checked for the test token. |
| A failover never changes permissions | Kept. Attachments are part of the grant before launch; a switch reuses the same grant. |
| Custom code stays isolated | Kept for Code-CAD (bubblewrap, unchanged). Custom MCP servers are not "custom code" the model wrote: they are the user's own programs, gated per call. |
| User files are never overwritten | Kept. Attachments are copied out, never written back; folder attachments are read-only. |
| Cancellation is never presented as stopped when it isn't | Unchanged (overlay: uncertain calls). |
| No duplicate side effects | Kept: custom MCP calls are not ReadOnly, so they go through the same operation store (an identical call in flight or with an unknown outcome is refused), and each is approved individually. |
