using System.Numerics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Connectors;

namespace Mediator.UI;

/// <summary>
/// The overlay's look. HoloHud is the default: an original holographic HUD (glass, cyan, electric blue, amber, crimson).
/// ProDark is the JetBrains-style charcoal palette of V4–V6; CleanLight is a daylight theme.
/// </summary>
public enum MediatorTheme { HoloHud, ProDark, CleanLight }

/// <summary>What the animated orb in the header shows. The view maps each state to a colour and an animation.</summary>
public enum ReactorState { Idle, Active, AwaitingApproval, Error }

/// <summary>Per-user overlay settings, stored as settings.json in the data folder.</summary>
public sealed record MediatorSettings
{
    public MediatorTheme Theme { get; init; } = MediatorTheme.HoloHud;
    public string Hotkey { get; init; } = HotkeyGesture.Default.ToString();
    public bool FirstRunCompleted { get; init; }
    /// <summary>What the user last chose; the Run key itself is the source of truth and is re-read at start.</summary>
    public bool RunOnStartup { get; init; }
    /// <summary>V10: Auto Best / Quota Saver (Antigravity first) / Claude First.</summary>
    public Mediator.Core.Jobs.RoutingPolicy RoutingPolicy { get; init; } = Mediator.Core.Jobs.RoutingPolicy.AutoBest;
    /// <summary>Actions the user created with [ + New action ].</summary>
    public List<CustomActionDef>? CustomActions { get; init; }
    /// <summary>Where jobs save their output, chosen with [ Save to… ]; null = the folder selected in Explorer.</summary>
    public string? SaveFolder { get; init; }
    /// <summary>V10: the model picked in the prompt bar ("claude"/"gemini" + model id); null = Auto (built-in rules).</summary>
    public string? ModelProvider { get; init; }
    public string? ModelId { get; init; }
    /// <summary>V10: the effort picked in the prompt bar (low|medium|high|xhigh|max); null = Auto (the CLI's default).</summary>
    public string? Effort { get; init; }
}

/// <summary>A user-made action: a saved instruction that runs as an assistant job, with the prompt as its input.</summary>
public sealed record CustomActionDef(string Id, string Title, string Description, string Instructions, bool NeedsFolder);

public interface ISettingsStore
{
    MediatorSettings Load();
    void Save(MediatorSettings settings);
}

