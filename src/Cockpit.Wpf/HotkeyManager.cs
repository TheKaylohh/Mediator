using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cockpit.Wpf;

/// <summary>
/// A system-wide hotkey through RegisterHotKey on a message-only window. Ctrl+Shift+Space by default; if another
/// program already owns the combination, registration fails and the caller says so (the tray icon still works).
/// </summary>
public sealed partial class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;
    public const uint VkSpace = 0x20;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xC0C1;

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        // HWND_MESSAGE parent: invisible, never activated, receives WM_HOTKEY.
        var parameters = new HwndSourceParameters("AiCockpitHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers modifiers+key; throws <see cref="Win32Exception"/> (usually ERROR_HOTKEY_ALREADY_REGISTERED) when taken.</summary>
    public void Register(uint modifiers = ModControl | ModShift, uint virtualKey = VkSpace)
    {
        Unregister();
        if (!RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, virtualKey))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _registered = true;
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
