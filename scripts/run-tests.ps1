<#
.SYNOPSIS
  Windows twin of verify/run-tests.sh: builds against the verify stand-ins with warnings as errors and runs the
  suite. On Windows the NTFS tests (junctions, junction swap race, alternate data streams, 8.3 names) and the
  sharing-violation journal test run instead of being skipped.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\run-tests.ps1
  $env:MEDIATOR_REQUIRE_ALL = '1'; powershell -File scripts\run-tests.ps1   # release gate: any skip fails

  NOT RUN BY ITS AUTHOR (no Windows host was available for V6). The test runner itself is the same binary that
  ran 87/89 on Linux; this script only builds and starts it.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
foreach ($p in 'src/Mediator.Bridge', 'src/Mediator.SwxWorker', 'verify/stubs/SwxWorkerNoInterop', 'src/Mediator.CliHost', 'src/Mediator.Wpf', 'verify/tests/Mediator.Tests') {
    dotnet build $p -nologo -v q -p:MediatorVerify=true -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}
dotnet verify/tests/Mediator.Tests/bin/Debug/net8.0/Mediator.Tests.dll `
    src/Mediator.Bridge/bin/Debug/net8.0/mediator-mcp-bridge.dll `
    src/Mediator.CliHost/bin/Debug/net8.0/mediator-cli.dll
exit $LASTEXITCODE
