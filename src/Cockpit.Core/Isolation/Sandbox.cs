using System.Diagnostics;
using System.Globalization;
using Cockpit.Core.Jobs;
using Cockpit.Core.Platform;
using Cockpit.Core.Tools;

namespace Cockpit.Core.Isolation;

/// <summary>What a runner must provide for a given kind of code.</summary>
public enum IsolationRequirement
{
    /// <summary>Fixed code shipped with Cockpit (templates with numeric parameters). May run on the host interpreter.</summary>
    HostAllowed,
    /// <summary>Model-written code. Runs only inside a restricted sandbox; if none is available the call is refused, never downgraded.</summary>
    RestrictedRequired,
}

/// <summary>Resource limits applied with setrlimit before the sandbox starts (inherited by everything inside).</summary>
public sealed record SandboxLimits
{
    /// <summary>RLIMIT_AS (virtual address space) per process.</summary>
    public long AddressSpaceBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>RLIMIT_CPU seconds per process.</summary>
    public int CpuSeconds { get; init; } = 600;
    /// <summary>RLIMIT_NPROC: processes for the sandbox user (counted per real uid; see ISOLATION.md).</summary>
    public int Processes { get; init; } = 64;
    /// <summary>RLIMIT_FSIZE: largest file any process may write.</summary>
    public long FileSizeBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>RLIMIT_NOFILE.</summary>
    public int OpenFiles { get; init; } = 256;
    /// <summary>Wall-clock limit for the whole run; the process tree is killed when it expires.</summary>
    public TimeSpan Wall { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// One sandboxed run. The process sees: the read-only system (/usr and the interpreter's own prefix), a read-only
/// /in (the approved script and its inputs, staged by Cockpit), a writable /out (a fresh, private staging folder,
/// never the user's folder), a private /tmp, its own /proc and a minimal /dev. Nothing else of the host exists.
/// </summary>
public sealed record SandboxRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<string> ReadOnlyBinds,
    IReadOnlyDictionary<string, string> Environment,
    SandboxLimits Limits);

public interface ISandboxRunner
{
    /// <summary>Stable identifier recorded in approvals and the audit (for example "bwrap-v1").</summary>
    string Id { get; }
    /// <summary>Null if usable; otherwise why not (shown to the user, and the call is refused).</summary>
    string? Unavailable { get; }
    /// <summary>What the boundary enforces, in one line (shown in the approval).</summary>
    string Describe { get; }
    Task<ProcessResult> RunAsync(SandboxRequest request, CancellationToken ct);
}

/// <summary>No sandbox on this machine: every restricted run is refused with the reason.</summary>
public sealed class UnavailableSandbox(string reason) : ISandboxRunner
{
    public string Id => "none";
    public string? Unavailable => reason;
    public string Describe => "no sandbox: " + reason;
    public Task<ProcessResult> RunAsync(SandboxRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("No sandbox is available: " + reason);
}

/// <summary>
/// bubblewrap on Linux (and inside WSL). Enforced:
///  - namespaces: user, mount, pid, net, ipc, uts, cgroup (<c>--unshare-all</c>); no network at all (an empty net namespace);
///  - nested user namespaces disabled (<c>--disable-userns</c>), all capabilities dropped, new session (no TIOCSTI);
///  - file system: only the binds listed in <see cref="SandboxRequest"/>; the rest of the host does not exist;
///  - environment cleared (<c>--clearenv</c>): no API keys, tokens or proxies leak in;
///  - limits via prlimit (address space, CPU, processes, file size, open files, no core dumps);
///  - when Cockpit runs as root, the sandbox first drops to uid/gid 65534 (setpriv), so even the outer process is unprivileged;
///  - death: <c>--die-with-parent</c>; on kill the sandbox's PID 1 dies and the kernel kills every process in its PID namespace.
/// Not enforced: a seccomp filter (the kernel attack surface is the full syscall set minus user namespaces).
/// </summary>
public sealed class BubblewrapSandbox : ISandboxRunner
{
    public const int NobodyId = 65534;
    private readonly string? _bwrap, _prlimit, _setpriv;
    private readonly IProcessRunner _runner;

    public BubblewrapSandbox(IProcessRunner? runner = null)
    {
        _runner = runner ?? DefaultProcessRunner.Instance;
        if (!OperatingSystem.IsLinux()) return;
        _bwrap = CliLaunchBuilder.ResolveOnPath("bwrap");
        _prlimit = CliLaunchBuilder.ResolveOnPath("prlimit");
        _setpriv = CliLaunchBuilder.ResolveOnPath("setpriv");
    }

    public string Id => "bwrap-v1";
    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    public static bool RunningAsRoot => OperatingSystem.IsLinux() && LinuxFs.GetEuid() == 0;

