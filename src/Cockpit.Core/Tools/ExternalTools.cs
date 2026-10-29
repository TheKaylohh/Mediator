using System.Diagnostics;
using System.Text;
using Cockpit.Core.Jobs;
using Cockpit.Core.Platform;
using Cockpit.Protocol;

namespace Cockpit.Core.Tools;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Tail(int max = 1500)
    {
        string all = (Stdout + "\n" + Stderr).Trim();
        return all.Length <= max ? all : "…" + all[^max..];
    }
}

/// <summary>Runs a helper program (ffprobe, blender, python) to completion. Pluggable so providers are testable offline.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Real runner: stdio captured (capped at 4 MiB each), the process placed in a kill-on-close job object, and its
/// whole tree killed on timeout or cancellation. Billing variables are removed: none of these helpers need them.
/// </summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    private const int Cap = 4 * 1024 * 1024;
    public static readonly DefaultProcessRunner Instance = new();

    public async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardOutputEncoding ??= Utf8.NoBom;
        psi.StandardErrorEncoding ??= Utf8.NoBom;
        foreach (var v in CliLaunchBuilder.BillingVariables) psi.Environment.Remove(v);

        using var group = new ChildProcessJob();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        group.Assign(p);
        var stdout = Capture(p.StandardOutput);
        var stderr = Capture(p.StandardError);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            group.TerminateAll();
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!timedOut) throw;
        }
        return new ProcessResult(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), timedOut);
    }

    private static async Task<string> Capture(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            if (sb.Length < Cap) sb.Append(buffer, 0, Math.Min(n, Cap - sb.Length));
        return sb.ToString();
    }
}

/// <summary>Finds helper programs: PATH first, then their usual Windows install folders. Null when absent.</summary>
public static class ToolLocator
{
    public static string? Ffprobe() => CliLaunchBuilder.ResolveOnPath("ffprobe");
    public static string? Ffmpeg() => CliLaunchBuilder.ResolveOnPath("ffmpeg");

    /// <summary>`blender` on PATH, else the newest C:\Program Files\Blender Foundation\Blender x.y\blender.exe.</summary>
    public static string? Blender()
    {
        if (CliLaunchBuilder.ResolveOnPath("blender") is string onPath) return onPath;
        if (!OperatingSystem.IsWindows()) return null;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, "Blender*")
            .Select(d => Path.Combine(d, "blender.exe")).Where(File.Exists)
            .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p))!.Replace("Blender", "", StringComparison.OrdinalIgnoreCase).Trim(), out var v) ? v : new Version(0, 0))
            .FirstOrDefault();
    }

    /// <summary>Windows: the `py -3` launcher, else python.exe; elsewhere python3. The Microsoft Store alias stub is skipped.</summary>
    public static (string Exe, string[] Prefix)? Python()
    {
        if (OperatingSystem.IsWindows())
        {
            if (CliLaunchBuilder.ResolveOnPath("py") is string py) return (py, ["-3"]);
            if (CliLaunchBuilder.ResolveOnPath("python") is string python && !python.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return (python, []);
            return null;
        }
        return CliLaunchBuilder.ResolveOnPath("python3") is string p3 ? (p3, []) : null;
    }
}

/// <summary>Runs a Python script for a tool (build123d, PyMuPDF, the Resolve scripting API).</summary>
public interface IPythonRunner
{
    bool IsAvailable { get; }
    string Describe { get; }
    /// <summary>The path the script sees for a Windows path (identity locally; /mnt/... inside WSL). Null if not reachable.</summary>
    string? MapPath(string hostPath);
    Task<ProcessResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken ct);
}

public sealed class LocalPythonRunner(string? executable, IReadOnlyList<string> prefix, IProcessRunner runner) : IPythonRunner
{
    public static LocalPythonRunner Detect(IProcessRunner? runner = null)
    {
        if (Environment.GetEnvironmentVariable("COCKPIT_PYTHON") is { Length: > 0 } custom) return new(custom, [], runner ?? DefaultProcessRunner.Instance);
        var found = ToolLocator.Python();
        return new(found?.Exe, found?.Prefix ?? [], runner ?? DefaultProcessRunner.Instance);
    }

