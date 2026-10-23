using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using Cockpit.Core.Jobs;
using Microsoft.Win32;

namespace Cockpit.Core.Runtime;

/// <summary>The desktop applications the Cockpit drives.</summary>
public enum DesktopApp { SolidWorks, Resolve, Blender }

/// <summary>One installation of a desktop application, and how it was found.</summary>
public sealed record DesktopAppInstall(DesktopApp App, string Executable, string? Version, string FoundBy)
{
    public string InstallDirectory => Path.GetDirectoryName(Executable) ?? Executable;
    /// <summary>The product name from the registry or the exe's version resource ("DaVinci Resolve Studio"), when known.</summary>
    public string? ProductName { get; init; }
    /// <summary>SolidWorks only: the folder with SolidWorks.Interop.sldworks.dll (&lt;install&gt;\api\redist), when present.</summary>
    public string? InteropDirectory { get; init; }

    public string Describe
    {
        get
        {
            string name = ProductName ?? DesktopAppLocator.DisplayName(App);
            string version = Version is null || name.Contains(Version, StringComparison.OrdinalIgnoreCase) ? "" : " " + Version;
            return $"{name}{version} at {Executable} (found via {FoundBy})";
        }
    }
}

/// <summary>Read-only registry access, so discovery is testable off Windows.</summary>
public interface IRegistryReader
{
    /// <summary>String values of <paramref name="key"/> ("HKLM\SOFTWARE\…", "HKCU\…"; the default value is ""), merged across the 64- and 32-bit views. Null when the key does not exist.</summary>
    IReadOnlyDictionary<string, string>? Values(string key);
    /// <summary>Names of the subkeys of <paramref name="key"/> in both views; empty when the key does not exist.</summary>
    IReadOnlyList<string> SubKeyNames(string key);
}

