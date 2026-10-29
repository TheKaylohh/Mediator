using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Mediator.Core.Runtime;

namespace Mediator.Wpf;

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

/// <summary>
/// V15: the Windows shell icon of an .exe ("C:\…\WINWORD.EXE") or of a file type (".docx") as an ImageSource, cached.
/// With Missing="True" it instead returns Visible when there is no icon, for the glyph shown in its place.
/// </summary>
public sealed partial class FileIconConverter : IValueConverter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public bool Missing { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        ImageSource? icon = value is string { Length: > 0 } path ? Cache.GetOrAdd(path, Load) : null;
        if (Missing) return icon is null ? Visibility.Visible : Visibility.Collapsed;
        return icon;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static ImageSource? Load(string path)
    {
        bool extension = path.StartsWith('.');
        try
        {
            if (!extension && !System.IO.File.Exists(path)) return null;
            var info = default(ShFileInfo);
            uint flags = ShgfiIcon | ShgfiLargeIcon | (extension ? ShgfiUseFileAttributes : 0);
            if (SHGetFileInfo(path, FileAttributeNormal, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return null;
            try
            {
                var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(info.Icon); }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0, ShgfiUseFileAttributes = 0x10, FileAttributeNormal = 0x80;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);
}

/// <summary>V15: a connector's status kind ("live", "warn", "off") as the theme's success, warning or muted brush.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = (value as string) switch
        {
            "live" => "SuccessBrush",
            "warn" => "WarningBrush",
            _ => "MutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
