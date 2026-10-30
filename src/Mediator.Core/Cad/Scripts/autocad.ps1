# Mediator V17 - AutoCAD connector (Windows PowerShell 5.1, AutoCAD COM API). One function per tool; arguments come from
# MEDIATOR_ARGS (JSON), the answer goes to MEDIATOR_RESULT (JSON). Drawing units are the drawing's own; angles in degrees.
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\autocad.ps1 overrides the built-in one.
$ErrorActionPreference = 'Stop'
$MediatorArgs = Get-Content -Raw -LiteralPath $env:MEDIATOR_ARGS | ConvertFrom-Json
$script:Log = New-Object System.Collections.ArrayList

function Write-Result($obj) {
    $json = $obj | ConvertTo-Json -Depth 12 -Compress
    [System.IO.File]::WriteAllText($env:MEDIATOR_RESULT, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-CadApp([string]$progId) {
    try { return [Runtime.InteropServices.Marshal]::GetActiveObject($progId) }
    catch {
        $o = New-Object -ComObject $progId
        $o.Visible = $true
        [void]$script:Log.Add("started AutoCAD")
        return $o
    }
}

function P3($p) { $z = 0.0; if ($p.Count -gt 2) { $z = [double]$p[2] }; return ,([double[]]@([double]$p[0], [double]$p[1], $z)) }
function Rad($deg) { return [double]$deg * [Math]::PI / 180.0 }
function Wait-Quiet { for ($i = 0; $i -lt 600; $i++) { if ($app.GetAcadState().IsQuiescent) { return }; Start-Sleep -Milliseconds 100 } }

function Open-Doc($path) {
    if (-not $path) {
        if ($app.Documents.Count -eq 0) { throw 'No drawing is open in AutoCAD; give a path.' }
        return $app.ActiveDocument
    }
    foreach ($d in $app.Documents) { if ($d.FullName -ieq $path) { $d.Activate(); return $d } }
    if (-not (Test-Path -LiteralPath $path)) { throw "$path does not exist" }
    return $app.Documents.Open($path)
}

function Entity($h) {
    try { return $doc.HandleToObject([string]$h) } catch { throw "no entity with handle $h (inspect_model entities lists them)" }
}

function Describe-Entity($e) {
    $o = [ordered]@{ handle = $e.Handle; type = $e.ObjectName; layer = $e.Layer }
    switch ($e.ObjectName) {
        'AcDbLine' { $o.start = @($e.StartPoint); $o.end = @($e.EndPoint); $o.length = [Math]::Round($e.Length, 4) }
        'AcDbCircle' { $o.center = @($e.Center); $o.radius = [Math]::Round($e.Radius, 4) }
        'AcDbArc' { $o.center = @($e.Center); $o.radius = [Math]::Round($e.Radius, 4) }
        'AcDbPolyline' { $o.closed = $e.Closed; $o.length = [Math]::Round($e.Length, 4); $o.area = [Math]::Round($e.Area, 4) }
        'AcDbText' { $o.text = $e.TextString; $o.at = @($e.InsertionPoint) }
        'AcDbMText' { $o.text = $e.TextString; $o.at = @($e.InsertionPoint) }
        'AcDbBlockReference' {
            $o.block = $e.EffectiveName; $o.at = @($e.InsertionPoint)
            if ($e.HasAttributes) { $atts = [ordered]@{}; foreach ($a in $e.GetAttributes()) { $atts[$a.TagString] = $a.TextString }; $o.attributes = $atts }
        }
    }
    return $o
}

function Finish($report, [bool]$save) {
    if ($save) {
        if (-not $doc.FullName) { throw 'This drawing was never saved; use document_ops save_as first.' }
        $doc.Save(); $report.saved = $doc.FullName
    } else { $report.saved = $false; $report.note = 'Not saved: review in AutoCAD (U undoes), or save with document_ops.' }
    if ($script:Log.Count -gt 0) { $report.log = @($script:Log) }
    return $report
}

function Run-Ops($ops, [scriptblock]$handler) {
    $results = New-Object System.Collections.ArrayList
    foreach ($op in $ops) {
        try { $detail = & $handler $op; [void]$results.Add([ordered]@{ op = $op.op; ok = $true; detail = $detail }) }
        catch { [void]$results.Add([ordered]@{ op = $op.op; ok = $false; error = $_.Exception.Message }) }
    }
    return ,$results
}

# -- verbs ---------------------------------------------------------------------------------------------------------

function Do-ActivePath { if ($app.Documents.Count -gt 0) { return @{ path = $app.ActiveDocument.FullName } } else { return @{ path = $null } } }

function Do-Session {
    $docs = @(); foreach ($d in $app.Documents) { $docs += [ordered]@{ title = $d.Name; path = $d.FullName; unsaved = -not $d.Saved } }
    $active = $null; if ($app.Documents.Count -gt 0) { $active = $app.ActiveDocument.Name }
    return [ordered]@{ running = $true; program = $app.Name; version = $app.Version; active = $active; documents = $docs }
}

function Do-Document($a) {
    switch ($a.action) {
        'open' { $d = Open-Doc $a.path; return [ordered]@{ opened = $d.FullName; title = $d.Name } }
        'save' { $d = Open-Doc $a.path; $d.Save(); return @{ saved = $d.FullName } }
        'save_as' { $d = Open-Doc $a.path; $d.SaveAs([string]$a.target); return [ordered]@{ savedAs = $a.target; note = "The open drawing is now $($a.target)." } }
        'close' {
            $d = Open-Doc $a.path
            if (-not $d.Saved -and -not $a.save -and -not $a.discard) { throw "$($d.Name) has unsaved changes: close it with save: true or discard: true" }
            $d.Close([bool]$a.save); return @{ closed = $true }
        }
    }
    throw "unknown action $($a.action)"
}

function Plot-To($d, $target, $device) {
    $d.SetVariable('BACKGROUNDPLOT', 0)
    $layout = $d.ActiveLayout
    $layout.ConfigName = $device
    $layout.PlotType = 1        # acExtents
    $layout.StandardScale = 0   # acScaleToFit
    $layout.CenterPlot = $true
    $d.Regen(1)
    if (-not $d.Plot.PlotToFile($target, $device)) { throw "plotting to $device failed" }
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
            try {
                switch ($f.name) {
                    'pdf' { Plot-To $d $target 'DWG To PDF.pc3' }
                    'png' { Plot-To $d $target 'PublishToWeb PNG.pc3' }
                    'dwg' { if (-not $d.Saved -or -not $d.FullName) { throw 'save the drawing first' }; Copy-Item -LiteralPath $d.FullName -Destination $target }
                    'dxf' {
                        if (-not $d.Saved -or -not $d.FullName) { throw 'save the drawing first (the DXF is written from the saved drawing)' }
                        $orig = $d.FullName
                        $d.SaveAs($target, 61)   # ac2013_dxf
                        $d.Close($false)
                        [void]$app.Documents.Open($orig)
                        [void]$script:Log.Add("reopened $orig after writing the DXF")
                    }
                }
                $files += $target
            } catch { $problems += "$($d.Name) -> $($f.name): $($_.Exception.Message)" }
        }
    }
    return [ordered]@{ files = $files; problems = $problems }
}

