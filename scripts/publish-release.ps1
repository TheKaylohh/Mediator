<#
.SYNOPSIS
  Builds every AI Cockpit project in Release mode and packages a clean, ready-to-run folder: dist\AiCockpit\.

.DESCRIPTION
  1. Checks the prerequisites: Windows, the .NET 8 SDK, and (for the Native AOT bridge) the MSVC linker from the
     "Desktop development with C++" workload, found with vswhere.
  2. Optionally runs the offline verification suite first (-RunTests; CockpitVerify=true, no SolidWorks needed).
  3. Publishes, each into its own staging folder:
       src\Cockpit.Wpf       -> AiCockpit.exe            (the tray overlay) + Themes\*.xaml (loose fallback copies)
       src\Cockpit.CliHost   -> cockpit.exe              (console host: doctor, organize, rollback, ...)
       src\Cockpit.Bridge    -> cockpit-mcp-bridge.exe   (Native AOT: -p:PublishAot=true, starts in milliseconds)
       src\Cockpit.SwxWorker -> cockpit-swx-worker.exe   (V8: ALWAYS. With the SolidWorks interop when it is found;
                                                          without it the worker answers "SolidWorks assemblies not
                                                          installed on this machine" and the build only warns, COCKPIT001)
     Self-contained win-x64 by default (no .NET install needed on the target PC); -FrameworkDependent makes a smaller
     folder that needs the .NET 8 Desktop Runtime.
  4. Merges the staging folders into dist\AiCockpit\. A file that two projects publish must be byte-identical
     (shared runtime files); anything else stops the script instead of silently overwriting. Debug symbols (.pdb) are
     left out unless -IncludeSymbols.
  5. Adds the zero-dependency launchers (launch.bat, install-shortcut.vbs: double-click, no PowerShell execution
     policy involved), Create-Desktop-Shortcut.ps1, README-FIRST.txt, VERSION.txt and SHA256SUMS.txt, checks that
     every required file is present (the four executables and the three themes), then runs `cockpit.exe doctor`
     from the package as a smoke test.

  Every build uses -p:TreatWarningsAsErrors=true. The one expected warning is COCKPIT001 (no SolidWorks interop),
  which never fails the build.

  This file is ASCII-only on purpose: Windows PowerShell 5.1 reads a UTF-8 file without BOM as ANSI, where some
  UTF-8 bytes become curly quotes that end strings (verify/tests checks every script).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -RunTests -FrameworkDependent
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -RequireSolidWorks    # fail instead of the no-SolidWorks worker
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist\AiCockpit'),
    [string]$Runtime = 'win-x64',
    [string]$SolidWorksInteropDir,
    [switch]$RequireSolidWorks,
    [switch]$FrameworkDependent,
    [switch]$RunTests,
    # Escape hatch when the C++ build tools are not installed: the bridge is published as a normal .NET app.
    [switch]$NoAot,
    [switch]$IncludeSymbols,
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

# Same search as the app's DesktopAppLocator: HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup (newest first), then
# the default install folder. Returns <SOLIDWORKS folder>\api\redist when it holds the interop assemblies.
function Find-SolidWorksInterop {
    $candidates = @()
    $base = 'HKLM:\SOFTWARE\SolidWorks'
    if (Test-Path $base) {
        $versions = Get-ChildItem $base -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like 'SOLIDWORKS *' } | Sort-Object PSChildName -Descending
        foreach ($v in $versions) {
            $folder = (Get-ItemProperty -Path (Join-Path $v.PSPath 'Setup') -Name 'SolidWorks Folder' -ErrorAction SilentlyContinue).'SolidWorks Folder'
            if ($folder) { $candidates += $folder }
        }
    }
    foreach ($pf in @($env:ProgramW6432, $env:ProgramFiles) | Where-Object { $_ }) {
        $candidates += (Join-Path $pf 'SOLIDWORKS Corp\SOLIDWORKS')
    }
    foreach ($folder in $candidates) {
        $redist = Join-Path $folder 'api\redist'
        if ((Test-Path (Join-Path $redist 'SolidWorks.Interop.sldworks.dll')) -and (Test-Path (Join-Path $redist 'SolidWorks.Interop.swconst.dll'))) { return $redist }
    }
    return $null
}

function Test-MsvcLinker {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    return [bool]$path
}

# -- 1. Prerequisites -----------------------------------------------------------------------------------
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
$withSolidWorks = [bool]$SolidWorksInteropDir
if ($withSolidWorks) { Write-Host "SolidWorks interop: $SolidWorksInteropDir" }
elseif ($RequireSolidWorks) { throw 'SolidWorks interop not found and -RequireSolidWorks was given. Pass -SolidWorksInteropDir "<SOLIDWORKS folder>\api\redist".' }
else {
    Write-Warning ('SolidWorks interop not found: cockpit-swx-worker is built WITHOUT SolidWorks support (it answers "SolidWorks assemblies ' +
                   'not installed on this machine"). Everything else is unaffected. Pass -SolidWorksInteropDir to include it.')
}

# -- 2. Offline tests (optional) ------------------------------------------------------------------------
if ($RunTests) {
    Step 'Offline verification suite'
    Invoke-Checked 'verification suite' { & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'run-tests.ps1') }
    # Verify and ship builds share obj\ folders; clear them so the ship restore starts clean.
    Get-ChildItem -Path src, verify -Directory -Recurse -Filter obj | Remove-Item -Recurse -Force
}

