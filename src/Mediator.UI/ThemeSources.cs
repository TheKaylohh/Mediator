namespace Mediator.UI;

/// <summary>
/// Where a theme dictionary comes from. Strings only: the <c>pack://</c> scheme is registered by WPF, so a
/// <see cref="Uri"/> is built from these in Mediator.Wpf (ThemeManager) once WPF is loaded. Kept here so the rules are
/// tested on any OS, and so XamlChecker can check the URIs against the real project (assembly name, file present).
/// </summary>
public static class ThemeSources
{
    /// <summary>The overlay's assembly name. Must equal &lt;AssemblyName&gt; in Mediator.Wpf.csproj (XamlChecker enforces it).</summary>
    public const string WpfAssemblyName = "Mediator";

    /// <summary>Folder (relative to the WPF project and to the published Mediator.exe) that holds the theme dictionaries.</summary>
    public const string Folder = "Themes";

    /// <summary>V18: the V7-V17 themes, kept unchanged in Themes/Legacy.</summary>
    public const string LegacyFolder = "Legacy";

    public static bool IsLegacy(MediatorTheme theme) => theme is MediatorTheme.HoloHud or MediatorTheme.ProDark or MediatorTheme.CleanLight;

    /// <summary>The light themes (the toggle goes from these to Dark).</summary>
    public static bool IsLight(MediatorTheme theme) => theme is MediatorTheme.Light or MediatorTheme.CleanLight;

    /// <summary>
    /// "Themes/Dark.xaml", "Themes/Legacy/HoloHud.xaml": the compiled resource's path inside the assembly, and the loose file's
    /// path next to the exe.
    /// </summary>
    public static string RelativePath(MediatorTheme theme) => IsLegacy(theme) ? $"{Folder}/{LegacyFolder}/{theme}.xaml" : $"{Folder}/{theme}.xaml";

    /// <summary>
    /// The absolute pack URI of the compiled (BAML) dictionary, e.g.
    /// <c>pack://application:,,,/Mediator;component/Themes/Dark.xaml</c>. Absolute, so it resolves the same way
    /// whatever the caller's base URI is (a relative <c>/Mediator;component/…</c> URI set from code depends on it).
    /// </summary>
    public static string PackUri(MediatorTheme theme) => $"pack://application:,,,/{WpfAssemblyName};component/{RelativePath(theme)}";

    /// <summary>The loose copy published next to the exe (<c>&lt;base&gt;\Themes\Dark.xaml</c>): the fallback when the compiled resource cannot be found.</summary>
    public static string LooseFile(string baseDirectory, MediatorTheme theme) => Path.Combine([baseDirectory, .. RelativePath(theme).Split('/')]);
}
