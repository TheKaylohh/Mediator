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
    AppStatus SolidWorks();
    AppStatus Resolve();
    AppStatus Blender();
    /// <summary>V15: "word", "excel", "powerpoint" or "outlook".</summary>
    AppStatus Office(string id) => new(id, Mediator.Core.Office.OfficeApps.DisplayName(id), false, "not checked");
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
    private readonly DesktopAppLocator _apps = apps ?? DesktopAppLocator.Default;
    private readonly Func<string, int?> _running = runningProcess ?? FirstProcess;
    /// <summary>Windows apps are reported as such off Windows, unless a locator was injected (tests).</summary>
    private readonly bool _windowsApps = apps is not null || OperatingSystem.IsWindows();

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

    public AppStatus SolidWorks()
    {
        if (!_windowsApps) return new AppStatus("solidworks", "SolidWorks", false, "Windows only");
        var install = _apps.Find(DesktopApp.SolidWorks);
        string installed = install is null ? "installation not found in the registry or the default folder" : $"installed: {Label(install)}";
        if (_running("SLDWORKS") is { } pid)
            return new AppStatus("solidworks", "SolidWorks", true, $"running (PID {pid}); the worker attaches to it. {installed}", install?.Executable);
        return install is null
            ? new AppStatus("solidworks", "SolidWorks", false, $"not installed (looked in {string.Join(", ", _apps.SearchedLocations(DesktopApp.SolidWorks))})")
            : new AppStatus("solidworks", "SolidWorks", true, $"{installed}; not running: the Mediator starts it when a SolidWorks job needs it", install.Executable);
    }

    public AppStatus Resolve()
    {
        if (!_windowsApps) return new AppStatus("resolve", "DaVinci Resolve", false, "Windows only");
        var install = _apps.Find(DesktopApp.Resolve);
        if (install is null)
            return new AppStatus("resolve", "DaVinci Resolve", false, $"not installed (looked in {string.Join(", ", _apps.SearchedLocations(DesktopApp.Resolve))})");
        string edition = (install.ProductName ?? "").Contains("Studio", StringComparison.OrdinalIgnoreCase)
            ? "Studio: the scripting bridge can create the timeline (enable External scripting: Local)"
            : "edition not confirmed as Studio: FCPXML/EDL import works in both; the scripting bridge needs Studio";
        string running = _running("Resolve") is { } id ? $"running (PID {id})" : "not running";
        return new AppStatus("resolve", "DaVinci Resolve", true, $"{running}; {edition}; {Label(install)}", install.Executable);
    }

    public AppStatus Blender()
    {
        if (executables is not null && executables.TryGetValue("blender", out var forced))
            return forced is null
                ? new AppStatus("blender", "Blender", false, "not found")
                : new AppStatus("blender", "Blender", true, forced);
        var install = _apps.Find(DesktopApp.Blender);
        return install is null
            ? new AppStatus("blender", "Blender", false, $"not found (looked in {string.Join(", ", _apps.SearchedLocations(DesktopApp.Blender))})")
            : new AppStatus("blender", "Blender", true, Label(install), install.Executable);
    }

    /// <summary>V15: Word, Excel and PowerPoint are optional (the Office tools write the files themselves; the app only
    /// opens them). Outlook is usable only as classic desktop Outlook with a mail profile.</summary>
    public AppStatus Office(string id)
    {
        string name = Mediator.Core.Office.OfficeApps.DisplayName(id);
        if (!_windowsApps) return new AppStatus(id, name, false, "Windows only");
        string? exe = executables is not null && executables.TryGetValue(id, out var forced) ? forced : Mediator.Core.Office.OfficeApps.Locate(id);
        if (id != "outlook")
            return exe is null
                ? new AppStatus(id, name, false, "not installed. The Mediator still reads and writes these files; install Microsoft 365 to open them.")
                : new AppStatus(id, name, true, $"installed — {exe}. The Mediator reads and writes the files itself and opens them here.", exe);
        if (exe is null)
            return Mediator.Core.Office.OfficeApps.LocateNewOutlook() is { } olk
                ? new AppStatus(id, name, false, "only the new Outlook is installed, and it cannot be automated. Install classic Outlook (Microsoft 365 apps) for mail and calendar.", olk)
                : new AppStatus(id, name, false, "classic Outlook is not installed.");
        if (executables is not null) return new AppStatus(id, name, true, exe, exe);
        string? why = new Mediator.Core.Office.OutlookClient().Unavailable();
        return why is null
            ? new AppStatus(id, name, true, $"classic Outlook with a mail profile — {exe}. Search, read, draft (never sends) and calendar.", exe)
            : new AppStatus(id, name, false, why, exe);
    }

    /// <summary>"Blender 4.2 — C:\…\blender.exe (Program Files\Blender Foundation\Blender 4.2)".</summary>
    private static string Label(DesktopAppInstall install)
    {
        string name = install.ProductName ?? DesktopAppLocator.DisplayName(install.App);
        string version = install.Version is null || name.Contains(install.Version, StringComparison.OrdinalIgnoreCase) ? "" : " " + install.Version;
        return $"{name}{version} — {install.Executable} ({install.FoundBy})";
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
