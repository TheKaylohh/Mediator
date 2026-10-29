using System.Runtime.InteropServices;
using Cockpit.UI;
using Cockpit.Wpf;

namespace Cockpit.Tests;

/// <summary>
/// V8: multi-monitor / mixed-DPI placement of the panel. The geometry (Cockpit.UI PanelPlacement) and the
/// WM_WINDOWPOSCHANGING rewrite (Cockpit.Wpf MonitorPlacement.KeepAnchored, compiled in the verify build) run here;
/// only the Win32 queries that feed them (GetMonitorInfo, GetDpiForMonitor, SHAppBarMessage) need Windows.
/// </summary>
static class PlacementTests
{
    static readonly IReadOnlyDictionary<ScreenEdge, int> NoAutoHide = new Dictionary<ScreenEdge, int>();

    static MonitorSnapshot Mon(int l, int t, int r, int b, (int L, int T, int R, int B)? work = null, uint dpi = 96, ScreenEdge? edge = null, Dictionary<ScreenEdge, int>? autoHide = null)
    {
        var w = work ?? (l, t, r, b);
        return new MonitorSnapshot(new PixelRect(l, t, r, b), new PixelRect(w.L, w.T, w.R, w.B), dpi, edge, autoHide ?? new Dictionary<ScreenEdge, int>());
    }

