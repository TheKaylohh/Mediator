using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Connectors;

namespace Cockpit.UI;

/// <summary>
/// The overlay's look. HoloHud is the default: an original holographic HUD (glass, cyan, electric blue, amber, crimson).
/// ProDark is the JetBrains-style charcoal palette of V4–V6; CleanLight is a daylight theme.
/// </summary>
public enum CockpitTheme { HoloHud, ProDark, CleanLight }

/// <summary>What the animated orb in the header shows. The view maps each state to a colour and an animation.</summary>
public enum ReactorState { Idle, Active, AwaitingApproval, Error }

/// <summary>Per-user overlay settings, stored as settings.json in the data folder.</summary>
public sealed record CockpitSettings
{
    public CockpitTheme Theme { get; init; } = CockpitTheme.HoloHud;
    public string Hotkey { get; init; } = HotkeyGesture.Default.ToString();
    public bool FirstRunCompleted { get; init; }
    /// <summary>What the user last chose; the Run key itself is the source of truth and is re-read at start.</summary>
    public bool RunOnStartup { get; init; }
}

public interface ISettingsStore
{
    CockpitSettings Load();
    void Save(CockpitSettings settings);
}

/// <summary>JSON file, written atomically. Unknown or malformed values fall back to defaults; a malformed file is kept as settings.json.bad.</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string? LoadError { get; private set; }

    public CockpitSettings Load()
    {
        LoadError = null;
        if (!File.Exists(Path)) return new CockpitSettings();
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? throw new JsonException("not a JSON object");
            var d = new CockpitSettings();
            return new CockpitSettings
            {
                Theme = Enum.TryParse<CockpitTheme>((string?)o["theme"], ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : d.Theme,
                Hotkey = HotkeyGesture.TryParse((string?)o["hotkey"], out var g, out _) ? g.ToString() : d.Hotkey,
                FirstRunCompleted = (bool?)o["firstRunCompleted"] ?? false,
                RunOnStartup = (bool?)o["runOnStartup"] ?? false,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException)
        {
            LoadError = ex.Message;
            try { File.Copy(Path, Path + ".bad", overwrite: true); } catch (IOException) { }
            return new CockpitSettings();
        }
    }

    public void Save(CockpitSettings settings) => AtomicFile.WriteAllText(Path, new JsonObject
    {
        ["theme"] = settings.Theme.ToString(),
        ["hotkey"] = settings.Hotkey,
        ["firstRunCompleted"] = settings.FirstRunCompleted,
        ["runOnStartup"] = settings.RunOnStartup,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>For tests and hosts without a data folder.</summary>
public sealed class MemorySettingsStore(CockpitSettings? initial = null) : ISettingsStore
{
    public CockpitSettings Current { get; private set; } = initial ?? new CockpitSettings();
    public int Saves { get; private set; }
    public CockpitSettings Load() => Current;
    public void Save(CockpitSettings settings) { Current = settings; Saves++; }
}

/// <summary>Win32 RegisterHotKey modifiers.</summary>
[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

/// <summary>
/// A global hotkey such as "Ctrl+Shift+Space". Needs Ctrl, Alt or Win (Shift alone would steal typing) and a key
/// from a small safe set: letters, digits, F1–F24, Space. Pure, so the picker and settings are testable on any OS.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static readonly HotkeyGesture Default = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Space");

    /// <summary>Offered in the first-run card and settings; any valid gesture can also be typed.</summary>
    public static readonly IReadOnlyList<string> Presets = ["Ctrl+Shift+Space", "Ctrl+Alt+Space", "Ctrl+Shift+K", "Ctrl+Alt+J", "Win+Shift+Space"];

    /// <summary>The Win32 virtual-key code.</summary>
    public uint VirtualKey => Key switch
    {
        "Space" => 0x20,
        { Length: 1 } k when k[0] is >= 'A' and <= 'Z' => k[0],
        { Length: 1 } k when k[0] is >= '0' and <= '9' => k[0],
        { Length: >= 2 } k when k[0] == 'F' && int.TryParse(k[1..], out int n) && n is >= 1 and <= 24 => (uint)(0x70 + n - 1),
        _ => 0,
    };

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = Default;
        error = null;
        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) { error = "Use a modifier and a key, for example Ctrl+Shift+Space."; return false; }
        var mods = HotkeyModifiers.None;
        foreach (var p in parts[..^1])
        {
            var m = p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" or "meta" => HotkeyModifiers.Win,
                _ => (HotkeyModifiers?)null,
            };
            if (m is null) { error = $"'{p}' is not a modifier (Ctrl, Alt, Shift, Win)."; return false; }
            if (mods.HasFlag(m.Value)) { error = $"{p} is listed twice."; return false; }
            mods |= m.Value;
        }
        if ((mods & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        { error = "Include Ctrl, Alt or Win, so the hotkey does not take over normal typing."; return false; }
        if (mods == HotkeyModifiers.Alt && parts[^1].Equals("space", StringComparison.OrdinalIgnoreCase))
        { error = "Alt+Space opens every window's system menu; pick another."; return false; }
        string key = parts[^1].Length == 1 ? parts[^1].ToUpperInvariant() : char.ToUpperInvariant(parts[^1][0]) + parts[^1][1..].ToLowerInvariant();
        var candidate = new HotkeyGesture(mods, key);
        if (candidate.VirtualKey == 0) { error = $"'{parts[^1]}' is not supported; use a letter, a digit, F1–F24 or Space."; return false; }
        gesture = candidate;
        return true;
    }
}