/// <summary>JSON file, written atomically. Unknown or malformed values fall back to defaults; a malformed file is kept as settings.json.bad.</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string? LoadError { get; private set; }

    public MediatorSettings Load()
    {
        LoadError = null;
        if (!File.Exists(Path)) return new MediatorSettings();
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? throw new JsonException("not a JSON object");
            var d = new MediatorSettings();
            return new MediatorSettings
            {
                Theme = Enum.TryParse<MediatorTheme>((string?)o["theme"], ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : d.Theme,
                Hotkey = HotkeyGesture.TryParse((string?)o["hotkey"], out var g, out _) ? g.ToString() : d.Hotkey,
                FirstRunCompleted = (bool?)o["firstRunCompleted"] ?? false,
                RunOnStartup = (bool?)o["runOnStartup"] ?? false,
                RoutingPolicy = Enum.TryParse<Mediator.Core.Jobs.RoutingPolicy>((string?)o["routingPolicy"], ignoreCase: true, out var rp) && Enum.IsDefined(rp) ? rp : d.RoutingPolicy,
                ModelProvider = (string?)o["modelProvider"],
                ModelId = (string?)o["modelId"],
                Effort = Mediator.Core.Jobs.ModelCatalog.IsEffort((string?)o["effort"]) ? (string?)o["effort"] : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException)
        {
            LoadError = ex.Message;
            try { File.Copy(Path, Path + ".bad", overwrite: true); } catch (IOException) { }
            return new MediatorSettings();
        }
    }

    public void Save(MediatorSettings settings) => AtomicFile.WriteAllText(Path, new JsonObject
    {
        ["theme"] = settings.Theme.ToString(),
        ["hotkey"] = settings.Hotkey,
        ["firstRunCompleted"] = settings.FirstRunCompleted,
        ["runOnStartup"] = settings.RunOnStartup,
        ["routingPolicy"] = settings.RoutingPolicy.ToString(),
        ["modelProvider"] = settings.ModelProvider,
        ["modelId"] = settings.ModelId,
        ["effort"] = settings.Effort,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>For tests and hosts without a data folder.</summary>
public sealed class MemorySettingsStore(MediatorSettings? initial = null) : ISettingsStore
{
    public MediatorSettings Current { get; private set; } = initial ?? new MediatorSettings();
    public int Saves { get; private set; }
    public MediatorSettings Load() => Current;
    public void Save(MediatorSettings settings) { Current = settings; Saves++; }
}

/// <summary>Win32 RegisterHotKey modifiers.</summary>
[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

/// <summary>
/// A global hotkey such as "Ctrl+Shift+Space". Needs Ctrl, Alt or Win (Shift alone would steal typing) and a key
/// from a small safe set: letters, digits, F1–F24, Space, and (V9) Tab, Enter and OemTilde (the ~ / ` key). Tab and
/// Enter need two modifiers, and the system's own Alt+Tab / Win+Tab / Ctrl+Alt+Tab are refused. Pure, so the picker,
/// the settings and <see cref="HotkeyParser"/> are testable on any OS.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static readonly HotkeyGesture Default = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Space");

    /// <summary>Offered in the first-run card and settings; any valid gesture can also be typed.</summary>
    public static readonly IReadOnlyList<string> Presets = ["Ctrl+Shift+Space", "Ctrl+Alt+Space", "Ctrl+Shift+K", "Ctrl+Alt+J", "Win+Shift+Space"];

    /// <summary>The Win32 virtual-key code (0 = not a supported key).</summary>
    public uint VirtualKey => Key switch
    {
        "Space" => 0x20,
        "Tab" => 0x09,
        "Enter" => 0x0D,
        "OemTilde" => 0xC0, // VK_OEM_3: the ` ~ key on US layouts
        { Length: 1 } k when k[0] is >= 'A' and <= 'Z' => k[0],
        { Length: 1 } k when k[0] is >= '0' and <= '9' => k[0],
        { Length: >= 2 } k when k[0] == 'F' && int.TryParse(k[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 24 => (uint)(0x70 + n - 1),
        _ => 0,
    };

    /// <summary>Canonical form, stored in settings.json and accepted by <see cref="TryParse"/> (round-trips).</summary>
    public override string ToString() => string.Join('+', ModifierNames().Append(Key));

    /// <summary>For people: like <see cref="ToString"/> but the OemTilde key shows as "~" (tray menu, setup card).</summary>
    public string DisplayText => string.Join('+', ModifierNames().Append(Key == "OemTilde" ? "~" : Key));

    private IEnumerable<string> ModifierNames()
    {
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) yield return "Shift";
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) yield return "Win";
    }

    private static string? NormalizeKey(string raw) => raw.ToLowerInvariant() switch
    {
        "space" or "spacebar" => "Space",
        "tab" => "Tab",
        "enter" or "return" => "Enter",
        "~" or "`" or "tilde" or "backtick" or "oemtilde" or "oem3" => "OemTilde",
        _ when raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0]) => raw.ToUpperInvariant(),
        _ when raw.Length is 2 or 3 && raw[0] is 'f' or 'F' && raw[1..].All(char.IsAsciiDigit) => "F" + raw[1..],
        _ => null,
    };

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
        if (NormalizeKey(parts[^1]) is not { } key || new HotkeyGesture(mods, key).VirtualKey == 0)
        { error = $"'{parts[^1]}' is not supported; use a letter, a digit, F1–F24, Space, Tab, Enter or ~."; return false; }
        if (mods == HotkeyModifiers.Alt && key == "Space")
        { error = "Alt+Space opens every window's system menu; pick another."; return false; }
        if (key == "Tab" && (mods & (HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0)
        { error = "Alt+Tab and Win+Tab belong to Windows' task switcher; pick another."; return false; }
        int count = BitOperations.PopCount((uint)mods);
        if (key is "Tab" or "Enter" && count < 2)
        { error = $"{(mods.HasFlag(HotkeyModifiers.Control) ? "Ctrl" : "That modifier")}+{key} is used by most apps; add a second modifier, for example Ctrl+Shift+{key}."; return false; }
        gesture = new HotkeyGesture(mods, key);
        return true;
    }
}
