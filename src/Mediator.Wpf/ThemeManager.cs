using System.IO;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Markup;
using Mediator.UI;

namespace Mediator.Wpf;

/// <summary>
/// Swaps the theme dictionary at Application.Resources.MergedDictionaries[0]. Every brush, colour, font and radius
/// in the windows and styles is a DynamicResource, so the change applies at once without reopening anything.
/// The three dictionaries define identical key sets (checked offline by XamlChecker).
///
/// Loading order (V8):
///   1. the compiled resource through its absolute pack URI (<see cref="ThemeSources.PackUri"/>);
///   2. if that throws (IOException "Cannot locate resource": unusual hosts, trimmed or repackaged builds), the loose
///      copy that the build publishes next to the exe (<see cref="ThemeSources.LooseFile"/>);
///   3. if both fail, the current dictionary stays (at startup that is Dark, merged by App.xaml) and
///      <see cref="LoadFailed"/> reports why. A theme problem never stops the app.
/// </summary>
public static class ThemeManager
{
    static ThemeManager()
    {
        // new Uri("pack://…") throws UriFormatException ("Invalid port specified") until the pack:// scheme is
        // registered. Application has done that by the time OnStartup runs; touching PackUriHelper (whose static
        // constructor registers it) also covers callers that run earlier. A failure is still caught in Load.
        _ = PackUriHelper.UriSchemePack;
    }

    /// <summary>Raised with the theme and the reasons when neither the compiled resource nor the loose file could be loaded.</summary>
    public static event Action<MediatorTheme, string>? LoadFailed;

    /// <summary>Where the dictionary in use was loaded from: the pack URI, or the loose file's path.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>The absolute pack URI of Themes/&lt;name&gt;.xaml for every <see cref="MediatorTheme"/>.</summary>
    public static Uri UriFor(MediatorTheme theme) => new(ThemeSources.PackUri(theme), UriKind.Absolute);

    public static void Apply(MediatorTheme theme) => TryApply(theme);

    /// <summary>Loads and merges <paramref name="theme"/>; false (and the current theme kept) when it cannot be loaded.</summary>
    public static bool TryApply(MediatorTheme theme)
    {
        if (Load(theme, out string? from, out string problem) is not { } next)
        {
            LoadFailed?.Invoke(theme, problem);
            return false;
        }
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(next);
        else merged[0] = next;
        LoadedFrom = from;
        return true;
    }

    private static ResourceDictionary? Load(MediatorTheme theme, out string? from, out string problem)
    {
        from = null;
        var problems = new List<string>(2);

        // 1. Compiled resource (the normal case).
        try
        {
            var uri = UriFor(theme);
            var compiled = new ResourceDictionary { Source = uri };
            from = uri.ToString();
            problem = "";
            return compiled;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or XamlParseException or InvalidOperationException)
        {
            problems.Add($"{ThemeSources.PackUri(theme)}: {ex.Message}");
        }

        // 2. Loose copy next to the exe. Only this fixed, known file name is ever read (never a user-supplied path).
        string loose = ThemeSources.LooseFile(AppContext.BaseDirectory, theme);
        try
        {
            if (!File.Exists(loose))
            {
                problems.Add($"{loose}: not found");
            }
            else
            {
                using var stream = File.OpenRead(loose);
                var context = new ParserContext { BaseUri = new Uri(loose, UriKind.Absolute) };
                if (XamlReader.Load(stream, context) is ResourceDictionary dictionary)
                {
                    from = loose;
                    problem = "";
                    return dictionary;
                }
                problems.Add($"{loose}: the root element is not a ResourceDictionary");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XamlParseException)
        {
            problems.Add($"{loose}: {ex.Message}");
        }

        problem = string.Join("; ", problems);
        return null;
    }
}
