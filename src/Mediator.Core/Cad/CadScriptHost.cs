using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mediator.Core.Cad;

/// <summary>How a CAD program's scripts are run: the interpreter, the arguments before the script path, and the file extension.</summary>
/// <param name="Executable">powershell.exe, FreeCADCmd.exe, run_journal.exe, python3 (tests)...</param>
/// <param name="PrefixArgs">Arguments before the script path (e.g. -NoProfile -ExecutionPolicy Bypass -File).</param>
/// <param name="SuffixArgs">Arguments after the script path.</param>
/// <param name="Extension">".ps1", ".py".</param>
/// <param name="Utf8Bom">Write the script with a UTF-8 BOM (Windows PowerShell 5.1 reads BOM-less files in the ANSI code page).</param>
public sealed record CadScriptRunner(string Executable, IReadOnlyList<string> PrefixArgs, IReadOnlyList<string> SuffixArgs, string Extension, bool Utf8Bom = false);

/// <summary>What a script run produced.</summary>
/// <param name="Result">The JSON object the script wrote to its result file, or null when it wrote none.</param>
public sealed record CadScriptOutput(JsonObject? Result, int ExitCode, string StdoutTail, string StderrTail, bool TimedOut);

/// <summary>
/// V17: runs one CAD automation script as a child process: no console window, no shell, stdin closed, the whole process tree
/// killed on timeout or cancellation. The script reads its arguments from the JSON file named by MEDIATOR_ARGS and writes
/// its answer as a JSON object to MEDIATOR_RESULT (CAD programs print plenty to stdout, so stdout is only kept as a tail
/// for diagnostics). Each run gets its own folder under the temp directory, deleted afterwards (MEDIATOR_WORK).
/// </summary>
public static class CadScriptHost
{
    private const int TailChars = 4000;

    public static async Task<CadScriptOutput> RunAsync(CadScriptRunner runner, string script, JsonObject args, TimeSpan timeout, CancellationToken ct,
        string? workRoot = null)
    {
        string work = Path.Combine(workRoot ?? Path.Combine(Path.GetTempPath(), "Mediator-cad"), Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        try
        {
            string scriptPath = Path.Combine(work, "mediator_script" + runner.Extension);
            string argsPath = Path.Combine(work, "args.json"), resultPath = Path.Combine(work, "result.json");
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(runner.Utf8Bom), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(argsPath, args.ToJsonString(), new UTF8Encoding(false), ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo(runner.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = work,
            };
            foreach (var a in runner.PrefixArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(scriptPath);
            foreach (var a in runner.SuffixArgs) psi.ArgumentList.Add(a);
            psi.Environment["MEDIATOR_ARGS"] = argsPath;
            psi.Environment["MEDIATOR_RESULT"] = resultPath;
            psi.Environment["MEDIATOR_WORK"] = work;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) Append(stdout, e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) Append(stderr, e.Data); };
            try { process.Start(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new CadAppException($"Could not start {Path.GetFileName(runner.Executable)}: {ex.Message}");
            }
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool timedOut = false;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(timeout);
                try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    timedOut = !ct.IsCancellationRequested;
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (TimeoutException) { }
                    ct.ThrowIfCancellationRequested();
                }
            }
            if (!timedOut) process.WaitForExit(); // flushes the redirected streams

            JsonObject? result = null;
            if (File.Exists(resultPath))
            {
                try { result = JsonNode.Parse(await File.ReadAllTextAsync(resultPath, CancellationToken.None).ConfigureAwait(false)) as JsonObject; }
                catch (JsonException ex) { result = new JsonObject { ["error"] = "the script's result is not valid JSON: " + ex.Message }; }
            }
            string Tail(StringBuilder sb) { lock (sb) return sb.Length > TailChars ? sb.ToString(sb.Length - TailChars, TailChars) : sb.ToString(); }
            return new CadScriptOutput(result, timedOut ? -1 : process.ExitCode, Tail(stdout), Tail(stderr), timedOut);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Append(StringBuilder sb, string line)
    {
        sb.AppendLine(line);
        if (sb.Length > TailChars * 4) sb.Remove(0, sb.Length - TailChars * 2);
    }

    /// <summary>
    /// The script's answer as a report: its result object, with the exit code and output tails added when something went
    /// wrong or nothing was returned. Throws <see cref="CadAppException"/> for a timeout.
    /// </summary>
    public static JsonObject ToReport(CadScriptOutput o, string program, TimeSpan timeout)
    {
        if (o.TimedOut) throw new CadAppException($"{program} did not finish within {timeout.TotalSeconds:0} s; the script was stopped. Is a dialog open in {program}?");
        var report = o.Result ?? new JsonObject();
        if (o.Result is null)
        {
            if (o.ExitCode != 0) report["error"] = $"the script ended with exit code {o.ExitCode} and no result";
            else report["ok"] = true;
        }
        if (o.Result is null || report["error"] is not null || o.ExitCode != 0)
        {
            if (o.StdoutTail.Trim().Length > 0) report["output"] = o.StdoutTail.Trim();
            if (o.StderrTail.Trim().Length > 0) report["errors"] = o.StderrTail.Trim();
        }
        return report;
    }
}
