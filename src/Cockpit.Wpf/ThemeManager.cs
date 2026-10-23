using System.Windows;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// Swaps the theme dictionary at Application.Resources.MergedDictionaries[0]. Every brush, colour, font and radius
/// in the windows and styles is a DynamicResource, so the change applies at once without reopening anything.
/// The three dictionaries define identical key sets (checked offline by XamlChecker).
/// </summary>
public static class ThemeManager
{
    /// <summary>Themes/&lt;name&gt;.xaml for every <see cref="CockpitTheme"/>.</summary>
    public static Uri UriFor(CockpitTheme theme) => new($"/AiCockpit;component/Themes/{theme}.xaml", UriKind.Relative);

    public static void Apply(CockpitTheme theme)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary { Source = UriFor(theme) };
        if (merged.Count == 0) merged.Add(next);
        else merged[0] = next;
    }
}
