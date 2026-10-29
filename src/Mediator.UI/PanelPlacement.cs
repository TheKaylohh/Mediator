namespace Mediator.UI;

/// <summary>A screen edge. The values are the Win32 ABE_* constants (ABE_LEFT = 0 … ABE_BOTTOM = 3).</summary>
public enum ScreenEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>A rectangle in physical pixels, virtual-screen coordinates (left/top can be negative on secondary monitors).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(PixelRect r) => r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;
    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}×{Height}";
}

/// <summary>
/// What placement needs to know about one monitor (gathered with Win32 by Mediator.Wpf's MonitorPlacement).
/// <paramref name="AutoHideTaskbars"/>: edges with an auto-hide taskbar on this monitor and its thickness in pixels
/// when shown (0 = unknown, <see cref="PanelPlacement.AutoHideTaskbarDip"/> is assumed).
/// </summary>
public sealed record MonitorSnapshot(PixelRect Monitor, PixelRect WorkArea, uint Dpi, ScreenEdge? TaskbarEdge, IReadOnlyDictionary<ScreenEdge, int> AutoHideTaskbars)
{
    public double Scale => (Dpi == 0 ? 96 : Dpi) / 96.0;
}

/// <summary>
/// Where the panel goes: <see cref="Window"/> in physical pixels, inside <see cref="Usable"/>, anchored at the corner
/// nearest the notification area. Kept while the panel is open so a height change re-anchors in place
/// (<see cref="PanelPlacement.Reanchor"/>) instead of growing downwards and jumping back.
/// </summary>
public sealed record PanelPlacementResult(PixelRect Window, PixelRect Usable, ScreenEdge Vertical, ScreenEdge Horizontal, double Scale)
{
    /// <summary>The tallest the window may be, in device-independent units at this monitor's scale (WPF MaxHeight).</summary>
    public double MaxHeightDip => Usable.Height / Scale;
    public double MaxWidthDip => Usable.Width / Scale;
}

/// <summary>
/// The panel's placement rules (V8), pure arithmetic so they are tested on any OS:
///  • the work area excludes docked taskbars on any edge (Windows reports it so); an auto-hide taskbar does not shrink
///    the work area, so its strip (its shown thickness) is kept free, otherwise the taskbar would pop up over the panel;
///  • the anchor is the corner by the tray: bottom-right for a bottom or right taskbar, top-right for a top taskbar,
///    bottom-left for a left taskbar;
///  • sizes are converted at the TARGET monitor's DPI, so a window moving from a 100 % to a 200 % screen is placed
///    for its final size and does not land half off-screen;
///  • the window never leaves the usable area: if it is larger it is clamped to the top/left edge (the header stays
///    visible) and <see cref="PanelPlacementResult.MaxHeightDip"/> caps WPF's SizeToContent height.
/// </summary>
public static class PanelPlacement
{
    /// <summary>Gap between the window's (transparent, shadow-carrying) edge and the usable area.</summary>
    public const double MarginDip = 6;
    /// <summary>Thickness assumed for an auto-hide taskbar whose size is unknown (the Windows 11 default).</summary>
    public const double AutoHideTaskbarDip = 48;

