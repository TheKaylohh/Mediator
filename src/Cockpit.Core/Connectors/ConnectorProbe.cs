using System.Diagnostics;
using System.Text.RegularExpressions;
using Cockpit.Core.Jobs;
using Cockpit.Core.Tools;

namespace Cockpit.Core.Connectors;

/// <summary>A command-line prerequisite: where it is and what <c>--version</c> said.</summary>
public sealed record ToolProbe(string Id, string DisplayName, string? Path, string? Version, string? Problem)
{
    public bool Found => Path is not null && Problem is null;
}

/// <summary>
/// What the Cockpit can honestly say about a CLI sign-in without reading secrets: whether the CLI's own credential
/// file exists (it is never opened), and whether a run in this session failed because it was not signed in.
/// </summary>
public enum CredentialState { NotFound, FilePresent, LastRunFailed }

/// <summary>A desktop application the Cockpit drives (SolidWorks, Resolve, Blender).</summary>
public sealed record AppStatus(string Id, string DisplayName, bool Available, string Detail);

public interface IConnectorProbe
{
    /// <summary>"claude", "gemini", "python" or "git": located and asked for its version (10 s limit).</summary>
    Task<ToolProbe> ProbeToolAsync(string id, CancellationToken ct = default);
    /// <summary>"claude" or "gemini". Only checks that the CLI's credential file exists; never reads it.</summary>
    CredentialState Credentials(string cliId);
    AppStatus SolidWorks();
    AppStatus Resolve();
    AppStatus Blender();
    /// <summary>A visible terminal running the CLI with API-key variables removed, so it signs in with the subscription. Null with a reason when not possible.</summary>
    (ProcessStartInfo? StartInfo, string? Problem) SignIn(string cliId);
}

/// <summary>The real probe. Paths and the process runner can be replaced for tests.</summary>
public sealed partial class ConnectorProbe(IProcessRunner runner, string? home = null, IReadOnlyDictionary<string, string?>? executables = null) : IConnectorProbe
{
    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly IReadOnlyList<string> Prerequisites = ["claude", "gemini", "python", "git"];

    public static string DisplayName(string id) => id switch
    {
        "claude" => "Claude Code CLI",
        "gemini" => "Gemini CLI",
        "python" => "Python 3",
        "git" => "Git",
        _ => id,
    };