function Do-Inspect($a) {
    $d = Open-Doc $a.path
    $sections = @($a.sections)
    $report = [ordered]@{ title = $d.Name; path = $d.FullName; unsaved = -not $d.Saved; units = $d.GetVariable('INSUNITS'); extentsMin = @($d.GetVariable('EXTMIN')); extentsMax = @($d.GetVariable('EXTMAX')) }
    $report.layouts = @(); foreach ($l in $d.Layouts) { $report.layouts += $l.Name }
    if ($sections -contains 'layers' -or $sections -contains 'features') {
        $report.layers = @(); foreach ($l in $d.Layers) { $report.layers += [ordered]@{ name = $l.Name; color = $l.color; on = $l.LayerOn; frozen = $l.Freeze; locked = $l.Lock } }
        $report.currentLayer = $d.ActiveLayer.Name
    }
    if ($sections -contains 'blocks' -or $sections -contains 'components') {
        $report.blocks = @(); foreach ($b in $d.Blocks) { if (-not $b.IsLayout -and -not $b.Name.StartsWith('*')) { $report.blocks += [ordered]@{ name = $b.Name; entities = $b.Count } } }
    }
    if ($sections -contains 'entities' -or $sections -contains 'features') {
        $counts = @{}; $list = @(); $i = 0
        foreach ($e in $d.ModelSpace) {
            $counts[$e.ObjectName] = 1 + [int]$counts[$e.ObjectName]
            if ($i -lt 200) { $list += (Describe-Entity $e) }; $i++
        }
        $report.entityCounts = $counts; $report.entities = $list; $report.entityTotal = $i
    }
    if ($sections -contains 'properties') {
        $props = [ordered]@{}; $si = $d.SummaryInfo
        for ($k = 0; $k -lt $si.NumCustomInfo(); $k++) { $key = ''; $val = ''; $si.GetCustomByIndex($k, [ref]$key, [ref]$val); $props[$key] = $val }
        $report.properties = [ordered]@{ title = $si.Title; subject = $si.Subject; author = $si.Author; custom = $props }
    }
    if ($sections -contains 'selection') {
        $ss = $null; try { $ss = $d.PickfirstSelectionSet } catch { }
        $report.selection = @(); if ($ss) { foreach ($e in $ss) { $report.selection += (Describe-Entity $e) } }
    }
    return $report
}

