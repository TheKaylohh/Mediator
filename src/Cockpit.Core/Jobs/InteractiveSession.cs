using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cockpit.Protocol;

namespace Cockpit.Core.Jobs;

/// <summary>
/// Sentinel files for a CLI running in a terminal tab. The tab is not our child: `wt.exe` hands it to the running
/// Windows Terminal and exits at once, so its exit says nothing about the session. Instead the generated script
/// writes <c>interactive.pid</c> ("&lt;pid&gt; &lt;start time as UTC file time&gt;") when it starts and
/// <c>interactive.done</c> (the CLI's exit code) in a <c>finally</c> block when the CLI exits.
/// </summary>
public sealed class InteractiveSession
{
    public const string PidFileName = "interactive.pid";
    public const string DoneFileName = "interactive.done";

    public InteractiveSession(string workspaceRoot)
    {
        PidPath = Path.Combine(workspaceRoot, PidFileName);
        DonePath = Path.Combine(workspaceRoot, DoneFileName);
    }

    public string PidPath { get; }
    public string DonePath { get; }

    public static string FormatPid(int pid, DateTime startTimeUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{pid} {startTimeUtc.ToFileTimeUtc()}");

    /// <summary>For tests and non-PowerShell launchers: record a process as the session's terminal.</summary>
    public void WritePid(Process process) =>
        File.WriteAllText(PidPath, FormatPid(process.Id, process.StartTime.ToUniversalTime()), Encoding.ASCII);

    public bool TryReadPid(out int pid, out long startFileTimeUtc)
    {
        pid = 0;
        startFileTimeUtc = 0;
        string? text = TryRead(PidPath);
        if (text is null) return false;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out startFileTimeUtc);
    }

    public bool TryReadDone(out int exitCode)
    {
        exitCode = 0;
        string? text = TryRead(DonePath);
        return text is not null && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exitCode);
    }

    /// <summary>
    /// The recorded terminal process if it is still the same process (PID and start time match within 2 s, so a
    /// recycled PID is never mistaken for it); otherwise null. The caller disposes it.
    /// </summary>
    public Process? FindLiveProcess()
    {
        if (!TryReadPid(out int pid, out long start)) return null;
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.HasExited) { p.Dispose(); return null; }
            var recorded = DateTime.FromFileTimeUtc(start);
            if (Math.Abs((p.StartTime.ToUniversalTime() - recorded).TotalSeconds) > 2) { p.Dispose(); return null; }
            return p;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Ends the terminal's process tree (closing the tab's shell and the CLI in it). Returns false if it was not running.</summary>
    public bool KillTerminal()
    {
        using var p = FindLiveProcess();
        if (p is null) return false;
        try { p.Kill(entireProcessTree: true); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.ASCII).Trim() : null; }
        catch (IOException) { return null; } // the script may be writing it right now; read again next poll
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The PowerShell wrapper for an interactive CLI: sentinel files, billing scrub, working folder, then the CLI.
    /// UTF-8 WITH a BOM: Windows PowerShell 5.1 reads BOM-less scripts as ANSI and would mangle a non-ASCII prompt.
    /// </summary>
    public static string WriteScript(string scriptPath, string workspaceRoot, IEnumerable<string> scrubVariables, bool allowMeteredBilling,
        IReadOnlyDictionary<string, string> setVariables, string workingDirectory, string commandLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$cockpitWs = {Ps(workspaceRoot)}");
        sb.AppendLine("# Sentinel for the Cockpit: this shell's PID and start time (wt.exe exits at once, so the Cockpit watches this instead).");
        sb.AppendLine($"$me = Get-Process -Id $PID");
        sb.AppendLine($"Set-Content -LiteralPath (Join-Path $cockpitWs '{PidFileName}') -Value (\"$PID \" + $me.StartTime.ToUniversalTime().ToFileTimeUtc()) -Encoding ascii");
        sb.AppendLine("$cockpitExit = 1");
        sb.AppendLine("try {");
        if (allowMeteredBilling) sb.AppendLine("    # Metered billing was opted in for this job: API-key variables are left as they are.");
        foreach (var v in scrubVariables) sb.AppendLine(CultureInfo.InvariantCulture, $"    Remove-Item Env:{v} -ErrorAction SilentlyContinue");
        foreach (var (k, v) in setVariables) sb.AppendLine(CultureInfo.InvariantCulture, $"    $env:{k} = {Ps(v)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    Set-Location -LiteralPath {Ps(workingDirectory)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    {commandLine}");
        sb.AppendLine("    $cockpitExit = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }");
        sb.AppendLine("} finally {");
        sb.AppendLine($"    Set-Content -LiteralPath (Join-Path $cockpitWs '{DoneFileName}') -Value $cockpitExit -Encoding ascii");
        sb.AppendLine("}");
        File.WriteAllText(scriptPath, sb.ToString(), Utf8.WithBom);
        return scriptPath;
    }

    /// <summary>PowerShell single-quoted literal: only the quote itself needs escaping.</summary>
    public static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