    public static int ToPixels(double dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
    public static double ToDip(int pixels, uint dpi) => pixels * 96.0 / (dpi == 0 ? 96 : dpi);

    /// <summary>The edge on which the work area is smaller than the monitor (a docked taskbar), or null.</summary>
    public static ScreenEdge? InferTaskbarEdge(PixelRect monitor, PixelRect work)
    {
        var insets = new (ScreenEdge Edge, int Inset)[]
        {
            (ScreenEdge.Bottom, monitor.Bottom - work.Bottom), (ScreenEdge.Top, work.Top - monitor.Top),
            (ScreenEdge.Left, work.Left - monitor.Left), (ScreenEdge.Right, monitor.Right - work.Right),
        };
        var widest = insets.OrderByDescending(i => i.Inset).First(); // ties keep the order above (bottom first)
        return widest.Inset > 0 ? widest.Edge : null;
    }

    /// <summary>Anchor corner for a taskbar edge (null = bottom-right, the Windows default).</summary>
    public static (ScreenEdge Vertical, ScreenEdge Horizontal) Corner(ScreenEdge? taskbar) => taskbar switch
    {
        ScreenEdge.Top => (ScreenEdge.Top, ScreenEdge.Right),
        ScreenEdge.Left => (ScreenEdge.Bottom, ScreenEdge.Left),
        _ => (ScreenEdge.Bottom, ScreenEdge.Right),
    };

    /// <summary>The work area minus auto-hide taskbar strips and the margin.</summary>
    public static PixelRect UsableArea(MonitorSnapshot m)
    {
        var w = m.WorkArea;
        int left = w.Left, top = w.Top, right = w.Right, bottom = w.Bottom;
        foreach (var (edge, thickness) in m.AutoHideTaskbars)
        {
            // Only when the work area does not already exclude that edge (it never does for auto-hide, but be safe).
            int t = thickness > 0 ? thickness : ToPixels(AutoHideTaskbarDip, m.Scale);
            switch (edge)
            {
                case ScreenEdge.Bottom when bottom >= m.Monitor.Bottom: bottom = m.Monitor.Bottom - t; break;
                case ScreenEdge.Top when top <= m.Monitor.Top: top = m.Monitor.Top + t; break;
                case ScreenEdge.Left when left <= m.Monitor.Left: left = m.Monitor.Left + t; break;
                case ScreenEdge.Right when right >= m.Monitor.Right: right = m.Monitor.Right - t; break;
            }
        }
        int margin = ToPixels(MarginDip, m.Scale);
        left += margin; top += margin; right -= margin; bottom -= margin;
        if (right <= left) (left, right) = (w.Left, w.Right);   // absurdly small monitor: drop the insets
        if (bottom <= top) (top, bottom) = (w.Top, w.Bottom);
        return new PixelRect(left, top, right, bottom);
    }

    /// <summary>Places a window of <paramref name="widthDip"/> × <paramref name="heightDip"/> (device-independent) on <paramref name="m"/>.</summary>
    public static PanelPlacementResult Place(MonitorSnapshot m, double widthDip, double heightDip)
    {
        var usable = UsableArea(m);
        var taskbar = m.TaskbarEdge ?? InferTaskbarEdge(m.Monitor, m.WorkArea) ?? m.AutoHideTaskbars.Keys.Cast<ScreenEdge?>().FirstOrDefault();
        var (vertical, horizontal) = Corner(taskbar);
        int w = Math.Min(ToPixels(widthDip, m.Scale), usable.Width);
        int h = Math.Min(ToPixels(heightDip, m.Scale), usable.Height);
        var anchor = new PanelPlacementResult(default, usable, vertical, horizontal, m.Scale);
        return anchor with { Window = Reanchor(anchor, w, h) };
    }

    /// <summary>
    /// Where a window of <paramref name="widthPx"/> × <paramref name="heightPx"/> goes for the same anchor: used when
    /// SizeToContent or a DPI change resizes the open panel (WM_WINDOWPOSCHANGING), so it never jumps. The size is
    /// not changed; only the position, clamped so the top-left corner (the header) stays in the usable area.
    /// </summary>
    public static PixelRect Reanchor(PanelPlacementResult anchor, int widthPx, int heightPx)
    {
        var u = anchor.Usable;
        int x = anchor.Horizontal == ScreenEdge.Left ? u.Left : u.Right - widthPx;
        int y = anchor.Vertical == ScreenEdge.Top ? u.Top : u.Bottom - heightPx;
        x = Math.Max(u.Left, Math.Min(x, u.Right - widthPx));
        y = Math.Max(u.Top, Math.Min(y, u.Bottom - heightPx));
        return new PixelRect(x, y, x + widthPx, y + heightPx);
    }
}
