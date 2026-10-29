using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Tests;

/// <summary>
/// V8: static checks of the Windows release scripts (they cannot run on Linux). Each rule is a Windows failure
/// that is easy to reintroduce: non-ASCII bytes in a BOM-less .ps1 (Windows PowerShell 5.1 reads it as ANSI and a
/// UTF-8 "─" becomes a curly quote that ends a string), LF-only batch files, a parenthesised cmd block that breaks
/// on "C:\… (x86)\…" paths, and a package that silently misses the worker or a theme.
/// </summary>
static class ReleaseScriptTests
{
    static string Scripts => Path.Combine(XamlChecker.RepoRoot(), "scripts");

    public static Task ScriptsAreWindowsSafe()
    {
        foreach (var ps1 in Directory.EnumerateFiles(Scripts, "*.ps1"))
        {
            var bytes = File.ReadAllBytes(ps1);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int at = Array.FindIndex(bytes, b => b > 0x7F);
            Check.True(bom || at < 0, $"{Path.GetFileName(ps1)} is ASCII-only or has a UTF-8 BOM (first non-ASCII byte at {at})");
        }

        foreach (var name in new[] { "launch-cockpit.bat", "install-shortcut.vbs" })
        {
            string path = Path.Combine(Scripts, name);
            Check.True(File.Exists(path), $"{name} exists");
            var bytes = File.ReadAllBytes(path);
            Check.True(bytes.All(b => b <= 0x7F), $"{name} is ASCII");
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] == '\n') Check.True(i > 0 && bytes[i - 1] == '\r', $"{name}: CRLF line endings (bare LF at byte {i})");
        }

        string bat = File.ReadAllText(Path.Combine(Scripts, "launch-cockpit.bat"), Encoding.ASCII);
        var code = bat.Split("\r\n").Where(l => !Regex.IsMatch(l, @"^\s*(rem\b|::|$)", RegexOptions.IgnoreCase)).ToList();
        Check.True(code.Count > 0 && code[0].Trim().Equals("@echo off", StringComparison.OrdinalIgnoreCase), "launch.bat starts with @echo off");
        Check.True(bat.Contains("set \"HERE=%~dp0\"", StringComparison.Ordinal) && bat.Contains("\"%HERE%AiCockpit.exe\"", StringComparison.Ordinal), "launch.bat starts AiCockpit.exe from its own folder, quoted");
        Check.True(Regex.IsMatch(bat, @"start\s+""""\s+/D\s+""%HERE%\.""", RegexOptions.IgnoreCase), "start \"\" /D \"%HERE%.\" (title argument, no trailing-backslash quote)");
        Check.True(!code.Any(l => Regex.IsMatch(l, @"\(\s*$") || l.TrimStart().StartsWith(')')), "no parenthesised blocks (a path with parentheses would end them)");
        Check.True(!code.Any(l => l.Contains("powershell", StringComparison.OrdinalIgnoreCase)), "launch.bat does not run PowerShell");
        Check.True(Regex.IsMatch(bat, @"(?im)^:doctor\s*$") && Regex.IsMatch(bat, @"(?im)^:missing\s*$") && bat.Contains("goto doctor", StringComparison.Ordinal) && bat.Contains("goto missing", StringComparison.Ordinal), "labels exist for every goto");

        string vbs = File.ReadAllText(Path.Combine(Scripts, "install-shortcut.vbs"), Encoding.ASCII);
        Check.True(Regex.IsMatch(vbs, @"(?m)^Option Explicit\s*$"), "install-shortcut.vbs: Option Explicit");
        Check.True(vbs.Contains("WScript.ScriptFullName", StringComparison.Ordinal) && vbs.Contains("\"AiCockpit.exe\"", StringComparison.Ordinal), "shortcut target is AiCockpit.exe next to the script");
        Check.True(vbs.Contains("SpecialFolders(\"Desktop\")", StringComparison.Ordinal) && vbs.Contains("SpecialFolders(\"Programs\")", StringComparison.Ordinal), "Desktop and Start menu (per-user Programs)");
        Check.True(!Regex.IsMatch(vbs, @"AllUsers|HKLM|runas", RegexOptions.IgnoreCase), "per-user only, never elevated");
        var declared = Regex.Match(vbs, @"(?m)^Dim (.+)$").Groups[1].Value.Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(vbs, @"(?m)^\s*(?:Set\s+)?([a-zA-Z]\w*)\s*=(?!=)"))
            Check.True(declared.Contains(m.Groups[1].Value), $"install-shortcut.vbs: '{m.Groups[1].Value}' is declared (Option Explicit would stop at run time)");

        string attrs = File.ReadAllText(Path.Combine(XamlChecker.RepoRoot(), ".gitattributes"));
        Check.True(Regex.IsMatch(attrs, @"(?m)^\*\.bat\s+text\s+eol=crlf") && Regex.IsMatch(attrs, @"(?m)^\*\.vbs\s+text\s+eol=crlf"), ".gitattributes keeps CRLF for .bat and .vbs");
        return Task.CompletedTask;
    }

    public static Task PublishScriptPackagesEverything()
    {
        string ps = File.ReadAllText(Path.Combine(Scripts, "publish-release.ps1"));
        var projects = Regex.Match(ps, @"\$projects\s*=\s*\[ordered\]@\{(?<body>.*?)\n\}", RegexOptions.Singleline);
        Check.True(projects.Success, "the $projects table is found");
        foreach (var p in new[] { "Cockpit.Wpf", "Cockpit.CliHost", "Cockpit.SwxWorker", "Cockpit.Bridge" })
            Check.True(projects.Groups["body"].Value.Contains($"'src\\{p}'", StringComparison.Ordinal), $"{p} is always published (V8: the worker too)");
        Check.True(!Regex.IsMatch(ps, @"if\s*\(\s*\$\w+\s*\)\s*\{\s*\$projects\[", RegexOptions.IgnoreCase), "no project is added conditionally");

        var required = Regex.Match(ps, @"\$required\s*=\s*@\((?<list>.*?)\)", RegexOptions.Singleline).Groups["list"].Value;
        foreach (var f in new[] { "AiCockpit.exe", "cockpit.exe", "cockpit-mcp-bridge.exe", "cockpit-swx-worker.exe", "launch.bat", "install-shortcut.vbs" })
            Check.True(required.Contains($"'{f}'", StringComparison.Ordinal), $"package check requires {f}");
        foreach (var theme in Enum.GetNames<Cockpit.UI.CockpitTheme>())
            Check.True(required.Contains($"'Themes\\{theme}.xaml'", StringComparison.Ordinal), $"package check requires Themes\\{theme}.xaml");

        Check.True(Regex.IsMatch(ps, @"Copy-Item \(Join-Path \$PSScriptRoot 'launch-cockpit\.bat'\) \(Join-Path \$OutDir 'launch\.bat'\)"), "launch-cockpit.bat is published as launch.bat");
        Check.True(Regex.IsMatch(ps, @"Copy-Item \(Join-Path \$PSScriptRoot 'install-shortcut\.vbs'\)"), "install-shortcut.vbs is published");
        Check.True(ps.Contains("-p:TreatWarningsAsErrors=true", StringComparison.Ordinal) && ps.Contains("-p:CockpitRequireSolidWorks=true", StringComparison.Ordinal), "warnings as errors; -RequireSolidWorks maps to CockpitRequireSolidWorks");
        Check.True(ps.Contains("*.pdb", StringComparison.Ordinal), "symbols left out of the clean package");
        return Task.CompletedTask;
    }
}
