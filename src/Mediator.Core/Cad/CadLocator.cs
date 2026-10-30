using Mediator.Core.Runtime;

namespace Mediator.Core.Cad;

/// <summary>
/// Where a CAD program lives on Windows, as data. <paramref name="ProgramFilesPatterns"/> are paths below Program Files with
/// "*" in folder names (version folders: "Autodesk\AutoCAD *\acad.exe"); <paramref name="UninstallPrefixes"/> match "Apps &amp;
/// features" display names, whose InstallLocation is searched for <paramref name="RelativeExes"/>.
/// </summary>
public sealed record CadProgramSpec(string Id, string Name, string OverrideVariable, string[] PathCommands, string[] ProgramFilesPatterns,
    string[] UninstallPrefixes, string[] RelativeExes, string? ProgId = null);

/// <summary>V17: finds CAD programs (the override variable, PATH, Program Files version folders, Apps &amp; features, COM registration).</summary>
public sealed class CadLocator(Func<string, string?> env, IRegistryReader? registry, Func<string, string?> onPath, bool windows)
{
    public static CadLocator Default { get; } = new(Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : null, Mediator.Core.Jobs.CliLaunchBuilder.ResolveOnPath, OperatingSystem.IsWindows());

    /// <summary>The executable, newest version first, or null.</summary>
    public string? Find(CadProgramSpec spec)
    {
        if (env(spec.OverrideVariable) is { Length: > 0 } forced && File.Exists(forced.Trim('"'))) return Path.GetFullPath(forced.Trim('"'));
        foreach (var cmd in spec.PathCommands) if (onPath(cmd) is { } p) return p;
        if (!windows) return null;
        foreach (var root in ProgramFilesRoots())
            foreach (var pattern in spec.ProgramFilesPatterns)
                if (Expand(root, pattern.Split('\\', '/')).FirstOrDefault() is { } hit) return hit;
        if (registry is not null)
            foreach (var hive in new[] { "HKLM", "HKCU" })
            {
                string key = $@"{hive}\{DesktopAppLocator.UninstallKey}";
                foreach (var sub in registry.SubKeyNames(key))
                {
                    var values = registry.Values($@"{key}\{sub}");
                    if (values?.GetValueOrDefault("DisplayName") is not { } name || !spec.UninstallPrefixes.Any(pr => name.StartsWith(pr, StringComparison.OrdinalIgnoreCase))) continue;
                    if (values.GetValueOrDefault("InstallLocation") is { Length: > 0 } dir)
                        foreach (var rel in spec.RelativeExes.Select(r => r.Replace('\\', Path.DirectorySeparatorChar)))
                            if (File.Exists(Path.Combine(dir, rel))) return Path.Combine(dir, rel);
                }
            }
        return null;
    }

    /// <summary>The program's COM class is registered (it may be installed somewhere the patterns do not cover).</summary>
    public bool ComRegistered(CadProgramSpec spec) =>
        spec.ProgId is { } id && registry?.Values($@"HKLM\SOFTWARE\Classes\{id}\CLSID")?.GetValueOrDefault("") is { Length: > 0 };

    /// <summary>Windows PowerShell 5.1 (it has COM GetActiveObject; PowerShell 7 does not), or null.</summary>
    public string? WindowsPowerShell()
    {
        if (!windows) return null;
        string root = env("SystemRoot") ?? @"C:\Windows";
        string p = Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(p) ? p : onPath("powershell");
    }

    private List<string> ProgramFilesRoots()
    {
        var roots = new List<string>();
        foreach (var v in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            if (env(v) is { Length: > 0 } dir && Directory.Exists(dir) && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase)) roots.Add(dir);
        return roots;
    }

    /// <summary>Files matching a path with "*" segments below <paramref name="dir"/>, newest-named version folders first.</summary>
    internal static IEnumerable<string> Expand(string dir, string[] segments)
    {
        if (segments.Length == 0) yield break;
        string head = segments[0];
        if (segments.Length == 1)
        {
            if (!head.Contains('*')) { string f = Path.Combine(dir, head); if (File.Exists(f)) yield return f; yield break; }
            foreach (var f in Safe(() => Directory.EnumerateFiles(dir, head)).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)) yield return f;
            yield break;
        }
        var next = head.Contains('*')
            ? Safe(() => Directory.EnumerateDirectories(dir, head)).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : Directory.Exists(Path.Combine(dir, head)) ? [Path.Combine(dir, head)] : [];
        foreach (var d in next)
            foreach (var hit in Expand(d, segments[1..])) yield return hit;
    }

    private static IEnumerable<string> Safe(Func<IEnumerable<string>> list)
    {
        try { return list().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }
}