    public bool IsAvailable => executable is not null;
    public string? Executable => executable;
    public string Describe => executable is null ? "no Python found (install Python 3 or set COCKPIT_PYTHON)" : $"{executable} {string.Join(' ', prefix)}".Trim();
    public string? MapPath(string hostPath) => Path.GetFullPath(hostPath);

    public Task<ProcessResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable ?? throw new InvalidOperationException(Describe)) { WorkingDirectory = Path.GetDirectoryName(scriptPath)! };
        foreach (var a in prefix.Append("-I").Append(scriptPath).Concat(args)) psi.ArgumentList.Add(a); // -I: isolated, ignores user site and cwd
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        return runner.RunAsync(psi, timeout, ct);
    }
}

/// <summary>
/// Runs Python inside the restricted WSL distro (automount off), where only the exchange folder is visible:
/// scripts and outputs must live under <paramref name="windowsExchange"/>, seen as <paramref name="linuxExchange"/>.
/// This is the recommended runner for Code-CAD: model-chosen parameters execute away from your Windows files.
/// </summary>
public sealed class WslPythonRunner(string distro, string windowsExchange, string linuxExchange, IProcessRunner runner) : IPythonRunner
{
    private readonly string _win = JobsPath(windowsExchange);

    private static string JobsPath(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;

    public bool IsAvailable => (OperatingSystem.IsWindows() ? File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")) : CliLaunchBuilder.ResolveOnPath("wsl") is not null);
    public string Describe => $"python3 in WSL distro '{distro}' ({linuxExchange})";

    public string? MapPath(string hostPath)
    {
        string full = Path.GetFullPath(hostPath);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(_win, cmp)) return null;
        string rel = full.Length > _win.Length ? full[_win.Length..] : "";
        return linuxExchange.TrimEnd('/') + "/" + rel.Replace('\\', '/');
    }

    public Task<ProcessResult> RunScriptAsync(string scriptPath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken ct)
    {
        string script = MapPath(scriptPath) ?? throw new InvalidOperationException($"{scriptPath} is not under the WSL exchange folder {_win}.");
        var psi = new ProcessStartInfo("wsl.exe");
        foreach (var a in new[] { "-d", distro, "--", "env", "PYTHONIOENCODING=utf-8" }
                     .Concat((environment ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key}={kv.Value}"))
                     .Concat(["python3", "-I", script]).Concat(args))
            psi.ArgumentList.Add(a);
        return runner.RunAsync(psi, timeout, ct);
    }
}

/// <summary>Runs Blender in background mode (`blender -b ...`).</summary>
public interface IBlenderRunner
{
    bool IsAvailable { get; }
    string Describe { get; }
    Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

public sealed class BlenderRunner(string? executable, IProcessRunner runner) : IBlenderRunner
{
    public static BlenderRunner Detect(IProcessRunner? runner = null) =>
        new(Environment.GetEnvironmentVariable("COCKPIT_BLENDER") is { Length: > 0 } b ? b : ToolLocator.Blender(), runner ?? DefaultProcessRunner.Instance);

    public bool IsAvailable => executable is not null;
    public string Describe => executable ?? "Blender not found (install it or set COCKPIT_BLENDER)";

    public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable ?? throw new InvalidOperationException(Describe));
        foreach (var a in args) psi.ArgumentList.Add(a);
        return runner.RunAsync(psi, timeout, ct);
    }
}

/// <summary>File names that never overwrite: "edits/timeline.fcpxml" → "edits/timeline-2.fcpxml" if taken.</summary>
public static class SafeFiles
{
    public static string Unique(string folder, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(folder, fileName);
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++) candidate = Path.Combine(folder, $"{stem}-{i}{ext}");
        return candidate;
    }

    /// <summary>A file-name-safe version of user or model text.</summary>
    public static string Slug(string text, string fallback)
    {
        var sb = new StringBuilder();
        foreach (char c in text.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : c == ' ' ? '_' : '\0');
        string s = sb.ToString().Replace("\0", "", StringComparison.Ordinal);
        if (s.Length > 60) s = s[..60];
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>Writes a new file (fails if it exists).</summary>
    public static void WriteNew(string path, string content)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var w = new StreamWriter(fs, Utf8.NoBom);
        w.Write(content);
    }
}
