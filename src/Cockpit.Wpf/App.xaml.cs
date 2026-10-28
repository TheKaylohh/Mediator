using Cockpit.Core.Jobs;
using System.IO;
using System.Windows;
using Cockpit.Core.Connectors;
using Cockpit.Core.Context;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Runtime;
using Cockpit.Core.Tools;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// Composition root for the overlay: one CockpitRuntime (bus on \\.\pipe\ai-cockpit-bus), the ViewModel as its
/// approval prompt, the panel, the tray icon, the global hotkey, the theme, the Connectors tab and the first-run
/// card. Single instance per user session.
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

        var log = new StderrLog("cockpit");
        try
        {
            _runtime = await CockpitRuntime.CreateAsync(new RuntimeOptions { Log = log });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"The Cockpit could not start its bus or databases:\n\n{ex.Message}", "AI Cockpit", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var dispatcher = new WpfDispatcher(Dispatcher);
        var settings = new JsonSettingsStore(_runtime.Paths.SettingsFile);
        var vm = new CockpitOverlayViewModel(_runtime, dispatcher, settings);
        _runtime.Approvals.Target = vm;

        // Theme before the first frame; later changes come from the header button or the first-run card.
        // A theme that cannot be loaded keeps the current one (App.xaml merges HoloHud) and is reported, never fatal.
        string? themeProblem = null;
        ThemeManager.LoadFailed += (theme, why) =>
        {
            log.Warn($"Theme {theme} could not be loaded; keeping the current theme. {why}");
            if (_tray is { } tray) tray.Notify("Theme unavailable", $"{theme} could not be loaded; the current theme is kept.");
            else themeProblem = $"{theme} could not be loaded; HoloHud is used.";
        };
        ThemeManager.Apply(vm.CurrentTheme);
        vm.ThemeChanged += ThemeManager.Apply;

        var probe = new ConnectorProbe(DefaultProcessRunner.Instance);
        vm.Connectors = new ConnectorsViewModel(probe, _runtime, _runtime.CustomMcp, dispatcher);
        var onboarding = new OnboardingViewModel(probe, new StartupRegistration(), vm);
        vm.Onboarding = onboarding;

        vm.Chats = new ChatStore(System.IO.Path.Combine(_runtime.Paths.DataDirectory, "chats"));
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
        if (themeProblem is not null) _tray.Notify("Theme unavailable", themeProblem);
        if (settings.LoadError is { } settingsError)
            _tray.Notify("Settings reset", $"settings.json could not be read ({settingsError}); defaults are used and the old file was kept as settings.json.bad.");

        // Hotkey (V9): the configured one, else Ctrl+Shift+Space; the setup card rebinds live and keeps the old one if
        // the new combination is taken. The tray menu always shows the hotkey that works right now.
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => vm.OnHotkey(CaptureContext());
        _hotkey.ActiveChanged += active => _tray?.SetHotkey(active?.DisplayText);
        var bound = _hotkey.Start(vm.Settings.Hotkey);
        _tray.SetHotkey(bound.Active?.DisplayText);
        if (!bound.Success) _tray.Notify("Hotkey unavailable", bound.Message);
        onboarding.Rebinder = gesture => _hotkey.Rebind(gesture.ToString());

        // First launch, or neither CLI found: open the setup card.
        if (await onboarding.StartAsync()) vm.Show();
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
