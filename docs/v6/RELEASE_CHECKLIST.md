# Release Checklist (V6 and later)

Tick each box with evidence: a link to a CI run, or a transcript committed under `docs/v6/`. A skipped suite is never ticked.

## 1. Source

- [ ] Clean checkout of the release commit. `git status` is clean.
- [ ] CHANGELOG.md updated, including migrations and compatibility notes.
- [ ] No `TODO` in critical paths: `git grep -n "TODO" src/` is reviewed.

## 2. Linux layers (any machine; ran for V6)

- [ ] `bash verify/run-tests.sh` → exit 0, **0 failed**. Builds use warnings as errors.
- [ ] `MEDIATOR_LIVE_PYTHON=<venv> bash verify/run-tests.sh` with `verify/live-requirements.txt`, bubblewrap and ffmpeg installed → exit 0. The only skips allowed are the two Windows tests.
- [ ] The same suite run as root, so the uid-65534 permission and tmpfs disk-full tests run.
- [ ] `--bench`: numbers recorded in BENCHMARKS.md. Any regression larger than 2× is investigated.

## 3. Windows layers (V6: NOT RUN)

- [ ] `scripts\run-tests.ps1` → 0 failed. The NTFS junction/ADS/8.3 test and the sharing-violation test must show PASS.
- [ ] `$env:MEDIATOR_REQUIRE_ALL='1'; scripts\run-tests.ps1` with `MEDIATOR_LIVE_PYTHON` set → exit 0. No skips at all.
- [ ] `scripts\check-windows-build.ps1 -RunVerifySuite`: the ship build with NuGet SQLite, hashing and WPF, warnings as errors.
- [ ] Clean publish into `dist\`; `dist\mediator-cli.exe doctor` → no `fail`. Write down the audit chain head.
- [ ] Overlay checks by hand (INTEGRATION_MATRIX.md §Windows step 3), on two monitors with mixed DPI.
- [ ] Interactive tab check (INTEGRATION_MATRIX.md §Windows step 4).

## 4. Licensed and account-bound layers (V6: NOT RUN)

- [ ] `scripts\swx-integration.ps1` on each declared SolidWorks version → all PASS. Record the minimum version.
- [ ] The Resolve procedure: Free import (frame-exact), missing media, and the Studio script.
- [ ] The Blender procedure on 4.x and 5.x: scene build and preview render are reported separately.
- [ ] `scripts\capture-cli.ps1` with your own subscriptions. The new captures are committed with a parse test.
- [ ] WSL sandbox: the adversarial scripts from `IsolationTests` pass inside the restricted distro.

## 5. Security sign-off

- [ ] THREAT_MODEL.md residual risks reviewed. None of R1–R3 is still open for the features being shipped. Otherwise ship those features disabled or marked "preview".
- [ ] `mediator-cli support-bundle` opened and checked by hand: no secrets, no file names.
- [ ] Billing: run one headless job with `ANTHROPIC_API_KEY` set in the parent environment. The CLI must not see it. If it does (`apiKeySource`), the job must be killed.

## 6. Classification

| All of 2–5 | Release |
|---|---|
| Done | **Release candidate** |
| 2 done, some of 3–4 not run | **Partial release candidate.** List what did not run in TEST_RESULTS.md. |
| Any failure | **Not ready** |

V6 as prepared here: **partial release candidate** (2 done; 3 and 4 not run).
