# Custom-Code Isolation

## Policy

Every code runner carries an isolation requirement (`Cockpit.Core.Isolation.IsolationRequirement`):

| Code | Requirement | What happens without a sandbox |
|---|---|---|
| `cad_generate_custom_step`: model-written build123d | **RestrictedRequired** | **Refused** with the reason. It never falls back to host Python, and the approval preview already says "CANNOT RUN". |
| `cad_generate_parametric_step`: Cockpit's fixed templates; the model supplies only range-checked numbers | HostAllowed | Runs on the host Python. The sandbox is still used when one is available. |
| Resolve Studio script, PyMuPDF extraction, SymPy script | HostAllowed | Fixed code. Inputs are paths authorized by `PathAuthorizer`. Resolve needs the host app. |
| Blender scene script | HostAllowed | Fixed template with numeric parameters. |

## Approval binding

The approval for custom code is bound to a `binding` object shown in the preview:
- `scriptSha256`: SHA-256 of the exact script text
- `argumentsSha256`: SHA-256 of the exact tool arguments
- `outputFolder`: the authorized real path
- `policy`: `cad-custom-v1`
- `runner`: the sandbox id, such as `bwrap-v1`
- `limits`: address space, CPU, process count, file size, open files, wall clock, and `net=none`

Just before running, the provider recomputes the binding and compares it with the preview the gate returned (`ToolCallContext.ApprovedPreview`). If they differ in any field, the call is refused before anything runs. This covers a changed script, changed arguments, a different folder, a different sandbox or changed limits. Test: `TamperingPrompt`; the sandbox is never invoked.

## The Linux / WSL sandbox (bubblewrap)

The command is built by `BubblewrapSandbox.BuildArguments`:

```
[setpriv --reuid=65534 --regid=65534 --clear-groups --inh-caps=-all --]      # only when Cockpit runs as root
prlimit --as=<4 GiB> --cpu=<600> --nproc=<64> --fsize=<512 MiB> --nofile=256 --core=0 --
bwrap --unshare-all --unshare-user --disable-userns --die-with-parent --new-session --cap-drop ALL
      --hostname cockpit-sandbox --clearenv
      --ro-bind /usr /usr  (+ /bin /lib /lib64 as links)  --ro-bind /etc/ld.so.cache …  --ro-bind /etc/alternatives …
      --ro-bind <interpreter prefix, e.g. the venv> …
      --proc /proc --dev /dev --tmpfs /tmp
      --ro-bind <staging>/in /in   --bind <staging>/out /out   --chdir /out
      --setenv PATH … HOME=/tmp LANG … PYTHONIOENCODING …
      <python> -I /in/generate_parts.py
```

**Guarantees, each verified by a test that ran here:**

