using System.ComponentModel;
using System.Windows;
using Cockpit.Core.Context;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Runtime;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// Composition root for the overlay: one CockpitRuntime (bus on \\.\pipe\ai-cockpit-bus), the ViewModel as its
/// approval prompt, the panel, the tray icon and the global hotkey. Single instance per user session.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\AiCockpit.Overlay";
    private Mutex? _single;
    private CockpitRuntime? _runtime;
    private HotkeyManager? _hotkey;
    private TrayIcon? _tray;

    public static bool IsQuitting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (!first)
        {
            MessageBox.Show("AI Cockpit is already running (see the tray).", "AI Cockpit", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _runtime = await CockpitRuntime.CreateAsync(new RuntimeOptions { Log = new StderrLog("cockpit") });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"The Cockpit could not start its bus or databases:\n\n{ex.Message}", "AI Cockpit", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var vm = new CockpitOverlayViewModel(_runtime, new WpfDispatcher(Dispatcher));
        _runtime.Approvals.Target = vm;
        var window = new MainWindow(vm);
        MainWindow = window;

        _tray = new TrayIcon();
        _tray.ToggleRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (DateTime.UtcNow - window.LastDeactivatedUtc < TimeSpan.FromMilliseconds(300)) return; // the click that just hid it
            vm.OnHotkey(null);
        });
        _tray.KillAllRequested += () => Dispatcher.BeginInvoke(vm.KillAll);
        _tray.QuitRequested += () => Dispatcher.BeginInvoke(Quit);
        vm.AttentionRequested += () =>
        {
            if (!window.IsActive) _tray?.Notify("Approval needed", vm.PendingApproval?.Title ?? "A job is waiting for you.");
        };

        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => vm.OnHotkey(CaptureContext());
        try
        {
            _hotkey.Register();
        }
        catch (Win32Exception)
        {
            _tray.Notify("Hotkey unavailable", "Another program owns Ctrl+Shift+Space. Use the tray icon to open the Cockpit.");
        }
    }

    /// <summary>Runs on the UI (STA) thread before the panel activates, so the foreground window is still the user's.</summary>
    private static ContextSnapshot? CaptureContext()
    {
        try { return ExplorerContext.Capture(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null; // context is a convenience; never block the hotkey on it
        }
    }

    private async void Quit()
    {
        IsQuitting = true;
        _hotkey?.Dispose();
        _tray?.Dispose();
        if (_runtime is not null) await _runtime.DisposeAsync(); // kills running jobs, stops the bus, closes databases
        _single?.ReleaseMutex();
        Shutdown();
    }
}
