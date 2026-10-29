using Cockpit.Core.Connectors;
using Cockpit.Core.Runtime;
using Cockpit.Core.Tools;

namespace Cockpit.Tests;

/// <summary>
/// V8: Windows discovery of SolidWorks, DaVinci Resolve and Blender. Runs on any OS against a fake registry and a fake
/// %ProgramFiles% / %LOCALAPPDATA% tree, so every rule (registry keys, version folders, Apps &amp; features, COM
/// registration, precedence, newest-wins) is exercised; only the real-registry adapter is left to Windows.
/// </summary>
static class DesktopAppTests
{
    sealed class FakeRegistry : IRegistryReader
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);
        public int Reads;

        public FakeRegistry Set(string key, string name, string value)
        {
            // Parent keys exist too, as in the real registry.
            for (int i = key.IndexOf('\\', StringComparison.Ordinal); i > 0; i = key.IndexOf('\\', i + 1))
                _keys.TryAdd(key[..i], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            if (!_keys.TryGetValue(key, out var values)) _keys[key] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values[name] = value;
            return this;
        }

        public IReadOnlyDictionary<string, string>? Values(string key)
        {
            Reads++;
            return _keys.TryGetValue(key, out var v) ? v : null;
        }

        public IReadOnlyList<string> SubKeyNames(string key) => _keys.Keys
            .Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase) && !k[(key.Length + 1)..].Contains('\\', StringComparison.Ordinal))
            .Select(k => k[(key.Length + 1)..]).ToList();
    }

    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-apps", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static string Touch(params string[] parts)
    {
        string p = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "MZ");
        return Path.GetFullPath(p);
    }

    sealed record Machine(string Root, string ProgramFiles, string ProgramFilesX86, string LocalAppData, Dictionary<string, string?> Env, FakeRegistry Registry)
    {
        public Dictionary<string, string> OnPath { get; } = new(StringComparer.Ordinal);
        public DesktopAppLocator Locator() => new(v => Env.GetValueOrDefault(v), Registry, n => OnPath.GetValueOrDefault(n), windowsLayout: true);
    }

    static Machine NewMachine()
    {
        string root = TempDir("pc");
        string pf = Path.Combine(root, "Program Files"), x86 = Path.Combine(root, "Program Files (x86)"), local = Path.Combine(root, "Users", "u", "AppData", "Local");
        foreach (var d in new[] { pf, x86, local }) Directory.CreateDirectory(d);
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["ProgramFiles"] = pf, ["ProgramW6432"] = pf, ["ProgramFiles(x86)"] = x86, ["LOCALAPPDATA"] = local };
        return new Machine(root, pf, x86, local, env, new FakeRegistry());
    }

    public static Task Blender()
    {
        var m = NewMachine();
        var none = m.Locator();
        Check.True(none.Find(DesktopApp.Blender) is null, "nothing installed: not found");
        Check.True(none.Summary(DesktopApp.Blender).Contains(@"%ProgramFiles%\Blender Foundation\Blender *", StringComparison.Ordinal), "not-found message lists the version-folder search");

        // Version folders under Program Files: the newest wins, whatever the enumeration order.
        Touch(m.ProgramFiles, "Blender Foundation", "Blender 4.2", "blender.exe");
        string b51 = Touch(m.ProgramFiles, "Blender Foundation", "Blender 5.1", "blender.exe");
        Touch(m.ProgramFiles, "Blender Foundation", "Blender 4.10", "blender.exe"); // 4.10 > 4.2, still < 5.1
        Directory.CreateDirectory(Path.Combine(m.ProgramFiles, "Blender Foundation", "Blender 9.9")); // folder without blender.exe
        var found = m.Locator().Find(DesktopApp.Blender);
        Check.True(found is { Version: "5.1" } && found.Executable == b51, $"newest version folder wins ({found?.Describe})");
        Check.Equal(3, m.Locator().FindAll(DesktopApp.Blender).Count, "every installed version listed, the empty folder ignored");

        // Per-user install (%LOCALAPPDATA%\Programs) with a newer version.
        string b52 = Touch(m.LocalAppData, "Programs", "Blender Foundation", "Blender 5.2", "blender.exe");
        Check.Equal(b52, m.Locator().Find(DesktopApp.Blender)?.Executable, "per-user install found and newer");

        // Registry (custom folder) and Apps & features (DisplayIcon with an icon index), Steam.
        string custom = Touch(m.Root, "Tools", "blender-6.0-portable", "blender.exe");
        m.Registry.Set(@"HKLM\SOFTWARE\BlenderFoundation", "Install_Dir", Path.GetDirectoryName(custom)!).Set(@"HKLM\SOFTWARE\BlenderFoundation", "ShortVersion", "6.0");
        string steam = Touch(m.ProgramFilesX86, "Steam", "steamapps", "common", "Blender", "blender.exe");
        string viaUninstall = Touch(m.Root, "Apps", "B", "blender.exe");
        m.Registry.Set(@"HKCU\" + DesktopAppLocator.UninstallKey + @"\{B-1}", "DisplayName", "blender")
                  .Set(@"HKCU\" + DesktopAppLocator.UninstallKey + @"\{B-1}", "DisplayIcon", $"\"{viaUninstall}\",0")
                  .Set(@"HKCU\" + DesktopAppLocator.UninstallKey + @"\{B-1}", "DisplayVersion", "4.0.2");
        var all = m.Locator().FindAll(DesktopApp.Blender);
        Check.True(all[0].Executable == custom && all[0].Version == "6.0" && all[0].FoundBy.Contains("BlenderFoundation", StringComparison.Ordinal), $"registry install (6.0) ranked first ({all[0].Describe})");
        Check.True(all.Any(a => a.Executable == steam && a.FoundBy == "Steam"), "Steam's Blender found");
        Check.True(all.Any(a => a.Executable == viaUninstall && a.Version == "4.0.2"), "Apps & features DisplayIcon \"…\\blender.exe\",0 parsed");

        // PATH beats discovery; COCKPIT_BLENDER beats PATH.
        string onPath = Touch(m.Root, "bin", "blender.exe");
        m.OnPath["blender"] = onPath;
        Check.True(m.Locator().Find(DesktopApp.Blender) is { FoundBy: "PATH" } p && p.Executable == onPath, "PATH wins over discovered installs");
        string forced = Touch(m.Root, "pinned", "blender.exe");
        m.Env["COCKPIT_BLENDER"] = forced;
        Check.True(m.Locator().Find(DesktopApp.Blender) is { FoundBy: "COCKPIT_BLENDER" } f && f.Executable == forced, "COCKPIT_BLENDER wins over PATH");
        m.Env["COCKPIT_BLENDER"] = Path.Combine(m.Root, "missing", "blender.exe");
        Check.True(m.Locator().Find(DesktopApp.Blender)?.FoundBy == "PATH", "a COCKPIT_BLENDER that does not exist is ignored");
        return Task.CompletedTask;
    }

    public static Task Resolve()
    {
        var m = NewMachine();
        Check.True(m.Locator().Find(DesktopApp.Resolve) is null, "not installed");

        // Default folder under Program Files (x86) only.
        string x86 = Touch(m.ProgramFilesX86, "Blackmagic Design", "DaVinci Resolve", "Resolve.exe");
        Check.True(m.Locator().Find(DesktopApp.Resolve) is { FoundBy: "default folder" } d && d.Executable == x86, "found in %ProgramFiles(x86)%");

        // A custom install folder known only to the registry, with junk values that must be ignored.
        var m2 = NewMachine();
        string custom = Touch(m2.Root, "D", "Video", "DaVinci Resolve", "Resolve.exe");
        m2.Registry.Set(DesktopAppLocator.ResolveKey, "InstallDir", Path.GetDirectoryName(custom)! + Path.DirectorySeparatorChar)
                   .Set(DesktopAppLocator.ResolveKey, "Version", "20.2.1")
                   .Set(DesktopAppLocator.ResolveKey, "Garbage", "\0not a path|<>")
                   .Set(DesktopAppLocator.ResolveKey + @"\Old", "Path", Path.Combine(m2.Root, "gone"));
        var r = m2.Locator().Find(DesktopApp.Resolve);
        Check.True(r is { Version: "20.2.1" } && r.Executable == custom && r.FoundBy == DesktopAppLocator.ResolveKey, $"registry folder found ({r?.Describe})");

        // Studio edition from Apps & features; ConnectorProbe reports it with the locator injected.
        m2.Registry.Set(@"HKLM\" + DesktopAppLocator.UninstallKey + @"\{R}", "DisplayName", "DaVinci Resolve Studio")
                   .Set(@"HKLM\" + DesktopAppLocator.UninstallKey + @"\{R}", "InstallLocation", Path.GetDirectoryName(custom)!);
        m2.Registry.Set(@"HKLM\" + DesktopAppLocator.UninstallKey + @"\{P}", "DisplayName", "DaVinci Resolve Panels")
                   .Set(@"HKLM\" + DesktopAppLocator.UninstallKey + @"\{P}", "InstallLocation", Path.Combine(m2.Root, "panels"));
        var merged = m2.Locator().FindAll(DesktopApp.Resolve);
        Check.True(merged.Count == 1 && merged[0].ProductName == "DaVinci Resolve Studio", "registry and Apps & features entries for one exe are merged (Studio name kept)");
        var probe = new ConnectorProbe(DefaultProcessRunner.Instance, TempDir("home"), apps: m2.Locator(), runningProcess: n => n == "Resolve" ? 777 : null);
        var status = probe.Resolve();
        Check.True(status.Available && status.Detail.Contains("running (PID 777)", StringComparison.Ordinal) && status.Detail.StartsWith("running", StringComparison.Ordinal)
            && status.Detail.Contains("Studio: the scripting bridge", StringComparison.Ordinal) && status.Detail.Contains(custom, StringComparison.Ordinal), $"probe: running Studio at the custom folder ({status.Detail})");
        var missing = new ConnectorProbe(DefaultProcessRunner.Instance, TempDir("home"), apps: NewMachine().Locator(), runningProcess: _ => null).Resolve();
        Check.True(!missing.Available && missing.Detail.Contains(DesktopAppLocator.ResolveKey, StringComparison.Ordinal), $"not installed: says where it looked ({missing.Detail})");
        return Task.CompletedTask;
    }

    public static Task SolidWorks()
    {
        var m = NewMachine();
        // Two versions in the registry: the newest year wins; its api\redist is reported for the interop build.
        string sw23 = Touch(m.Root, "SW2023", "SLDWORKS.exe");
        string sw24 = Touch(m.Root, "SW2024", "SOLIDWORKS", "SLDWORKS.exe");
        string redist = Path.GetDirectoryName(Touch(m.Root, "SW2024", "SOLIDWORKS", "api", "redist", "SolidWorks.Interop.sldworks.dll"))!;
        m.Registry.Set(DesktopAppLocator.SolidWorksKey + @"\SOLIDWORKS 2023\Setup", "SolidWorks Folder", Path.GetDirectoryName(sw23)! + Path.DirectorySeparatorChar)
                  .Set(DesktopAppLocator.SolidWorksKey + @"\SOLIDWORKS 2024\Setup", "SolidWorks Folder", Path.GetDirectoryName(sw24)!)
                  .Set(DesktopAppLocator.SolidWorksKey + @"\Applications", "x", "y");
        var best = m.Locator().Find(DesktopApp.SolidWorks);
        Check.True(best is { Version: "2024" } && best.Executable == sw24 && best.InteropDirectory == redist, $"SOLIDWORKS 2024 chosen with its api\\redist ({best?.Describe}, {best?.InteropDirectory})");
        Check.True(best!.Describe.Contains("SOLIDWORKS 2024 at", StringComparison.Ordinal) && !best.Describe.Contains("2024 2024", StringComparison.Ordinal), $"describe ({best.Describe})");

        // COM registration with a quoted path and switches; default folder.
        var m2 = NewMachine();
        string com = Touch(m2.Root, "Odd Place", "SLDWORKS.exe");
        m2.Registry.Set(@"HKLM\SOFTWARE\Classes\SldWorks.Application\CLSID", "", "{83A33D31-27C5-11CE-BFD4-00400513BB57}")
                   .Set(@"HKLM\SOFTWARE\Classes\CLSID\{83A33D31-27C5-11CE-BFD4-00400513BB57}\LocalServer32", "", $"\"{com}\" /automation");
        Check.True(m2.Locator().Find(DesktopApp.SolidWorks) is { } c && c.Executable == com && c.FoundBy.Contains("COM", StringComparison.Ordinal), "found through the COM LocalServer32");
        var m3 = NewMachine();
        string def = Touch(m3.ProgramFiles, "SOLIDWORKS Corp", "SOLIDWORKS", "SLDWORKS.exe");
        Check.Equal(def, m3.Locator().Find(DesktopApp.SolidWorks)?.Executable, @"default C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe");
        m3.OnPath["SLDWORKS"] = Touch(m3.Root, "bin", "SLDWORKS.exe");
        Check.Equal(def, m3.Locator().Find(DesktopApp.SolidWorks)?.Executable, "PATH is not used for SolidWorks");

        // ConnectorProbe: installed + running, installed + not running, not installed.
        var running = new ConnectorProbe(DefaultProcessRunner.Instance, TempDir("home"), apps: m.Locator(), runningProcess: n => n == "SLDWORKS" ? 4242 : null).SolidWorks();
        Check.True(running.Available && running.Detail.Contains("PID 4242", StringComparison.Ordinal) && running.Detail.Contains(sw24, StringComparison.Ordinal), $"running ({running.Detail})");
        var idle = new ConnectorProbe(DefaultProcessRunner.Instance, TempDir("home"), apps: m.Locator(), runningProcess: _ => null).SolidWorks();
        Check.True(!idle.Available && idle.Detail.Contains("installed: SOLIDWORKS 2024", StringComparison.Ordinal) && idle.Detail.Contains("not running", StringComparison.Ordinal), $"installed, not running ({idle.Detail})");
        var absent = new ConnectorProbe(DefaultProcessRunner.Instance, TempDir("home"), apps: NewMachine().Locator(), runningProcess: _ => null).SolidWorks();
        Check.True(!absent.Available && absent.Detail.StartsWith("not installed", StringComparison.Ordinal) && absent.Detail.Contains(@"SOLIDWORKS <year>\Setup", StringComparison.Ordinal), $"not installed ({absent.Detail})");

        // Off Windows with the real locator: nothing is looked up in a registry, and the probe says Windows only.
        if (!OperatingSystem.IsWindows())
        {
            Check.True(DesktopAppLocator.Default.Find(DesktopApp.SolidWorks) is null && DesktopAppLocator.Default.Find(DesktopApp.Resolve) is null, "real locator off Windows: no SolidWorks/Resolve");
            Check.Equal("Windows only", new ConnectorProbe(DefaultProcessRunner.Instance, TempDir("home")).SolidWorks().Detail, "probe without a locator off Windows");
        }
        return Task.CompletedTask;
    }
}
