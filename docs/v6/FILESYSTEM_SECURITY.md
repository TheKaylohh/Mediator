# Filesystem Authorization

Every path a job touches goes through one service: `Cockpit.Core.Security.PathAuthorizer`, exposed as `JobGrant.Paths`. Before V6, containment was checked with a string prefix after `Path.GetFullPath`. That check is gone: `JobGrant.IsInsideRoots` and `FileJournalService.InsideRoots` were deleted, so code using a lexical check no longer compiles.

## Threat

| Item | Description |
|---|---|
| Attacker | The model, through tool arguments (possibly prompt-injected), or a custom script running in the sandbox (see ISOLATION.md). Either one can leave files, symlinks or crafted timelines inside a granted folder. |
| Goal | Read, overwrite, move or delete something outside the granted roots. Or destroy the roots themselves. |
| Not in scope | Another process running as the same Windows user with arbitrary code. It already owns the user's files, so Cockpit does not try to defend against it. The TOCTOU guarantees below are about not being *tricked*, not about stopping the user's own malware. |

## Rules (in order, fail-closed)

1. **Lexical rules on the raw string, before normalization.** Windows strips trailing dots and spaces in `GetFullPath`, so the check has to run on the raw input. The rules live in `PathSyntax`, and both rule sets are unit-tested on every OS. These are refused:
   - device and extended paths: `\\?\`, `\\.\`, `\??\`
   - UNC paths (`\\server\share`), which also prevents NTLM credential leaks
   - drive-relative paths (`C:x`)
   - any `:` after the drive letter (alternate data streams, `::$DATA`)
   - segments that end with a dot or a space, or begin with a space
   - reserved DOS names (`CON`, `NUL.txt`, `COM1`…`LPT9`, `CONIN$`)
   - names shaped like 8.3 short names (`PROGRA~1`)
   - `.` and `..` segments
   - NUL and control characters
   - paths over 4096 characters
2. **Roots are resolved once, at grant time, to their real path.** On Linux this uses `openat2` plus `/proc/self/fd`; on Windows, `GetFinalPathNameByHandle`. A root that doesn't exist or can't be resolved fails the grant. If a root is itself a link, its lexical spelling is mapped onto the real root.
3. **Containment is checked by path component**, not by string prefix. `C:\Proj` doesn't contain `C:\Project`. Windows comparison is case-insensitive; POSIX comparison is case-sensitive.
4. **No reparse points below the root.** Every existing component, intermediate or final, must be a plain file or folder. That rules out symbolic links, junctions, mount points, dangling links, and WSL `LX_SYMLINK` reparse points as seen from Windows.
5. **No mount points between the root and the target.** On Linux, bind mounts aren't symlinks, so `/proc/self/mountinfo` is checked. On Windows, mounted folders are reparse points, and the volume is compared as well.
6. **The root itself can be listed and read, never modified.** That means it can't be moved, renamed, recreated, removed, or replaced by a file.
7. **Intent-specific checks:**
   - `Read` needs an existing file.
   - `CreateFile` needs nothing to exist at the path, so nothing is ever overwritten.
   - `Modify` needs the path to exist.

Every denial carries a rule code, for example `[path:reparse_point]`, `[path:outside_roots]`, `[path:crosses_mount]`, `[path:root_itself]` or `[path:race_detected]`. The code reaches the model as the tool error and lands in the audit trail.

## Closing the check-then-use gap

| Operation | Linux / WSL (kernel ≥ 5.6) | Windows | Other / fallback |
|---|---|---|---|
| Open for read | `openat2(rootfd, rel, RESOLVE_BENEATH\|NO_SYMLINKS\|NO_MAGICLINKS\|NO_XDEV)`: the kernel enforces rules 3–5 atomically with the open | `CreateFile(FILE_FLAG_OPEN_REPARSE_POINT)`, then `GetFinalPathNameByHandle` is re-checked against the root | open, then `/proc/self/fd` is re-checked |
| Create new file | the same `openat2` with `O_CREAT\|O_EXCL` | `CREATE_NEW`, handle verified; if it resolves outside, it is **deleted by handle** (`FileDispositionInfo`) and the call fails | `CreateNew`, `/proc` re-check, delete if outside |
| Create folder | `mkdirat` in a parent opened beneath the root, one component at a time | `CreateDirectory` per component, each re-checked | same as Windows |
| Move / rename | `renameat2(RENAME_NOREPLACE)` between parent folders opened beneath their roots, then both parents are `fsync`ed. Refused on file systems without NOREPLACE. | `MoveFileEx` without REPLACE_EXISTING, then the destination handle is verified | same as Windows |
| Remove empty folder | `unlinkat(AT_REMOVEDIR)` in a verified parent | `Directory.Delete` (non-recursive) after the check | same as Windows |
| Path given to an external program (ffprobe, Blender, SolidWorks, Python) | `VerifyExisting` right before launch | same | same |

**Linux** gives prevention: the kernel refuses the resolution. **Windows** gives detection and undo: a junction swapped in between the check and the open is caught by the handle check, and the stray file is removed by handle. For moves on Windows, a swapped intermediate junction is detected *after* the rename. The call then fails with `race_detected` and the batch stops, but the file has already moved, and Cockpit reports it rather than moving it a second time. This is the main residual risk on Windows (R-FS1 below).

## Where it is enforced

All of these use `call.Job.Paths`:

| Provider | What is authorized |
|---|---|
| Files (journal) | Source for Modify and destination for CreateFile at plan time, and again for every file at execute and rollback time. The authorizer is rebuilt from roots stored with the batch, so CLI rollback works without a job grant. |
| Resolve | The probe folder; each enumerated media file (files behind links are skipped and listed as `refused`); the edits folder; the FCPXML read through a verified handle with DTD ignored and no resolver. **Every `media-rep src` in a timeline is authorized**, so a crafted timeline can't make Resolve import outside media. |
| Blender | The output folder; generated scripts created with CreateNew; the `.blend` for render, verified again just before launch; rendered frames counted only if they are plain files. |
| Code-CAD | The output folder, the script (CreateNew), and each STEP produced (Read). |
| Tutor | The PDF (Read), passed to PyMuPDF after VerifyExisting, or read by the built-in reader through the verified handle with a 256 MiB cap. |
| SolidWorks | The STEP inputs, the output folder, `.SLDPRT` part paths and the assembly output (CreateFile). The template path must pass the lexical rules, so a UNC template is refused. |

## Tests (verify/tests/Cockpit.Tests/SecurityTests.cs)

| Test | What it proves | Run here |
|---|---|---|
| Lexical rules | 31 Windows cases and the POSIX cases, plus component containment and case rules | PASS |
| Real symlinks | Final, intermediate, relative and dangling links; the link itself; root protection; aliased root; missing root; no overwrite; move into a linked folder. Run in **both** the openat2 mode and the forced fallback. | PASS |
| Bind mount | A `mount --bind` of an outside folder inside a root is refused by Check and by the kernel open | PASS (container runs as root; SKIPs where mounting is not permitted) |
| Swap race | One thread swaps a folder ↔ symlink with atomic renames (about 50–70k swaps/s) while the main thread creates and reads through it for 1.5 s, in both modes. Result: 0 files outside, 0 outside reads. | PASS |
| Providers | A PDF behind a symlink is refused over the bus, and the audit row carries `[path:reparse_point]`. A timeline naming outside media is refused and no script is written. | PASS |
| Journal | Link sources and destinations are rejected at plan time. A destination folder swapped for a link after approval stops the move, and nothing reaches outside. | PASS |
| Windows junctions | Real `mklink /J`, a junction swap race, a real ADS | **SKIP (not run: no Windows here)** |

**Mutation checks** were run once by hand: the source was edited, the checks run, and the original restored.
- With the reparse walk disabled *and* openat2 unrestricted, the symlink, provider, journal and race tests all fail.
- With only the kernel restriction removed (component walk still on), the race test fails with "a read returned outside content". The kernel layer is therefore what closes the race; the walk alone is not enough.

## Residual risks

| ID | Risk | Mitigation / status |
|---|---|---|
| R-FS1 | Windows: an intermediate junction swapped during a rename is detected after the move, not prevented. | The batch stops with `race_detected` and the location is reported. It needs a same-user process creating junctions in the root at the right moment. Open. |
| R-FS2 | Paths handed to external programs (ffprobe, Blender, SolidWorks COM, Python) are re-verified just before launch, but the program opens them by name. | The window is milliseconds and needs a concurrent attacker inside the root. Custom code runs in the sandbox (ISOLATION.md), where the roots are not even visible. Open, low. |
| R-FS3 | Hard links: a hard link inside a root to a file outside it is not detected. | Hard links can't be created across mounts, and the sandbox only sees bind mounts, so sandboxed code can't make one. Creating one needs same-user code outside Cockpit. Open, low. |
| R-FS4 | NTFS per-directory case sensitivity (`fsutil file setCaseSensitiveInfo`) is not modelled. Comparison is case-insensitive on Windows. | That direction errs toward treating two names as the same root, not toward escaping. Open, informational. |
| R-FS5 | 8.3 detection is by shape. A real file named like `NOTES~1.TXT` is refused. | That fails closed. Accepted. |
| R-FS6 | `renameat2(RENAME_NOREPLACE)` isn't supported on some file systems (for example drvfs `/mnt/c` in WSL). Moves are refused there rather than downgraded. | Fails closed. The journal runs on the Windows host for Windows files. Accepted. |
| R-FS7 | The Windows code paths (CreateFile, GetFinalPathNameByHandle, DeleteByHandle, volume compare) are compiled but **not executed** in V6 verification. | Needs a Windows run of the suite. See RELEASE_CHECKLIST.md. Open. |
