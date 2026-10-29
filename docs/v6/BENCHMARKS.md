# Benchmarks

These are measurements, not targets. They were taken once with `dotnet verify/tests/Cockpit.Tests/bin/Debug/net8.0/Cockpit.Tests.dll --bench <bridge.dll> docs/v6/benchmarks.json`. The raw numbers are in `benchmarks.json`.

**Environment:** Ubuntu 24.04.5 in a cloud container; 2 vCPU (Intel Xeon @ 2.80 GHz), 7.8 GiB RAM, ext4; .NET 8.0.32.

**Build:** Debug, `CockpitVerify=true`. SQLite is the verify shim over the system libsqlite3, not the shipping Microsoft.Data.Sqlite. A Release build on the target Windows machine will differ. Expect slower process starts, because the bridge runs through `dotnet` here instead of as a native AOT executable.

| What | Result | Notes |
|---|---|---|
| Core start (four SQLite stores opened, bus listening) | p50 **13 ms**; first start 183 ms (JIT) | n = 5 |
| Core working set after start | **53 MiB** | the test runner process, which includes the test code |
| Bridge process start + MCP `initialize` | p50 **146 ms**, max 196 ms | Paid once per CLI session; runs through `dotnet bridge.dll` here |
| MCP round trip, read-only tool (CLI ↔ bridge ↔ pipe ↔ Core ↔ provider, with audit append) | p50 **1.1 ms**, p95 9.5 ms, p99 12.4 ms | n = 500 |
| MCP round trip, Write tool with durable operation record (5 synchronous=FULL transitions), auto approval, audit | p50 **2.9 ms**, p95 8.7 ms, p99 21 ms | n = 300; the operation record costs about **1.8 ms** at p50 |
| 8 jobs × 40 concurrent read-only calls | **204 ms** total (≈1570 calls/s) | — |
| Audit append (redaction + hash chain + synchronous=FULL) | **0.28 ms/row**; verifying 2000 rows takes 11 ms | fsync is cheap on this virtual disk; expect more on a laptop SSD |
| File journal, 2000 small files: plan / execute / roll back | **0.49 s / 3.9 s / 2.8 s** (≈2 ms per move) | Each move: kernel-checked open, fingerprint, renameat2, fsync of both folders, two journal transitions |
| Path check, lexical + component walk | p50 **0.056 ms** | n = 2000 |
| Verified open (openat2 + final-path check) | p50 **0.062 ms** | n = 2000 |
| Built-in PDF reader, 400 pages, 1.24 M characters | p50 **139 ms** | n = 3 |

## Not measured here

These need the target environment and were not run:
- Overlay hotkey-to-visible latency.
- WPF memory use.
- SolidWorks part-card and assembly workloads.
- Blender preview rendering.
- Resolve import.

The CI job `linux-live` runs `--bench` and uploads the JSON. On Windows, run the same command after `scripts\run-tests.ps1`.

## Resource limits now enforced

| Limit | Value | Where |
|---|---|---|
| Bus hello frame (before authentication) | 8 KiB | `BusOptions.MaxHelloBytes` |
| Bus frame (after authentication) | 16 MiB | `FrameIO.MaxFrameBytes` |
| Requests in flight per connection | 32 | `BusOptions.MaxInFlightPerConnection` |
| Helper process output | 4 MiB each for stdout and stderr | `DefaultProcessRunner` |
| PDF file / one decompressed stream / total decompressed | 256 MiB / 64 MiB / 512 MiB | `PdfTextExtractor.Limits` |
| PDF nesting / operands / pages / text / time | 128 / 4096 / 20 000 / 16 M chars / 60 s | `PdfTextExtractor.Limits` |
| STEP produced by Code-CAD | 200 MiB per file, 64 files per run | `StepValidator`, `SandboxOutputs` |
| Sandbox | 4 GiB address space, 600 s CPU, 64 processes, 512 MiB per file, 256 open files, 10 min wall clock, no network | `SandboxLimits` |
| Exact maths | 12×12 matrices (eigen checks 8×8), 400 digits, exponents up to ±400 | `ExactMatrix` |
| Audit log | rotates (renames) at 256 MiB; arguments are truncated at 64 KiB | `AuditOptions` |
