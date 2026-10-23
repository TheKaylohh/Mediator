using System.Drawing;
using Forms = System.Windows.Forms;

namespace Cockpit.Wpf;

/// <summary>
/// The tray icon, like Toolbox's: left click toggles the panel, right click offers Open / Kill all / Quit.
/// A balloon announces approvals that arrive while the panel is hidden.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public event Action? ToggleRequested;
    public event Action? KillAllRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open AI Cockpit  (Ctrl+Shift+Space)", null, (_, _) => ToggleRequested?.Invoke());
        menu.Items.Add("Kill all jobs", null, (_, _) => KillAllRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Text = "AI Cockpit",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleRequested?.Invoke(); };
    }

    private static Icon LoadIcon()
    {
        // The exe's own icon when one is embedded; otherwise the stock application icon.
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null && Icon.ExtractAssociatedIcon(exe) is { } own) return own;
        }
        catch (ArgumentException) { }
        return SystemIcons.Application;
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