    public static string InstallHint(string id) => id switch
    {
        "claude" => "Install: npm install -g @anthropic-ai/claude-code, then run claude and sign in with your Claude account.",
        "gemini" => "Install: npm install -g @google/gemini-cli, then run gemini and choose Login with Google.",
        "python" => "Install Python 3 from python.org (tick \"Add to PATH\"); needed by the CAD, tutor and Resolve scripting tools.",
        "git" => "Install Git from git-scm.com; Claude Code uses it in code projects.",
        _ => "",
    };

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.]+)?")]
    private static partial Regex VersionPattern();

    private (string Exe, string[] Prefix)? Locate(string id)
    {
        if (executables is not null && executables.TryGetValue(id, out var forced)) return forced is null ? null : (forced, []);
        return id switch
        {
            "python" => ToolLocator.Python(),
            _ => CliLaunchBuilder.ResolveOnPath(id) is string p ? (p, []) : null,
        };
    }

    public async Task<ToolProbe> ProbeToolAsync(string id, CancellationToken ct = default)
    {
        string name = DisplayName(id);
        if (Locate(id) is not { } exe) return new ToolProbe(id, name, null, null, "not found on PATH");
        var psi = CliLaunchBuilder.WrapForShim(exe.Exe, exe.Prefix.Append("--version"));
        foreach (var v in GeminiLaunchBuilder.VariablesToScrub(false)) psi.Environment.Remove(v);
        try
        {
            var r = await runner.RunAsync(psi, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (r.TimedOut) return new ToolProbe(id, name, exe.Exe, null, "did not answer --version within 10 s");
            string output = (r.Stdout + "\n" + r.Stderr).Trim();
            if (r.ExitCode != 0) return new ToolProbe(id, name, exe.Exe, null, $"--version exited with {r.ExitCode}");
            var m = VersionPattern().Match(output);
            return new ToolProbe(id, name, exe.Exe, m.Success ? m.Value : output.Split('\n')[0].Trim() is { Length: > 0 and <= 60 } line ? line : "unknown", null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new ToolProbe(id, name, exe.Exe, null, "could not be started: " + ex.Message);
        }
    }

    /// <summary>The CLIs' own credential files (Claude Code on Windows and Linux; Gemini CLI's Google login).</summary>
    public string CredentialFile(string cliId) => cliId switch
    {
        "claude" => Path.Combine(_home, ".claude", ".credentials.json"),
        "gemini" => Path.Combine(_home, ".gemini", "oauth_creds.json"),
        _ => throw new ArgumentException($"Unknown CLI '{cliId}'."),
    };

    public CredentialState Credentials(string cliId) => File.Exists(CredentialFile(cliId)) ? CredentialState.FilePresent : CredentialState.NotFound;

    public AppStatus SolidWorks()
    {
        if (!OperatingSystem.IsWindows()) return new AppStatus("solidworks", "SolidWorks", false, "Windows only");
        var p = FirstProcess("SLDWORKS");
        return p is { } pid
            ? new AppStatus("solidworks", "SolidWorks", true, $"running (PID {pid}); the worker attaches to it")
            : new AppStatus("solidworks", "SolidWorks", false, "not running: start SolidWorks before an assembly or STEP import job");
    }

    public AppStatus Resolve()
    {
        if (!OperatingSystem.IsWindows()) return new AppStatus("resolve", "DaVinci Resolve", false, "Windows only");
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blackmagic Design", "DaVinci Resolve", "Resolve.exe");
        bool installed = File.Exists(exe);
        string edition = "edition not detected";
        if (installed)
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            string product = $"{info.ProductName} {info.FileDescription}";
            edition = product.Contains("Studio", StringComparison.OrdinalIgnoreCase)
                ? "Studio: the scripting bridge can create the timeline (enable External scripting: Local)"
                : "edition not confirmed as Studio: FCPXML/EDL import works in both; the scripting bridge needs Studio";
        }
        var pid = FirstProcess("Resolve");
        string running = pid is { } id ? $"running (PID {id})" : "not running";
        return new AppStatus("resolve", "DaVinci Resolve", installed, installed ? $"{running}; {edition}" : "not installed in the default folder");
    }

    public AppStatus Blender()
    {
        string? path = executables is not null && executables.TryGetValue("blender", out var forced) ? forced : ToolLocator.Blender();
        return path is null
            ? new AppStatus("blender", "Blender", false, "not found on PATH or in Program Files\\Blender Foundation")
            : new AppStatus("blender", "Blender", true, path);
    }

    private static int? FirstProcess(string name)
    {
        var all = Process.GetProcessesByName(name);
        try { return all.Length > 0 ? all[0].Id : null; }
        finally { foreach (var p in all) p.Dispose(); }
    }

    public (ProcessStartInfo? StartInfo, string? Problem) SignIn(string cliId)
    {
        if (Locate(cliId) is not { } exe) return (null, $"{DisplayName(cliId)} is not installed. {InstallHint(cliId)}");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            foreach (var a in new[] { "/d", "/k", exe.Exe }) psi.ArgumentList.Add(a);
        }
        else if (CliLaunchBuilder.ResolveOnPath("x-terminal-emulator") is string term)
        {
            psi = new ProcessStartInfo(term);
            foreach (var a in new[] { "-e", exe.Exe }) psi.ArgumentList.Add(a);
        }
        else return (null, $"Open a terminal and run {cliId} to sign in.");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = false;
        psi.WorkingDirectory = _home;
        // Without these the CLI would sign in to pay-per-token billing instead of the subscription.
        foreach (var v in GeminiLaunchBuilder.VariablesToScrub(false)) psi.Environment.Remove(v);
        return (psi, null);
    }
}
