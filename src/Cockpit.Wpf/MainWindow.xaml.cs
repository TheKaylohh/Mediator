using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// The panel. All behaviour lives in <see cref="CockpitOverlayViewModel"/>; this class places the window above the
/// tray (bottom-right of the work area, like Toolbox), follows IsVisible, focuses the prompt, hides on deactivation
/// (unless pinned or an approval is open), and turns drag-and-drop, Ctrl+V and the Attach button into ViewModel
/// calls. Paths are only handed over: the Core checks and stages them.
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
        vm.BrowseForAttachmentsRequested += BrowseForAttachments;
        Deactivated += (_, _) =>
        {
            LastDeactivatedUtc = DateTime.UtcNow;
            _vm.OnDeactivated();
        };
        SizeChanged += (_, _) => PlaceAboveTray();
        _tick.Tick += (_, _) => _vm.Tick();

        // Preview (tunnelling) events, so the prompt TextBox cannot swallow a file drop before the window sees it.
        PreviewDragEnter += OnDragOver;
        PreviewDragOver += OnDragOver;
        PreviewDragLeave += OnDragLeave;
        PreviewDrop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
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
            _vm.IsDragOver = false;
            Hide();
        }
    }

    private void FocusPrompt()
    {
        PromptBox.Focus();
        PromptBox.SelectAll();
    }

    // ── Drag and drop ────────────────────────────────────────────────────────────────────────────────

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = HasFiles(e);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        _vm.IsDragOver = files;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only a pointer outside the window ends the hover.
        var p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X >= ActualWidth || p.Y >= ActualHeight) _vm.IsDragOver = false;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _vm.IsDragOver = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            _vm.AttachPaths(paths);
            e.Effects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    // ── Ctrl+V: files or a screenshot; plain text still pastes into the prompt ───────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection list = Clipboard.GetFileDropList();
                _vm.AttachPaths(list.Cast<string>().ToList());
                e.Handled = true;
            }
            else if (ClipboardPng() is byte[] png)
            {
                _vm.PasteImage(png);
                e.Handled = true;
            }
        }
        catch (ExternalException)
        {
            // The clipboard is busy (another app holds it open); let the TextBox try its own paste.
        }
    }

    /// <summary>
    /// The clipboard image as PNG bytes. Many apps (Snipping Tool, browsers) also publish a "PNG" stream, which keeps
    /// transparency; otherwise the bitmap is encoded here.
    /// </summary>
    private static byte[]? ClipboardPng()
    {
        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream raw) return raw.ToArray();
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not BitmapSource bitmap) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ── [ + Attach ] ─────────────────────────────────────────────────────────────────────────────────

    private void BrowseForAttachments()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files (jobs can read them, never change them)",
            Multiselect = true,
            CheckFileExists = true,
            DereferenceLinks = false, // a shortcut is attached as itself, never silently followed
        };
        bool? ok;
        using (_vm.BeginModal()) ok = dialog.ShowDialog(this);
        if (ok == true && dialog.FileNames.Length > 0) _vm.AttachPaths(dialog.FileNames);
        Activate();
        FocusPrompt();
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
