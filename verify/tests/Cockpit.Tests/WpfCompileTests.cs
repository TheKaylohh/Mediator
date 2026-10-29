using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Cockpit.Core.Runtime;
using Cockpit.UI;
using Cockpit.Verify.WpfStub;
using Cockpit.Wpf;

namespace Cockpit.Tests;

/// <summary>
/// V9: the overlay's C# is compiled for real on Linux (every src/Cockpit.Wpf/*.cs, against verify/stubs/WpfCompileStub
/// and the generated XAML partials; run-tests.sh builds it with warnings as errors). These tests run the parts of that
/// code that do not need a real window, and prove the compile pass would catch the mistakes it exists for.
/// </summary>
static class WpfCompileTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-wpfc", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>The value converters and ThemeManager's three-step load (compiled resource → loose file → keep current).</summary>
    public static Task ConvertersAndThemeManager()
    {
        var culture = CultureInfo.InvariantCulture;
        var b2v = new BoolToVisibilityConverter();
        Check.True(b2v.Convert(true, typeof(Visibility), null!, culture) is Visibility.Visible && b2v.Convert(false, typeof(Visibility), null!, culture) is Visibility.Collapsed, "bool → visibility");
        Check.True(b2v.Convert("yes", typeof(Visibility), null!, culture) is Visibility.Collapsed, "non-bool counts as false");
        Check.True(b2v.ConvertBack(Visibility.Visible, typeof(bool), null!, culture) is true, "back");
        var inv = new BoolToVisibilityConverter { Invert = true };
        Check.True(inv.Convert(true, typeof(Visibility), null!, culture) is Visibility.Collapsed && inv.ConvertBack(Visibility.Collapsed, typeof(bool), null!, culture) is true, "inverted");

        var empty = new EmptyToVisibilityConverter();
        foreach (object? v in new object?[] { null, "", 0, new List<int>(), Array.Empty<string>() })
            Check.True(empty.Convert(v!, typeof(Visibility), null!, culture) is Visibility.Collapsed, $"empty {v ?? "null"} → collapsed");
        foreach (object v in new object[] { "x", 3, new List<int> { 1 }, new object() })
            Check.True(empty.Convert(v, typeof(Visibility), null!, culture) is Visibility.Visible, $"non-empty {v} → visible");
        Check.True(empty.Convert("", typeof(Visibility), "INVERT", culture) is Visibility.Visible, "'invert' parameter flips (case-insensitive)");
        Check.True(ReferenceEquals(empty.ConvertBack(Visibility.Visible, typeof(object), null!, culture), System.Windows.Data.Binding.DoNothing), "one-way: DoNothing back");

        // JobStateToBrush looks the brush up in the application's resources (the theme dictionary), Gray when missing.
        var app = Application.Current ?? new Application();
        var accent = new System.Windows.Media.SolidColorBrush();
        var theme = new ResourceDictionary { ["AccentBrush"] = accent };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(theme);
        var brush = new JobStateToBrushConverter();
        Check.True(ReferenceEquals(brush.Convert(JobState.Running, typeof(object), null!, culture), accent), "running → AccentBrush from the theme");
        Check.True(ReferenceEquals(brush.Convert(JobState.Failed, typeof(object), null!, culture), System.Windows.Media.Brushes.Gray), "DangerBrush missing → Gray");

        // ThemeManager: absolute pack URIs for every theme.
        foreach (var t in Enum.GetValues<CockpitTheme>())
        {
            var uri = ThemeManager.UriFor(t);
            Check.True(uri.IsAbsoluteUri && uri.Scheme == "pack" && uri.OriginalString == ThemeSources.PackUri(t), $"{t}: {uri}");
        }

        // 1. Compiled resource found: used, LoadedFrom is the pack URI.
        var failures = new List<string>();
        void OnFail(CockpitTheme t, string why) => failures.Add($"{t}: {why}");
        ThemeManager.LoadFailed += OnFail;
        string loose = ThemeSources.LooseFile(AppContext.BaseDirectory, CockpitTheme.ProDark);
        try
        {
            StubHooks.ResourceLoader = uri => uri.OriginalString.EndsWith("/Themes/ProDark.xaml", StringComparison.Ordinal) ? new ResourceDictionary { ["MarkerFrom"] = "compiled" } : StubHooks.DefaultLoader(uri);
            Check.True(ThemeManager.TryApply(CockpitTheme.ProDark), "compiled ProDark applied");
            Check.True(app.Resources.MergedDictionaries[0]["MarkerFrom"] as string == "compiled" && ThemeManager.LoadedFrom == ThemeSources.PackUri(CockpitTheme.ProDark), "merged[0] replaced, from the pack URI");

            // 2. Compiled resource missing (IOException "Cannot locate resource"): the loose copy next to the exe.
            StubHooks.ResourceLoader = StubHooks.DefaultLoader;
            Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
            File.Copy(Path.Combine(XamlChecker.RepoRoot(), "src", "Cockpit.Wpf", "Themes", "ProDark.xaml"), loose, overwrite: true);
            Check.True(ThemeManager.TryApply(CockpitTheme.ProDark), "loose ProDark applied");
            var looseDict = app.Resources.MergedDictionaries[0];
            Check.True(ThemeManager.LoadedFrom == loose && looseDict.Contains("AccentBrush") && looseDict.Count == 44, $"loaded from {ThemeManager.LoadedFrom} with all 44 keys ({looseDict.Count})");
            Check.Equal(0, failures.Count, "no failure reported while a source worked");

            // 3. Neither: the current theme stays, LoadFailed names both reasons.
            File.Delete(loose);
            Check.True(!ThemeManager.TryApply(CockpitTheme.ProDark), "nothing to load");
            Check.True(ReferenceEquals(app.Resources.MergedDictionaries[0], looseDict), "current theme kept");
            Check.True(failures.Count == 1 && failures[0].Contains("Cannot locate resource", StringComparison.Ordinal) && failures[0].Contains("not found", StringComparison.Ordinal), "both reasons: " + string.Join(" | ", failures));

            // A corrupt loose file is a reported failure too, never an exception.
            File.WriteAllText(loose, "<ResourceDictionary");
            Check.True(!ThemeManager.TryApply(CockpitTheme.ProDark) && failures.Count == 2, "malformed loose theme reported: " + failures[^1]);
        }
        finally
        {
            ThemeManager.LoadFailed -= OnFail;
            StubHooks.ResourceLoader = StubHooks.DefaultLoader;
            if (File.Exists(loose)) File.Delete(loose);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The compile pass catches the mistakes hand review misses: a copy of the overlay (with the projects it needs) is
    /// built with three mutations, and each must be a compiler error: a dropped `using System.IO;` (WPF projects have
    /// no implicit System.IO), an x:Name renamed in the XAML but not in the code-behind, and a member that does not
    /// exist on a Windows Forms type.
    /// </summary>
    public static async Task CompilePassCatchesMistakes()
    {
        string root = XamlChecker.RepoRoot();
        string copy = TempDir("mut");
        foreach (var f in new[] { "Directory.Build.props", "Directory.Build.targets" }) File.Copy(Path.Combine(root, f), Path.Combine(copy, f));
        void CopyTree(string rel)
        {
            string from = Path.Combine(root, rel);
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                         .Where(f => !Path.GetRelativePath(from, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")))
            {
                string to = Path.Combine(copy, rel, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        }
        foreach (var rel in new[] { "src/Cockpit.Wpf", "src/Cockpit.UI", "src/Cockpit.Core", "src/Cockpit.Protocol", "verify/stubs/WpfCompileStub", "verify/stubs/SqliteShim", "verify/stubs/HashingShim" })
            CopyTree(rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(copy, "verify", "local-feed"));

        string wpf = Path.Combine(copy, "src", "Cockpit.Wpf");
        void Mutate(string file, string from, string to)
        {
            string p = Path.Combine(wpf, file);
            string text = File.ReadAllText(p);
            Check.True(text.Contains(from, StringComparison.Ordinal), $"mutation anchor '{from}' in {file}");
            File.WriteAllText(p, text.Replace(from, to, StringComparison.Ordinal));
        }
        Mutate("MainWindow.xaml.cs", "using System.IO;\n", "");
        Mutate("MainWindow.xaml", "x:Name=\"PromptBox\"", "x:Name=\"PromptInput\"");
        Mutate("TrayIcon.cs", "_icon.Visible = false;", "_icon.IsVisible = false;");

        var psi = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = wpf };
        foreach (var a in new[] { "build", "-nologo", "-v", "q", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:CockpitVerify=true", "-p:TreatWarningsAsErrors=true" })
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(110));
        string output = await stdout + await stderr;
        Check.True(p.ExitCode != 0, "the mutated overlay does not build");
        foreach (var (file, error, what) in new[]
        {
            ("MainWindow.xaml.cs", "error CS0246: The type or namespace name 'MemoryStream'", "a missing using System.IO"),
            ("MainWindow.xaml.cs", "error CS0103: The name 'PromptBox' does not exist", "an x:Name renamed in the XAML only"),
            ("TrayIcon.cs", "error CS1061: 'NotifyIcon' does not contain a definition for 'IsVisible'", "a member that Windows Forms does not have"),
        })
            Check.True(output.Split('\n').Any(l => l.Contains(file, StringComparison.Ordinal) && l.Contains(error, StringComparison.Ordinal)), $"compile pass catches {what}:\n{output}");
    }
}
