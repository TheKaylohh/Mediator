#!/usr/bin/env bash
# Offline verification: builds every project against the verify stand-ins and runs the test suite.
# Zero warnings is part of the contract: the build fails on any warning (TreatWarningsAsErrors).
# Optional live tests (generated scripts run for real): MEDIATOR_LIVE_PYTHON=<python with verify/live-requirements.txt> bash verify/run-tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."
# V8: src/Mediator.Wpf compiles its WPF-free Win32 placement code here; verify/stubs/SwxWorkerNoInterop is the worker
# built with no SolidWorks interop at all (SOLIDWORKS_NOT_INSTALLED), exercised by the "swx V8" tests.
for p in src/Mediator.Bridge src/Mediator.SwxWorker verify/stubs/SwxWorkerNoInterop src/Mediator.CliHost src/Mediator.Wpf verify/tests/Mediator.Tests "$@"; do
  dotnet build "$p" -nologo -v q -p:MediatorVerify=true -p:TreatWarningsAsErrors=true
done
dotnet verify/tests/Mediator.Tests/bin/Debug/net8.0/Mediator.Tests.dll \
  src/Mediator.Bridge/bin/Debug/net8.0/mediator-mcp-bridge.dll \
  src/Mediator.CliHost/bin/Debug/net8.0/mediator-cli.dll
