namespace Cockpit.UI;

/// <summary>
/// Where a theme dictionary comes from. Strings only: the <c>pack://</c> scheme is registered by WPF, so a
/// <see cref="Uri"/> is built from these in Cockpit.Wpf (ThemeManager) once WPF is loaded. Kept here so the rules are
/// tested on any OS, and so XamlChecker can check the URIs against the real project (assembly name, file present).
/// </summary>
public static class ThemeSources
{
    /// <summary>The overlay's assembly name. Must equal &lt;AssemblyName&gt; in Cockpit.Wpf.csproj (XamlChecker enforces it).</summary>
    public const string WpfAssemblyName = "AiCockpit";

    /// <summary>Folder (relative to the WPF project and to the published AiCockpit.exe) that holds the theme dictionaries.</summary>
    public const string Folder = "Themes";

    /// <summary>"Themes/HoloHud.xaml": the compiled resource's path inside the assembly, and the loose file's path next to the exe.</summary>
    public static string RelativePath(CockpitTheme theme) => $"{Folder}/{theme}.xaml";

    /// <summary>
    /// The absolute pack URI of the compiled (BAML) dictionary, e.g.
    /// <c>pack://application:,,,/AiCockpit;component/Themes/HoloHud.xaml</c>. Absolute, so it resolves the same way
    /// whatever the caller's base URI is (a relative <c>/AiCockpit;component/…</c> URI set from code depends on it).
    /// </summary>
    public static string PackUri(CockpitTheme theme) => $"pack://application:,,,/{WpfAssemblyName};component/{RelativePath(theme)}";

    /// <summary>The loose copy published next to the exe (<c>&lt;base&gt;\Themes\HoloHud.xaml</c>): the fallback when the compiled resource cannot be found.</summary>
    public static string LooseFile(string baseDirectory, CockpitTheme theme) => Path.Combine(baseDirectory, Folder, $"{theme}.xaml");
}
