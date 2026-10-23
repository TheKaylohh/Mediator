<#
.SYNOPSIS
  Real-SolidWorks integration procedure for V6. It drives cockpit-swx-worker.exe over its stdin/stdout JSON
  protocol against your running SolidWorks, using disposable copies of verify/fixtures/swx. It never touches
  your own files.

.DESCRIPTION
  Before you start:
    - Start SolidWorks normally (not as administrator).
    - Open ONE document of your own, any part, and leave it unsaved-modified. The procedure checks that it is
      still open and still dirty at the end: the worker must never save or close it.
    - Build the worker against your interop first: scripts\check-windows-build.ps1

  Cases, each with the expected result:
    health                      SolidWorks revision and PID; no hidden instance started
    import valid STEP           plate_solid.step -> new plate_solid.SLDPRT; reopened by part_cards: 1 body, 1 cylinder r=5 mm
    import surface-only         sheet_surface_only.step -> error no_solid_body, nothing saved
    import truncated / empty    import_failed (or com_error), nothing saved
    import existing target      a second import of plate_solid.step -> error exists, the first .SLDPRT unchanged (hash)
    import duplicate names      a\bracket.step + b\bracket.step -> both duplicate_target
    import already open         open shaft_d25.step in SolidWorks first (the script pauses) -> already_open, document untouched
    assembly                    shaft + plate, 1 concentric mate (valid) -> assembly saved; 1 mate between two planes
                                that cannot be concentric (invalid) -> reported mate error, nothing crashes
    worker restart              the worker is killed mid-session; a new one attaches to the SAME SolidWorks PID
    your document               still open, still modified, never saved

  Output: a transcript in docs\v6\swx-integration-<date>.txt. Copy the summary into TEST_RESULTS.md.

  NOT RUN BY ITS AUTHOR: no Windows or SolidWorks host was available. API names used are listed in SPEC.md section 8;
  ISldWorks.GetDocuments (new in V6, used for ownership) is assumed and must be confirmed by this run.
#>
[CmdletBinding()]
param([string]$Worker = (Join-Path $PSScriptRoot '..\dist\cockpit-swx-worker.exe'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $root "docs\v6\swx-integration-$stamp.txt"
$work = Join-Path $env:TEMP "cockpit-swx-$stamp"
New-Item -ItemType Directory -Path $work, "$work\out", "$work\a", "$work\b" | Out-Null
Copy-Item (Join-Path $root 'verify\fixtures\swx\*.step') $work
Copy-Item "$work\plate_solid.step" "$work\a\bracket.step"; Copy-Item "$work\plate_solid.step" "$work\b\bracket.step"
$results = [System.Collections.Generic.List[string]]::new()
function Note($line) { $results.Add($line); Write-Host $line; Add-Content -Path $log -Value $line }

$psi = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path $Worker).Path)
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
$script:p = [System.Diagnostics.Process]::Start($psi)
$script:id = 0
function Call($method, $params) {
    $script:id++
    $req = @{ id = $script:id; method = $method; params = $params; timeoutMs = 300000 } | ConvertTo-Json -Depth 20 -Compress
    $script:p.StandardInput.WriteLine($req); $script:p.StandardInput.Flush()
    $line = $script:p.StandardOutput.ReadLine()
    Add-Content -Path $log -Value ">> $req`n<< $line"
    return $line | ConvertFrom-Json
}
function Expect($ok, $what) { Note ("{0}  {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what) }

$h = Call 'health' @{}
Expect ($null -ne $h.result) "health: $($h.result)"
$pidBefore = ($h.result -split 'PID ')[1].Split(')')[0].Split(',')[0]

$r = Call 'import_step' @{ paths = @("$work\plate_solid.step"); outputFolder = "$work\out" }
$saved = $r.result.results[0].partPath
Expect ($saved -and (Test-Path $saved)) "import valid STEP -> $saved"
$cards = Call 'part_cards' @{ paths = @($saved) }
$cyl = $cards.result.cards[0].faces | Where-Object { $_.kind -eq 'Cylinder' }
Expect ($cyl -and [math]::Abs($cyl[0].radiusM - 0.005) -lt 1e-6) "reopened part has the 5 mm-radius bore"
$hash1 = (Get-FileHash $saved).Hash

foreach ($case in @(@('sheet_surface_only.step', 'no_solid_body'), @('plate_truncated.step', ''), @('empty.step', ''))) {
    $r = Call 'import_step' @{ paths = @("$work\$($case[0])"); outputFolder = "$work\out" }
    $err = $r.result.results[0].error
    $target = Join-Path "$work\out" ([IO.Path]::GetFileNameWithoutExtension($case[0]) + '.SLDPRT')
    Expect ($err -and (-not (Test-Path $target)) -and ($case[1] -eq '' -or $err.StartsWith($case[1]))) "$($case[0]) refused: $err"
}
$r = Call 'import_step' @{ paths = @("$work\plate_solid.step"); outputFolder = "$work\out" }
Expect ($r.result.results[0].error -like 'exists*' -and (Get-FileHash $saved).Hash -eq $hash1) "existing target never overwritten"
$r = Call 'import_step' @{ paths = @("$work\a\bracket.step", "$work\b\bracket.step"); outputFolder = "$work\out" }
Expect (($r.result.results | Where-Object { $_.error -like 'duplicate_target*' }).Count -eq 2) "duplicate target names both refused"

Read-Host "Open $work\shaft_d25.step in SolidWorks now (File > Open), then press Enter"
$r = Call 'import_step' @{ paths = @("$work\shaft_d25.step"); outputFolder = "$work\out" }
Expect ($r.result.results[0].error -like 'already_open*' -or $r.result.results[0].error -like 'returned_existing_document*') "a STEP open in SolidWorks is left alone: $($r.result.results[0].error)"
Read-Host "Check that shaft_d25 is still open in SolidWorks and unchanged; close it yourself, then press Enter"

$r = Call 'import_step' @{ paths = @("$work\shaft_d25.step"); outputFolder = "$work\out" }
$shaft = $r.result.results[0].partPath
Expect ($shaft -and (Test-Path $shaft)) "shaft imported for the assembly test"
Note "INFO  assembly cases: run 'cockpit assemble $work\out --interactive' and approve one concentric mate (expect saved), then a concentric mate on two planar faces (expect a reported mate error)."

$script:p.Kill(); $script:p.WaitForExit()
$script:p = [System.Diagnostics.Process]::Start($psi)
$h2 = Call 'health' @{}
$pidAfter = ($h2.result -split 'PID ')[1].Split(')')[0].Split(',')[0]
Expect ($pidAfter -eq $pidBefore) "restarted worker attached to the same SolidWorks (PID $pidAfter)"
$null = Call 'shutdown' @{}

Read-Host "Check your own document: still open, still modified (asterisk in the title), never saved. Press Enter"
Note ("SUMMARY  {0} pass, {1} fail; transcript {2}" -f ($results | Where-Object { $_ -like 'PASS*' }).Count, ($results | Where-Object { $_ -like 'FAIL*' }).Count, $log)
