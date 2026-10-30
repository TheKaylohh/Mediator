using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mediator.UI;

namespace Mediator.Wpf;

/// <summary>
/// The panel. All behaviour lives in <see cref="MediatorOverlayViewModel"/>; this class places the window by the tray
/// (the work-area corner next to the notification area, on the monitor under the mouse), follows IsVisible, focuses
/// the prompt, hides on deactivation (unless pinned or an approval is open), and turns drag-and-drop, Ctrl+V and the
/// Attach button into ViewModel calls. Paths are only handed over: the Core checks and stages them.
///
/// V8 placement: the window is positioned while still hidden, then shown; a WM_WINDOWPOSCHANGING hook keeps it
/// anchored whenever SizeToContent or a DPI change resizes it (no frame where it hangs off the screen), MaxHeight is
/// capped to the monitor's usable height, and work-area / display changes re-place it (see MonitorPlacement).
/// </summary>
public partial class MainWindow : Window, Mediator.UI.Workspace.IWorkspaceHost
{
    private readonly MediatorOverlayViewModel _vm;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private MonitorPlacement.POINT? _anchor; // the cursor when the panel opened: the monitor it stays on while visible
    private PanelPlacementResult? _placed;    // the current anchor while visible (WM_WINDOWPOSCHANGING keeps it)
    private readonly double _designMaxHeight; // MaxHeight from the XAML; the monitor can only lower it

