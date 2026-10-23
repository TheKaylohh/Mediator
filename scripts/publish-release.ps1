<#
.SYNOPSIS
  Builds every AI Cockpit project in Release mode and packages a ready-to-run folder: dist\AiCockpit\.

.DESCRIPTION
  1. Checks the prerequisites: Windows, the .NET 8 SDK, and (for the Native AOT bridge) the MSVC linker from the
     "Desktop development with C++" workload, found with vswhere.
  2. Optionally runs the offline verification suite first (-RunTests; CockpitVerify=true, no SolidWorks needed).
  3. Publishes, each into its own staging folder:
       src\Cockpit.Wpf       → AiCockpit.exe            (the overlay)
       src\Cockpit.CliHost   → cockpit.exe              (console host: doctor, organize, rollback, …)
       src\Cockpit.Bridge    → cockpit-mcp-bridge.exe   (Native AOT: -p:PublishAot=true, starts in milliseconds)
       src\Cockpit.SwxWorker → cockpit-swx-worker.exe   (only when the SolidWorks interop is found)
     Self-contained win-x64 by default (no .NET install needed on the target PC); -FrameworkDependent makes a smaller
     folder that needs the .NET 8 Desktop Runtime.
  4. Merges the staging folders into dist\AiCockpit\. A file that two projects publish must be byte-identical
     (shared runtime files); anything else stops the script instead of silently overwriting.
  5. Adds Create-Desktop-Shortcut.ps1, README-FIRST.txt, VERSION.txt and SHA256SUMS.txt, then runs
     `cockpit.exe doctor` from the package as a smoke test.

  Every build uses -p:TreatWarningsAsErrors=true.

  UNTESTED ON WINDOWS BY ITS AUTHOR: written on Linux, where the WPF project, Native AOT for Windows and the real
  SolidWorks interop cannot be built. Expect to fix small things on the first run; each step says what it ran.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -RunTests -FrameworkDependent
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist\AiCockpit'),
    [string]$Runtime = 'win-x64',
    [string]$SolidWorksInteropDir,
    [switch]$FrameworkDependent,
    [switch]$RunTests,
    # Escape hatch when the C++ build tools are not installed: the bridge is published as a normal .NET app.
    [switch]$NoAot,
    [switch]$SkipDoctor
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Find-SolidWorksInterop {
    $base = 'HKLM:\SOFTWARE\SolidWorks'
    if (-not (Test-Path $base)) { return $null }
    $versions = Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like 'SOLIDWORKS *' } | Sort-Object PSChildName -Descending
    foreach ($v in $versions) {
        $folder = (Get-ItemProperty -Path (Join-Path $v.PSPath 'Setup') -Name 'SolidWorks Folder' -ErrorAction SilentlyContinue).'SolidWorks Folder'
        if ($folder) {
            $redist = Join-Path $folder 'api\redist'
            if (Test-Path (Join-Path $redist 'SolidWorks.Interop.sldworks.dll')) { return $redist }
        }
    }
    return $null
}

function Test-MsvcLinker {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    return [bool]$path
}

# ── 1. Prerequisites ──────────────────────────────────────────────────────────────────────────────
Step 'Prerequisites'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Run this on Windows: the overlay is WPF and the package is win-x64.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0' }
$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^8\.')) { throw "No .NET 8 SDK found. Installed: $($sdks -join ', ')" }
Write-Host "dotnet SDKs: $($sdks -join '; ')"

$aot = -not $NoAot
if ($aot -and -not (Test-MsvcLinker)) {
    throw ('Native AOT for cockpit-mcp-bridge needs the MSVC linker ("Desktop development with C++" in the Visual Studio Installer, ' +
           'or the Build Tools). Install it, or run again with -NoAot to publish the bridge as a normal .NET app.')
}
if (-not $SolidWorksInteropDir) { $SolidWorksInteropDir = Find-SolidWorksInterop }
if ($SolidWorksInteropDir) { Write-Host "SolidWorks interop: $SolidWorksInteropDir" }
else { Write-Warning 'SolidWorks interop not found: cockpit-swx-worker is left out (assembly and STEP import jobs will say so). Pass -SolidWorksInteropDir to include it.' }

