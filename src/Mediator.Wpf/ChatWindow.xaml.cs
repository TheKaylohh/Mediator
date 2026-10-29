using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mediator.UI;

namespace Mediator.Wpf;

/// <summary>
/// Full-screen chat view of the overlay's conversation, laid out and driven like the Claude app (see ComposerKeys for the
/// prompt box). Shortcuts: Ctrl+Shift+O new chat, Ctrl+K search chats, Ctrl+B sidebar, Ctrl+Shift+C copy the last answer.
/// Closing only hides it; it is reused. V14.1: no Windows title bar; a slim bar (minimize, maximize, close) slides in when the
/// mouse reaches the top edge and hides about two seconds after it leaves.
/// </summary>
public partial class ChatWindow : Window
{
    private readonly MediatorOverlayViewModel _vm;
    private GridLength _sidebarWidth = new(270);
    private readonly DispatcherTimer _barHide = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _barShown;
    private const double BarRevealZone = 6; // px from the top edge of the content

    public ChatWindow(MediatorOverlayViewModel vm, DragEventHandler dragOver, DragEventHandler dragLeave, DragEventHandler drop)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ComposerKeys.Attach(PromptBox, vm);
        PreviewDragEnter += dragOver;
        PreviewDragOver += dragOver;
        PreviewDragLeave += dragLeave;
        PreviewDrop += drop;
        PreviewKeyDown += OnShortcut;
        vm.Conversation.CollectionChanged += (_, _) => ScrollToEnd();
        vm.FocusPromptRequested += () => { if (IsVisible) FocusPrompt(); };
        _barHide.Tick += (_, _) => { _barHide.Stop(); SetBar(false); };
        PreviewMouseMove += OnMouseMoveForBar;
        MouseLeave += (_, _) => { if (_barShown) _barHide.Start(); };
        StateChanged += (_, _) => FitToWorkArea();
        Loaded += (_, _) => FitToWorkArea();
#if MEDIATOR_WEBVIEW
        bool attached = false;
        Loaded += async (_, _) => { if (!attached) { attached = true; await ChatRenderer.TryAttachAsync(ChatScroll, vm); } };
#endif
    }

    public void Open()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Activate();
        ScrollToEnd();
        FocusPrompt();
    }

    // ── Window bar ───────────────────────────────────────────────────────────────────────────────────

    private void OnMouseMoveForBar(object sender, MouseEventArgs e)
    {
        double y = e.GetPosition(RootGrid).Y;
        if (y <= BarRevealZone || (_barShown && WindowBar.IsMouseOver)) { SetBar(true); _barHide.Stop(); }
        else if (_barShown && !_barHide.IsEnabled) _barHide.Start();
    }

    private void SetBar(bool show)
    {
        if (show == _barShown) return;
        _barShown = show;
        WindowBar.IsHitTestVisible = show;
        WindowBar.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 250)));
    }

    /// <summary>Without a title bar, a maximized window reaches past the screen by its resize border: pull the content in.</summary>
    private void FitToWorkArea()
    {
        RootGrid.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    /// <summary>Drag to move, double-click to maximize or restore (the bar's buttons handle their own clicks).</summary>
    private void WindowBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { MaximizeClick(sender, e); return; }
        try { DragMove(); } catch (InvalidOperationException) { } // the button was released before the drag started
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    public void FocusPrompt() => Dispatcher.BeginInvoke(() => { PromptBox.Focus(); PromptBox.CaretIndex = PromptBox.Text.Length; }, DispatcherPriority.Input);

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if (!ctrl) return;
        switch (e.Key)
        {
            case Key.O when shift:
                if (_vm.ClearConversationCommand.CanExecute(null)) _vm.ClearConversationCommand.Execute(null);
                FocusPrompt();
                break;
            case Key.K when !shift:
                if (SidebarColumn.Width.Value == 0) ToggleSidebar(this, e);
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.B when !shift:
                ToggleSidebar(this, e);
                break;
            case Key.C when shift:
                if (_vm.LastResponse is { } text) CopyText(text);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        bool hidden = SidebarColumn.Width.Value == 0;
        if (!hidden) _sidebarWidth = SidebarColumn.Width;
        SidebarColumn.Width = hidden ? _sidebarWidth : new GridLength(0);
        Sidebar.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
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