function Edit-Op($op) {
    switch ($op.op) {
        'set_layer' {
            if ($op.handle) { $e = Entity $op.handle; $e.Layer = [string]$op.layer; return "entity $($op.handle) -> layer $($op.layer)" }
            $l = $doc.Layers.Item([string]$op.layer)
            if ($null -ne $op.on) { $l.LayerOn = [bool]$op.on }
            if ($null -ne $op.frozen) { $l.Freeze = [bool]$op.frozen }
            if ($null -ne $op.locked) { $l.Lock = [bool]$op.locked }
            if ($null -ne $op.color) { $l.color = [int]$op.color }
            return "layer $($op.layer) updated"
        }
        'transform' {
            $e = Entity $op.handle
            if ($op.copy) { $e = $e.Copy() }
            $about = P3 @(0, 0, 0); if ($op.about) { $about = P3 $op.about }
            if ($op.move) { $e.Move((P3 @(0, 0, 0)), (P3 $op.move)) }
            if ($op.rotate) { $e.Rotate($about, (Rad $op.rotate)) }
            if ($op.scale) { $e.ScaleEntity($about, [double]$op.scale) }
            return "entity $($e.Handle) transformed"
        }
        'delete_entity' { (Entity $op.handle).Delete(); return "deleted $($op.handle)" }
        'set_attribute' {
            $e = Entity $op.handle
            foreach ($att in $e.GetAttributes()) { if ($att.TagString -ieq $op.tag) { $att.TextString = [string]$op.value; return "$($op.tag) = $($op.value)" } }
            throw "block $($op.handle) has no attribute $($op.tag)"
        }
        'set_property' {
            $si = $doc.SummaryInfo
            try { $si.SetCustomByKey([string]$op.name, [string]$op.value) } catch { $si.AddCustomInfo([string]$op.name, [string]$op.value) }
            return "$($op.name) = $($op.value)"
        }
        'delete_property' { $doc.SummaryInfo.RemoveCustomByKey([string]$op.name); return "deleted $($op.name)" }
        'run_command' { $doc.SendCommand([string]$op.command + ' '); Wait-Quiet; return "ran: $($op.command)" }
        'undo' { $n = 1; if ($op.steps) { $n = [int]$op.steps }; $doc.SendCommand("_U $n "); Wait-Quiet; return "undid $n step(s)" }
        'rebuild' { $doc.Regen(1); return 'regenerated' }
    }
    throw "unknown op $($op.op)"
}

