using Mediator.Core.Jobs;
using System.IO;
using System.Windows;
using Mediator.Core.Connectors;
using Mediator.Core.Context;
using Mediator.Core.Diagnostics;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;
using Mediator.UI;

namespace Mediator.Wpf;

/// <summary>
/// Composition root for the overlay: one MediatorRuntime (bus on \\.\pipe\mediator-bus), the ViewModel as its
/// approval prompt, the panel, the tray icon, the global hotkey, the theme, the Connectors tab and the first-run
/// card. Single instance per user session.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Mediator.Overlay";
    private Mutex? _single;
    private Mediator.Core.Browser.BrowserExtensionHost? _browser;
    private MediatorRuntime? _runtime;
    private Mediator.Core.Browser.BrowserPanelChats? _panelChats;
    private HotkeyManager? _hotkey;
    private TrayIcon? _tray;

    public static bool IsQuitting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (!first && !ReplaceOlderInstance())
        {
            Shutdown();
            return;
        }

        var log = new StderrLog("mediator");
        try
        {
            // V13: the Chrome extension bridge (127.0.0.1, paired) starts first, so Browser jobs can use it from the first prompt.
            var paths = MediatorPaths.Default();
            _browser = Mediator.Core.Browser.BrowserExtensionHost.TryStart(paths.DataDirectory, log);
            _runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions { Log = log, Paths = paths, BrowserBridge = _browser?.Bridge });
            RunningInstance.Write(paths.DataDirectory); // V16.1: a later build can tell this one apart and replace it
            log.Info("Mediator " + MediatorVersion.Display);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"The Mediator could not start its bus or databases:\n\n{ex.Message}", "Mediator", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // V14.1: the chat in the extension's side panel runs Browser jobs on this runtime.
        if (_browser is not null) _panelChats = Mediator.Core.Browser.BrowserPanelChats.Attach(_runtime, _browser.Bridge, log);

        var dispatcher = new WpfDispatcher(Dispatcher);
        var settings = new JsonSettingsStore(_runtime.Paths.SettingsFile);
        var vm = new MediatorOverlayViewModel(_runtime, dispatcher, settings);
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
        vm.Connectors = new ConnectorsViewModel(probe, _runtime, _runtime.CustomMcp, dispatcher, browser: _browser,
            copyText: text => Clipboard.SetText(text));
        vm.Usage = new UsageViewModel(_runtime, new ClaudePlanUsage(), dispatcher); // V14: reads meters only; runs no model
        var onboarding = new OnboardingViewModel(probe, new StartupRegistration(), vm);
        vm.Onboarding = onboarding;

        vm.Chats = new ChatStore(System.IO.Path.Combine(_runtime.Paths.DataDirectory, "chats"));
        var window = new MainWindow(vm) { DataDirectory = _runtime.Paths.DataDirectory };
        MainWindow = window;
        vm.WorkspaceHost = window; // V16.2: the workspace window opens files and programs and the quick panel's tabs through it

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

    /// <summary>
    /// V16.1: another overlay holds the single-instance lock. The same build: say so and stop. A different build (typically an
    /// older version left in the tray, which kept answering every chat with its old tools): offer to close it and start this one.
    /// True when this process now holds the lock and should start.
    /// </summary>
    private bool ReplaceOlderInstance()
    {
        var running = RunningInstance.Read(MediatorPaths.Default().DataDirectory);
        if (running is { IsThisBuild: true })
        {
            MessageBox.Show($"Mediator {MediatorVersion.Name} is already running (see the tray).", "Mediator", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var answer = MessageBox.Show(
            $"{RunningInstance.Describe(running)} is still running in the tray, so starting this copy would keep using it.\n\n" +
            $"Close it and start Mediator {MediatorVersion.Display} instead? Jobs running in the old one are stopped.",
            "Mediator", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        foreach (var old in RunningInstance.OthersToReplace(running, System.Diagnostics.Process.GetCurrentProcess().ProcessName))
        {
            using (old)
            {
                try { old.Kill(entireProcessTree: true); old.WaitForExit(10_000); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        _single?.Dispose();
        _single = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (!first)
        {
            try { first = _single.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { first = true; } // the old overlay was killed while holding it: it is ours now
        }
        if (!first) MessageBox.Show("The running Mediator could not be closed. Quit it from its tray icon, then start this one again.", "Mediator", MessageBoxButton.OK, MessageBoxImage.Warning);
        return first;
    }

    private async void Quit()
    {
        IsQuitting = true;
        _hotkey?.Dispose();
        _tray?.Dispose();
        _panelChats?.Dispose();
        if (_runtime is not null) await _runtime.DisposeAsync(); // kills running jobs, stops the bus, closes databases
        if (_browser is not null) await _browser.DisposeAsync();
        _single?.ReleaseMutex();
        Shutdown();
    }
}