# -- 3. Publish each project into its own staging folder ------------------------------------------------
$staging = Join-Path $root 'dist\.staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$common = @('-c', 'Release', '-r', $Runtime, '-nologo', '-p:TreatWarningsAsErrors=true')
if ($withSolidWorks) { $common += "-p:SolidWorksInteropDir=$SolidWorksInteropDir" }
if ($RequireSolidWorks) { $common += '-p:CockpitRequireSolidWorks=true' }
$selfContained = if ($FrameworkDependent) { @('--self-contained', 'false') } else { @('--self-contained', 'true') }

$projects = [ordered]@{
    'src\Cockpit.Wpf'       = @($selfContained)
    'src\Cockpit.CliHost'   = @($selfContained)
    'src\Cockpit.SwxWorker' = @($selfContained)
    'src\Cockpit.Bridge'    = if ($aot) { @('-p:PublishAot=true') } else { @('-p:PublishAot=false') + $selfContained }
}

foreach ($p in $projects.Keys) {
    Step "Publish $p"
    $dest = Join-Path $staging ($p -replace '[\\/]', '_')
    $extra = $projects[$p]
    Invoke-Checked "publish $p" { & dotnet publish $p @common @extra -o $dest }
}

# -- 4. Merge, refusing conflicting files ---------------------------------------------------------------
Step "Package into $OutDir"
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$seen = @{}
foreach ($dir in Get-ChildItem $staging -Directory) {
    foreach ($file in Get-ChildItem $dir.FullName -Recurse -File) {
        $rel = $file.FullName.Substring($dir.FullName.Length + 1)
        if (-not $IncludeSymbols -and $rel -like '*.pdb') { continue }
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

# -- 5. Launchers, shortcut script, readme, version, checksums ------------------------------------------
Copy-Item (Join-Path $PSScriptRoot 'launch-cockpit.bat') (Join-Path $OutDir 'launch.bat')
Copy-Item (Join-Path $PSScriptRoot 'install-shortcut.vbs') (Join-Path $OutDir 'install-shortcut.vbs')
Copy-Item (Join-Path $PSScriptRoot 'Create-Desktop-Shortcut.ps1') $OutDir

$required = @('AiCockpit.exe', 'cockpit.exe', 'cockpit-mcp-bridge.exe', 'cockpit-swx-worker.exe',
              'Themes\HoloHud.xaml', 'Themes\ProDark.xaml', 'Themes\CleanLight.xaml', 'launch.bat', 'install-shortcut.vbs')
foreach ($r in $required) {
    if (-not (Test-Path (Join-Path $OutDir $r))) { throw "$r is missing from the package." }
}

$commit = (& git -C $root rev-parse --short HEAD 2>$null)
$version = @(
    'AI Cockpit V8 (Windows stabilized release candidate)'
    "built $(Get-Date -Format 'yyyy-MM-dd HH:mm') from commit $commit"
    "runtime $Runtime, $(if ($FrameworkDependent) { 'framework-dependent (.NET 8 Desktop Runtime required)' } else { 'self-contained' })"
    "bridge: $(if ($aot) { 'Native AOT' } else { 'JIT' })"
    "SolidWorks worker: $(if ($withSolidWorks) { "with SolidWorks support ($SolidWorksInteropDir)" } else { 'without SolidWorks support (no interop found at build time)' })"
) -join "`r`n"
Set-Content -Path (Join-Path $OutDir 'VERSION.txt') -Value $version -Encoding ASCII
@"
AI Cockpit
==========

1. Double-click launch.bat (or AiCockpit.exe). It lives in the tray; Ctrl+Shift+Space opens the panel.
   Nothing to install and no administrator rights: this folder is the whole app. Keep it somewhere permanent,
   for example %LOCALAPPDATA%\Programs\AiCockpit, before creating shortcuts.
2. Double-click install-shortcut.vbs for Desktop and Start menu shortcuts.
   (If scripts are blocked on your PC, right-click AiCockpit.exe > Send to > Desktop (create shortcut) does the same.)
3. The first launch opens a setup card: it checks Claude Code, Gemini CLI, Python and Git, and lets you pick the theme,
   the hotkey and "Run on Windows startup". The Connectors tab shows SolidWorks, DaVinci Resolve and Blender, found
   through the registry and Program Files even when they are not on PATH.

If Windows shows "Windows protected your PC" (SmartScreen) for the unsigned exe, choose More info > Run anyway, or
unblock the downloaded zip (right-click > Properties > Unblock) before extracting it.

launch.bat doctor     checks the installation in a console window (same as cockpit.exe doctor).
Your data (settings, audit log, journal, attachments) is in %LOCALAPPDATA%\AiCockpit.
"@ | Set-Content -Path (Join-Path $OutDir 'README-FIRST.txt') -Encoding ASCII

$sums = Get-ChildItem $OutDir -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($OutDir.Length + 1))"
}
Set-Content -Path (Join-Path $OutDir 'SHA256SUMS.txt') -Value $sums -Encoding ASCII

# -- 6. Smoke test --------------------------------------------------------------------------------------
if (-not $SkipDoctor) {
    Step 'cockpit doctor (from the package)'
    & (Join-Path $OutDir 'cockpit.exe') doctor
    if ($LASTEXITCODE -ne 0) { Write-Warning 'doctor reported failures (FAIL lines above); the package was still written.' }
}

Step 'Done'
$size = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Package: $OutDir ($size MB, $((Get-ChildItem $OutDir -Recurse -File).Count) files)"
Write-Host 'Next: double-click launch.bat in that folder (install-shortcut.vbs adds Desktop and Start menu shortcuts).'
