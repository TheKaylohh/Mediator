using System.Globalization;
using System.Text.Json.Nodes;

namespace Mediator.UI.Workspace;

/// <summary>The six things the right sidebar can show, in the order of the top bar's buttons.</summary>
public enum RightMode { Cockpit, Artifacts, Access, Activity, Knowledge, Software }

/// <summary>
/// What the user chose for the workspace window, saved in settings.json: sidebar widths (device-independent pixels), whether
/// each sidebar is shown, the right sidebar's mode, and the software pinned in the Software Library.
/// </summary>
public sealed record WorkspacePrefs
{
    public double LeftWidth { get; init; } = WorkspaceLayout.LeftDefault;
    public double RightWidth { get; init; } = WorkspaceLayout.RightDefault;
    public bool LeftVisible { get; init; } = true;
    public bool RightVisible { get; init; } = true;
    public RightMode Mode { get; init; } = RightMode.Cockpit;
    public List<string> PinnedSoftware { get; init; } = [];
    /// <summary>V18: where the full-screen app was last (restored bounds, device-independent pixels), or null: centred, maximized.</summary>
    public WindowBounds? Window { get; init; }

    /// <summary>Value equality, the pinned list included (a record compares lists by reference).</summary>
    public bool Equals(WorkspacePrefs? other) => other is not null && LeftWidth.Equals(other.LeftWidth) && RightWidth.Equals(other.RightWidth)
        && LeftVisible == other.LeftVisible && RightVisible == other.RightVisible && Mode == other.Mode && PinnedSoftware.SequenceEqual(other.PinnedSoftware)
        && Equals(Window, other.Window);

    public override int GetHashCode() => HashCode.Combine(LeftWidth, RightWidth, LeftVisible, RightVisible, Mode, PinnedSoftware.Count);

    public JsonObject ToJson() => new()
    {
        ["leftWidth"] = Math.Round(LeftWidth, 1), ["rightWidth"] = Math.Round(RightWidth, 1),
        ["leftVisible"] = LeftVisible, ["rightVisible"] = RightVisible, ["mode"] = Mode.ToString(),
        ["pinnedSoftware"] = new JsonArray(PinnedSoftware.Select(s => (JsonNode)s).ToArray()),
        ["window"] = Window?.ToJson(),
    };

