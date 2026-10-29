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
        var projects = Regex.Match(ps, @"Projects\s*=\s*\[ordered\]@\{(?<body>.*?)\n\s*\}", RegexOptions.Singleline);
        Check.True(projects.Success, "the Projects table is found");
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

    /// <summary>V9: publish-release.ps1 restores from NuGet and falls back to the offline shims, and that build needs nothing NuGet-only.</summary>
    public static Task PublishScriptFallsBackToShims()
    {
        string ps = File.ReadAllText(Path.Combine(Scripts, "publish-release.ps1"));
        Check.True(Regex.IsMatch(ps, @"\[switch\]\$UseShims") && Regex.IsMatch(ps, @"\[switch\]\$NoShimFallback"), "-UseShims and -NoShimFallback switches");
        var plan = Regex.Match(ps, @"function Get-PublishPlan\(\[bool\]\$offline\)\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline).Groups["body"].Value;
        Check.True(plan.Length > 0, "Get-PublishPlan found");
        Check.True(plan.Contains("if ($offline) { $c += '-p:CockpitUseShims=true' }", StringComparison.Ordinal), "offline plan passes -p:CockpitUseShims=true");
        Check.True(plan.Contains("if ($FrameworkDependent -or $offline) { '-p:SelfContained=false' }", StringComparison.Ordinal), "offline plan is framework-dependent (runtime packs are NuGet downloads)");
        Check.True(plan.Contains("$nativeAot = $aot -and -not $offline", StringComparison.Ordinal), "offline plan never uses Native AOT (ILCompiler is a NuGet download)");

        int restore = ps.IndexOf("& dotnet restore $p -r $Runtime", StringComparison.Ordinal);
        int fallback = ps.IndexOf("$plan = Get-PublishPlan $true", StringComparison.Ordinal);
        int publish = ps.IndexOf("& dotnet publish $p @common @extra -o $dest", StringComparison.Ordinal);
        Check.True(restore > 0 && fallback > restore && publish > fallback, "restore first, fall back on failure, then publish with the chosen plan");
        Check.True(Regex.IsMatch(ps[restore..fallback], @"if \(\$LASTEXITCODE -ne 0\) \{ \$restored = \$false; break \}") && ps[restore..fallback].Contains("if ($NoShimFallback) { throw", StringComparison.Ordinal),
            "a failed restore triggers the fallback unless -NoShimFallback");
        Check.True(ps.Contains("$aot = $false", StringComparison.Ordinal) && !Regex.IsMatch(ps, @"throw \('Native AOT"), "missing MSVC tools never stop the script");
        return Task.CompletedTask;
    }

    /// <summary>V9: the MSBuild switch itself, evaluated by MSBuild (dotnet msbuild -getProperty/-getItem), not read as text.</summary>
    public static async Task ShimSwitchEvaluation()
    {
        string root = XamlChecker.RepoRoot();
        async Task<string> Eval(string project, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Cockpit.Core.Runtime.ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (var a in new[] { "msbuild", Path.Combine(root, project), "-nologo" }.Concat(args)) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Check.True(p.ExitCode == 0, $"msbuild {string.Join(' ', args)} on {project}: {output}");
            return output.Trim();
        }
        string core = Path.Combine("src", "Cockpit.Core", "Cockpit.Core.csproj"), bridge = Path.Combine("src", "Cockpit.Bridge", "Cockpit.Bridge.csproj");
        string feed = Path.Combine(root, "verify", "local-feed");

        // Default ship build: NuGet packages, AOT on.
        var ship = System.Text.Json.Nodes.JsonNode.Parse(await Eval(core, "-p:CockpitVerify=false", "-getProperty:CockpitUseShims", "-getProperty:RestoreSources", "-getItem:PackageReference", "-getItem:ProjectReference"))!;
        Check.Equal("false", (string?)ship["Properties"]!["CockpitUseShims"], "ship default: no shims");
        var packages = ship["Items"]!["PackageReference"]!.AsArray().Select(i => (string)i!["Identity"]!).ToList();
        Check.True(packages.Contains("Microsoft.Data.Sqlite") && packages.Contains("System.IO.Hashing"), "ship default: NuGet packages " + string.Join(",", packages));
        Check.True((string?)ship["Properties"]!["RestoreSources"] is null or "", "ship default: normal NuGet sources");

        // Offline ship build: the real (non-verify) products, shims instead of packages, the empty local feed, no AOT.
        var offline = System.Text.Json.Nodes.JsonNode.Parse(await Eval(core, "-p:CockpitVerify=false", "-p:CockpitUseShims=true", "-getProperty:CockpitUseShims", "-getProperty:RestoreSources", "-getItem:PackageReference", "-getItem:ProjectReference"))!;
        Check.Equal("true", (string?)offline["Properties"]!["CockpitUseShims"], "offline: shims on");
        Check.True(offline["Items"]!["PackageReference"] is null || offline["Items"]!["PackageReference"]!.AsArray().Count == 0, "offline: no PackageReference at all");
        var refs = offline["Items"]!["ProjectReference"]!.AsArray().Select(i => (string)i!["Identity"]!).ToList();
        Check.True(refs.Any(r => r.EndsWith("SqliteShim.csproj", StringComparison.Ordinal)) && refs.Any(r => r.EndsWith("HashingShim.csproj", StringComparison.Ordinal)), "offline: shim projects referenced");
        Check.True(((string?)offline["Properties"]!["RestoreSources"] ?? "").TrimEnd('/', '\\').EndsWith(Path.Combine("verify", "local-feed"), StringComparison.Ordinal), "offline: restore only from the empty local feed");
        Check.True(Directory.Exists(feed) && !Directory.EnumerateFiles(feed, "*.nupkg").Any(), "the local feed is empty: nothing can come from NuGet");
        Check.Equal("", await Eval(bridge, "-p:CockpitVerify=false", "-p:CockpitUseShims=true", "-getProperty:PublishAot"), "offline: bridge without Native AOT");
        Check.Equal("true", await Eval(bridge, "-p:CockpitVerify=false", "-getProperty:PublishAot"), "ship default: bridge with Native AOT");
        Check.Equal("true", await Eval(core, "-p:CockpitVerify=true", "-getProperty:CockpitUseShims"), "verify builds default to shims");
    }

    /// <summary>
    /// V9: a real ship build (CockpitVerify=false) with CockpitUseShims=true of the console host and the bridge, from an
    /// empty package feed, then `cockpit doctor --json` from that build: the real databases open through the SQLite shim
    /// and the bridge self-test passes. The WPF overlay and the worker need the Windows SDK's desktop packs; the same
    /// switch builds them there (publish-release.ps1).
    /// </summary>
    public static async Task OfflineShipBuild()
    {
        string root = XamlChecker.RepoRoot();
        string copy = Path.Combine(Path.GetTempPath(), "cockpit-shimship", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(copy, "verify", "local-feed"));
        foreach (var f in new[] { "Directory.Build.props", "Directory.Build.targets" }) File.Copy(Path.Combine(root, f), Path.Combine(copy, f));
        foreach (var rel in new[] { "src/Cockpit.Protocol", "src/Cockpit.Core", "src/Cockpit.Bridge", "src/Cockpit.CliHost", "verify/stubs/SqliteShim", "verify/stubs/HashingShim" })
        {
            string from = Path.Combine(root, rel);
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(from, f).Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj")))
            {
                string to = Path.Combine(copy, rel, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        }
        async Task<(int Exit, string Output, string Stdout)> Run(string exe, IEnumerable<string> args, IDictionary<string, string>? env = null)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = copy };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));
            string stdout = await o;
            return (p.ExitCode, stdout + "\n" + await e, stdout);
        }
        string dotnet = Cockpit.Core.Runtime.ComponentLocator.DotnetHost();
        foreach (var project in new[] { "Cockpit.Bridge", "Cockpit.CliHost" })
        {
            var build = await Run(dotnet, ["build", Path.Combine("src", project), "-nologo", "-v", "q", "-nodeReuse:false", "-c", "Release",
                "-p:UseSharedCompilation=false", "-p:CockpitVerify=false", "-p:CockpitUseShims=true", "-p:TreatWarningsAsErrors=true", "-warnaserror"]);
            Check.True(build.Exit == 0 && build.Output.Contains("0 Warning(s)", StringComparison.Ordinal), $"{project}: ship build with shims, no NuGet, 0 warnings:\n{build.Output}");
            var assets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(copy, "src", project, "obj", "project.assets.json")))!;
            var packages = assets["libraries"]!.AsObject().Where(kv => (string?)kv.Value!["type"] == "package").Select(kv => kv.Key).ToList();
            Check.True(packages.Count == 0, $"{project}: no NuGet package in the restore graph ({string.Join(", ", packages)})");
        }
        string bin = Path.Combine(copy, "src", "Cockpit.CliHost", "bin", "Release", "net8.0");
        Check.True(File.Exists(Path.Combine(bin, "Microsoft.Data.Sqlite.CockpitShim.dll")) && !File.Exists(Path.Combine(bin, "Microsoft.Data.Sqlite.dll")), "the shim ships instead of the package");

        string data = Path.Combine(copy, "data");
        var doctor = await Run(dotnet, [Path.Combine(bin, "cockpit.dll"), "doctor", "--json", "--bridge", Path.Combine(copy, "src", "Cockpit.Bridge", "bin", "Release", "net8.0", "cockpit-mcp-bridge.dll")],
            new Dictionary<string, string> { ["COCKPIT_DATA_DIR"] = data });
        var checks = System.Text.Json.Nodes.JsonNode.Parse(doctor.Stdout)!.AsArray().ToDictionary(c => (string)c!["name"]!, c => (string)c!["status"]!);
        foreach (var name in new[] { "state folder", "bus", "audit chain", "bridge", "bridge self-test" })
            Check.True(checks.GetValueOrDefault(name) == "ok", $"doctor '{name}' is ok in the offline build:\n{doctor.Output}");
        Check.True(File.Exists(Path.Combine(data, "audit.db")) && File.Exists(Path.Combine(data, "journal.db")) && File.Exists(Path.Combine(data, "operations.db")), "real databases created through the shim");
    }
}
