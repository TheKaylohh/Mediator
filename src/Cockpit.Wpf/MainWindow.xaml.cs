using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// The panel. All behaviour lives in <see cref="CockpitOverlayViewModel"/>; this class only places the window
/// above the tray (bottom-right of the work area, like Toolbox), follows IsVisible, focuses the prompt, and
/// hides on deactivation (the ViewModel refuses while an approval is open).
/// </summary>
public partial class MainWindow : Window
{
    private readonly CockpitOverlayViewModel _vm;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private MonitorPlacement.POINT? _anchor; // the cursor when the panel opened: the monitor it stays on while visible

    public MainWindow(CockpitOverlayViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.PropertyChanged += OnViewModelChanged;
        vm.FocusPromptRequested += () => Dispatcher.BeginInvoke(FocusPrompt, DispatcherPriority.Input);
        Deactivated += (_, _) =>
        {
            LastDeactivatedUtc = DateTime.UtcNow;
            _vm.Hide();
        };
        SizeChanged += (_, _) => PlaceAboveTray();
        _tick.Tick += (_, _) => _vm.Tick();
    }

    /// <summary>Clicking the tray icon first deactivates the panel; the tray handler uses this to avoid re-opening it.</summary>
    public DateTime LastDeactivatedUtc { get; private set; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CockpitOverlayViewModel.IsVisible)) return;
        if (_vm.IsVisible)
        {
            if (!IsVisible) _anchor = MonitorPlacement.Cursor(); // follow the mouse only when opening, not while open
            Show();
            PlaceAboveTray();
            Activate();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
            Hide();
        }
    }

    private void FocusPrompt()
    {
        PromptBox.Focus();
        PromptBox.SelectAll();
    }

    /// <summary>
    /// Bottom-right of the work area of the monitor the mouse was on when the panel opened (above the taskbar).
    /// Falls back to the primary work area if the Win32 calls fail.
    /// </summary>
    private void PlaceAboveTray()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && _anchor is { } anchor && MonitorPlacement.PlaceBottomRight(hwnd, anchor)) return;
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides; quitting is done from the tray menu.
        e.Cancel = !App.IsQuitting;
        if (e.Cancel) _vm.Hide();
        base.OnClosing(e);
    }
}
