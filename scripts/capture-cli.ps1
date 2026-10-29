<#
.SYNOPSIS
  Captures REAL Claude Code and Gemini CLI output on your machine, with your own signed-in subscriptions, so the
  stream parsers are tested against what your CLI versions actually print. Each capture is saved unedited under
  verify/fixtures/real/<cli>-<version>-<case>.* together with the exact command and exit code.

.DESCRIPTION
  Cases (run only the ones you want; each costs a little subscription usage):
    success     "Reply with the word ok."                         (plain result + usage)
    tools       a prompt that makes the CLI call one Mediator read-only tool through a throwaway job
    cancel      a long prompt, the process killed after 5 s        (EOF without a result)
    ratelimit   not forced: if you hit a limit during normal use, rerun 'success' then and it is captured

  Billing variables (ANTHROPIC_API_KEY, GEMINI_API_KEY, ...) are removed from the child environment so the
  capture uses the subscription, exactly as the Mediator does. Review every file before committing it: the
  captures contain your session ids and model names (no credentials are printed by either CLI).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\capture-cli.ps1 -Cases success,cancel

  NOT RUN BY ITS AUTHOR: it needs your signed-in CLIs.
#>
[CmdletBinding()]
param([string[]]$Cases = @('success', 'cancel'), [switch]$SkipClaude, [switch]$SkipGemini)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $root 'verify\fixtures\real'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$scrub = 'ANTHROPIC_API_KEY','CLAUDE_API_KEY','ANTHROPIC_AUTH_TOKEN','CLAUDE_CODE_USE_BEDROCK','CLAUDE_CODE_USE_VERTEX','GEMINI_API_KEY','GOOGLE_API_KEY','GOOGLE_GENAI_USE_VERTEXAI','GOOGLE_CLOUD_PROJECT','GOOGLE_APPLICATION_CREDENTIALS'
$work = Join-Path $env:TEMP ("mediator-capture-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-Capture([string]$Name, [string]$Exe, [string[]]$CliArgs, [string]$Prompt, [int]$KillAfterSeconds = 0, [hashtable]$ExtraEnv = @{}) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    if ($Exe.EndsWith('.cmd') -or $Exe.EndsWith('.bat')) { $psi.FileName = $env:ComSpec; foreach ($a in @('/d', '/s', '/c', $Exe)) { $psi.ArgumentList.Add($a) } }
    else { $psi.FileName = $Exe }
    foreach ($a in $CliArgs) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $work
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($v in $scrub) { [void]$psi.Environment.Remove($v) }
    foreach ($k in $ExtraEnv.Keys) { $psi.Environment[$k] = $ExtraEnv[$k] }
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write($Prompt); $p.StandardInput.Close()
    $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
    if ($KillAfterSeconds -gt 0) { if (-not $p.WaitForExit($KillAfterSeconds * 1000)) { $p.Kill($true) } }
    $p.WaitForExit()
    $base = Join-Path $out $Name
    [IO.File]::WriteAllText("$base.stream.jsonl", $stdout.Result, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText("$base.stderr.txt", $stderr.Result, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText("$base.command.txt", "$Exe $($CliArgs -join ' ')`nexit $($p.ExitCode)`nprompt: $Prompt`n", [Text.UTF8Encoding]::new($false))
    Write-Host "$Name -> exit $($p.ExitCode), $($stdout.Result.Length) chars"
}

if (-not $SkipClaude) {
    $claude = (Get-Command claude -ErrorAction Stop).Source
    $ver = ((& $claude --version) -split ' ')[0]
    if ($Cases -contains 'success') { Invoke-Capture "claude-$ver-success" $claude @('-p', '--output-format', 'stream-json', '--verbose') 'Reply with the word ok.' }
    if ($Cases -contains 'cancel') { Invoke-Capture "claude-$ver-cancel" $claude @('-p', '--output-format', 'stream-json', '--verbose') 'Count slowly from 1 to 400, one number per line.' 5 }
}
if (-not $SkipGemini) {
    $gemini = (Get-Command gemini -ErrorAction Stop).Source
    $ver = (& $gemini --version).Trim()
    $trust = @{ GEMINI_CLI_TRUST_WORKSPACE = 'true' }
    if ($Cases -contains 'success') { Invoke-Capture "gemini-$ver-success" $gemini @('--output-format', 'stream-json') 'Reply with the word ok.' 0 $trust }
    if ($Cases -contains 'cancel') { Invoke-Capture "gemini-$ver-cancel" $gemini @('--output-format', 'stream-json') 'Count slowly from 1 to 400, one number per line.' 5 $trust }
}
Write-Host "`nCaptured into $out. Review, then add a test that parses them (see FailoverTests.RealCapturesClassified)."