function Do-Edit($a) { $script:doc = Open-Doc $a.path; $r = Run-Ops $a.ops ${function:Edit-Op}; return Finish ([ordered]@{ results = $r }) $a.save }

function Add-Entity($e) {
    $ms = $doc.ModelSpace
    switch ($e.type) {
        'line' { return $ms.AddLine((P3 $e.a), (P3 $e.b)) }
        'centerline' {
            try { [void]$doc.Linetypes.Load('CENTER', 'acad.lin') } catch { }
            $l = $ms.AddLine((P3 $e.a), (P3 $e.b)); try { $l.Linetype = 'CENTER' } catch { }; return $l
        }
        'circle' { return $ms.AddCircle((P3 @($e.cx, $e.cy)), [double]$e.d / 2) }
        'point' { return $ms.AddPoint((P3 $e.at)) }
        'arc' {
            $ax = [double]$e.a[0]; $ay = [double]$e.a[1]; $bx = [double]$e.mid[0]; $by = [double]$e.mid[1]; $cx = [double]$e.b[0]; $cy = [double]$e.b[1]
            $dd = 2 * ($ax * ($by - $cy) + $bx * ($cy - $ay) + $cx * ($ay - $by))
            $ux = (($ax * $ax + $ay * $ay) * ($by - $cy) + ($bx * $bx + $by * $by) * ($cy - $ay) + ($cx * $cx + $cy * $cy) * ($ay - $by)) / $dd
            $uy = (($ax * $ax + $ay * $ay) * ($cx - $bx) + ($bx * $bx + $by * $by) * ($ax - $cx) + ($cx * $cx + $cy * $cy) * ($bx - $ax)) / $dd
            $rad = [Math]::Sqrt(($ax - $ux) * ($ax - $ux) + ($ay - $uy) * ($ay - $uy))
            $s = [Math]::Atan2($ay - $uy, $ax - $ux); $t = [Math]::Atan2($cy - $uy, $cx - $ux)
            $ccw = (($bx - $ax) * ($cy - $ay) - ($by - $ay) * ($cx - $ax)) -gt 0
            if ($ccw) { return $ms.AddArc((P3 @($ux, $uy)), $rad, $s, $t) } else { return $ms.AddArc((P3 @($ux, $uy)), $rad, $t, $s) }
        }
        { $_ -in 'rect', 'polygon' } {
            if ($e.type -eq 'rect') {
                $x = [double]$e.cx; $y = [double]$e.cy; $w = [double]$e.w / 2; $h = [double]$e.h / 2
                $pts = [double[]]@(($x - $w), ($y - $h), ($x + $w), ($y - $h), ($x + $w), ($y + $h), ($x - $w), ($y + $h))
            } else { $pts = [double[]]@($e.points | ForEach-Object { [double]$_[0]; [double]$_[1] }) }
            $pl = $ms.AddLightWeightPolyline($pts); $pl.Closed = $true; return $pl
        }
        'slot' {
            $ax = [double]$e.a[0]; $ay = [double]$e.a[1]; $bx = [double]$e.b[0]; $by = [double]$e.b[1]; $w = [double]$e.width / 2
            $len = [Math]::Sqrt(($bx - $ax) * ($bx - $ax) + ($by - $ay) * ($by - $ay)); $nx = - ($by - $ay) / $len * $w; $ny = ($bx - $ax) / $len * $w
            $pts = [double[]]@(($ax + $nx), ($ay + $ny), ($bx + $nx), ($by + $ny), ($bx - $nx), ($by - $ny), ($ax - $nx), ($ay - $ny))
            $pl = $ms.AddLightWeightPolyline($pts); $pl.SetBulge(1, -1.0); $pl.SetBulge(3, -1.0); $pl.Closed = $true; return $pl
        }
        'spline' {
            $pts = [double[]]@($e.points | ForEach-Object { [double]$_[0]; [double]$_[1]; 0.0 })
            return $ms.AddSpline($pts, (P3 @(0, 0, 0)), (P3 @(0, 0, 0))) }
    }
    throw "unknown entity type $($e.type)"
}

