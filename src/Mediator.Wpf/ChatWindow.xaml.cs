using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mediator.UI;
using Mediator.UI.Workspace;

namespace Mediator.Wpf;

/// <summary>
/// V16.2: the full-screen workspace. Top bar (project, search and command palette, the six right-sidebar modes,
/// notifications, profile), left sidebar (new chat, search, jobs, pinned and recent chats, projects), the conversation, and
/// the right sidebar. Layout lives in <see cref="WorkspaceViewModel"/>: this window only reports its width and forwards keys.
/// Shortcuts: Ctrl+N new chat, Ctrl+K search chats, Ctrl+P palette, Ctrl+B left sidebar, Ctrl+Alt+B right sidebar,
/// Ctrl+1..6 modes, Ctrl+Shift+C copy the last answer, Esc closes the palette or menus. Closing only hides the window.
/// V18: a standalone app window: its own taskbar button and Alt+Tab entry, it reopens where it was, `Mediator.exe --workspace`
/// (the "Mediator Workspace" shortcut) opens it directly, and while it is open the quick panel stays hidden.
/// </summary>
public partial class ChatWindow : Window
{
    private readonly WorkspaceViewModel _ws;
    private readonly MediatorOverlayViewModel _vm;
    /// <summary>The two splitter columns between the regions.</summary>
    private const double SplitterWidth = 10;

    public ChatWindow(MediatorOverlayViewModel vm, DragEventHandler dragOver, DragEventHandler dragLeave, DragEventHandler drop)
    {
        InitializeComponent();
        _vm = vm;
        _ws = vm.Workspace;
        DataContext = _ws;
        ComposerKeys.Attach(PromptBox, vm);
        PreviewDragEnter += dragOver;
        PreviewDragOver += dragOver;
        PreviewDragLeave += dragLeave;
        PreviewDrop += drop;
        PreviewKeyDown += OnShortcut;
        vm.Conversation.CollectionChanged += (_, _) => ScrollToEnd();
        vm.FocusPromptRequested += () => { if (IsVisible) FocusPrompt(); };
        _ws.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkspaceViewModel.IsPaletteOpen) && _ws.IsPaletteOpen) FocusPalette(); };
        BodyGrid.SizeChanged += (_, _) => _ws.AvailableWidth = Math.Max(0, BodyGrid.ActualWidth - SplitterWidth);
        ChatScroll.ScrollChanged += (_, _) =>
            JumpToLatest.Visibility = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset > 200 ? Visibility.Visible : Visibility.Collapsed;
        StateChanged += (_, _) => FitToWorkArea();
        Loaded += (_, _) => FitToWorkArea();
        // V18: while this window is open the quick panel stays hidden (the hotkey and the tray bring this one forward).
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) SavePlacement();
            _vm.IsWorkspaceOpen = IsVisible;
        };
#if MEDIATOR_WEBVIEW
        bool attached = false;
        Loaded += async (_, _) => { if (!attached) { attached = true; await ChatRenderer.TryAttachAsync(ChatScroll, vm); } };
#endif
    }

    private bool _placed;

    public void Open()
    {
        if (!_placed) RestorePlacement();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        ScrollToEnd();
        FocusPrompt();
    }

    /// <summary>The first open goes where the window was last time (if that is still on a screen), else centred and maximized.</summary>
    private void RestorePlacement()
    {
        _placed = true;
        if (_ws.WindowPlacement is not { } b) return;
        bool onScreen = b.Left + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && b.Left + b.Width - 100 > SystemParameters.VirtualScreenLeft
            && b.Top >= SystemParameters.VirtualScreenTop - 20 && b.Top + 40 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!onScreen) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || !double.IsFinite(r.Width)) return;
        _ws.SaveWindowPlacement(new WindowBounds(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized));
    }

    // ── Window caption (the top bar) ────────────────────────────────────────────────────────────────

    /// <summary>Without a title bar, a maximized window reaches past the screen by its resize border: pull the content in.</summary>
    private void FitToWorkArea()
    {
        RootGrid.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

    /// <summary>The top bar is the caption: drag to move, double-click to maximize or restore (its buttons handle their own clicks).</summary>
    private void TopBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { MaximizeClick(sender, e); return; }
        try { DragMove(); } catch (InvalidOperationException) { } // the button was released before the drag started
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    // ── Overlays and focus ───────────────────────────────────────────────────────────────────────────

    /// <summary>A click outside the palette closes it.</summary>
    private void OverlayBackgroundClick(object sender, MouseButtonEventArgs e) => _ws.CloseOverlaysCommand.Execute(null);

    /// <summary>A click inside the palette card stays there.</summary>
    private void SwallowClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void FocusPalette() => Dispatcher.BeginInvoke(() => { PaletteBox.Focus(); PaletteBox.SelectAll(); }, DispatcherPriority.Input);

    public void FocusPrompt() => Dispatcher.BeginInvoke(() => { PromptBox.Focus(); PromptBox.CaretIndex = PromptBox.Text.Length; }, DispatcherPriority.Input);

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void JumpToLatestClick(object sender, RoutedEventArgs e) => ScrollToEnd();

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // with Alt held, WPF reports the key as System
        if (key == Key.Escape && (_ws.IsPaletteOpen || _ws.IsProfileOpen || _ws.IsShortcutsOpen || _ws.IsAboutOpen))
        {
            _ws.CloseOverlaysCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!ctrl) return;
        switch (key)
        {
            case Key.N when !shift && !alt:
            case Key.O when shift:
                _ws.NewChatCommand.Execute(null);
                FocusPrompt();
                break;
            case Key.K when !shift:
                if (!_ws.IsLeftShown) _ws.ToggleLeftCommand.Execute(null);
                Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
                break;
            case Key.P when !shift:
                _ws.OpenPaletteCommand.Execute(null);
                break;
            case Key.B when alt:
                _ws.ToggleRightCommand.Execute(null);
                break;
            case Key.B when !shift:
                _ws.ToggleLeftCommand.Execute(null);
                break;
            case >= Key.D1 and <= Key.D6 when !shift && !alt:
                _ws.SelectMode((RightMode)(key - Key.D1));
                break;
            case Key.C when shift:
                if (_vm.LastResponse is { } text) CopyText(text);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Enter commits a rename, Esc cancels it.</summary>
    private void TitleKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); FocusPrompt(); e.Handled = true; }
        else if (e.Key == Key.Escape) { box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); FocusPrompt(); e.Handled = true; }
    }

    /// <summary>Copies to the clipboard; another app holding it open is retried once, then ignored.</summary>
    internal static void CopyText(string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true; // keep the window (and its bindings) for the next open
        Hide();
        base.OnClosing(e);
    }
}
