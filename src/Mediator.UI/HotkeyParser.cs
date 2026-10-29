namespace Mediator.UI;

/// <summary>
/// The hotkey string → RegisterHotKey arguments (V9). A thin, Win32-shaped front over <see cref="HotkeyGesture"/>,
/// which stays the one parser (settings, setup card and tray all agree on what is valid).
/// </summary>
public static class HotkeyParser
{
    /// <summary>
    /// Parses "Ctrl+Shift+Space", "ctrl + alt + c", "Win+Shift+~", "Ctrl+Shift+F12"… into Win32 MOD_* flags and a
    /// virtual-key code. <paramref name="normalizedDisplay"/> is the canonical text ("Ctrl+Alt+C"). False for a
    /// modifier-only, unknown-key, Shift-only or system-reserved combination.
    /// </summary>
    public static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey, out string normalizedDisplay) =>
        TryParse(gesture, out modifiers, out virtualKey, out normalizedDisplay, out _);

    public static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey, out string normalizedDisplay, out string? error)
    {
        if (HotkeyGesture.TryParse(gesture, out var g, out error))
        {
            modifiers = (uint)g.Modifiers;
            virtualKey = g.VirtualKey;
            normalizedDisplay = g.ToString();
            return true;
        }
        modifiers = 0;
        virtualKey = 0;
        normalizedDisplay = "";
        return false;
    }
}

/// <summary>Registers one global hotkey. Mediator.Wpf's HotkeyManager (RegisterHotKey) in the app; a fake in tests.</summary>
public interface IHotkeyRegistrar
{
    /// <summary>Registers MOD_* <paramref name="modifiers"/> + <paramref name="virtualKey"/>, replacing nothing (call <see cref="Unregister"/> first). False with the Win32 error when taken.</summary>
    bool TryRegister(uint modifiers, uint virtualKey, out int win32Error);
    void Unregister();
}

/// <summary>What a bind or rebind did. <see cref="Active"/> is the hotkey that works now (null: none, use the tray).</summary>
public sealed record HotkeyBindResult(bool Success, HotkeyGesture? Active, string Message);

/// <summary>
/// The hotkey's life cycle (V9), pure so it is tested on any OS:
/// <list type="bullet">
/// <item>Start: the configured hotkey; if another program owns it, Ctrl+Shift+Space; if that is taken too, none
/// (the tray icon still opens the panel). The setting itself is not overwritten, so the user's choice comes back
/// once the other program releases it.</item>
/// <item>Rebind: unregisters the old hotkey and registers the new one at once, without a restart. If the new one is
/// taken, the old one is registered again and stays active, and the message says so.</item>
/// </list>
/// </summary>
public sealed class HotkeyBinding(IHotkeyRegistrar registrar)
{
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    public HotkeyGesture? Active { get; private set; }

    /// <summary>Raised whenever <see cref="Active"/> changes (the tray menu shows it).</summary>
    public event Action<HotkeyGesture?>? ActiveChanged;

    private static string Why(int error) => error == ErrorHotkeyAlreadyRegistered ? "is already used by another program" : $"could not be registered (Windows error {error})";

    private bool TryActivate(HotkeyGesture gesture, out int error)
    {
        if (!registrar.TryRegister((uint)gesture.Modifiers, gesture.VirtualKey, out error)) return false;
        SetActive(gesture);
        return true;
    }

    private void SetActive(HotkeyGesture? gesture)
    {
        if (Active == gesture) return;
        Active = gesture;
        ActiveChanged?.Invoke(gesture);
    }

    public HotkeyBindResult Start(string? configured)
    {
        var wanted = HotkeyGesture.TryParse(configured, out var parsed, out _) ? parsed : HotkeyGesture.Default;
        registrar.Unregister();
        SetActive(null);
        if (TryActivate(wanted, out int error)) return new(true, wanted, $"{wanted.DisplayText} opens the panel.");
        string first = $"{wanted.DisplayText} {Why(error)}";
        if (wanted != HotkeyGesture.Default && TryActivate(HotkeyGesture.Default, out _))
            return new(false, HotkeyGesture.Default, $"{first}; using {HotkeyGesture.Default.DisplayText} for now.");
        return new(false, null, $"{first}. Use the tray icon, or pick another hotkey in the setup card.");
    }

    public HotkeyBindResult Rebind(string? gesture)
    {
        if (!HotkeyGesture.TryParse(gesture, out var next, out var parseError)) return new(false, Active, parseError ?? "invalid hotkey");
        if (next == Active) return new(true, Active, $"{next.DisplayText} opens the panel.");
        var previous = Active;
        registrar.Unregister();
        SetActive(null);
        if (TryActivate(next, out int error)) return new(true, next, $"{next.DisplayText} opens the panel.");
        string failed = $"{next.DisplayText} {Why(error)}";
        if (previous is not null && TryActivate(previous, out _))
            return new(false, previous, $"{failed}; keeping {previous.DisplayText}.");
        return new(false, null, $"{failed}, and the previous hotkey could not be restored. Use the tray icon.");
    }
}