function Do-Sketch($a) {
    $script:doc = Open-Doc $a.path
    $s = $a.sketch; $made = @(); $problems = @()
    foreach ($e in $s.entities) { $made += (Add-Entity $e) }
    foreach ($d in @($s.dimensions)) {
        if (-not $d) { continue }
        try {
            $ent = $made[[int]$d.entity]
            if ($ent.ObjectName -eq 'AcDbCircle') {
                $c = @($ent.Center); $rr = $ent.Radius
                [void]$doc.ModelSpace.AddDimDiametric((P3 @(($c[0] + $rr), $c[1])), (P3 @(($c[0] - $rr), $c[1])), $rr)
            } elseif ($ent.ObjectName -eq 'AcDbLine') {
                $p1 = @($ent.StartPoint); $p2 = @($ent.EndPoint)
                [void]$doc.ModelSpace.AddDimAligned((P3 $p1), (P3 $p2), (P3 @((($p1[0] + $p2[0]) / 2), (($p1[1] + $p2[1]) / 2 + 5))))
            } else { $problems += "dimension on a $($ent.ObjectName) is not supported" }
        } catch { $problems += "dimension: $($_.Exception.Message)" }
    }
    if (@($s.constraints).Count -gt 0) { $problems += 'geometric constraints are not applied in AutoCAD drawings (use run_script with GCHORIZONTAL etc. if needed)' }
    $handles = @(); foreach ($m in $made) { $handles += $m.Handle }
    return Finish ([ordered]@{ entities = $handles.Count; handles = $handles; problems = $problems }) $a.save
}

function Annotate-Op($op) {
    $ms = $doc.ModelSpace
    switch ($op.op) {
        'dimension' {
            $p = @($op.points)
            switch ($op.type) {
                'linear' { $dim = $ms.AddDimRotated((P3 $p[0]), (P3 $p[1]), (P3 $op.at), 0.0) }
                'aligned' { $dim = $ms.AddDimAligned((P3 $p[0]), (P3 $p[1]), (P3 $op.at)) }
                'radial' { $dim = $ms.AddDimRadial((P3 $p[0]), (P3 $p[1]), 5.0) }
                'diameter' { $dim = $ms.AddDimDiametric((P3 $p[0]), (P3 $p[1]), 5.0) }
                'angular' { $dim = $ms.AddDimAngular((P3 $p[0]), (P3 $p[1]), (P3 $p[2]), (P3 $op.at)) }
                default { throw "unknown dimension type $($op.type)" }
            }
            if ($op.text) { $dim.TextOverride = [string]$op.text }
            return "dimension $($dim.Handle)"
        }
        'note' { $h = 2.5; if ($op.height) { $h = [double]$op.height }; $t = $ms.AddMText((P3 $op.at), 0.0, [string]$op.text); $t.Height = $h; return "note $($t.Handle)" }
        'leader' {
            $pts = [double[]]@($op.points | ForEach-Object { [double]$_[0]; [double]$_[1]; 0.0 })
            $idx = 0; $ml = $ms.AddMLeader($pts, [ref]$idx); $ml.TextString = [string]$op.text; return "leader $($ml.Handle)"
        }
        'table' {
            $rows = @($op.rows); $cols = ($rows | ForEach-Object { @($_).Count } | Measure-Object -Maximum).Maximum
            $n = $rows.Count; if ($op.title) { $n++ }
            $t = $ms.AddTable((P3 $op.at), $n, $cols, 8.0, 30.0)
            $r0 = 0; if ($op.title) { $t.SetText(0, 0, [string]$op.title); $r0 = 1 }
            for ($i = 0; $i -lt $rows.Count; $i++) { $cells = @($rows[$i]); for ($j = 0; $j -lt $cells.Count; $j++) { $t.SetText($i + $r0, $j, [string]$cells[$j]) } }
            return "table $($t.Handle) ($($rows.Count) x $cols)"
        }
        'insert_block' {
            $name = $op.name; if ($op.path) { $name = [string]$op.path }
            $s = 1.0; if ($op.scale) { $s = [double]$op.scale }
            $rot = 0.0; if ($op.rotation) { $rot = Rad $op.rotation }
            $b = $ms.InsertBlock((P3 $op.at), $name, $s, $s, $s, $rot)
            if ($op.attributes -and $b.HasAttributes) {
                foreach ($att in $b.GetAttributes()) { $v = $op.attributes.($att.TagString); if ($null -ne $v) { $att.TextString = [string]$v } }
            }
            return "block $($b.EffectiveName) $($b.Handle)"
        }
        'hatch' {
            $pts = [double[]]@($op.boundary | ForEach-Object { [double]$_[0]; [double]$_[1] })
            $pl = $ms.AddLightWeightPolyline($pts); $pl.Closed = $true
            $pattern = 'ANSI31'; if ($op.pattern) { $pattern = [string]$op.pattern }
            $h = $ms.AddHatch(0, $pattern, $true)
            $h.AppendOuterLoop([object[]]@($pl))
            if ($op.scale) { $h.PatternScale = [double]$op.scale }
            $h.Evaluate()
            return "hatch $($h.Handle)"
        }
        'layer' {
            $l = $null; try { $l = $doc.Layers.Item([string]$op.name) } catch { $l = $doc.Layers.Add([string]$op.name) }
            if ($null -ne $op.color) { $l.color = [int]$op.color }
            if ($op.linetype) { try { [void]$doc.Linetypes.Load([string]$op.linetype, 'acad.lin') } catch { }; $l.Linetype = [string]$op.linetype }
            if ($op.current) { $doc.ActiveLayer = $l }
            return "layer $($op.name)"
        }
    }
    throw "unknown op $($op.op)"
}

