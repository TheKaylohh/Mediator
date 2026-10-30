# Mediator V17 - CATIA V5 connector (Windows PowerShell 5.1, CATIA COM automation). One function per tool; arguments come
# from MEDIATOR_ARGS (JSON), the answer goes to MEDIATOR_RESULT (JSON). Lengths in mm.
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\catia.ps1 overrides the built-in one.
$ErrorActionPreference = 'Stop'
$MediatorArgs = Get-Content -Raw -LiteralPath $env:MEDIATOR_ARGS | ConvertFrom-Json
$script:Log = New-Object System.Collections.ArrayList

function Write-Result($obj) {
    $json = $obj | ConvertTo-Json -Depth 12 -Compress
    [System.IO.File]::WriteAllText($env:MEDIATOR_RESULT, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-CadApp([string]$progId) {
    try { return [Runtime.InteropServices.Marshal]::GetActiveObject($progId) }
    catch { $o = New-Object -ComObject $progId; $o.Visible = $true; [void]$script:Log.Add('started CATIA'); return $o }
}

function Open-Doc($path) {
    if (-not $path) { if ($app.Documents.Count -eq 0) { throw 'No document is open in CATIA; give a path.' }; return $app.ActiveDocument }
    foreach ($d in $app.Documents) { if ($d.FullName -ieq $path) { $d.Activate(); return $d } }
    if (-not (Test-Path -LiteralPath $path)) { throw "$path does not exist" }
    return $app.Documents.Open($path)
}

function Kind($d) { $n = $d.Name.ToLower(); if ($n.EndsWith('.catpart')) { 'part' } elseif ($n.EndsWith('.catproduct')) { 'assembly' } elseif ($n.EndsWith('.catdrawing')) { 'drawing' } else { 'other' } }

function Run-Ops($ops, [scriptblock]$handler) {
    $results = New-Object System.Collections.ArrayList
    foreach ($op in $ops) {
        try { $detail = & $handler $op; [void]$results.Add([ordered]@{ op = $op.op; ok = $true; detail = $detail }) }
        catch { [void]$results.Add([ordered]@{ op = $op.op; ok = $false; error = $_.Exception.Message }) }
    }
    return ,$results
}

function Do-ActivePath { if ($app.Documents.Count -gt 0) { return @{ path = $app.ActiveDocument.FullName } } else { return @{ path = $null } } }

function Do-Session {
    $docs = @(); foreach ($d in $app.Documents) { $docs += [ordered]@{ title = $d.Name; path = $d.FullName; type = (Kind $d); unsaved = -not $d.Saved } }
    $active = $null; if ($app.Documents.Count -gt 0) { $active = $app.ActiveDocument.Name }
    $ver = $null; try { $ver = "V$($app.SystemConfiguration.Version) R$($app.SystemConfiguration.Release) SP$($app.SystemConfiguration.ServicePack)" } catch { }
    return [ordered]@{ running = $true; program = 'CATIA'; version = $ver; active = $active; documents = $docs }
}

function Do-Document($a) {
    switch ($a.action) {
        'open' { $d = Open-Doc $a.path; return [ordered]@{ opened = $d.FullName; title = $d.Name; type = (Kind $d) } }
        'save' { $d = Open-Doc $a.path; $d.Save(); return @{ saved = $d.FullName } }
        'save_as' { $d = Open-Doc $a.path; $d.SaveAs([string]$a.target); return [ordered]@{ savedAs = $a.target; note = "The open document is now $($a.target)." } }
        'close' {
            $d = Open-Doc $a.path
            if (-not $d.Saved -and -not $a.save -and -not $a.discard) { throw "$($d.Name) has unsaved changes: close it with save: true or discard: true" }
            if ($a.save) { $d.Save() }
            $d.Close(); return @{ closed = $true }
        }
    }
    throw "unknown action $($a.action)"
}

function Do-Export($a) {
    $files = @(); $problems = @()
    $paths = @($a.paths); if ($paths.Count -eq 0) { $paths = @($null) }
    foreach ($p in $paths) {
        $d = Open-Doc $p
        $stem = Join-Path $a.folder ([IO.Path]::GetFileNameWithoutExtension($d.Name))
        foreach ($f in $a.formats) {
            $target = $stem + $f.ext
            if (Test-Path -LiteralPath $target) { $problems += "$([IO.Path]::GetFileName($target)) already exists: not replaced"; continue }
            try { $d.ExportData($target, ([string]$f.ext).TrimStart('.')); $files += $target } catch { $problems += "$($d.Name) -> $($f.name): $($_.Exception.Message)" }
        }
    }
    return [ordered]@{ files = $files; problems = $problems }
}

function Bom($product, $bom, $depth) {
    if ($depth -gt 12) { return }
    foreach ($child in $product.Products) {
        $key = $child.PartNumber; $bom[$key] = 1 + [int]$bom[$key]
        Bom $child $bom ($depth + 1)
    }
}

function Do-Inspect($a) {
    $d = Open-Doc $a.path; $sections = @($a.sections); $kind = Kind $d
    $report = [ordered]@{ title = $d.Name; path = $d.FullName; type = $kind; unsaved = -not $d.Saved }
    if ($kind -eq 'drawing') { $report.sheets = @(); foreach ($s in $d.Sheets) { $report.sheets += [ordered]@{ name = $s.Name; views = $s.Views.Count } }; return $report }
    $prod = $d.Product
    if ($sections -contains 'parameters' -or $sections -contains 'dimensions' -or $sections -contains 'equations') {
        $params = $null; if ($kind -eq 'part') { $params = $d.Part.Parameters } else { $params = $prod.Parameters }
        $report.parameters = @()
        for ($i = 1; $i -le [Math]::Min($params.Count, 400); $i++) { $p = $params.Item($i); $report.parameters += [ordered]@{ name = $p.Name; value = $p.ValueAsString() } }
    }
    if ($sections -contains 'features' -and $kind -eq 'part') {
        $report.features = @()
        foreach ($b in $d.Part.Bodies) { foreach ($s in $b.Shapes) { $report.features += [ordered]@{ body = $b.Name; name = $s.Name } } }
    }
    if ($sections -contains 'properties') {
        $user = [ordered]@{}; $up = $prod.UserRefProperties
        for ($i = 1; $i -le $up.Count; $i++) { $user[$up.Item($i).Name] = $up.Item($i).ValueAsString() }
        $report.properties = [ordered]@{ partNumber = $prod.PartNumber; revision = $prod.Revision; definition = $prod.Definition; nomenclature = $prod.Nomenclature; description = $prod.DescriptionRef; custom = $user }
    }
    if ($sections -contains 'mass') {
        $an = $prod.Analyze
        $report.mass = [ordered]@{ massKg = [Math]::Round($an.Mass, 5); volumeMm3 = [Math]::Round($an.Volume * 1e9, 2); surfaceAreaMm2 = [Math]::Round($an.WetArea * 1e6, 2) }
    }
    if ($sections -contains 'components' -and $kind -eq 'assembly') {
        $bom = @{}; Bom $prod $bom 0
        $top = @(); foreach ($c in $prod.Products) { $top += [ordered]@{ name = $c.Name; partNumber = $c.PartNumber } }
        $report.components = [ordered]@{ bom = $bom; topLevel = $top }
    }
    if ($sections -contains 'selection') { $sel = $d.Selection; $report.selection = @(); for ($i = 1; $i -le $sel.Count; $i++) { $report.selection += $sel.Item($i).Value.Name } }
    return $report
}

function Edit-Op($op) {
    $kind = Kind $doc
    switch ($op.op) {
        { $_ -in 'set_dimension', 'set_global' } {
            $params = $null; if ($kind -eq 'part') { $params = $doc.Part.Parameters } else { $params = $doc.Product.Parameters }
            $p = $params.Item([string]$op.name); $old = $p.ValueAsString()
            $unit = 'mm'; if ($op.unit -eq 'deg') { $unit = 'deg' } elseif ($op.unit -eq 'in') { $unit = 'in' }
            $p.ValuateFromString("$([double]$op.value)$unit"); return "$($op.name): $old -> $($p.ValueAsString())"
        }
        'set_property' {
            $up = $doc.Product.UserRefProperties
            try { $up.Item([string]$op.name).ValuateFromString([string]$op.value) } catch { [void]$up.CreateString([string]$op.name, [string]$op.value) }
            return "$($op.name) = $($op.value)"
        }
        'rename_feature' { $doc.Part.FindObjectByName([string]$op.feature).Name = [string]$op.newName; return "$($op.feature) -> $($op.newName)" }
        'suppress' { $doc.Part.Inactivate($doc.Part.FindObjectByName([string]$op.feature)); return "$($op.feature) deactivated" }
        'unsuppress' { $doc.Part.Activate($doc.Part.FindObjectByName([string]$op.feature)); return "$($op.feature) activated" }
        'rebuild' { if ($kind -eq 'part') { $doc.Part.Update() } else { $doc.Product.Update() }; return 'updated' }
    }
    throw "unknown op $($op.op)"
}

function Do-Edit($a) {
    $script:doc = Open-Doc $a.path
    $r = Run-Ops $a.ops ${function:Edit-Op}
    try { if ((Kind $doc) -eq 'part') { $doc.Part.Update() } else { $doc.Product.Update() } } catch { [void]$script:Log.Add("update: $($_.Exception.Message)") }
    $report = [ordered]@{ results = $r }
    if ($a.save) { $doc.Save(); $report.saved = $doc.FullName } else { $report.saved = $false; $report.note = 'Not saved: review in CATIA, or save with document_ops.' }
    if ($script:Log.Count -gt 0) { $report.log = @($script:Log) }
    return $report
}

$Sight = @{
    iso = @(@(-1, -1, -1), @(0, 0, 1)); front = @(@(0, 1, 0), @(0, 0, 1)); back = @(@(0, -1, 0), @(0, 0, 1)); top = @(@(0, 0, -1), @(0, 1, 0))
    bottom = @(@(0, 0, 1), @(0, 1, 0)); right = @(@(-1, 0, 0), @(0, 0, 1)); left = @(@(1, 0, 0), @(0, 0, 1)); trimetric = @(@(-1, -0.6, -0.8), @(0, 0, 1))
}

function Do-Views($a) {
    $d = Open-Doc $a.path; $d.Activate()
    $viewer = $app.ActiveWindow.ActiveViewer; $images = @(); $notes = @(); $i = 0
    foreach ($v in $a.views) {
        try {
            if ($v -ne 'current') {
                $vp = $viewer.Viewpoint3D
                $vp.PutSightDirection([object[]]$Sight[$v][0]); $vp.PutUpDirection([object[]]$Sight[$v][1])
            }
            $viewer.Reframe(); $viewer.Update()
            $i++; $target = Join-Path $a.folder ("{0:00}-{1}.jpg" -f $i, $v)
            $viewer.CaptureToFile(5, $target)   # catCaptureFormatJPEG
            $images += $target
        } catch { $notes += "$v : $($_.Exception.Message)" }
    }
    return [ordered]@{ document = $d.Name; images = $images; log = $notes }
}

function Do-Script($a) {
    if ($a.path) { $script:doc = Open-Doc $a.path } elseif ($app.Documents.Count -gt 0) { $script:doc = $app.ActiveDocument }
    $out = & ([ScriptBlock]::Create([string]$a.code)) 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $env:MEDIATOR_RESULT)) { Write-Result ([ordered]@{ ok = $true; output = $out.Trim() }) }
}

try {
    $app = Get-CadApp 'CATIA.Application'
    $app.DisplayFileAlerts = $false
    $doc = $null
    $result = switch ($MediatorArgs.verb) {
        'active_path' { Do-ActivePath }
        'session' { Do-Session }
        'document' { Do-Document $MediatorArgs }
        'export' { Do-Export $MediatorArgs }
        'inspect' { Do-Inspect $MediatorArgs }
        'edit' { Do-Edit $MediatorArgs }
        'views' { Do-Views $MediatorArgs }
        'script' { Do-Script $MediatorArgs; $null }
        default { throw "CATIA cannot do '$($MediatorArgs.verb)' here" }
    }
    if ($null -ne $result) { Write-Result $result }
} catch {
    Write-Result ([ordered]@{ error = $_.Exception.Message; at = $_.InvocationInfo.PositionMessage; log = @($script:Log) })
    exit 1
} finally {
    try { $app.DisplayFileAlerts = $true } catch { }
}
