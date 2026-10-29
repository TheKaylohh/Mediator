<#
.SYNOPSIS
  Builds the AI Cockpit for real on Windows (NuGet packages + your SolidWorks interop), publishes the
  shipping binaries into one folder, and runs `cockpit doctor` from there.

.DESCRIPTION
  1. Finds the SolidWorks interop folder from the registry
     (HKLM:\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup, value "SolidWorks Folder" + api\redist), newest first,
     unless -SolidWorksInteropDir is given.
  2. Optionally runs the offline verification suite (-RunVerifySuite) with CockpitVerify=true.
  3. Builds Bridge, SwxWorker, CliHost and Wpf in ship mode with warnings as errors.
  4. Publishes them side by side into -OutDir (default .\dist), so `cockpit` and `AiCockpit` find the
     bridge and the worker next to themselves.
  5. Runs `cockpit doctor` (add -CheckSolidWorks to also attach to / start SolidWorks through the worker).

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
    Write-Warning 'SolidWorks interop not found in the registry. The worker will be skipped; pass -SolidWorksInteropDir to build it.'
}

if ($RunVerifySuite) {
    Step 'Offline verification suite (CockpitVerify=true, stand-ins, no SolidWorks)'
    foreach ($p in 'src\Cockpit.Bridge', 'src\Cockpit.SwxWorker', 'src\Cockpit.CliHost', 'verify\tests\Cockpit.Tests') {
        & dotnet build $p -nologo -v q -p:CockpitVerify=true -p:TreatWarningsAsErrors=true
        if ($LASTEXITCODE -ne 0) { throw "verify build failed: $p" }
    }
    $verifyOut = Join-Path $root 'verify\tests\Cockpit.Tests\bin\Debug\net8.0'
    & dotnet (Join-Path $verifyOut 'Cockpit.Tests.dll') `
        (Join-Path $root 'src\Cockpit.Bridge\bin\Debug\net8.0\cockpit-mcp-bridge.dll') `
        (Join-Path $root 'src\Cockpit.CliHost\bin\Debug\net8.0\cockpit.dll')
    if ($LASTEXITCODE -ne 0) { throw 'verification suite failed' }
    # Verify and ship builds share obj\ folders; clear them so the ship restore starts clean.
    Get-ChildItem -Path src, verify -Directory -Recurse -Filter obj | Remove-Item -Recurse -Force
}

Step "Ship build ($Configuration): NuGet packages and real SolidWorks interop"
$common = @('-c', $Configuration, '-nologo', '-p:TreatWarningsAsErrors=true')
if ($SolidWorksInteropDir) { $common += "-p:SolidWorksInteropDir=$SolidWorksInteropDir" }

$projects = @('src\Cockpit.Bridge', 'src\Cockpit.CliHost')
if ($SolidWorksInteropDir) { $projects += 'src\Cockpit.SwxWorker' }
if (-not $SkipWpf) { $projects += 'src\Cockpit.Wpf' }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
foreach ($p in $projects) {
    Write-Host "-- $p"
    # Framework-dependent, win-x64, into one folder; the bridge optionally Native AOT (self-contained by nature).
    $aot = if ($AotBridge -and $p -eq 'src\Cockpit.Bridge') { @('-p:PublishAot=true') } else { @('-p:PublishAot=false', '--self-contained', 'false') }
    & dotnet publish $p @common -r win-x64 @aot -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
}

Step 'cockpit doctor'
$cockpit = Join-Path $OutDir 'cockpit.exe'
$doctorArgs = @('doctor')
if ($CheckSolidWorks) { $doctorArgs += '--check-swx' }
& $cockpit @doctorArgs
$doctorExit = $LASTEXITCODE

Step 'Done'
Write-Host "Binaries: $OutDir"
Write-Host "  cockpit.exe      console host (doctor / assemble / organize / rollback)"
Write-Host "  AiCockpit.exe    overlay (tray + Ctrl+Shift+Space)"
Write-Host "  cockpit-mcp-bridge.exe, cockpit-swx-worker.exe"
if ($doctorExit -ne 0) { Write-Warning 'doctor reported failures (FAIL lines above).'; exit $doctorExit }