    public static Task Geometry()
    {
        // 1080p at 100 %, bottom taskbar (48 px): bottom-right, 6 px margin.
        var fhd = PanelPlacement.Place(Mon(0, 0, 1920, 1080, (0, 0, 1920, 1032)), 452, 600);
        Check.Equal(new PixelRect(1462, 426, 1914, 1026), fhd.Window, "1080p 100 %: above the bottom taskbar, right edge");

        // Surface Pro at 200 %: 452×600 DIP is 904×1200 px; margin scales too.
        var surface = PanelPlacement.Place(Mon(0, 0, 2736, 1824, (0, 0, 2736, 1728), dpi: 192), 452, 600);
        Check.Equal(new PixelRect(1820, 516, 2724, 1716), surface.Window, "200 %: sizes converted at the target DPI");
        Check.Equal(852.0, surface.MaxHeightDip, "MaxHeight in DIP at 200 % = usable height / 2");

        // Mixed DPI: the window measured 452×600 px on a 100 % screen is placed on the 200 % screen for its final size.
        double wDip = PanelPlacement.ToDip(452, 96), hDip = PanelPlacement.ToDip(600, 96);
        Check.Equal(surface.Window, PanelPlacement.Place(Mon(0, 0, 2736, 1824, (0, 0, 2736, 1728), dpi: 192), wDip, hDip).Window, "100 % → 200 %: same result as a native 200 % window");
        Check.Equal(452.0, PanelPlacement.ToDip(904, 192), "GetDpiForWindow conversion back to DIP");

        // 150 %: rounding is consistent (452 × 1.5 = 678).
        var oneFifty = PanelPlacement.Place(Mon(0, 0, 2880, 1920, (0, 0, 2880, 1848), dpi: 144), 452, 500);
        Check.True(oneFifty.Window.Width == 678 && oneFifty.Window.Height == 750 && oneFifty.Window.Right == 2880 - 9 && oneFifty.Window.Bottom == 1848 - 9, $"150 % ({oneFifty.Window})");

        // A secondary monitor to the LEFT of the primary (negative coordinates), no taskbar on it.
        var left = PanelPlacement.Place(Mon(-1920, 0, 0, 1080), 452, 600);
        Check.Equal(new PixelRect(-458, 474, -6, 1074), left.Window, "negative virtual-screen coordinates, no taskbar: bottom-right");
        // …and one ABOVE the primary.
        var above = PanelPlacement.Place(Mon(0, -1440, 2560, 0, (0, -1440, 2560, -48)), 452, 600);
        Check.True(above.Window.Bottom == -54 && above.Window.Top == -654, $"monitor above the primary ({above.Window})");

        // Taskbar on the top / left / right edge: the corner follows the tray.
        var top = PanelPlacement.Place(Mon(0, 0, 1920, 1080, (0, 48, 1920, 1080)), 452, 600);
        Check.True(top.Vertical == ScreenEdge.Top && top.Window.Top == 54 && top.Window.Right == 1914, $"top taskbar: top-right below it ({top.Window})");
        var leftBar = PanelPlacement.Place(Mon(0, 0, 1920, 1080, (62, 0, 1920, 1080)), 452, 600);
        Check.True(leftBar.Horizontal == ScreenEdge.Left && leftBar.Window.Left == 68 && leftBar.Window.Bottom == 1074, $"left taskbar: bottom-left beside it ({leftBar.Window})");
        var rightBar = PanelPlacement.Place(Mon(0, 0, 1920, 1080, (0, 0, 1858, 1080)), 452, 600);
        Check.True(rightBar.Window.Right == 1852 && rightBar.Window.Bottom == 1074, $"right taskbar: bottom-right beside it ({rightBar.Window})");
        // The primary taskbar's reported edge wins over inference (e.g. a docked app bar on another edge).
        var reported = PanelPlacement.Place(Mon(0, 0, 1920, 1080, (0, 100, 1920, 1032), edge: ScreenEdge.Bottom), 452, 600);
        Check.True(reported.Vertical == ScreenEdge.Bottom && reported.Window.Bottom == 1026, "reported taskbar edge used");

        // Auto-hide taskbar: the work area is the whole monitor, so the shown taskbar's strip is reserved.
        var hidden = PanelPlacement.Place(Mon(0, 0, 1920, 1080, autoHide: new() { [ScreenEdge.Bottom] = 48 }), 452, 600);
        Check.True(hidden.Window.Bottom == 1080 - 48 - 6 && hidden.Vertical == ScreenEdge.Bottom, $"auto-hide bottom taskbar kept clear ({hidden.Window})");
        var hiddenTop = PanelPlacement.Place(Mon(0, 0, 2736, 1824, dpi: 192, autoHide: new() { [ScreenEdge.Top] = 0 }), 452, 600);
        Check.True(hiddenTop.Vertical == ScreenEdge.Top && hiddenTop.Window.Top == 96 + 12, $"auto-hide top taskbar of unknown size: 48 DIP × 2 reserved ({hiddenTop.Window})");

        // Too tall for the screen (1366×768 laptop, 720 DIP panel): clamped, header visible, MaxHeight lowered.
        var small = PanelPlacement.Place(Mon(0, 0, 1366, 768, (0, 0, 1366, 728)), 452, 720);
        Check.True(small.Window.Top == 6 && small.Window.Bottom == 722 && small.MaxHeightDip == 716, $"too tall: clamped to the usable height ({small.Window}, max {small.MaxHeightDip})");
        // Too wide (a 400 px-wide portrait strip): left-aligned inside, never past the left edge.
        var narrow = PanelPlacement.Place(Mon(5000, 0, 5400, 1200), 452, 300);
        Check.True(narrow.Usable.Contains(narrow.Window) && narrow.Window.Width == 388, $"too wide: clamped ({narrow.Window})");

        // Re-anchoring when SizeToContent grows the open panel: the bottom edge stays, the top moves up.
        var grown = PanelPlacement.Reanchor(fhd, 452, 700);
        Check.Equal(new PixelRect(1462, 326, 1914, 1026), grown, "grows upwards from the same bottom-right corner");
        var huge = PanelPlacement.Reanchor(fhd, 452, 5000);
        Check.True(huge.Top == fhd.Usable.Top, "taller than the screen: top edge pinned, never above the work area");
        var topGrown = PanelPlacement.Reanchor(top, 452, 700);
        Check.True(topGrown.Top == top.Window.Top, "top taskbar: grows downwards, top edge stays");

        // Property: for random monitors, DPIs, taskbars and sizes the window's top-left is always in the usable area,
        // and the whole window is whenever it fits.
        var rng = new Random(8);
        uint[] dpis = [96, 120, 144, 168, 192, 240, 288];
        for (int i = 0; i < 5000; i++)
        {
            int l = rng.Next(-8000, 8000), t = rng.Next(-5000, 5000), w = rng.Next(640, 7680), h = rng.Next(480, 4320);
            var edge = (ScreenEdge)rng.Next(0, 5) is var e && (int)e < 4 ? e : (ScreenEdge?)null;
            int bar = rng.Next(30, 120);
            var work = edge switch
            {
                ScreenEdge.Bottom => (l, t, l + w, t + h - bar), ScreenEdge.Top => (l, t + bar, l + w, t + h),
                ScreenEdge.Left => (l + bar, t, l + w, t + h), ScreenEdge.Right => (l, t, l + w - bar, t + h), _ => (l, t, l + w, t + h),
            };
            var auto = edge is null && rng.Next(3) == 0 ? new Dictionary<ScreenEdge, int> { [(ScreenEdge)rng.Next(0, 4)] = rng.Next(0, 80) } : null;
            var m = Mon(l, t, l + w, t + h, work, dpis[rng.Next(dpis.Length)], autoHide: auto);
            double wd = rng.Next(200, 1200), hd = rng.Next(0, 2000);
            var p = PanelPlacement.Place(m, wd, hd);
            Check.True(m.WorkArea.Contains(p.Usable), $"#{i} usable inside the work area");
            Check.True(p.Window.Left >= p.Usable.Left && p.Window.Top >= p.Usable.Top && p.Window.Left <= p.Usable.Right && p.Window.Top <= p.Usable.Bottom, $"#{i} top-left visible: {p.Window} in {p.Usable}");
            Check.True(p.Usable.Contains(p.Window), $"#{i} window inside the usable area: {p.Window} in {p.Usable}");
            var r = PanelPlacement.Reanchor(p, p.Window.Width, Math.Min(p.Usable.Height, p.Window.Height + rng.Next(0, 400)));
            Check.True(p.Usable.Contains(r), $"#{i} re-anchored growth stays inside: {r} in {p.Usable}");
        }
        return Task.CompletedTask;
    }

