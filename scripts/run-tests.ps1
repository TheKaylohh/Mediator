<#
.SYNOPSIS
  Windows twin of verify/run-tests.sh: builds against the verify stand-ins with warnings as errors and runs the
  suite. On Windows the NTFS tests (junctions, junction swap race, alternate data streams, 8.3 names) and the
  sharing-violation journal test run instead of being skipped.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\run-tests.ps1
  $env:COCKPIT_REQUIRE_ALL = '1'; powershell -File scripts\run-tests.ps1   # release gate: any skip fails

  NOT RUN BY ITS AUTHOR (no Windows host was available for V6). The test runner itself is the same binary that
  ran 87/89 on Linux; this script only builds and starts it.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
foreach ($p in 'src/Cockpit.Bridge', 'src/Cockpit.SwxWorker', 'verify/stubs/SwxWorkerNoInterop', 'src/Cockpit.CliHost', 'src/Cockpit.Wpf', 'verify/tests/Cockpit.Tests') {
    dotnet build $p -nologo -v q -p:CockpitVerify=true -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}
dotnet verify/tests/Cockpit.Tests/bin/Debug/net8.0/Cockpit.Tests.dll `
    src/Cockpit.Bridge/bin/Debug/net8.0/cockpit-mcp-bridge.dll `
    src/Cockpit.CliHost/bin/Debug/net8.0/cockpit.dll
exit $LASTEXITCODE