    public string? Unavailable =>
        !OperatingSystem.IsLinux() ? "bubblewrap runs on Linux; on Windows use the WSL sandbox" :
        _bwrap is null ? "bwrap is not installed (apt install bubblewrap)" :
        _prlimit is null ? "prlimit is not installed (util-linux)" :
        RunningAsRoot && _setpriv is null ? "running as root and setpriv is missing, so the sandbox cannot drop privileges" :
        null;

    public string Describe => "bubblewrap: no network, no host files except read-only system + staged inputs, private /out, cleared environment, "
        + "4 GiB address space, CPU/process/file-size limits" + (RunningAsRoot ? ", runs as uid 65534" : "");

    /// <summary>The full command line, exposed for tests and the doctor.</summary>
    public IReadOnlyList<string> BuildArguments(SandboxRequest r)
    {
        var a = new List<string>();
        if (RunningAsRoot) a.AddRange([_setpriv!, $"--reuid={NobodyId}", $"--regid={NobodyId}", "--clear-groups", "--inh-caps=-all", "--"]);
        var l = r.Limits;
        a.AddRange([_prlimit!,
            $"--as={l.AddressSpaceBytes.ToString(CultureInfo.InvariantCulture)}",
            $"--cpu={l.CpuSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"--nproc={l.Processes.ToString(CultureInfo.InvariantCulture)}",
            $"--fsize={l.FileSizeBytes.ToString(CultureInfo.InvariantCulture)}",
            $"--nofile={l.OpenFiles.ToString(CultureInfo.InvariantCulture)}",
            "--core=0", "--"]);
        a.AddRange([_bwrap!, "--unshare-all", "--unshare-user", "--disable-userns", "--die-with-parent", "--new-session", "--cap-drop", "ALL",
            "--hostname", "cockpit-sandbox", "--clearenv"]);
        // The read-only system. Merged-/usr distributions keep /bin, /lib and /lib64 as links into /usr.
        a.AddRange(["--ro-bind", "/usr", "/usr"]);
        foreach (var top in new[] { "bin", "sbin", "lib", "lib64", "lib32" })
        {
            string host = "/" + top;
            var info = new FileInfo(host);
            if (info.LinkTarget is string target) a.AddRange(["--symlink", target, host]);
            else if (Directory.Exists(host)) a.AddRange(["--ro-bind", host, host]);
        }
        foreach (var etc in new[] { "/etc/ld.so.cache", "/etc/ld.so.conf" })
            if (File.Exists(etc)) a.AddRange(["--ro-bind", etc, etc]);
        // Debian/Ubuntu interpreters are often reached through /etc/alternatives links (python3 → /etc/alternatives/python3).
        if (Directory.Exists("/etc/alternatives")) a.AddRange(["--ro-bind", "/etc/alternatives", "/etc/alternatives"]);
        foreach (var extra in r.ReadOnlyBinds.Distinct(StringComparer.Ordinal))
            if (!extra.StartsWith("/usr/", StringComparison.Ordinal) && extra != "/usr") a.AddRange(["--ro-bind", extra, extra]);
        a.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
            "--ro-bind", r.InputDirectory, "/in", "--bind", r.OutputDirectory, "/out", "--chdir", "/out"]);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/tmp", ["LANG"] = "C.UTF-8", ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONDONTWRITEBYTECODE"] = "1", ["OMP_NUM_THREADS"] = "2",
        };
        foreach (var (k, v) in r.Environment) env[k] = v;
        foreach (var (k, v) in env.OrderBy(kv => kv.Key, StringComparer.Ordinal)) a.AddRange(["--setenv", k, v]);
        a.Add(r.Executable);
        a.AddRange(r.Arguments);
        return a;
    }

    public async Task<ProcessResult> RunAsync(SandboxRequest r, CancellationToken ct)
    {
        if (Unavailable is string why) throw new InvalidOperationException("Sandbox unavailable: " + why);
        if (RunningAsRoot)
        {
            // The unprivileged sandbox user must be able to write its own staging folder and nothing else.
            LinuxFs.Chown(r.OutputDirectory, NobodyId, NobodyId);
            File.SetUnixFileMode(r.OutputDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(r.InputDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var args = BuildArguments(r);
        var psi = new ProcessStartInfo(args[0]);
        foreach (var x in args.Skip(1)) psi.ArgumentList.Add(x);
        // Nothing from Cockpit's own environment reaches the sandbox (bwrap --clearenv), but keep the outer
        // setpriv/prlimit/bwrap processes clean too.
        psi.Environment.Clear();
        psi.Environment["PATH"] = "/usr/bin:/bin";
        return await _runner.RunAsync(psi, r.Limits.Wall, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The same bubblewrap sandbox, run inside the restricted WSL distro from Windows. Staging folders must live under
/// the WSL exchange folder (mapped to <paramref name="linuxExchange"/>). NOT RUN in V6 verification (no Windows host);
/// the command line is unit-tested.
/// </summary>
public sealed class WslBubblewrapSandbox(string distro, string windowsExchange, string linuxExchange, IProcessRunner? runner = null) : ISandboxRunner
{
    private readonly IProcessRunner _runner = runner ?? DefaultProcessRunner.Instance;
    private readonly string _win = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsExchange)) + Path.DirectorySeparatorChar;

    public string Id => "wsl-bwrap-v1";
    public string? Unavailable => OperatingSystem.IsWindows() && !File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")) ? "WSL is not installed" : null;
    public string Describe => $"bubblewrap inside WSL distro '{distro}': no network, no Windows drives (automount off), staged inputs only";

    public string? Map(string hostPath)
    {
        string full = Path.GetFullPath(hostPath);
        if (!(full + Path.DirectorySeparatorChar).StartsWith(_win, StringComparison.OrdinalIgnoreCase)) return null;
        string rel = full.Length > _win.Length ? full[_win.Length..] : "";
        return linuxExchange.TrimEnd('/') + "/" + rel.Replace('\\', '/');
    }

    public IReadOnlyList<string> BuildArguments(SandboxRequest r)
    {
        string input = Map(r.InputDirectory) ?? throw new InvalidOperationException($"{r.InputDirectory} is not under the WSL exchange folder.");
        string output = Map(r.OutputDirectory) ?? throw new InvalidOperationException($"{r.OutputDirectory} is not under the WSL exchange folder.");
        var l = r.Limits;
        var a = new List<string> { "-d", distro, "--", "prlimit",
            $"--as={l.AddressSpaceBytes.ToString(CultureInfo.InvariantCulture)}", $"--cpu={l.CpuSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"--nproc={l.Processes.ToString(CultureInfo.InvariantCulture)}", $"--fsize={l.FileSizeBytes.ToString(CultureInfo.InvariantCulture)}",
            $"--nofile={l.OpenFiles.ToString(CultureInfo.InvariantCulture)}", "--core=0", "--",
            "bwrap", "--unshare-all", "--unshare-user", "--disable-userns", "--die-with-parent", "--new-session", "--cap-drop", "ALL", "--clearenv",
            "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib64", "/lib64",
            "--ro-bind", "/etc/ld.so.cache", "/etc/ld.so.cache" };
        foreach (var extra in r.ReadOnlyBinds) a.AddRange(["--ro-bind", extra, extra]);
        a.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--ro-bind", input, "/in", "--bind", output, "/out", "--chdir", "/out",
            "--setenv", "PATH", "/usr/bin:/bin", "--setenv", "HOME", "/tmp", "--setenv", "PYTHONIOENCODING", "utf-8"]);
        foreach (var (k, v) in r.Environment) a.AddRange(["--setenv", k, v]);
        a.Add(r.Executable);
        a.AddRange(r.Arguments);
        return a;
    }

    public Task<ProcessResult> RunAsync(SandboxRequest r, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("wsl.exe");
        foreach (var x in BuildArguments(r)) psi.ArgumentList.Add(x);
        return _runner.RunAsync(psi, r.Limits.Wall, ct);
    }
}

/// <summary>
/// Validates what a sandbox produced before Cockpit touches it, then copies the accepted files into the user's
/// folder through the job's <see cref="Security.PathAuthorizer"/> (new files only). The staging folder is never
/// exposed to the user or to other tools.
/// </summary>
public static class SandboxOutputs
{
    public sealed record Accepted(string Name, string StagedPath, long Size);

    /// <summary>Plain files only (no links, no folders, no devices), names from an allow-list, each under the size cap.</summary>
    public static (List<Accepted> Accepted, List<string> Refused) Inspect(string staging, Func<string, bool> allowedName, long maxBytes, int maxFiles = 64)
    {
        var ok = new List<Accepted>();
        var bad = new List<string>();
        foreach (var entry in new DirectoryInfo(staging).EnumerateFileSystemInfos())
        {
            if (ok.Count >= maxFiles) { bad.Add($"{entry.Name}: more than {maxFiles} outputs"); continue; }
            if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0) { bad.Add($"{entry.Name}: a link (refused)"); continue; }
            if (entry is not FileInfo f) { bad.Add($"{entry.Name}: not a plain file"); continue; }
            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(f.FullName) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0) { bad.Add($"{entry.Name}: setuid/setgid bit"); continue; }
            if (!allowedName(f.Name)) { bad.Add($"{entry.Name}: unexpected output name"); continue; }
            if (f.Length > maxBytes) { bad.Add($"{entry.Name}: {f.Length} bytes is over the {maxBytes} limit"); continue; }
            ok.Add(new Accepted(f.Name, f.FullName, f.Length));
        }
        return (ok, bad);
    }
}
