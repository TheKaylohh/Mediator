using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Cockpit.Core.Runtime;

namespace Cockpit.Wpf;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

/// <summary>Collapsed when the value is null, "", 0 or an empty collection; ConverterParameter "invert" flips it.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => true,
            string s => s.Length == 0,
            int n => n == 0,
            ICollection c => c.Count == 0,
            _ => false,
        };
        bool invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return empty ^ invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class JobStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            JobState.Starting or JobState.Running => "AccentBrush",
            JobState.Succeeded or JobState.Ended => "SuccessBrush",
            JobState.Killed or JobState.Failed => "DangerBrush",
            _ => "MutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
