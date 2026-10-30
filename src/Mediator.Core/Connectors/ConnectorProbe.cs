using System.Diagnostics;
using System.Text.RegularExpressions;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Core.Connectors;

/// <summary>A command-line prerequisite: where it is and what <c>--version</c> said.</summary>
public sealed record ToolProbe(string Id, string DisplayName, string? Path, string? Version, string? Problem)
{
    public bool Found => Path is not null && Problem is null;
}

/// <summary>
/// What the Mediator can honestly say about a CLI sign-in without reading secrets: whether the CLI's own credential
/// file exists (it is never opened), and whether a run in this session failed because it was not signed in.
/// </summary>
public enum CredentialState { NotFound, FilePresent, LastRunFailed }

/// <summary>A desktop application the Mediator drives (SolidWorks, Resolve, Blender, Office). V15: Executable is the installed
/// .exe when known (the Connectors tab shows its icon); installed but not usable (e.g. Outlook without a profile) is
/// Available false with an Executable.</summary>
public sealed record AppStatus(string Id, string DisplayName, bool Available, string Detail, string? Executable = null);

public interface IConnectorProbe
{
    /// <summary>"claude", "agy", "python" or "git": located and asked for its version (10 s limit).</summary>
    Task<ToolProbe> ProbeToolAsync(string id, CancellationToken ct = default);
    /// <summary>"claude" or "agy". Only checks that the CLI's credential file exists; never reads it.</summary>
    CredentialState Credentials(string cliId);
    /// <summary>V18: a desktop program some connector drives ("solidworks", "word"...): asked of that connector.</summary>
    AppStatus App(string id) => new(id, id, false, "not checked");
    /// <summary>A visible terminal running the CLI with API-key variables removed, so it signs in with the subscription. Null with a reason when not possible.</summary>
    (ProcessStartInfo? StartInfo, string? Problem) SignIn(string cliId);
}

/// <summary>
/// The real probe. Paths, the process runner and the desktop-app locator can be replaced for tests.
/// V8: SolidWorks, Resolve and Blender are found by <see cref="DesktopAppLocator"/> (registry, Program Files, version
/// folders, Apps &amp; features), not just PATH and one default folder.
/// </summary>
public sealed partial class ConnectorProbe(IProcessRunner runner, string? home = null, IReadOnlyDictionary<string, string?>? executables = null,
    DesktopAppLocator? apps = null, Func<string, int?>? runningProcess = null) : IConnectorProbe
{
    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly IReadOnlyList<string> Prerequisites = ["claude", "agy", "python", "git"];

    public static string DisplayName(string id) => id switch
    {
        "claude" => "Claude Code CLI",
        "agy" => "Antigravity CLI",
        "python" => "Python 3",
        "git" => "Git",
        _ => id,
    };

    public static string InstallHint(string id) => id switch
    {
        "claude" => "Install: npm install -g @anthropic-ai/claude-code, then run claude and sign in with your Claude account.",
        "agy" => "Install in PowerShell: irm https://antigravity.google/cli/install.ps1 | iex, then run agy and sign in with your Google AI Pro account.",
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
            "agy" => GeminiLaunchBuilder.Locate() is string a ? (a, []) : null,
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

    /// <summary>The CLIs' own credential files (Claude Code on Windows and Linux; for Antigravity, whose token is in the OS keyring, the onboarding record written after sign-in).</summary>
    public string CredentialFile(string cliId) => cliId switch
    {
        "claude" => Path.Combine(_home, ".claude", ".credentials.json"),
        "agy" => Path.Combine(_home, ".gemini", "antigravity-cli", "cache", "onboarding.json"),
        _ => throw new ArgumentException($"Unknown CLI '{cliId}'."),
    };

    public CredentialState Credentials(string cliId) => File.Exists(CredentialFile(cliId)) ? CredentialState.FilePresent : CredentialState.NotFound;

    /// <summary>What connectors use to check their programs (the locator, running processes, test overrides).</summary>
    public ProgramProbe Programs { get; } = new(apps ?? DesktopAppLocator.Default, runningProcess ?? FirstProcess, executables, apps is not null || OperatingSystem.IsWindows());

    public AppStatus App(string id) => ConnectorCatalog.ProbeProgram(id, Programs);

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

/// <summary>
/// V18: what a connector needs to report on its desktop program: the shared locator, running processes and the executables
/// tests force. <see cref="WindowsApps"/> is false off Windows (unless a locator was injected): Windows programs say "Windows only".
/// </summary>
public sealed class ProgramProbe(DesktopAppLocator apps, Func<string, int?> running, IReadOnlyDictionary<string, string?>? executables, bool windowsApps)
{
    public DesktopAppLocator Apps { get; } = apps;
    public bool WindowsApps { get; } = windowsApps;

    /// <summary>The PID of a running process with this name, or null.</summary>
    public int? Running(string processName) => running(processName);

    /// <summary>True when a test forced the executable of <paramref name="id"/> (null: forced missing).</summary>
    public bool Forced(string id, out string? executable)
    {
        executable = null;
        return executables is not null && executables.TryGetValue(id, out executable);
    }

    /// <summary>True when tests injected executables: no real program is asked anything.</summary>
    public bool IsTest => executables is not null;

    public string SearchedLocations(DesktopApp app) => string.Join(", ", Apps.SearchedLocations(app));

    /// <summary>"Blender 4.2 — C:\…\blender.exe (Program Files\Blender Foundation\Blender 4.2)".</summary>
    public static string Label(DesktopAppInstall install)
    {
        string name = install.ProductName ?? DesktopAppLocator.DisplayName(install.App);
        string version = install.Version is null || name.Contains(install.Version, StringComparison.OrdinalIgnoreCase) ? "" : " " + install.Version;
        return $"{name}{version} — {install.Executable} ({install.FoundBy})";
    }
}