function Do-Annotate($a) { $script:doc = Open-Doc $a.path; $r = Run-Ops $a.ops ${function:Annotate-Op}; return Finish ([ordered]@{ results = $r }) $a.save }

function Do-Views($a) {
    $d = Open-Doc $a.path
    $images = @(); $notes = @(); $i = 0
    foreach ($v in $a.views) {
        if ($v -ne 'current') { $notes += "$v view: AutoCAD drawings are captured as plotted (extents)" }
        $i++
        $target = Join-Path $a.folder ("{0:00}-{1}.png" -f $i, $v)
        try { Plot-To $d $target 'PublishToWeb PNG.pc3'; $images += $target } catch { $notes += "$v : $($_.Exception.Message)" }
        if ($v -eq 'current') { break }
    }
    return [ordered]@{ document = $d.Name; images = $images; log = $notes }
}

function Do-Script($a) {
    if ($a.path) { $script:doc = Open-Doc $a.path } elseif ($app.Documents.Count -gt 0) { $script:doc = $app.ActiveDocument }
    $out = & ([ScriptBlock]::Create([string]$a.code)) 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $env:MEDIATOR_RESULT)) { Write-Result ([ordered]@{ ok = $true; output = $out.Trim() }) }
}

try {
    $app = Get-CadApp 'AutoCAD.Application'
    $doc = $null
    $result = switch ($MediatorArgs.verb) {
        'active_path' { Do-ActivePath }
        'session' { Do-Session }
        'document' { Do-Document $MediatorArgs }
        'export' { Do-Export $MediatorArgs }
        'inspect' { Do-Inspect $MediatorArgs }
        'edit' { Do-Edit $MediatorArgs }
        'sketch' { Do-Sketch $MediatorArgs }
        'annotate_drawing' { Do-Annotate $MediatorArgs }
        'views' { Do-Views $MediatorArgs }
        'script' { Do-Script $MediatorArgs; $null }
        default { throw "AutoCAD cannot do '$($MediatorArgs.verb)' here" }
    }
    if ($null -ne $result) { Write-Result $result }
} catch {
    Write-Result ([ordered]@{ error = $_.Exception.Message; at = $_.InvocationInfo.PositionMessage; log = @($script:Log) })
    exit 1
}
