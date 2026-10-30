# Mediator V17 - SolidWorks run_script host (Windows PowerShell 5.1, SolidWorks COM API). The other SolidWorks tools run
# inside Mediator; this script only runs the code the user approved, with $app (SldWorks) and $doc (the document) in scope.
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\solidworks.ps1 overrides the built-in one.
$ErrorActionPreference = 'Stop'
$MediatorArgs = Get-Content -Raw -LiteralPath $env:MEDIATOR_ARGS | ConvertFrom-Json

function Write-Result($obj) {
    $json = $obj | ConvertTo-Json -Depth 12 -Compress
    [System.IO.File]::WriteAllText($env:MEDIATOR_RESULT, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function DocType($path) { switch ([IO.Path]::GetExtension($path).ToLower()) { '.sldprt' { 1 } '.sldasm' { 2 } '.slddrw' { 3 } default { 0 } } }

try {
    try { $app = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application') }
    catch { throw 'SolidWorks is not running (the other swx_ tools start it).' }
    $doc = $app.ActiveDoc
    if ($MediatorArgs.path) {
        $doc = $null
        foreach ($d in $app.GetDocuments()) { if ($d.GetPathName() -ieq $MediatorArgs.path) { $doc = $d } }
        if (-not $doc) { $doc = $app.OpenDoc([string]$MediatorArgs.path, (DocType $MediatorArgs.path)) }
        if (-not $doc) { throw "SolidWorks could not open $($MediatorArgs.path)" }
    }
    if ($MediatorArgs.verb -ne 'script') { throw "SolidWorks scripts only run approved code here" }
    $out = & ([ScriptBlock]::Create([string]$MediatorArgs.code)) 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $env:MEDIATOR_RESULT)) { Write-Result ([ordered]@{ ok = $true; output = $out.Trim() }) }
} catch {
    Write-Result ([ordered]@{ error = $_.Exception.Message; at = $_.InvocationInfo.PositionMessage })
    exit 1
}