    /// <summary>Reads what <see cref="ToJson"/> wrote; anything missing or out of range falls back to the default.</summary>
    public static WorkspacePrefs FromJson(JsonObject? o)
    {
        var d = new WorkspacePrefs();
        if (o is null) return d;
        static double Width(JsonNode? n, double fallback, double min, double max) =>
            n is JsonValue v && v.TryGetValue(out double w) && double.IsFinite(w) ? Math.Clamp(w, min, max) : fallback;
        return new WorkspacePrefs
        {
            LeftWidth = Width(o["leftWidth"], d.LeftWidth, WorkspaceLayout.LeftMin, WorkspaceLayout.LeftMax),
            RightWidth = Width(o["rightWidth"], d.RightWidth, WorkspaceLayout.RightMin, WorkspaceLayout.RightMax),
            LeftVisible = (bool?)o["leftVisible"] ?? true,
            RightVisible = (bool?)o["rightVisible"] ?? true,
            Mode = Enum.TryParse<RightMode>((string?)o["mode"], ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : d.Mode,
            PinnedSoftware = (o["pinnedSoftware"] as JsonArray ?? []).Select(n => (string?)n).OfType<string>().Distinct().ToList(),
            Window = WindowBounds.FromJson(o["window"] as JsonObject),
        };
    }
}

/// <summary>V18: the full-screen app's restored position and size, and whether it was maximized.</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized)
{
    public const double MinWidth = 720, MinHeight = 480;

    public JsonObject ToJson() => new()
    {
        ["left"] = Math.Round(Left), ["top"] = Math.Round(Top), ["width"] = Math.Round(Width), ["height"] = Math.Round(Height), ["maximized"] = Maximized,
    };

    /// <summary>Null when missing or not a usable size (the window then opens centred and maximized).</summary>
    public static WindowBounds? FromJson(JsonObject? o)
    {
        if (o is null) return null;
        static double? N(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : null;
        if (N(o["left"]) is not { } l || N(o["top"]) is not { } t || N(o["width"]) is not { } w || N(o["height"]) is not { } h) return null;
        if (w < MinWidth || h < MinHeight || w > 20000 || h > 20000 || Math.Abs(l) > 40000 || Math.Abs(t) > 40000) return null;
        return new WindowBounds(l, t, w, h, (bool?)o["maximized"] ?? true);
    }
}

/// <summary>Where the sidebars actually are for one window width.</summary>
/// <param name="LeftAutoHidden">The user wants the left sidebar but the window is too narrow for it right now.</param>
public readonly record struct WorkspaceFit(bool LeftShown, double LeftWidth, bool RightShown, double RightWidth, double CenterMinWidth,
    bool LeftAutoHidden, bool RightAutoHidden)
{
    public double CenterWidth(double total) => total - (LeftShown ? LeftWidth : 0) - (RightShown ? RightWidth : 0);
}

/// <summary>
/// The three-region layout rules, pure so they are tested on any OS. The conversation is protected: on a large window
/// (≥ <see cref="LargeScreen"/>) it keeps at least half the width; on a smaller one at least <see cref="CenterMinSmall"/>.
/// When the sidebars do not fit, the right one shrinks to its minimum, then the left one, then the right one is hidden,
/// then the left one. Hiding for lack of room never changes the user's choice: they come back when the window grows.
/// </summary>
public static class WorkspaceLayout
{
    public const double LeftMin = 200, LeftMax = 420, LeftDefault = 270;
    public const double RightMin = 300, RightMax = 640, RightDefault = 380;
    public const double LargeScreen = 1280, CenterMinSmall = 480;

    public static double CenterMin(double total) => total >= LargeScreen ? Math.Ceiling(total * 0.5) : Math.Min(CenterMinSmall, Math.Max(0, total));

    public static WorkspaceFit Fit(double total, WorkspacePrefs prefs)
    {
        total = double.IsFinite(total) && total > 0 ? total : 0;
        double center = CenterMin(total), room = Math.Max(0, total - center);
        bool left = prefs.LeftVisible, right = prefs.RightVisible;
        double lw = Math.Clamp(prefs.LeftWidth, LeftMin, LeftMax), rw = Math.Clamp(prefs.RightWidth, RightMin, RightMax);
        double Used() => (left ? lw : 0) + (right ? rw : 0);

        if (Used() > room && right) rw = Math.Max(RightMin, rw - (Used() - room));
        if (Used() > room && left) lw = Math.Max(LeftMin, lw - (Used() - room));
        bool rightHidden = false, leftHidden = false;
        if (Used() > room && right) { right = false; rightHidden = true; }
        if (Used() > room && left) { left = false; leftHidden = true; }
        // With the right sidebar gone, the left one may have room for its chosen width again.
        if (left && rightHidden) lw = Math.Min(Math.Clamp(prefs.LeftWidth, LeftMin, LeftMax), Math.Max(LeftMin, room));
        return new WorkspaceFit(left, lw, right, rw, center, leftHidden, rightHidden);
    }

    /// <summary>The widest a sidebar may be dragged right now, so the conversation keeps its share.</summary>
    public static double MaxLeft(double total, WorkspaceFit fit) =>
        Math.Clamp(total - CenterMin(total) - (fit.RightShown ? fit.RightWidth : 0), LeftMin, LeftMax);

    public static double MaxRight(double total, WorkspaceFit fit) =>
        Math.Clamp(total - CenterMin(total) - (fit.LeftShown ? fit.LeftWidth : 0), RightMin, RightMax);

    public static string Describe(WorkspaceFit f) => string.Create(CultureInfo.InvariantCulture,
        $"left {(f.LeftShown ? f.LeftWidth.ToString("0", CultureInfo.InvariantCulture) : "hidden")}, right {(f.RightShown ? f.RightWidth.ToString("0", CultureInfo.InvariantCulture) : "hidden")}, center ≥ {f.CenterMinWidth:0}");
}
