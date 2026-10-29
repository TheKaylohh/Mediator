using System.Runtime.InteropServices;

namespace Cockpit.Wpf;

/// <summary>
/// Places the panel on the monitor under the mouse, at the bottom-right of that monitor's work area (above the
/// taskbar, like Toolbox). Works in physical pixels with SetWindowPos, so mixed-DPI setups are handled: when the
/// window lands on a monitor with another scale, WPF resizes it (WM_DPICHANGED) and the caller places it again.
/// </summary>
internal static partial class MonitorPlacement
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>The cursor position now (physical pixels), to choose the monitor when the panel opens.</summary>
    public static POINT? Cursor() => GetCursorPos(out var p) ? p : null;

    /// <summary>Moves the window to the bottom-right of the work area of the monitor containing <paramref name="anchor"/>.</summary>
    public static bool PlaceBottomRight(IntPtr hwnd, POINT anchor, int marginPx = 8)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        IntPtr monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var rect)) return false;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var work = info.rcWork;
        int x = Math.Max(work.Left, work.Right - width - marginPx);
        int y = Math.Max(work.Top, work.Bottom - height - marginPx);
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
