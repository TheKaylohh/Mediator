<#
.SYNOPSIS
  Builds the Mediator for real on Windows (NuGet packages + your SolidWorks interop), publishes the
  shipping binaries into one folder, and runs `mediator-cli doctor` from there.

.DESCRIPTION
  1. Finds the SolidWorks interop folder from the registry
     (HKLM:\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup, value "SolidWorks Folder" + api\redist), newest first,
     unless -SolidWorksInteropDir is given.
  2. Optionally runs the offline verification suite (-RunVerifySuite) with MediatorVerify=true.
  3. Builds Bridge, SwxWorker, CliHost and Wpf in ship mode with warnings as errors.
  4. Publishes them side by side into -OutDir (default .\dist), so `mediator-cli` and `Mediator` find the
     bridge and the worker next to themselves.
  5. Runs `mediator-cli doctor` (add -CheckSolidWorks to also attach to / start SolidWorks through the worker).

  UNTESTED ON WINDOWS BY ITS AUTHOR: written and reviewed on Linux, where the WPF project and the real
  SolidWorks interop cannot be built. Expect to fix small things on the first run; the doctor output says what.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\check-windows-build.ps1 -RunVerifySuite
#>
[CmdletBinding()]
param(
    [string]$SolidWorksInteropDir,
    [string]$Configuration = 'Release',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist'),
    [switch]$RunVerifySuite,
    [switch]$CheckSolidWorks,
    [switch]$SkipWpf,
    # Native AOT for the bridge (fast start per CLI session). Needs the "Desktop development with C++" workload.
    [switch]$AotBridge
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Find-SolidWorksInterop {
    $base = 'HKLM:\SOFTWARE\SolidWorks'
    if (-not (Test-Path $base)) { return $null }
    $versions = Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like 'SOLIDWORKS *' } |
        Sort-Object PSChildName -Descending
    foreach ($v in $versions) {
        $setup = Join-Path $v.PSPath 'Setup'
        $folder = (Get-ItemProperty -Path $setup -Name 'SolidWorks Folder' -ErrorAction SilentlyContinue).'SolidWorks Folder'
        if ($folder) {
            $redist = Join-Path $folder 'api\redist'
            if (Test-Path (Join-Path $redist 'SolidWorks.Interop.sldworks.dll')) {
                Write-Host "Found $($v.PSChildName): $redist"
                return $redist
            }
        }
    }
    return $null
}

Step 'Prerequisites'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0' }
$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^8\.')) { throw "No .NET 8 SDK found. Installed: $($sdks -join ', ')" }
Write-Host "dotnet SDKs: $($sdks -join '; ')"

if (-not $SolidWorksInteropDir) { $SolidWorksInteropDir = Find-SolidWorksInterop }
if (-not $SolidWorksInteropDir) {
    if ($CheckSolidWorks) { throw 'SolidWorks interop not found in the registry, and -CheckSolidWorks needs it. Pass -SolidWorksInteropDir.' }
    # V8: the worker is still built; without the interop it answers "SolidWorks assemblies not installed on this machine" (warning MEDIATOR001).
    Write-Warning 'SolidWorks interop not found in the registry: mediator-swx-worker is built without SolidWorks support. Pass -SolidWorksInteropDir to include it.'
}

if ($RunVerifySuite) {
    Step 'Offline verification suite (MediatorVerify=true, stand-ins, no SolidWorks)'
    foreach ($p in 'src\Mediator.Bridge', 'src\Mediator.SwxWorker', 'verify\stubs\SwxWorkerNoInterop', 'src\Mediator.CliHost', 'src\Mediator.Wpf', 'verify\tests\Mediator.Tests') {
        & dotnet build $p -nologo -v q -p:MediatorVerify=true -p:TreatWarningsAsErrors=true
        if ($LASTEXITCODE -ne 0) { throw "verify build failed: $p" }
    }
    $verifyOut = Join-Path $root 'verify\tests\Mediator.Tests\bin\Debug\net8.0'
    & dotnet (Join-Path $verifyOut 'Mediator.Tests.dll') `
        (Join-Path $root 'src\Mediator.Bridge\bin\Debug\net8.0\mediator-mcp-bridge.dll') `
        (Join-Path $root 'src\Mediator.CliHost\bin\Debug\net8.0\mediator-cli.dll')
    if ($LASTEXITCODE -ne 0) { throw 'verification suite failed' }
    # Verify and ship builds share obj\ folders; clear them so the ship restore starts clean.
    Get-ChildItem -Path src, verify -Directory -Recurse -Filter obj | Remove-Item -Recurse -Force
}

Step "Ship build ($Configuration): NuGet packages and real SolidWorks interop"
$common = @('-c', $Configuration, '-nologo', '-p:TreatWarningsAsErrors=true')
if ($SolidWorksInteropDir) { $common += "-p:SolidWorksInteropDir=$SolidWorksInteropDir" }
# -CheckSolidWorks: a missing interop must fail the build (MEDIATOR002) instead of producing the no-SolidWorks worker.
if ($CheckSolidWorks) { $common += '-p:MediatorRequireSolidWorks=true' }

# V8: the worker is always built (with or without SolidWorks support).
$projects = @('src\Mediator.Bridge', 'src\Mediator.CliHost', 'src\Mediator.SwxWorker')
if (-not $SkipWpf) { $projects += 'src\Mediator.Wpf' }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
foreach ($p in $projects) {
    Write-Host "-- $p"
    # Framework-dependent, win-x64, into one folder; the bridge optionally Native AOT (self-contained by nature).
    $aot = if ($AotBridge -and $p -eq 'src\Mediator.Bridge') { @('-p:PublishAot=true') } else { @('-p:PublishAot=false', '--self-contained', 'false') }
    & dotnet publish $p @common -r win-x64 @aot -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}

Step 'mediator-cli doctor'
$mediator = Join-Path $OutDir 'mediator-cli.exe'
$doctorArgs = @('doctor')
if ($CheckSolidWorks) { $doctorArgs += '--check-swx' }
& $mediator @doctorArgs
$doctorExit = $LASTEXITCODE

Step 'Done'
Write-Host "Binaries: $OutDir"
Write-Host "  mediator-cli.exe      console host (doctor / assemble / organize / rollback)"
Write-Host "  Mediator.exe    overlay (tray + Ctrl+Shift+Space)"
Write-Host "  mediator-mcp-bridge.exe, mediator-swx-worker.exe"
if ($doctorExit -ne 0) { Write-Warning 'doctor reported failures (FAIL lines above).'; exit $doctorExit }
