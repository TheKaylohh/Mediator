# Mediator V17 - PTC Creo connector (Windows PowerShell 5.1, Creo VB API: pfcls COM classes). The VB API must be registered
# (vb_api_register.bat in <Creo>\Parametric\bin, once) and PRO_COMM_MSG_EXE set; Creo must be running. One function per tool;
# arguments come from MEDIATOR_ARGS (JSON), the answer goes to MEDIATOR_RESULT (JSON). Values are in the model's units.
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\creo.ps1 overrides the built-in one.
$ErrorActionPreference = 'Stop'
$MediatorArgs = Get-Content -Raw -LiteralPath $env:MEDIATOR_ARGS | ConvertFrom-Json
$script:Log = New-Object System.Collections.ArrayList

function Write-Result($obj) {
    $json = $obj | ConvertTo-Json -Depth 12 -Compress
    [System.IO.File]::WriteAllText($env:MEDIATOR_RESULT, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Connect-Creo {
    $async = New-Object -ComObject pfcls.pfcAsyncConnection
    try { return $async.Connect($null, $null, '.', 10) }
    catch { throw "Could not connect to Creo: start Creo first, and check the VB API is registered (vb_api_register.bat) and PRO_COMM_MSG_EXE is set. ($($_.Exception.Message))" }
}

function TypeName($t) { switch ([int]$t) { 0 { 'assembly' } 1 { 'part' } 2 { 'drawing' } default { 'other' } } }

function Open-Model($path) {
    if (-not $path) { $m = $session.CurrentModel; if (-not $m) { throw 'No model is open in Creo; give a path.' }; return $m }
    $session.ChangeDirectory([IO.Path]::GetDirectoryName($path))
    $desc = (New-Object -ComObject pfcls.pfcModelDescriptor).CreateFromFileName([IO.Path]::GetFileName($path))
    $m = $session.RetrieveModel($desc)
    try { $m.Display(); $session.CurrentWindow.Activate() } catch { }
    return $m
}

function ParamValue($p) {
    $v = $p.Value
    switch ([int]$v.discr) { 0 { $v.StringValue } 1 { $v.IntValue } 2 { $v.BoolValue } 3 { $v.DoubleValue } default { $null } }
}

function Run-Ops($ops, [scriptblock]$handler) {
    $results = New-Object System.Collections.ArrayList
    foreach ($op in $ops) {
        try { $detail = & $handler $op; [void]$results.Add([ordered]@{ op = $op.op; ok = $true; detail = $detail }) }
        catch { [void]$results.Add([ordered]@{ op = $op.op; ok = $false; error = $_.Exception.Message }) }
    }
    return ,$results
}

function Do-ActivePath { $m = $session.CurrentModel; if ($m) { return @{ path = (Join-Path $session.GetCurrentDirectory() $m.FileName) } } else { return @{ path = $null } } }

function Do-Session {
    $models = @(); foreach ($m in $session.ListModels()) { $models += [ordered]@{ name = $m.FileName; type = (TypeName $m.Type); modified = $m.IsModified } }
    $current = $null; if ($session.CurrentModel) { $current = $session.CurrentModel.FileName }
    return [ordered]@{ running = $true; program = 'PTC Creo'; workingDirectory = $session.GetCurrentDirectory(); active = $current; documents = $models }
}

function Do-Document($a) {
    switch ($a.action) {
        'open' { $m = Open-Model $a.path; return [ordered]@{ opened = $m.FileName; type = (TypeName $m.Type) } }
        'save' { $m = Open-Model $a.path; $m.Save(); return @{ saved = $m.FileName } }
        'close' {
            $m = Open-Model $a.path
            if ($m.IsModified -and -not $a.save -and -not $a.discard) { throw "$($m.FileName) has unsaved changes: close it with save: true or discard: true" }
            if ($a.save) { $m.Save() }
            try { $session.CurrentWindow.Close() } catch { }
            $m.Erase(); return @{ closed = $true }
        }
        'save_as' { throw 'Save As is not offered for Creo here: use run_script (pfcModel.Copy / Backup).' }
    }
    throw "unknown action $($a.action)"
}

function Do-Inspect($a) {
    $m = Open-Model $a.path; $sections = @($a.sections)
    $report = [ordered]@{ title = $m.FileName; type = (TypeName $m.Type); modified = $m.IsModified }
    if ($sections -contains 'parameters' -or $sections -contains 'equations' -or $sections -contains 'dimensions') {
        $report.parameters = @(); foreach ($p in $m.ListParams()) { $report.parameters += [ordered]@{ name = $p.Name; value = (ParamValue $p) } }
    }
    if ($sections -contains 'features') {
        $report.features = @(); $i = 0
        foreach ($f in $m.ListFeaturesByType($false, $null)) { if ($i++ -ge 300) { break }; $report.features += [ordered]@{ id = $f.Id; name = $f.GetName(); type = $f.FeatTypeName; status = $f.Status } }
    }
    if ($sections -contains 'mass') {
        try { $mp = $m.GetMassProperty($null); $c = $mp.GravityCenter; $report.mass = [ordered]@{ mass = $mp.Mass; volume = $mp.Volume; surfaceArea = $mp.SurfaceArea; centerOfMass = @($c.Item(0), $c.Item(1), $c.Item(2)); units = 'model units' } }
        catch { $report.mass = @{ error = $_.Exception.Message } }
    }
    return $report
}

function Edit-Op($op) {
    switch ($op.op) {
        { $_ -in 'set_global', 'set_dimension' } {
            $p = $model.GetParam([string]$op.name); if (-not $p) { throw "no parameter named $($op.name)" }
            $mi = New-Object -ComObject pfcls.MpfcModelItem
            $p.Value = $mi.CreateDoubleParamValue([double]$op.value)
            return "$($op.name) = $($op.value)"
        }
        'rebuild' { $model.Regenerate($null); return 'regenerated' }
    }
    throw "unknown op $($op.op)"
}

function Do-Edit($a) {
    $script:model = Open-Model $a.path
    $r = Run-Ops $a.ops ${function:Edit-Op}
    try { $model.Regenerate($null) } catch { [void]$script:Log.Add("regenerate: $($_.Exception.Message)") }
    $report = [ordered]@{ results = $r }
    if ($a.save) { $model.Save(); $report.saved = $model.FileName } else { $report.saved = $false; $report.note = 'Not saved: review in Creo, or save with document_ops.' }
    if ($script:Log.Count -gt 0) { $report.log = @($script:Log) }
    return $report
}

function Do-Views($a) {
    $m = Open-Model $a.path; $win = $session.CurrentWindow; $images = @(); $notes = @(); $i = 0
    foreach ($v in $a.views) {
        if ($v -ne 'current') { $notes += "$v view: Creo pictures here are of the current view (named orientations via run_script)"; continue }
        try {
            $i++; $target = Join-Path $a.folder ("{0:00}-current.png" -f $i)
            $instr = (New-Object -ComObject pfcls.pfcPNGImageExportInstructions).Create(8.0, 6.0)
            $win.ExportRasterImage($target, $instr); $images += $target
        } catch { $notes += "current : $($_.Exception.Message)" }
    }
    return [ordered]@{ document = $m.FileName; images = $images; log = $notes }
}

function Do-Script($a) {
    if ($a.path) { $script:model = Open-Model $a.path } else { $script:model = $session.CurrentModel }
    $out = & ([ScriptBlock]::Create([string]$a.code)) 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $env:MEDIATOR_RESULT)) { Write-Result ([ordered]@{ ok = $true; output = $out.Trim() }) }
}

$conn = $null
try {
    $conn = Connect-Creo
    $session = $conn.Session
    $model = $null
    $result = switch ($MediatorArgs.verb) {
        'active_path' { Do-ActivePath }
        'session' { Do-Session }
        'document' { Do-Document $MediatorArgs }
        'inspect' { Do-Inspect $MediatorArgs }
        'edit' { Do-Edit $MediatorArgs }
        'views' { Do-Views $MediatorArgs }
        'script' { Do-Script $MediatorArgs; $null }
        default { throw "Creo cannot do '$($MediatorArgs.verb)' here" }
    }
    if ($null -ne $result) { Write-Result $result }
} catch {
    Write-Result ([ordered]@{ error = $_.Exception.Message; at = $_.InvocationInfo.PositionMessage; log = @($script:Log) })
    exit 1
} finally {
    if ($conn) { try { $conn.Disconnect(2) } catch { } }
}
