using System.Runtime.InteropServices;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// Win32 side of the panel's placement (V8). Gathers what <see cref="PanelPlacement"/> needs about the monitor under
/// the anchor point (work area, DPI, which edge the taskbar is on, auto-hide taskbars) and moves the window with
/// SetWindowPos in physical pixels. No WPF types here: this file is also compiled by the Linux verify build, and the
/// WINDOWPOS handling is unit-tested there.
///
/// Why the old placement flickered on mixed-DPI setups: the window was shown first and moved afterwards, its size was
/// taken at the old monitor's DPI, and every SizeToContent growth first extended the window downwards (one frame off
/// the bottom of the screen) before SizeChanged moved it back up. Now the window is placed before it is shown, sizes
/// are converted at the target monitor's DPI (GetDpiForMonitor; GetDpiForWindow for the current size), and
/// <see cref="KeepAnchored"/> rewrites the position inside WM_WINDOWPOSCHANGING so any resize (content, DPI change)
/// lands anchored in the same message, with no intermediate frame.
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    internal const int WM_WINDOWPOSCHANGING = 0x0046;
    internal const int WM_DISPLAYCHANGE = 0x007E;
    internal const int WM_SETTINGCHANGE = 0x001A;
    internal const int SPI_SETWORKAREA = 0x002F;

    internal const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_HIDEWINDOW = 0x0080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint ABM_GETTASKBARPOS = 0x5, ABM_GETAUTOHIDEBAREX = 0xB;
    private const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Windows 10 1607+. 0 if the window is invalid.</summary>
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Windows 8.1+ (shcore). The effective DPI of a monitor: the scale the user chose for it.</summary>
    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("shell32.dll")]
    private static partial UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    /// <summary>The cursor position now (physical pixels), to choose the monitor when the panel opens.</summary>
    public static POINT? Cursor() => GetCursorPos(out var p) ? p : null;

    private static PixelRect Px(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Monitor, work area, DPI and taskbars of the monitor nearest <paramref name="anchor"/>; null if Win32 fails.</summary>
    public static MonitorSnapshot? Describe(POINT anchor)
    {
        IntPtr monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;

        uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0 ? dx : 96;

        // The primary taskbar reports its edge; use it when it is on this monitor. Secondary taskbars are inferred
        // from the work area (PanelPlacement.InferTaskbarEdge).
        ScreenEdge? edge = null;
        var bar = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref bar) != UIntPtr.Zero && bar.uEdge <= 3)
        {
            var rc = bar.rc;
            if (MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST) == monitor) edge = (ScreenEdge)bar.uEdge;
        }

        // Auto-hide taskbars on this monitor: the work area does not exclude them, so their strip is reserved.
        var autoHide = new Dictionary<ScreenEdge, int>();
        foreach (var e in new[] { ScreenEdge.Bottom, ScreenEdge.Top, ScreenEdge.Left, ScreenEdge.Right })
        {
            var query = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), uEdge = (uint)e, rc = info.rcMonitor };
            IntPtr hidden = (IntPtr)(nint)(nuint)SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref query);
            if (hidden == IntPtr.Zero) continue;
            int thickness = GetWindowRect(hidden, out var r) ? (e is ScreenEdge.Left or ScreenEdge.Right ? r.Right - r.Left : r.Bottom - r.Top) : 0;
            autoHide[e] = Math.Max(0, thickness);
        }
        return new MonitorSnapshot(Px(info.rcMonitor), Px(info.rcWork), dpi, edge, autoHide);
    }

    /// <summary>
    /// Places the window by the tray of the monitor containing <paramref name="anchor"/> and returns the placement
    /// (keep it for <see cref="KeepAnchored"/>), or null if a Win32 call failed. The window's current size is read
    /// with GetWindowRect and converted to device-independent units at its own DPI (GetDpiForWindow), then to pixels
    /// at the target monitor's DPI, so the position is right for the size the window will have there. Only the
    /// position is set (SWP_NOSIZE): WPF resizes the window itself when its DPI changes, and KeepAnchored follows.
    /// Works while the window is hidden, so it can be placed before it is shown.
    /// </summary>
    public static PanelPlacementResult? PlaceAboveTray(IntPtr hwnd, POINT anchor, double fallbackWidthDip = 452)
    {
        if (Describe(anchor) is not { } target || !GetWindowRect(hwnd, out var rect)) return null;
        uint windowDpi = GetDpiForWindow(hwnd) is var d and > 0 ? d : 96;
        double widthDip = rect.Right - rect.Left > 0 ? PanelPlacement.ToDip(rect.Right - rect.Left, windowDpi) : fallbackWidthDip;
        double heightDip = PanelPlacement.ToDip(Math.Max(0, rect.Bottom - rect.Top), windowDpi);
        var placed = PanelPlacement.Place(target, widthDip, heightDip);
        return SetWindowPos(hwnd, IntPtr.Zero, placed.Window.Left, placed.Window.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE) ? placed : null;
    }

    /// <summary>V7 name, kept for callers: same as <see cref="PlaceAboveTray"/>.</summary>
    public static bool PlaceBottomRight(IntPtr hwnd, POINT anchor) => PlaceAboveTray(hwnd, anchor) is not null;

    /// <summary>
    /// WM_WINDOWPOSCHANGING handler: when the window is being resized or moved, rewrites the proposed position so the
    /// window stays anchored to <paramref name="anchor"/>'s corner and inside its usable area. Returns true if the
    /// WINDOWPOS at <paramref name="lParam"/> was changed. <paramref name="currentSize"/> supplies the size when the
    /// message carries SWP_NOSIZE (a move only). Hides and pure z-order changes are left alone.
    /// </summary>
    public static bool KeepAnchored(IntPtr lParam, PanelPlacementResult anchor, Func<(int Width, int Height)?> currentSize)
    {
        if (lParam == IntPtr.Zero) return false;
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SWP_HIDEWINDOW) != 0 || (pos.flags & (SWP_NOSIZE | SWP_NOMOVE)) == (SWP_NOSIZE | SWP_NOMOVE)) return false;

        int cx = pos.cx, cy = pos.cy;
        if ((pos.flags & SWP_NOSIZE) != 0)
        {
            if (currentSize() is not { } size) return false;
            (cx, cy) = size;
        }
        var target = PanelPlacement.Reanchor(anchor, cx, cy);
        if ((pos.flags & SWP_NOMOVE) == 0 && pos.x == target.Left && pos.y == target.Top) return false;
        pos.x = target.Left;
        pos.y = target.Top;
        pos.flags &= ~SWP_NOMOVE;
        Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
        return true;
    }

    /// <summary>The window's current size in pixels, for <see cref="KeepAnchored"/>.</summary>
    public static (int Width, int Height)? WindowSize(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : null;
}