| Guarantee | How | Test result |
|---|---|---|
| No host files | Only the binds above exist. `/home`, `/root`, the user's folders and the secrets file at an absolute host path are absent. | `secret`, `shadow`, `passwd`, `home`, `rootdir` all DENIED; `/` lists only bin, dev, etc, in, lib, lib64, out, proc, sbin, tmp, usr |
| Read-only system and inputs | `--ro-bind` | writes to `/usr` and `/in` DENIED |
| Never writes into the user's folder | `/out` is a fresh private staging folder. Cockpit copies accepted outputs into the user folder via `PathAuthorizer.CreateNew`. | staging ≠ user folder; staging removed afterwards |
| No network | Empty network namespace (`--unshare-all` without `--share-net`) | host loopback listener, 1.1.1.1:443 and DNS all FAILED; only `lo` exists; the listener never accepted |
| No secrets | `--clearenv`, and the outer process environment is cleared too | env has no KEY, TOKEN or PROXY names, even with ANTHROPIC_API_KEY and COCKPIT_JOB_TOKEN set in the parent |
| Unprivileged | When running as root, the sandbox drops to uid 65534. `--cap-drop ALL`. | uid = 65534; `mount(2)` fails |
| No nested user namespaces | `--disable-userns` | `unshare -U` fails |
| Own PID namespace | `--unshare-all` | at most 4 PIDs visible |
| Memory | RLIMIT_AS | 3 GiB allocation under a 1 GiB limit → MemoryError |
| CPU | RLIMIT_CPU | busy loop under a 1 s limit was stopped quickly (exit ≠ 0, not by the wall clock) |
| Processes | RLIMIT_NPROC | fork loop under a limit of 16 stopped with an OSError after fewer than 16 forks |
| File size | RLIMIT_FSIZE | a 10 MiB write under a 1 MiB limit left a file of at most 1 MiB |
| Wall clock and cleanup | `DefaultProcessRunner` kills the tree at the limit; `--die-with-parent`; when PID 1 of the namespace dies, the kernel kills everything in it | a detached `sleep` grandchild does not survive a normal exit or a wall-clock kill (checked by scanning the host's /proc) |

**Mutation checks**, run by hand:
- Adding `--share-net` makes the network test fail: CONNECTED to the host loopback.
- Removing the sandbox requirement and the binding check makes the Code-CAD and binding tests fail.

## What it does not guarantee (stated plainly)

- **No seccomp filter.** Code inside can make any syscall the kernel allows an unprivileged user in a user namespace, apart from creating further user namespaces. A kernel vulnerability reachable from that syscall surface is a sandbox escape. bubblewrap is a strong container-style boundary. It is **not** a VM.
- **RLIMIT_NPROC is counted per real uid.** As root, the sandbox runs as uid 65534, so the count covers only sandbox processes. As a normal user, as in WSL, the limit counts all of that user's processes. The restricted distro runs nothing else, but on a desktop Linux the limit is less precise. A cgroup `pids.max` would be exact but needs systemd delegation; not done.
- **The interpreter's own files are visible read-only.** That covers `/usr` and the venv. Nothing in them is secret by assumption; anything secret under `/usr` would be readable.
- **CPU time is per process**, not for the whole tree. The wall-clock limit bounds the total.
- **Running as uid 65534** relies on that uid owning nothing of value on the host.

## Windows

Windows has no sandbox in this release unless the user configures the restricted WSL distro:
- `wsl.conf` with automount disabled, interop disabled and `appendWindowsPath=false`, generated by `IsolationConfig`
- bubblewrap installed in the distro
- `RuntimeOptions.CadSandbox = new WslBubblewrapSandbox(distro, exchange, /mnt/exchange)`
- `CadSandboxPython` pointing at the distro's Python
- `SandboxStagingDirectory` inside the exchange folder

Without that configuration, Windows runs use `UnavailableSandbox`, and custom code is **refused**.

| Claim | Status |
|---|---|
| WSL2 runs a separate Linux kernel in a lightweight VM. With automount and interop off, the distro cannot see Windows drives except the exchange folder. | Microsoft's documented behaviour. **Not verified here** (no Windows). |
| `wsl.exe` is started as the Windows user. Windows-side isolation therefore depends on those `wsl.conf` settings being in force, which needs `wsl --terminate` after a change. | Not verified here. |
| `WslBubblewrapSandbox` command line | Built and unit-tested as a string only. **The WSL path was NOT RUN.** |
| Windows Sandbox (`.wsb`, generated by `IsolationConfig`) | **Not** used for automatic custom-code execution. It needs a Windows Pro/Enterprise feature, starts slowly, and has no programmatic result channel that Cockpit verifies. Its isolation is not claimed. |

## Output validation (independent of the code that ran)

1. `SandboxOutputs.Inspect` keeps only the expected names (`<key>.step`). It accepts only plain files: no links, folders, devices or setuid bits. Each file must be under 200 MiB, and at most 64 files are accepted. A link planted in `/out` is refused. Cleaning up the staging folder removes the link, not its target; this is tested against a victim folder.
2. `StepValidator` (C#, in Cockpit) checks the Part 21 structure:
   - the file starts with `ISO-10303-21;` and ends with `END-ISO-10303-21;`
   - HEADER and DATA sections are present and closed
   - it contains at least one `MANIFOLD_SOLID_BREP`, `BREP_WITH_VOIDS` or `FACETED_BREP`

   It refuses surface-only files, wireframe-only files and truncated files.
3. **Re-import** by a fixed script, `StepReimportScript`, in a *fresh* sandbox run. That run sees only copies of the STEP files: no generator, no `/out` of the first run. It reports solid count, volume and bounding box.

   The part is accepted only if:
   - there is at least one solid
   - the volume is greater than 0
   - the bounding box fits the envelope (+0.01 mm)
   - the bounding box agrees with what the generator claimed (±0.05 mm)

   A generator cannot vouch for its own output.
4. Accepted files are copied into the user's folder as **new** files, and the exact approved script is saved beside them as a record.

The live test ran real build123d inside real bubblewrap through the bus:
- **Valid plate:** 1 solid, 40 mm wide, accepted.
- **Hostile script:** it tried to read a host secret, open a socket, and plant `/out/evil.step → /etc/passwd`. It produced nothing usable. Nothing was copied, and the secret was untouched.

## Environment note for the tests

The live venv was moved from the session scratch directory to `/opt/cockpit-pyv`, because the scratch directory is mode 0700 and uid 65534 cannot traverse it. That is a property of this test machine, not of Cockpit. In production the sandbox Python lives in the WSL distro.
