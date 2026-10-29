#!/usr/bin/env bash
# Offline verification: builds every project against the verify stand-ins and runs the test suite.
# Zero warnings is part of the contract: the build fails on any warning (TreatWarningsAsErrors).
# Optional live tests (generated scripts run for real): COCKPIT_LIVE_PYTHON=<python with verify/live-requirements.txt> bash verify/run-tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."
for p in src/Cockpit.Bridge src/Cockpit.SwxWorker src/Cockpit.CliHost verify/tests/Cockpit.Tests "$@"; do
  dotnet build "$p" -nologo -v q -p:CockpitVerify=true -p:TreatWarningsAsErrors=true
done
dotnet verify/tests/Cockpit.Tests/bin/Debug/net8.0/Cockpit.Tests.dll \
  src/Cockpit.Bridge/bin/Debug/net8.0/cockpit-mcp-bridge.dll \
  src/Cockpit.CliHost/bin/Debug/net8.0/cockpit.dll