    /// <summary>The WM_WINDOWPOSCHANGING rewrite on a real unmanaged WINDOWPOS buffer (as Windows passes it).</summary>
    public static Task WindowPosChanging()
    {
        var anchor = PanelPlacement.Place(Mon(0, 0, 1920, 1080, (0, 0, 1920, 1032)), 452, 600);
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MonitorPlacement.WINDOWPOS>());
        try
        {
            MonitorPlacement.WINDOWPOS Run(MonitorPlacement.WINDOWPOS input, (int, int)? current, out bool changed)
            {
                Marshal.StructureToPtr(input, buffer, fDeleteOld: false);
                changed = MonitorPlacement.KeepAnchored(buffer, anchor, () => current);
                return Marshal.PtrToStructure<MonitorPlacement.WINDOWPOS>(buffer);
            }

            // SizeToContent growth: WPF resizes with SWP_NOMOVE at the old top-left → moved up in the same message.
            var grow = Run(new() { x = 1462, y = 426, cx = 904 / 2, cy = 700, flags = MonitorPlacement.SWP_NOMOVE | MonitorPlacement.SWP_NOZORDER }, null, out bool c1);
            Check.True(c1 && grow.x == 1462 && grow.y == 326 && (grow.flags & MonitorPlacement.SWP_NOMOVE) == 0 && grow.cx == 452 && grow.cy == 700,
                $"resize re-anchored, NOMOVE cleared, size untouched (x={grow.x} y={grow.y} flags={grow.flags:X})");

            // WM_DPICHANGED's suggested rect (new size at a position Windows chose) is corrected the same way.
            var dpi = Run(new() { x = 900, y = 300, cx = 452, cy = 600, flags = MonitorPlacement.SWP_NOZORDER | MonitorPlacement.SWP_NOACTIVATE }, null, out bool c2);
            Check.True(c2 && dpi.x == 1462 && dpi.y == 426, "DPI-change rect re-anchored");

            // Already right: nothing written.
            Run(new() { x = 1462, y = 426, cx = 452, cy = 600, flags = MonitorPlacement.SWP_NOZORDER }, null, out bool c3);
            Check.True(!c3, "a correct position is left alone");

            // Move only (SWP_NOSIZE): the current size comes from GetWindowRect (here: the callback).
            var move = Run(new() { x = 10, y = 10, flags = MonitorPlacement.SWP_NOSIZE | MonitorPlacement.SWP_NOZORDER }, (452, 500), out bool c4);
            Check.True(c4 && move.x == 1462 && move.y == 526, "a stray move is pulled back to the anchor");
            Run(new() { x = 10, y = 10, flags = MonitorPlacement.SWP_NOSIZE }, null, out bool c5);
            Check.True(!c5, "no current size known: left alone");

            // Hides and z-order-only changes are never touched.
            Run(new() { x = 0, y = 0, cx = 452, cy = 600, flags = MonitorPlacement.SWP_HIDEWINDOW }, null, out bool c6);
            Run(new() { flags = MonitorPlacement.SWP_NOSIZE | MonitorPlacement.SWP_NOMOVE }, (452, 600), out bool c7);
            Check.True(!c6 && !c7, "SWP_HIDEWINDOW and z-order-only messages untouched");
            Check.True(!MonitorPlacement.KeepAnchored(IntPtr.Zero, anchor, () => null), "null lParam ignored");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        Check.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<MonitorPlacement.WINDOWPOS>(), "WINDOWPOS layout matches winuser.h");
        Check.Equal(IntPtr.Size == 8 ? 48 : 36, Marshal.SizeOf<MonitorPlacement.APPBARDATA>(), "APPBARDATA layout matches shellapi.h");
        Check.Equal(40, Marshal.SizeOf<MonitorPlacement.MONITORINFO>(), "MONITORINFO layout matches winuser.h");
        return Task.CompletedTask;
    }
}
