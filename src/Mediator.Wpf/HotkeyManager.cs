using System.Runtime.InteropServices;
using System.Windows.Interop;
using Mediator.UI;

namespace Mediator.Wpf;

/// <summary>
/// The system-wide hotkey: RegisterHotKey on a message-only window. Which combination, the fallback when another
/// program owns it, and live rebinding are decided by <see cref="HotkeyBinding"/> (Mediator.UI, tested on any OS);
/// this class only talks to Win32. The hotkey string comes from settings.json / the setup card and is parsed by
/// <see cref="HotkeyParser"/> (V9: it was always registered from the parsed setting; now a conflict keeps the
/// previous hotkey instead of leaving none, and the tray shows the active one).
/// </summary>
public sealed partial class HotkeyManager : IHotkeyRegistrar, IDisposable
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xC0C1;

    private readonly HwndSource _source;
    private readonly HotkeyBinding _binding;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>The hotkey that currently opens the panel changed (null: none; the tray icon still works).</summary>
    public event Action<HotkeyGesture?>? ActiveChanged
    {
        add => _binding.ActiveChanged += value;
        remove => _binding.ActiveChanged -= value;
    }

    public HotkeyManager()
    {
        // HWND_MESSAGE parent: invisible, never activated, receives WM_HOTKEY.
        var parameters = new HwndSourceParameters("MediatorHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _binding = new HotkeyBinding(this);
    }

    public HotkeyGesture? Active => _binding.Active;

    /// <summary>At startup: the configured hotkey, else Ctrl+Shift+Space, else none (see <see cref="HotkeyBinding.Start"/>).</summary>
    public HotkeyBindResult Start(string? configuredGesture) => _binding.Start(configuredGesture);

    /// <summary>Switches to <paramref name="gesture"/> immediately; keeps the current hotkey if the new one is taken.</summary>
    public HotkeyBindResult Rebind(string gesture) => _binding.Rebind(gesture);

    bool IHotkeyRegistrar.TryRegister(uint modifiers, uint virtualKey, out int win32Error)
    {
        Unregister();
        if (!RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, virtualKey))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }
        win32Error = 0;
        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