# ── 2. Offline tests (optional) ───────────────────────────────────────────────────────────────────
if ($RunTests) {
    Step 'Offline verification suite'
    Invoke-Checked 'verification suite' { & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-tests.ps1') }
    # Verify and ship builds share obj\ folders; clear them so the ship restore starts clean.
    Get-ChildItem -Path src, verify -Directory -Recurse -Filter obj | Remove-Item -Recurse -Force
}

# ── 3. Publish each project into its own staging folder ───────────────────────────────────────────
$staging = Join-Path $root 'dist\.staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$common = @('-c', 'Release', '-r', $Runtime, '-nologo', '-p:TreatWarningsAsErrors=true')
if ($SolidWorksInteropDir) { $common += "-p:SolidWorksInteropDir=$SolidWorksInteropDir" }
$selfContained = if ($FrameworkDependent) { @('--self-contained', 'false') } else { @('--self-contained', 'true') }

$projects = [ordered]@{
    'src\Cockpit.Wpf'     = @($selfContained)
    'src\Cockpit.CliHost' = @($selfContained)
    'src\Cockpit.Bridge'  = if ($aot) { @('-p:PublishAot=true') } else { @('-p:PublishAot=false') + $selfContained }
}
if ($SolidWorksInteropDir) { $projects['src\Cockpit.SwxWorker'] = @($selfContained) }

foreach ($p in $projects.Keys) {
    Step "Publish $p"
    $dest = Join-Path $staging ($p -replace '[\\/]', '_')
    $extra = $projects[$p]
    Invoke-Checked "publish $p" { & dotnet publish $p @common @extra -o $dest }
}

# ── 4. Merge, refusing conflicting files ──────────────────────────────────────────────────────────
Step "Package into $OutDir"
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$seen = @{}
foreach ($dir in Get-ChildItem $staging -Directory) {
    foreach ($file in Get-ChildItem $dir.FullName -Recurse -File) {
        $rel = $file.FullName.Substring($dir.FullName.Length + 1)
        if ($rel -like '*.pdb') { continue }
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        if ($seen.ContainsKey($rel)) {
            if ($seen[$rel].Hash -ne $hash) { throw "Conflict: $rel differs between $($seen[$rel].From) and $($dir.Name). Publish with one runtime version, or -FrameworkDependent." }
            continue
        }
        $seen[$rel] = @{ Hash = $hash; From = $dir.Name }
        $target = Join-Path $OutDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
    }
}
Remove-Item $staging -Recurse -Force

foreach ($required in 'AiCockpit.exe', 'cockpit.exe', 'cockpit-mcp-bridge.exe') {
    if (-not (Test-Path (Join-Path $OutDir $required))) { throw "$required is missing from the package." }
}

# ── 5. Extras: shortcut script, readme, version, checksums ───────────────────────────────────────
Copy-Item (Join-Path $PSScriptRoot 'Create-Desktop-Shortcut.ps1') $OutDir
$commit = (& git -C $root rev-parse --short HEAD 2>$null)
$version = "AI Cockpit V7`nbuilt $(Get-Date -Format 'yyyy-MM-dd HH:mm') from commit $commit`nruntime $Runtime, $(if ($FrameworkDependent) { 'framework-dependent (.NET 8 Desktop Runtime required)' } else { 'self-contained' })`nbridge: $(if ($aot) { 'Native AOT' } else { 'JIT' })`nSolidWorks worker: $(if ($SolidWorksInteropDir) { 'included' } else { 'not included' })"
Set-Content -Path (Join-Path $OutDir 'VERSION.txt') -Value $version -Encoding UTF8
@"
AI Cockpit
==========

1. Run AiCockpit.exe. It lives in the tray; Ctrl+Shift+Space opens the panel.
2. The first launch opens a setup card: it checks Claude Code, Gemini CLI, Python and Git, and lets you pick the theme,
   the hotkey and "Run on Windows startup".
3. Optional: right-click Create-Desktop-Shortcut.ps1 > Run with PowerShell for a desktop shortcut.

cockpit.exe doctor   checks the installation from a terminal.
Your data (settings, audit log, journal, attachments) is in %LOCALAPPDATA%\AiCockpit.
"@ | Set-Content -Path (Join-Path $OutDir 'README-FIRST.txt') -Encoding UTF8

$sums = Get-ChildItem $OutDir -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($OutDir.Length + 1))"
}
Set-Content -Path (Join-Path $OutDir 'SHA256SUMS.txt') -Value $sums -Encoding UTF8

# ── 6. Smoke test ─────────────────────────────────────────────────────────────────────────────────
if (-not $SkipDoctor) {
    Step 'cockpit doctor (from the package)'
    & (Join-Path $OutDir 'cockpit.exe') doctor
    if ($LASTEXITCODE -ne 0) { Write-Warning 'doctor reported failures (FAIL lines above); the package was still written.' }
}

Step 'Done'
$size = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Package: $OutDir ($size MB, $((Get-ChildItem $OutDir -Recurse -File).Count) files)"
Write-Host 'Next: run Create-Desktop-Shortcut.ps1 from that folder, or start AiCockpit.exe.'
