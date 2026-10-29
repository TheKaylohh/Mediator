#!/usr/bin/env bash
# Offline verification: builds every project against the verify stand-ins and runs the test suite.
# Zero warnings is part of the contract: the build fails on any warning (TreatWarningsAsErrors).
# Optional live tests (generated scripts run for real): COCKPIT_LIVE_PYTHON=<python with verify/live-requirements.txt> bash verify/run-tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."
# V8: src/Cockpit.Wpf compiles its WPF-free Win32 placement code here; verify/stubs/SwxWorkerNoInterop is the worker
# built with no SolidWorks interop at all (SOLIDWORKS_NOT_INSTALLED), exercised by the "swx V8" tests.
for p in src/Cockpit.Bridge src/Cockpit.SwxWorker verify/stubs/SwxWorkerNoInterop src/Cockpit.CliHost src/Cockpit.Wpf verify/tests/Cockpit.Tests "$@"; do
  dotnet build "$p" -nologo -v q -p:CockpitVerify=true -p:TreatWarningsAsErrors=true
done
dotnet verify/tests/Cockpit.Tests/bin/Debug/net8.0/Cockpit.Tests.dll \
  src/Cockpit.Bridge/bin/Debug/net8.0/cockpit-mcp-bridge.dll \
  src/Cockpit.CliHost/bin/Debug/net8.0/cockpit.dll