/// <summary>The real registry. Unreadable keys are treated as absent; nothing is ever written.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryReader : IRegistryReader
{
    public static readonly WindowsRegistryReader Instance = new();
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    private static (RegistryHive Hive, string Path) Split(string key)
    {
        int slash = key.IndexOf('\\', StringComparison.Ordinal);
        string hive = slash < 0 ? key : key[..slash], rest = slash < 0 ? "" : key[(slash + 1)..];
        return hive.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => (RegistryHive.LocalMachine, rest),
            "HKCU" or "HKEY_CURRENT_USER" => (RegistryHive.CurrentUser, rest),
            "HKCR" or "HKEY_CLASSES_ROOT" => (RegistryHive.ClassesRoot, rest),
            _ => throw new ArgumentException($"Unsupported registry hive in '{key}'.", nameof(key)),
        };
    }

    private static void Each(string key, Action<RegistryKey> use)
    {
        var (hive, path) = Split(key);
        foreach (var view in Views)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(path, writable: false);
                if (k is not null) use(k);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // unreadable: treated as absent
            }
        }
    }

    public IReadOnlyDictionary<string, string>? Values(string key)
    {
        Dictionary<string, string>? values = null;
        Each(key, k =>
        {
            values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in k.GetValueNames())
                if (k.GetValue(name) is string s && !values.ContainsKey(name)) values[name] = s;
        });
        return values;
    }

    public IReadOnlyList<string> SubKeyNames(string key)
    {
        var names = new List<string>();
        Each(key, k => names.AddRange(k.GetSubKeyNames()));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Finds SolidWorks, DaVinci Resolve and Blender on Windows. Neither the Resolve nor the Blender installer adds its
/// folder to PATH, so PATH alone misses most installations. Search order (first hit of a higher tier wins; inside
/// the last tier the newest version wins):
/// <list type="number">
///   <item>an explicit override: COCKPIT_SOLIDWORKS / COCKPIT_RESOLVE / COCKPIT_BLENDER (path to the exe);</item>
///   <item>PATH (Resolve, Blender; SolidWorks never is);</item>
///   <item>the vendors' registry keys (HKLM\SOFTWARE\SolidWorks\SOLIDWORKS &lt;year&gt;\Setup, HKLM\SOFTWARE\Blackmagic Design\DaVinci Resolve,
///     HKLM|HKCU\SOFTWARE\BlenderFoundation), SolidWorks' COM registration, the "Apps &amp; features" uninstall entries, and the
///     standard folders under %ProgramFiles% / %ProgramW6432% / %ProgramFiles(x86)% / %LOCALAPPDATA%\Programs, with version
///     folders matched by wildcard (Blender 4.2, Blender 5.1, …), and Steam's Blender.</item>
/// </list>
/// Read-only: the registry is never written. Environment, registry and PATH are injectable for tests.
/// </summary>
public sealed partial class DesktopAppLocator
{
    private readonly Func<string, string?> _env;
    private readonly IRegistryReader? _registry;
    private readonly Func<string, string?> _onPath;
    private readonly bool _windows;

    public DesktopAppLocator(Func<string, string?> environment, IRegistryReader? registry, Func<string, string?> onPath, bool windowsLayout)
    {
        _env = environment;
        _registry = registry;
        _onPath = onPath;
        _windows = windowsLayout;
    }

    /// <summary>The real machine: environment, PATH, and on Windows the registry and the Windows folder layout.</summary>
    public static DesktopAppLocator Default { get; } = new(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : null,
        CliLaunchBuilder.ResolveOnPath,
        OperatingSystem.IsWindows());

    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string SolidWorksKey = @"HKLM\SOFTWARE\SolidWorks";
    public const string ResolveKey = @"HKLM\SOFTWARE\Blackmagic Design\DaVinci Resolve";
    public const string BlenderKey = @"SOFTWARE\BlenderFoundation";

    public static string DisplayName(DesktopApp app) => app switch
    {
        DesktopApp.SolidWorks => "SolidWorks",
        DesktopApp.Resolve => "DaVinci Resolve",
        DesktopApp.Blender => "Blender",
        _ => app.ToString(),
    };

    public static string ExeName(DesktopApp app) => app switch
    {
        DesktopApp.SolidWorks => "SLDWORKS.exe",
        DesktopApp.Resolve => "Resolve.exe",
        DesktopApp.Blender => "blender.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(app)),
    };

    public static string OverrideVariable(DesktopApp app) => "COCKPIT_" + app.ToString().ToUpperInvariant();

    /// <summary>The best installation, or null.</summary>
    public DesktopAppInstall? Find(DesktopApp app) => FindAll(app).FirstOrDefault();

    /// <summary>Every distinct installation found, best first (see the class comment for the order).</summary>
    public IReadOnlyList<DesktopAppInstall> FindAll(DesktopApp app)
    {
        var preferred = new List<DesktopAppInstall>();
        var discovered = new List<DesktopAppInstall>();

        if (_env(OverrideVariable(app)) is { Length: > 0 } forced && File.Exists(Unquote(forced)))
            preferred.Add(new DesktopAppInstall(app, Path.GetFullPath(Unquote(forced)), null, OverrideVariable(app)));
        if (app != DesktopApp.SolidWorks && _onPath(Path.GetFileNameWithoutExtension(ExeName(app))) is { } onPath && (_windows || app == DesktopApp.Blender))
            preferred.Add(new DesktopAppInstall(app, Path.GetFullPath(onPath), null, "PATH"));

        if (_windows)
        {
            switch (app)
            {
                case DesktopApp.SolidWorks: SolidWorksCandidates(discovered); break;
                case DesktopApp.Resolve: ResolveCandidates(discovered); break;
                case DesktopApp.Blender: BlenderCandidates(discovered); break;
            }
        }

        // Newest first inside the discovered tier; the sort is stable, so equal versions keep the search order.
        var ranked = preferred.Concat(discovered.OrderByDescending(c => ParseVersion(c.Version) ?? new Version(0, 0))).ToList();
        var comparer = _windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new List<DesktopAppInstall>();
        foreach (var c in ranked)
        {
            int i = result.FindIndex(r => comparer.Equals(r.Executable, c.Executable));
            if (i < 0) result.Add(Enrich(c));
            else result[i] = result[i] with
            {
                Version = result[i].Version ?? c.Version,
                ProductName = result[i].ProductName ?? c.ProductName,
                InteropDirectory = result[i].InteropDirectory ?? c.InteropDirectory,
            };
        }
        return result;
    }

    /// <summary>Plain-words list of where <see cref="FindAll"/> looks, for "not found" messages.</summary>
    public IReadOnlyList<string> SearchedLocations(DesktopApp app)
    {
        var where = new List<string> { $"%{OverrideVariable(app)}%" };
        if (app != DesktopApp.SolidWorks) where.Add("PATH");
        if (!_windows) return where;
        where.AddRange(app switch
        {
            DesktopApp.SolidWorks => [@"HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year>\Setup", "SldWorks.Application COM registration", "Apps & features", @"%ProgramFiles%\SOLIDWORKS Corp\SOLIDWORKS"],
            DesktopApp.Resolve => [ResolveKey, "Apps & features", @"%ProgramFiles%\Blackmagic Design\DaVinci Resolve"],
            _ => new[] { @"HKLM\" + BlenderKey, @"HKCU\" + BlenderKey, "Apps & features", @"%ProgramFiles%\Blender Foundation\Blender *", @"%LOCALAPPDATA%\Programs\Blender Foundation\Blender *", "Steam" },
        });
        return where;
    }

    // ── per application ──────────────────────────────────────────────────────────────────────────────

    private void SolidWorksCandidates(List<DesktopAppInstall> found)
    {
        const string exe = "SLDWORKS.exe";
        // HKLM\SOFTWARE\SolidWorks\SOLIDWORKS 2024\Setup  "SolidWorks Folder" = C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\
        foreach (var name in Registry().SubKeyNames(SolidWorksKey).Where(n => n.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase)))
        {
            var setup = Registry().Values($@"{SolidWorksKey}\{name}\Setup");
            if (setup?.GetValueOrDefault("SolidWorks Folder") is { } folder && ExeIn(folder, exe) is { } path)
                found.Add(new DesktopAppInstall(DesktopApp.SolidWorks, path, YearOf(name), $@"HKLM\SOFTWARE\SolidWorks\{name}\Setup") { ProductName = name.ToUpperInvariant() });
        }
        // COM registration: SldWorks.Application → CLSID → LocalServer32 "C:\…\SLDWORKS.exe" [/args]
        if (Registry().Values(@"HKLM\SOFTWARE\Classes\SldWorks.Application\CLSID")?.GetValueOrDefault("") is { Length: > 0 } clsid &&
            Registry().Values($@"HKLM\SOFTWARE\Classes\CLSID\{clsid}\LocalServer32")?.GetValueOrDefault("") is { } server &&
            ExeFromCommand(server, exe) is { } comExe)
            found.Add(new DesktopAppInstall(DesktopApp.SolidWorks, comExe, null, "SldWorks.Application COM registration"));
        foreach (var (entry, values) in UninstallEntries(n => n.StartsWith("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) && !n.Contains("Explorer", StringComparison.OrdinalIgnoreCase) && !n.Contains("PDM", StringComparison.OrdinalIgnoreCase)))
            foreach (var dir in PathsIn(values, "InstallLocation", "DisplayIcon"))
                if ((ExeIn(dir, exe) ?? ExeIn(Path.Combine(dir, "SOLIDWORKS"), exe)) is { } p)
                    found.Add(new DesktopAppInstall(DesktopApp.SolidWorks, p, YearOf(values.GetValueOrDefault("DisplayName") ?? ""), "Apps & features: " + entry) { ProductName = values.GetValueOrDefault("DisplayName") });
        foreach (var pf in ProgramFilesRoots())
            if (ExeIn(Path.Combine(pf, "SOLIDWORKS Corp", "SOLIDWORKS"), exe) is { } p)
                found.Add(new DesktopAppInstall(DesktopApp.SolidWorks, p, null, "default folder"));
    }

    private void ResolveCandidates(List<DesktopAppInstall> found)
    {
        const string exe = "Resolve.exe";
        foreach (var (key, values) in KeyAndSubKeys(ResolveKey))
            foreach (var dir in PathsIn(values))
                if (ExeIn(dir, exe) is { } p)
                    found.Add(new DesktopAppInstall(DesktopApp.Resolve, p, values.GetValueOrDefault("Version"), key) { ProductName = values.GetValueOrDefault("ProductName") });
        foreach (var (entry, values) in UninstallEntries(n => n.StartsWith("DaVinci Resolve", StringComparison.OrdinalIgnoreCase) && !n.Contains("Panel", StringComparison.OrdinalIgnoreCase)))
            foreach (var dir in PathsIn(values, "InstallLocation", "DisplayIcon"))
                if (ExeIn(dir, exe) is { } p)
                    found.Add(new DesktopAppInstall(DesktopApp.Resolve, p, values.GetValueOrDefault("DisplayVersion"), "Apps & features: " + entry) { ProductName = values.GetValueOrDefault("DisplayName") });
        foreach (var pf in ProgramFilesRoots())
            if (ExeIn(Path.Combine(pf, "Blackmagic Design", "DaVinci Resolve"), exe) is { } p)
                found.Add(new DesktopAppInstall(DesktopApp.Resolve, p, null, "default folder"));
    }

    private void BlenderCandidates(List<DesktopAppInstall> found)
    {
        const string exe = "blender.exe";
        // Version folders: C:\Program Files\Blender Foundation\Blender 4.2\blender.exe (also per-user installs).
        var roots = ProgramFilesRoots().Select(pf => (Path.Combine(pf, "Blender Foundation"), "Program Files")).ToList();
        if (_env("LOCALAPPDATA") is { Length: > 0 } local) roots.Add((Path.Combine(local, "Programs", "Blender Foundation"), @"%LOCALAPPDATA%\Programs"));
        foreach (var (root, label) in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirectories(root, "Blender*"))
                if (ExeIn(dir, exe) is { } p)
                    found.Add(new DesktopAppInstall(DesktopApp.Blender, p, VersionIn(Path.GetFileName(dir)), $@"{label}\Blender Foundation\{Path.GetFileName(dir)}"));
        }
        foreach (var hive in new[] { "HKLM", "HKCU" })
            foreach (var (key, values) in KeyAndSubKeys($@"{hive}\{BlenderKey}"))
                foreach (var dir in PathsIn(values))
                    if (ExeIn(dir, exe) is { } p)
                        found.Add(new DesktopAppInstall(DesktopApp.Blender, p, values.GetValueOrDefault("ShortVersion") ?? VersionIn(Path.GetFileName(dir.TrimEnd('\\', '/'))), key));
        foreach (var (entry, values) in UninstallEntries(n => n.StartsWith("Blender", StringComparison.OrdinalIgnoreCase)))
            foreach (var dir in PathsIn(values, "InstallLocation", "DisplayIcon"))
                if (ExeIn(dir, exe) is { } p)
                    found.Add(new DesktopAppInstall(DesktopApp.Blender, p, values.GetValueOrDefault("DisplayVersion"), "Apps & features: " + entry));
        foreach (var pf in ProgramFilesRoots())
            if (ExeIn(Path.Combine(pf, "Steam", "steamapps", "common", "Blender"), exe) is { } p)
                found.Add(new DesktopAppInstall(DesktopApp.Blender, p, null, "Steam"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private IRegistryReader Registry() => _registry ?? EmptyRegistry.Instance;

    /// <summary>%ProgramW6432%, %ProgramFiles%, %ProgramFiles(x86)%: distinct, existing, in that order.</summary>
    private List<string> ProgramFilesRoots()
    {
        var roots = new List<string>();
        foreach (var v in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            if (_env(v) is { Length: > 0 } dir && Directory.Exists(dir) && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase)) roots.Add(dir);
        return roots;
    }

    /// <summary>The key itself and its direct subkeys (installers put the path in either), with their values.</summary>
    private IEnumerable<(string Key, IReadOnlyDictionary<string, string> Values)> KeyAndSubKeys(string key)
    {
        if (Registry().Values(key) is { } own) yield return (key, own);
        foreach (var sub in Registry().SubKeyNames(key))
            if (Registry().Values($@"{key}\{sub}") is { } values) yield return ($@"{key}\{sub}", values);
    }

    /// <summary>"Apps &amp; features" entries (HKLM, both views, and HKCU) whose DisplayName matches.</summary>
    private IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> UninstallEntries(Func<string, bool> displayName)
    {
        foreach (var hive in new[] { "HKLM", "HKCU" })
        {
            string root = $@"{hive}\{UninstallKey}";
            foreach (var sub in Registry().SubKeyNames(root))
                if (Registry().Values($@"{root}\{sub}") is { } values && values.GetValueOrDefault("DisplayName") is { } name && displayName(name))
                    yield return (name, values);
        }
    }

    /// <summary>Directories named by registry values: folders, exe paths, or "C:\…\x.exe",0 icon references.</summary>
    private static IEnumerable<string> PathsIn(IReadOnlyDictionary<string, string> values, params string[] only)
    {
        foreach (var (name, raw) in values)
        {
            if (only.Length > 0 && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            string v = Unquote(IconSuffix().Replace(raw.Trim(), ""));
            if (v.Length == 0 || v.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
            if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { if (Path.GetDirectoryName(v) is { Length: > 0 } d) yield return d; }
            else if (Directory.Exists(v)) yield return v;
        }
    }

    private static string? ExeIn(string directory, string exe)
    {
        if (directory.Length == 0) return null;
        string candidate = Path.Combine(directory, exe);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    /// <summary>The exe from a command line such as <c>"C:\…\SLDWORKS.exe" /automation</c> or <c>C:\…\SLDWORKS.exe</c>.</summary>
    private static string? ExeFromCommand(string command, string exe)
    {
        string c = command.Trim();
        string path = c.StartsWith('"') && c.IndexOf('"', 1) is var end and > 0 ? c[1..end]
            : c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? c[..(i + 4)] : c;
        return Path.GetFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static IEnumerable<string> SafeDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return []; }
    }

    private DesktopAppInstall Enrich(DesktopAppInstall c)
    {
        if (c.App == DesktopApp.SolidWorks && c.InteropDirectory is null)
        {
            string redist = Path.Combine(c.InstallDirectory, "api", "redist");
            if (File.Exists(Path.Combine(redist, "SolidWorks.Interop.sldworks.dll"))) c = c with { InteropDirectory = redist };
        }
        if (_windows && OperatingSystem.IsWindows() && (c.ProductName is null || c.Version is null))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(c.Executable);
                string? product = string.Join(' ', new[] { info.ProductName, info.FileDescription }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal)) is { Length: > 0 } p ? p : null;
                c = c with { ProductName = c.ProductName ?? product, Version = c.Version ?? (string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion) };
            }
            catch (FileNotFoundException) { /* removed meanwhile */ }
        }
        return c;
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    private static string? YearOf(string name) => Year().Match(name) is { Success: true } m ? m.Value : null;

    private static string? VersionIn(string text) => VersionNumber().Match(text) is { Success: true } m ? m.Value : null;

    internal static Version? ParseVersion(string? text)
    {
        if (text is null || VersionNumber().Match(text) is not { Success: true } m) return null;
        string v = m.Value.Contains('.', StringComparison.Ordinal) ? m.Value : m.Value + ".0";
        return Version.TryParse(v, out var parsed) ? parsed : null;
    }

    [GeneratedRegex(@",\s*-?\d+$")]
    private static partial Regex IconSuffix();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)")]
    private static partial Regex Year();

    [GeneratedRegex(@"\d+(?:\.\d+){0,3}")]
    private static partial Regex VersionNumber();

    private sealed class EmptyRegistry : IRegistryReader
    {
        public static readonly EmptyRegistry Instance = new();
        public IReadOnlyDictionary<string, string>? Values(string key) => null;
        public IReadOnlyList<string> SubKeyNames(string key) => [];
    }

    /// <summary>A short "SolidWorks 2024 at C:\…" line, or where it looked.</summary>
    public string Summary(DesktopApp app) => Find(app) is { } install
        ? install.Describe
        : $"{DisplayName(app)} not found (looked in {string.Join(", ", SearchedLocations(app))})";
}
