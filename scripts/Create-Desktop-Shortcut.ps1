<#
.SYNOPSIS
  Creates an "Mediator" shortcut on your desktop (and optionally in the Start menu) pointing at Mediator.exe.

.DESCRIPTION
  Run it from the packaged folder (dist\Mediator), where it sits next to Mediator.exe, or pass -Target.
  Per-user only: nothing needs administrator rights. Run on Windows startup is set in the app's first-run card
  instead (it writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run), so it can also be turned off there.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Create-Desktop-Shortcut.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Create-Desktop-Shortcut.ps1 -StartMenu
#>
[CmdletBinding()]
param(
    [string]$Target = (Join-Path $PSScriptRoot 'Mediator.exe'),
    [string]$Name = 'Mediator',
    [switch]$StartMenu
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Target)) { throw "Mediator.exe was not found at $Target. Run this from the packaged folder, or pass -Target." }
$Target = (Resolve-Path $Target).Path

$shell = New-Object -ComObject WScript.Shell
$places = @([Environment]::GetFolderPath('Desktop'))
if ($StartMenu) { $places += (Join-Path ([Environment]::GetFolderPath('Programs')) '') }

foreach ($folder in $places) {
    $link = Join-Path $folder "$Name.lnk"
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Description = 'Mediator: Ctrl+Shift+Space opens the panel'
    $shortcut.Save()
    Write-Host "Created $link"
}
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
