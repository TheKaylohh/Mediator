# Mediator V17 - Autodesk Inventor connector (Windows PowerShell 5.1, Inventor COM API). One function per tool; arguments
# come from MEDIATOR_ARGS (JSON), the answer goes to MEDIATOR_RESULT (JSON). The API works in cm and radians; this script
# converts from and to mm and degrees. Front = XY, Top = XZ, Right = YZ (Inventor's origin planes 3, 2, 1).
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\inventor.ps1 overrides the built-in one.
$ErrorActionPreference = 'Stop'
$MediatorArgs = Get-Content -Raw -LiteralPath $env:MEDIATOR_ARGS | ConvertFrom-Json
$script:Log = New-Object System.Collections.ArrayList

function Write-Result($obj) {
    $json = $obj | ConvertTo-Json -Depth 12 -Compress
    [System.IO.File]::WriteAllText($env:MEDIATOR_RESULT, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-CadApp([string]$progId) {
    try { return [Runtime.InteropServices.Marshal]::GetActiveObject($progId) }
    catch { $o = New-Object -ComObject $progId; $o.Visible = $true; [void]$script:Log.Add('started Inventor'); return $o }
}

$kPart = 12290; $kAssembly = 12291; $kDrawing = 12292
$kJoin = 20481; $kCut = 20482; $kPositive = 20993; $kNegative = 20994; $kSymmetric = 20995
$kHiddenLine = 32257
$Orient = @{ iso = 10759; front = 10764; top = 10765; right = 10766; back = 10756; bottom = 10757; left = 10763 }
$PlaneIndex = @{ front = 3; xy = 3; top = 2; xz = 2; right = 1; yz = 1 }
$AxisIndex = @{ x = 1; y = 2; z = 3 }

function Cm($mm) { return [double]$mm / 10.0 }
function Pt2($p) { return $app.TransientGeometry.CreatePoint2d((Cm $p[0]), (Cm $p[1])) }
function Dist($a, $b) { return [Math]::Sqrt([Math]::Pow($a.X - $b.X, 2) + [Math]::Pow($a.Y - $b.Y, 2) + [Math]::Pow($a.Z - $b.Z, 2)) }

function Open-Doc($path) {
    if (-not $path) { if (-not $app.ActiveDocument) { throw 'No document is open in Inventor; give a path.' }; return $app.ActiveDocument }
    foreach ($d in $app.Documents) { if ($d.FullFileName -ieq $path) { $d.Activate(); return $d } }
    if (-not (Test-Path -LiteralPath $path)) { throw "$path does not exist" }
    return $app.Documents.Open($path, $true)
}

function Finish($report, [bool]$save) {
    $doc.Update()
    if ($save) {
        if (-not $doc.FullFileName) { throw 'This document was never saved; use document_ops save_as first.' }
        $doc.Save(); $report.saved = $doc.FullFileName
    } else { $report.saved = $false; $report.note = 'Not saved: review in Inventor (Undo reverts), or save with document_ops.' }
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

function UserProps { return $doc.PropertySets.Item('Inventor User Defined Properties') }
function Unit($op) { if ($op.unit -eq 'deg') { return 'deg' } elseif ($op.unit -eq 'in') { return 'in' } else { return 'mm' } }

# -- verbs ---------------------------------------------------------------------------------------------------------

function Do-ActivePath { if ($app.ActiveDocument) { return @{ path = $app.ActiveDocument.FullFileName } } else { return @{ path = $null } } }

function TypeName($t) { switch ($t) { 12290 { 'part' } 12291 { 'assembly' } 12292 { 'drawing' } default { 'other' } } }

function Do-Session {
    $docs = @(); foreach ($d in $app.Documents) { $docs += [ordered]@{ title = $d.DisplayName; path = $d.FullFileName; type = (TypeName $d.DocumentType); unsaved = $d.Dirty } }
    $active = $null; if ($app.ActiveDocument) { $active = $app.ActiveDocument.DisplayName }
    return [ordered]@{ running = $true; program = 'Autodesk Inventor'; version = $app.SoftwareVersion.DisplayVersion; active = $active; documents = $docs }
}

function Do-Document($a) {
    switch ($a.action) {
        'open' { $d = Open-Doc $a.path; return [ordered]@{ opened = $d.FullFileName; title = $d.DisplayName; type = (TypeName $d.DocumentType) } }
        'save' { $d = Open-Doc $a.path; $d.Save(); return @{ saved = $d.FullFileName } }
        'save_as' { $d = Open-Doc $a.path; $d.SaveAs([string]$a.target, $false); return [ordered]@{ savedAs = $a.target; note = "The open document is now $($a.target)." } }
        'close' {
            $d = Open-Doc $a.path
            if ($d.Dirty -and -not $a.save -and -not $a.discard) { throw "$($d.DisplayName) has unsaved changes: close it with save: true or discard: true" }
            if ($a.save) { $d.Save() }
            $d.Close($true); return @{ closed = $true }
        }
    }
    throw "unknown action $($a.action)"
}

function Do-Export($a) {
    $files = @(); $problems = @()
    $paths = @($a.paths); if ($paths.Count -eq 0) { $paths = @($null) }
    foreach ($p in $paths) {
        $d = Open-Doc $p
        $stem = Join-Path $a.folder ([IO.Path]::GetFileNameWithoutExtension($d.FullFileName))
        foreach ($f in $a.formats) {
            $target = $stem + $f.ext
            if (Test-Path -LiteralPath $target) { $problems += "$([IO.Path]::GetFileName($target)) already exists: not replaced"; continue }
            try { $d.SaveAs($target, $true); $files += $target } catch { $problems += "$($d.DisplayName) -> $($f.name): $($_.Exception.Message)" }
        }
    }
    return [ordered]@{ files = $files; problems = $problems }
}

function Do-Inspect($a) {
    $d = Open-Doc $a.path; $script:doc = $d
    $sections = @($a.sections)
    $report = [ordered]@{ title = $d.DisplayName; path = $d.FullFileName; type = (TypeName $d.DocumentType); unsaved = $d.Dirty }
    if ($d.DocumentType -eq $kDrawing) {
        $report.sheets = @(); foreach ($s in $d.Sheets) { $report.sheets += [ordered]@{ name = $s.Name; views = $s.DrawingViews.Count } }
        return $report
    }
    $cd = $d.ComponentDefinition
    try { $box = $cd.RangeBox; $report.sizeMm = [ordered]@{ x = [Math]::Round(($box.MaxPoint.X - $box.MinPoint.X) * 10, 3); y = [Math]::Round(($box.MaxPoint.Y - $box.MinPoint.Y) * 10, 3); z = [Math]::Round(($box.MaxPoint.Z - $box.MinPoint.Z) * 10, 3) } } catch { }
    if ($sections -contains 'parameters' -or $sections -contains 'dimensions' -or $sections -contains 'equations') {
        $report.parameters = @()
        foreach ($p in $cd.Parameters) { $report.parameters += [ordered]@{ name = $p.Name; expression = $p.Expression; units = $p.Units } }
    }
    if ($sections -contains 'features' -and $d.DocumentType -eq $kPart) {
        $report.features = @(); foreach ($f in $cd.Features) { $report.features += [ordered]@{ name = $f.Name; suppressed = $f.Suppressed } }
    }
    if ($sections -contains 'properties') {
        $props = [ordered]@{}; foreach ($p in (UserProps)) { $props[$p.Name] = [string]$p.Value }
        $dt = $d.PropertySets.Item('Design Tracking Properties')
        $report.properties = [ordered]@{ partNumber = $dt.Item('Part Number').Value; description = $dt.Item('Description').Value; custom = $props }
    }
    if ($sections -contains 'mass') {
        $mp = $cd.MassProperties; $c = $mp.CenterOfMass
        $mat = $null; try { $mat = $d.ActiveMaterial.DisplayName } catch { try { $mat = $cd.Material.Name } catch { } }
        $report.mass = [ordered]@{ massKg = [Math]::Round($mp.Mass, 5); volumeMm3 = [Math]::Round($mp.Volume * 1000, 2); surfaceAreaMm2 = [Math]::Round($mp.Area * 100, 2); centerOfMassMm = @(($c.X * 10), ($c.Y * 10), ($c.Z * 10)); material = $mat }
    }
    if ($d.DocumentType -eq $kAssembly) {
        if ($sections -contains 'components') {
            $bom = @{}; foreach ($o in $cd.Occurrences.AllLeafOccurrences) { if (-not $o.Suppressed) { $k = [IO.Path]::GetFileName($o.Definition.Document.FullFileName); $bom[$k] = 1 + [int]$bom[$k] } }
            $top = @(); foreach ($o in $cd.Occurrences) { $top += [ordered]@{ name = $o.Name; file = [IO.Path]::GetFileName($o.Definition.Document.FullFileName); grounded = $o.Grounded; suppressed = $o.Suppressed } }
            $report.components = [ordered]@{ bom = $bom; topLevel = $top }
        }
        if ($sections -contains 'mates') {
            $report.mates = @(); foreach ($c in $cd.Constraints) { $report.mates += [ordered]@{ name = $c.Name; suppressed = $c.Suppressed } }
        }
        if ($sections -contains 'interference') {
            $set = $app.TransientObjects.CreateObjectCollection(); foreach ($o in $cd.Occurrences) { $set.Add($o) }
            $res = $cd.AnalyzeInterference($set)
            $report.interference = @(); foreach ($i in $res) { $report.interference += [ordered]@{ a = $i.OccurrenceOne.Name; b = $i.OccurrenceTwo.Name; volumeMm3 = [Math]::Round($i.Volume * 1000, 3) } }
        }
    }
    if ($sections -contains 'selection') { $report.selection = [ordered]@{ count = $d.SelectSet.Count } }
    return $report
}

function Edit-Op($op) {
    $cd = $doc.ComponentDefinition
    switch ($op.op) {
        { $_ -in 'set_dimension', 'set_global' } { $p = $cd.Parameters.Item([string]$op.name); $old = $p.Expression; $p.Expression = "$([double]$op.value) $(Unit $op)"; return "$($op.name): $old -> $($p.Expression)" }
        'add_global' { [void]$cd.Parameters.UserParameters.AddByExpression([string]$op.name, "$([double]$op.value) mm", 'mm'); return "added $($op.name)" }
        'suppress' { $cd.Features.Item([string]$op.feature).Suppressed = $true; return "$($op.feature) suppressed" }
        'unsuppress' { $cd.Features.Item([string]$op.feature).Suppressed = $false; return "$($op.feature) unsuppressed" }
        'rename_feature' { $cd.Features.Item([string]$op.feature).Name = [string]$op.newName; return "$($op.feature) -> $($op.newName)" }
        'delete_feature' { $cd.Features.Item([string]$op.feature).Delete(); return "deleted $($op.feature)" }
        'set_property' { $ps = UserProps; try { $ps.Item([string]$op.name).Value = [string]$op.value } catch { [void]$ps.Add([string]$op.value, [string]$op.name) }; return "$($op.name) = $($op.value)" }
        'delete_property' { (UserProps).Item([string]$op.name).Delete(); return "deleted $($op.name)" }
        'set_material' {
            $asset = $null
            try { $asset = $doc.MaterialAssets.Item([string]$op.material) } catch { $asset = $app.ActiveMaterialLibrary.MaterialAssets.Item([string]$op.material).CopyTo($doc) }
            $doc.ActiveMaterial = $asset; return "material $($op.material)"
        }
        'suppress_component' { $cd.Occurrences.ItemByName([string]$op.component).Suppress(); return "$($op.component) suppressed" }
        'unsuppress_component' { $cd.Occurrences.ItemByName([string]$op.component).Unsuppress(); return "$($op.component) unsuppressed" }
        'undo' { $n = 1; if ($op.steps) { $n = [int]$op.steps }; for ($i = 0; $i -lt $n; $i++) { $app.TransactionManager.UndoTransaction() }; return "undid $n step(s)" }
        'rebuild' { $doc.Rebuild(); return 'rebuilt' }
    }
    throw "unknown op $($op.op)"
}

function Do-Edit($a) { $script:doc = Open-Doc $a.path; $r = Run-Ops $a.ops ${function:Edit-Op}; return Finish ([ordered]@{ results = $r }) $a.save }

function Plane-Of($cd, $on) {
    $key = ([string]$on).ToLower().Replace(' plane', '')
    if ($PlaneIndex.ContainsKey($key)) { return $cd.WorkPlanes.Item($PlaneIndex[$key]) }
    return $cd.WorkPlanes.Item([string]$on)
}

function Add-SketchEntity($sk, $e) {
    switch ($e.type) {
        'line' { return $sk.SketchLines.AddByTwoPoints((Pt2 $e.a), (Pt2 $e.b)) }
        'centerline' { $l = $sk.SketchLines.AddByTwoPoints((Pt2 $e.a), (Pt2 $e.b)); $l.Centerline = $true; return $l }
        'circle' { return $sk.SketchCircles.AddByCenterRadius((Pt2 @($e.cx, $e.cy)), (Cm ([double]$e.d / 2))) }
        'arc' { return $sk.SketchArcs.AddByThreePoints((Pt2 $e.a), (Pt2 $e.mid), (Pt2 $e.b)) }
        'point' { return $sk.SketchPoints.Add((Pt2 $e.at), $false) }
        'rect' {
            $x = [double]$e.cx; $y = [double]$e.cy
            $lines = $sk.SketchLines.AddAsTwoPointCenteredRectangle((Pt2 @($x, $y)), (Pt2 @(($x + [double]$e.w / 2), ($y + [double]$e.h / 2))))
            return $lines.Item(1)
        }
        'polygon' {
            $pts = @($e.points); $first = $sk.SketchLines.AddByTwoPoints((Pt2 $pts[0]), (Pt2 $pts[1])); $prev = $first
            for ($i = 2; $i -lt $pts.Count; $i++) { $prev = $sk.SketchLines.AddByTwoPoints($prev.EndSketchPoint, (Pt2 $pts[$i])) }
            [void]$sk.SketchLines.AddByTwoPoints($prev.EndSketchPoint, $first.StartSketchPoint)
            return $first
        }
        'spline' {
            $col = $app.TransientObjects.CreateObjectCollection(); foreach ($p in $e.points) { $col.Add((Pt2 $p)) }
            return $sk.SketchSplines.Add($col)
        }
        'slot' {
            $ax = [double]$e.a[0]; $ay = [double]$e.a[1]; $bx = [double]$e.b[0]; $by = [double]$e.b[1]; $w = [double]$e.width / 2
            $len = [Math]::Sqrt(($bx - $ax) * ($bx - $ax) + ($by - $ay) * ($by - $ay)); $nx = - ($by - $ay) / $len * $w; $ny = ($bx - $ax) / $len * $w
            $l1 = $sk.SketchLines.AddByTwoPoints((Pt2 @(($ax + $nx), ($ay + $ny))), (Pt2 @(($bx + $nx), ($by + $ny))))
            $l2 = $sk.SketchLines.AddByTwoPoints((Pt2 @(($bx - $nx), ($by - $ny))), (Pt2 @(($ax - $nx), ($ay - $ny))))
            [void]$sk.SketchArcs.AddByCenterStartEndPoint((Pt2 @($bx, $by)), $l2.StartSketchPoint, $l1.EndSketchPoint, $true)
            [void]$sk.SketchArcs.AddByCenterStartEndPoint((Pt2 @($ax, $ay)), $l1.StartSketchPoint, $l2.EndSketchPoint, $true)
            return $l1
        }
    }
    throw "unknown entity type $($e.type)"
}

function New-Sketch($cd, $on, $name) {
    if ($on -is [array]) { throw 'Inventor sketches here go on planes (Front/Top/Right or a work plane name); use run_script to sketch on a face.' }
    $sk = $cd.Sketches.Add((Plane-Of $cd $on))
    if ($name) { $sk.Name = [string]$name }
    return $sk
}

function Do-Sketch($a) {
    $script:doc = Open-Doc $a.path; $cd = $doc.ComponentDefinition; $s = $a.sketch
    $sk = New-Sketch $cd $s.on $s.name
    $made = @(); $problems = @()
    foreach ($e in $s.entities) { $made += (Add-SketchEntity $sk $e) }
    $gc = $sk.GeometricConstraints
    foreach ($c in @($s.constraints)) {
        if (-not $c) { continue }
        try {
            $e1 = $made[[int]$c.entities[0]]; $e2 = $null; if (@($c.entities).Count -gt 1) { $e2 = $made[[int]$c.entities[1]] }
            switch ($c.type) {
                'horizontal' { [void]$gc.AddHorizontal($e1) } 'vertical' { [void]$gc.AddVertical($e1) }
                'parallel' { [void]$gc.AddParallel($e1, $e2) } 'perpendicular' { [void]$gc.AddPerpendicular($e1, $e2) }
                'tangent' { [void]$gc.AddTangent($e1, $e2) } 'concentric' { [void]$gc.AddConcentric($e1, $e2) }
                'collinear' { [void]$gc.AddCollinear($e1, $e2) } 'fix' { [void]$gc.AddGround($e1) }
                'coincident' { [void]$gc.AddCoincident($e1.EndSketchPoint, $e2) }
                'equal' { if ($e1.Type -eq $e2.Type -and $e1.Radius) { [void]$gc.AddEqualRadius($e1, $e2) } else { [void]$gc.AddEqualLength($e1, $e2) } }
                default { throw "unknown constraint $($c.type)" }
            }
        } catch { $problems += "constraint $($c.type): $($_.Exception.Message)" }
    }
    $dc = $sk.DimensionConstraints
    foreach ($d in @($s.dimensions)) {
        if (-not $d) { continue }
        try {
            if ($null -ne $d.entity) {
                $e = $made[[int]$d.entity]
                try { $r = $e.Radius; $dim = $dc.AddDiameter($e, $e.CenterSketchPoint.Geometry) } catch { $dim = $dc.AddTwoPointDistance($e.StartSketchPoint, $e.EndSketchPoint, 19202, $e.StartSketchPoint.Geometry) }
                $dim.Parameter.Expression = "$([double]$d.value) mm"
            } else {
                $e1 = $made[[int]$d.entities[0]]; $e2 = $made[[int]$d.entities[1]]
                if ($d.type -eq 'angle') { $dim = $dc.AddTwoLineAngle($e1, $e2, $e1.EndSketchPoint.Geometry); $dim.Parameter.Expression = "$([double]$d.value) deg" }
                else { $dim = $dc.AddTwoPointDistance($e1.StartSketchPoint, $e2.StartSketchPoint, 19202, $e2.StartSketchPoint.Geometry); $dim.Parameter.Expression = "$([double]$d.value) mm" }
            }
        } catch { $problems += "dimension: $($_.Exception.Message)" }
    }
    $report = [ordered]@{ sketch = $sk.Name; entities = $made.Count; problems = $problems }
    return Finish $report $a.save
}

function Profile-For($cd, $f) {
    if ($f.sketch) { $sk = $cd.Sketches.Item([string]$f.sketch) }
    elseif ($null -ne $f.on -and $f.profile) {
        $sk = New-Sketch $cd $f.on $null
        foreach ($shape in $f.profile) {
            foreach ($k in 'rect', 'circle', 'polygon', 'centerline', 'line') {
                if ($null -ne $shape.$k) {
                    $e = $shape.$k
                    if ($k -eq 'polygon') { $e = [pscustomobject]@{ type = 'polygon'; points = $shape.$k } }
                    elseif ($k -in 'centerline', 'line') { $e = [pscustomobject]@{ type = $k; a = $shape.$k[0]; b = $shape.$k[1] } }
                    else { $e | Add-Member -NotePropertyName type -NotePropertyValue $k -Force }
                    [void](Add-SketchEntity $sk $e)
                }
            }
            if ($shape.type) { [void](Add-SketchEntity $sk $shape) }
        }
    } else { throw 'give sketch (a sketch name) or on + profile' }
    return $sk
}

function Edges-Near($body, $points) {
    $ec = $app.TransientObjects.CreateEdgeCollection()
    if (-not $points) { foreach ($e in $body.Edges) { $ec.Add($e) }; return $ec }
    foreach ($p in $points) {
        $target = $app.TransientGeometry.CreatePoint((Cm $p[0]), (Cm $p[1]), (Cm $p[2])); $best = $null; $bestD = [double]::MaxValue
        foreach ($e in $body.Edges) {
            $cands = @($e.PointOnEdge); try { $cands += $e.StartVertex.Point; $cands += $e.StopVertex.Point } catch { }
            foreach ($c in $cands) { $dd = Dist $c $target; if ($dd -lt $bestD) { $bestD = $dd; $best = $e } }
        }
        if ($best) { $ec.Add($best) }
    }
    return $ec
}

function Feature-Op($f) {
    $cd = $doc.ComponentDefinition; $feats = $cd.Features
    switch ($f.op) {
        { $_ -in 'extrude', 'cut' } {
            $sk = Profile-For $cd $f; $prof = $sk.Profiles.AddForSolid()
            $op = $kJoin; if ($f.op -eq 'cut') { $op = $kCut }
            $def = $feats.ExtrudeFeatures.CreateExtrudeDefinition($prof, $op)
            $dir = $kPositive; if ($f.reverse -or ($f.op -eq 'cut' -and -not $f.midPlane)) { $dir = $kNegative }; if ($f.midPlane) { $dir = $kSymmetric }
            if ($f.depth -eq 'through_all') { $def.SetThroughAllExtent($kSymmetric) } else { $def.SetDistanceExtent("$([double]$f.depth) mm", $dir) }
            $feat = $feats.ExtrudeFeatures.Add($def)
            if ($f.name) { $feat.Name = [string]$f.name }
            return $feat.Name
        }
        'revolve' {
            $sk = Profile-For $cd $f; $prof = $sk.Profiles.AddForSolid()
            $axis = $null; foreach ($l in $sk.SketchLines) { if ($l.Centerline) { $axis = $l; break } }
            if (-not $axis) { throw 'the sketch needs a centerline as the axis' }
            $op = $kJoin; if ($f.cut) { $op = $kCut }
            $angle = 360; if ($f.angle) { $angle = [double]$f.angle }
            if ($angle -ge 360) { $feat = $feats.RevolveFeatures.AddFull($prof, $axis, $op) } else { $feat = $feats.RevolveFeatures.AddByAngle($prof, $axis, "$angle deg", $kPositive, $op) }
            if ($f.name) { $feat.Name = [string]$f.name }
            return $feat.Name
        }
        'fillet' { $ec = Edges-Near $cd.SurfaceBodies.Item(1) $f.edgesNear; $feat = $feats.FilletFeatures.AddSimple($ec, "$([double]$f.radius) mm"); return "$($feat.Name) on $($ec.Count) edges" }
        'chamfer' { $ec = Edges-Near $cd.SurfaceBodies.Item(1) $f.edgesNear; $feat = $feats.ChamferFeatures.AddUsingDistance($ec, "$([double]$f.distance) mm"); return "$($feat.Name) on $($ec.Count) edges" }
        'shell' {
            $faces = $app.TransientObjects.CreateFaceCollection()
            if ($f.openAt) {
                $target = $app.TransientGeometry.CreatePoint((Cm $f.openAt[0]), (Cm $f.openAt[1]), (Cm $f.openAt[2])); $best = $null; $bestD = [double]::MaxValue
                foreach ($fc in $cd.SurfaceBodies.Item(1).Faces) { $dd = Dist $fc.PointOnFace $target; if ($dd -lt $bestD) { $bestD = $dd; $best = $fc } }
                $faces.Add($best)
            }
            $def = $feats.ShellFeatures.CreateShellDefinition($faces, "$([double]$f.thickness) mm")
            return $feats.ShellFeatures.Add($def).Name
        }
        'pattern_circular' {
            $col = $app.TransientObjects.CreateObjectCollection(); foreach ($n in $f.features) { $col.Add($feats.Item([string]$n)) }
            $axis = $cd.WorkAxes.Item($AxisIndex[([string]$f.axis).ToLower()])
            $angle = 360; if ($f.angle) { $angle = [double]$f.angle }
            return $feats.CircularPatternFeatures.Add($col, $axis, $true, [int]$f.count, "$angle deg").Name
        }
        'delete' { $feats.Item([string]$f.feature).Delete(); return "deleted $($f.feature)" }
    }
    throw "unknown op $($f.op)"
}

function Do-Features($a) { $script:doc = Open-Doc $a.path; $r = Run-Ops $a.ops ${function:Feature-Op}; return Finish ([ordered]@{ results = $r }) $a.save }

function Occ($name) { return $doc.ComponentDefinition.Occurrences.ItemByName([string]$name) }

function Proxy($ref) {
    $occ = Occ $ref.component; $def = $occ.Definition
    if ($ref.plane) { $geo = $def.WorkPlanes.Item($PlaneIndex[([string]$ref.plane).ToLower()]) }
    elseif ($ref.axis) { $geo = $def.WorkAxes.Item($AxisIndex[([string]$ref.axis).ToLower()]) }
    else { throw 'Inventor constraints here use {component, plane: front|top|right} or {component, axis: x|y|z}' }
    $proxy = $null; $occ.CreateGeometryProxy($geo, [ref]$proxy); return $proxy
}

function Assembly-Op($op) {
    $cd = $doc.ComponentDefinition; $tg = $app.TransientGeometry
    switch ($op.op) {
        'insert' {
            $m = $tg.CreateMatrix(); if ($op.position) { $m.SetTranslation($tg.CreateVector((Cm $op.position[0]), (Cm $op.position[1]), (Cm $op.position[2]))) }
            $occ = $cd.Occurrences.Add([string]$op.path, $m)
            if ($op.name) { $occ.Name = [string]$op.name }
            if ($op.fixed) { $occ.Grounded = $true }
            return "inserted $($occ.Name)"
        }
        'mate' {
            $a1 = Proxy $op.a; $b1 = Proxy $op.b; $v = 0.0; if ($op.value) { $v = [double]$op.value }
            switch ($op.type) {
                'coincident' { if ($op.align -eq 'aligned') { $c = $cd.Constraints.AddFlush($a1, $b1, "$v mm") } else { $c = $cd.Constraints.AddMate($a1, $b1, "$v mm") } }
                'distance' { $c = $cd.Constraints.AddMate($a1, $b1, "$v mm") }
                'concentric' { $c = $cd.Constraints.AddMate($a1, $b1, '0 mm') }
                'parallel' { $c = $cd.Constraints.AddAngle($a1, $b1, '0 deg') }
                'perpendicular' { $c = $cd.Constraints.AddAngle($a1, $b1, '90 deg') }
                'angle' { $c = $cd.Constraints.AddAngle($a1, $b1, "$v deg") }
                default { throw "Inventor here supports coincident, distance, concentric (axes), parallel, perpendicular and angle; not $($op.type)" }
            }
            return "constraint $($c.Name)"
        }
        'fix' { (Occ $op.component).Grounded = $true; return "$($op.component) grounded" }
        'float' { (Occ $op.component).Grounded = $false; return "$($op.component) free" }
        'move' { $o = Occ $op.component; $m = $o.Transformation; $m.SetTranslation($tg.CreateVector((Cm $op.position[0]), (Cm $op.position[1]), (Cm $op.position[2]))); $o.Transformation = $m; return "$($op.component) moved" }
        'replace' { (Occ $op.component).Replace([string]$op.path, $false); return "$($op.component) replaced" }
        'delete' { (Occ $op.component).Delete(); return "deleted $($op.component)" }
        'pattern_linear' {
            $src = Occ $op.component; $file = $src.Definition.Document.FullFileName; $base = $src.Transformation.Translation
            $dx = 0; $dy = 0; $dz = 0; switch (([string]$op.direction).ToLower()) { 'x' { $dx = 1 } 'y' { $dy = 1 } 'z' { $dz = 1 } }
            $s = Cm $op.spacing; $names = @()
            for ($i = 1; $i -lt [int]$op.count; $i++) {
                $m = $tg.CreateMatrix(); $m.SetTranslation($tg.CreateVector(($base.X + $dx * $s * $i), ($base.Y + $dy * $s * $i), ($base.Z + $dz * $s * $i)))
                $names += $cd.Occurrences.Add($file, $m).Name
            }
            return "copies: $($names -join ', ')"
        }
    }
    throw "unknown op $($op.op)"
}

function Do-Assembly($a) {
    $script:doc = Open-Doc $a.path
    if ($doc.DocumentType -ne $kAssembly) { throw 'assembly_ops works on an assembly (.iam)' }
    $r = Run-Ops $a.ops ${function:Assembly-Op}; return Finish ([ordered]@{ results = $r }) $a.save
}

function Do-Drawing($a) {
    $model = Open-Doc $a.model
    $dwg = $app.Documents.Add($kDrawing, $app.FileManager.GetTemplateFile($kDrawing), $true)
    $sheet = $dwg.Sheets.Item(1); $tg = $app.TransientGeometry; $w = $sheet.Width; $h = $sheet.Height; $log = @()
    $base = $sheet.DrawingViews.AddBaseView($model, $tg.CreatePoint2d(($w * 0.3), ($h * 0.6)), 1.0, $Orient.front, $kHiddenLine)
    $right = $w * 0.65; $below = $h * 0.25; if ($a.firstAngle) { $right = $w * 0.05; $below = $h * 0.9 }
    [void]$sheet.DrawingViews.AddProjectedView($base, $tg.CreatePoint2d($right, ($h * 0.6)), $kHiddenLine)
    [void]$sheet.DrawingViews.AddProjectedView($base, $tg.CreatePoint2d(($w * 0.3), $below), $kHiddenLine)
    if ($a.isometric) { [void]$sheet.DrawingViews.AddProjectedView($base, $tg.CreatePoint2d(($w * 0.75), ($h * 0.3)), 32259) }
    if ($a.dimensions) { try { [void]$sheet.DrawingDimensions.GeneralDimensions.Retrieve($base); $log += 'model dimensions retrieved' } catch { $log += "dimensions: $($_.Exception.Message)" } }
    $dwg.SaveAs([string]$a.output, $false)
    $report = [ordered]@{ saved = $a.output; model = $model.FullFileName; views = $sheet.DrawingViews.Count; log = $log }
    if ($a.pdf) { $pdf = [IO.Path]::ChangeExtension([string]$a.output, '.pdf'); try { $dwg.SaveAs($pdf, $true); $report.pdf = $pdf } catch { $report.pdf = "PDF export failed: $($_.Exception.Message)" } }
    return $report
}

function Do-Views($a) {
    $d = Open-Doc $a.path; $d.Activate()
    $view = $app.ActiveView; $images = @(); $notes = @(); $i = 0
    foreach ($v in $a.views) {
        try {
            if ($v -ne 'current') { $cam = $view.Camera; $cam.ViewOrientationType = $Orient[$v]; $cam.Fit(); $cam.Apply() }
            $i++; $target = Join-Path $a.folder ("{0:00}-{1}.png" -f $i, $v)
            $view.SaveAsBitmap($target, 1280, 960); $images += $target
        } catch { $notes += "$v : $($_.Exception.Message)" }
    }
    return [ordered]@{ document = $d.DisplayName; images = $images; log = $notes }
}

function Do-Script($a) {
    if ($a.path) { $script:doc = Open-Doc $a.path } elseif ($app.ActiveDocument) { $script:doc = $app.ActiveDocument }
    $out = & ([ScriptBlock]::Create([string]$a.code)) 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $env:MEDIATOR_RESULT)) { Write-Result ([ordered]@{ ok = $true; output = $out.Trim() }) }
}

try {
    $app = Get-CadApp 'Inventor.Application'
    $doc = $null
    $result = switch ($MediatorArgs.verb) {
        'active_path' { Do-ActivePath }
        'session' { Do-Session }
        'document' { Do-Document $MediatorArgs }
        'export' { Do-Export $MediatorArgs }
        'inspect' { Do-Inspect $MediatorArgs }
        'edit' { Do-Edit $MediatorArgs }
        'sketch' { Do-Sketch $MediatorArgs }
        'model_features' { Do-Features $MediatorArgs }
        'assembly_ops' { Do-Assembly $MediatorArgs }
        'drawing' { Do-Drawing $MediatorArgs }
        'views' { Do-Views $MediatorArgs }
        'script' { Do-Script $MediatorArgs; $null }
        default { throw "Inventor cannot do '$($MediatorArgs.verb)' here" }
    }
    if ($null -ne $result) { Write-Result $result }
} catch {
    Write-Result ([ordered]@{ error = $_.Exception.Message; at = $_.InvocationInfo.PositionMessage; log = @($script:Log) })
    exit 1
}