    public MainWindow(MediatorOverlayViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _designMaxHeight = MaxHeight;
        ComposerKeys.Attach(PromptBox, vm);

        vm.PropertyChanged += OnViewModelChanged;
        vm.FocusPromptRequested += () => Dispatcher.BeginInvoke(FocusPrompt, DispatcherPriority.Input);
        vm.BrowseForAttachmentsRequested += BrowseForAttachments;
        vm.BrowseForSaveFolderRequested += BrowseForSaveFolder;
        vm.BrowseForAllowFolderRequested += BrowseForAllowFolder;
        vm.OpenFullViewRequested += OpenWorkspace;
        Deactivated += (_, _) =>
        {
            LastDeactivatedUtc = DateTime.UtcNow;
            // V18.1: clicking into the full-screen app is not "another window": the panel stays beside it (pin or Esc to hide).
            Dispatcher.BeginInvoke(() => { if (_chat is not { IsActive: true }) _vm.OnDeactivated(); }, DispatcherPriority.Background);
        };
        SizeChanged += (_, _) => PlaceAboveTray();
        DpiChanged += (_, _) => PlaceAboveTray();
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
        if (e.PropertyName != nameof(MediatorOverlayViewModel.IsVisible)) return;
        if (_vm.IsVisible)
        {
            if (!IsVisible)
            {
                _anchor = MonitorPlacement.Cursor(); // follow the mouse only when opening, not while open
                _ = new WindowInteropHelper(this).EnsureHandle(); // so the very first open is placed before it is shown
                PlaceAboveTray();                             // while hidden: no frame at the old position
            }
            Show();
            PlaceAboveTray(); // again with the laid-out size (the hook has usually done it already)
            Activate();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
            _vm.IsDragOver = false;
            Hide();
            _placed = null;
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

    private ChatWindow? _chat;

    /// <summary>V18: opens the full-screen app (or brings it forward, restoring it if minimized).</summary>
    public void OpenWorkspace()
    {
        _chat ??= new ChatWindow(_vm, OnDragOver, OnDragLeave, OnDrop);
        _chat.Open();
    }

    // ── V16.2: what the workspace window asks of the app ────────────────────────────────────────────

    /// <summary>The data folder (logs, audit, chats), opened by Profile > Logs &amp; Diagnostics.</summary>
    public string? DataDirectory { get; set; }

    void Mediator.UI.Workspace.IWorkspaceHost.ActivateComposer() => _chat?.FocusPrompt();

    void Mediator.UI.Workspace.IWorkspaceHost.OpenOverlayTab(int tab) => _vm.ShowPanelTab(tab);

    void Mediator.UI.Workspace.IWorkspaceHost.OpenDataFolder()
    {
        if (DataDirectory is { } dir) ShellOpen(dir);
    }

    void Mediator.UI.Workspace.IWorkspaceHost.OpenPath(string path) => ShellOpen(path);

    void Mediator.UI.Workspace.IWorkspaceHost.Launch(string executable) => ShellOpen(executable);

    /// <summary>Opens a file or folder with its default program, or starts a program; a failure is shown, never thrown.</summary>
    private void ShellOpen(string path)
    {
        try { using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _vm.NotifyStatus($"Could not open {path}: {ex.Message}");
        }
    }

    /// <summary>[ Save to… ]: a Windows folder picker; the choice is where every job saves until cleared.</summary>
    private void BrowseForSaveFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save job output to this folder" };
        if (_vm.SaveFolder is { } current) dialog.InitialDirectory = current;
        Window owner = _chat is { IsActive: true } ? _chat : this;
        bool? ok;
        using (_vm.BeginModal()) ok = dialog.ShowDialog(owner);
        if (ok == true && dialog.FolderName.Length > 0) _vm.SetSaveFolder(dialog.FolderName);
        owner.Activate();
    }

    /// <summary>Allow folder: any folder the user picks may be read and written by this chat's jobs (protected places are refused).</summary>
    private void BrowseForAllowFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Allow this chat's jobs to read and write a folder" };
        Window owner = _chat is { IsActive: true } ? _chat : this;
        bool? ok;
        using (_vm.BeginModal()) ok = dialog.ShowDialog(owner);
        if (ok == true && dialog.FolderName.Length > 0) _ = _vm.AllowFolderAsync(dialog.FolderName);
        owner.Activate();
    }

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
        Window owner = _chat is { IsActive: true } ? _chat : this;
        using (_vm.BeginModal()) ok = dialog.ShowDialog(owner);
        if (ok == true && dialog.FileNames.Length > 0) _vm.AttachPaths(dialog.FileNames);
        owner.Activate();
        if (owner == this) FocusPrompt(); else _chat!.FocusPrompt();
    }

    /// <summary>
    /// The tray corner of the usable work area of the monitor the mouse was on when the panel opened (taskbar on any
    /// edge, auto-hide taskbars kept clear, target-monitor DPI). Falls back to the primary work area if Win32 fails.
    /// </summary>
    private void PlaceAboveTray()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        double width = double.IsNaN(Width) ? ActualWidth : Width;
        if (hwnd != IntPtr.Zero && (_anchor ?? MonitorPlacement.Cursor()) is { } anchor && MonitorPlacement.PlaceAboveTray(hwnd, anchor, width) is { } placed)
        {
            _placed = placed;
            double max = Math.Min(_designMaxHeight, placed.MaxHeightDip);
            if (Math.Abs(MaxHeight - max) > 0.5) MaxHeight = max; // SizeChanged follows if this shrinks the window
            return;
        }
        _placed = null;
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Min(_designMaxHeight, area.Height - 8);
        Left = Math.Max(area.Left, area.Right - ActualWidth - 4);
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 4);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    /// <summary>Keeps the open panel anchored through resizes, and re-places it when the work area or the displays change.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case MonitorPlacement.WM_WINDOWPOSCHANGING when _placed is { } placed && IsVisible:
                // Not marked handled: WPF still sees the (corrected) message.
                MonitorPlacement.KeepAnchored(lParam, placed, () => MonitorPlacement.WindowSize(hwnd));
                break;
            case MonitorPlacement.WM_DISPLAYCHANGE:
            case MonitorPlacement.WM_SETTINGCHANGE when wParam == MonitorPlacement.SPI_SETWORKAREA:
                // A monitor was added/removed or rescaled, or a taskbar moved / changed auto-hide.
                if (IsVisible) Dispatcher.BeginInvoke(PlaceAboveTray, DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides; quitting is done from the tray menu.
        e.Cancel = !App.IsQuitting;
        if (e.Cancel) _vm.Hide();
        base.OnClosing(e);
    }
}
