// VERIFY ONLY: the Windows Forms tray icon and System.Drawing.Icon surface used by Mediator.Wpf/TrayIcon.cs.
using System.Diagnostics.CodeAnalysis;
// Nullable annotations as in .NET 8 (Windows Forms and System.Drawing are annotated there).
#nullable enable
using System.ComponentModel;
using System.Drawing;

namespace System.Drawing
{
    public abstract class Image : MarshalByRefObject, IDisposable
    {
        public void Dispose() { }
    }

    public sealed class Icon : MarshalByRefObject, IDisposable, ICloneable
    {
        private Icon() { }
        public static Icon? ExtractAssociatedIcon(string filePath) => filePath.Length == 0 ? throw new ArgumentException("filePath") : null;
        public object Clone() => new Icon();
        public void Dispose() { }
        internal static Icon Stock() => new();
    }

    public static class SystemIcons
    {
        public static Icon Application { get; } = Icon.Stock();
        public static Icon Information { get; } = Icon.Stock();
        public static Icon Warning { get; } = Icon.Stock();
    }
}

namespace System.Windows.Forms
{
    [Flags]
    public enum MouseButtons { None = 0, Left = 0x100000, Right = 0x200000, Middle = 0x400000, XButton1 = 0x800000, XButton2 = 0x1000000 }

    public enum ToolTipIcon { None = 0, Info = 1, Warning = 2, Error = 3 }

    public class MouseEventArgs : EventArgs
    {
        public MouseEventArgs(MouseButtons button, int clicks, int x, int y, int delta) { Button = button; Clicks = clicks; X = x; Y = y; Delta = delta; }
        public MouseButtons Button { get; }
        public int Clicks { get; }
        public int X { get; }
        public int Y { get; }
        public int Delta { get; }
    }
    public delegate void MouseEventHandler(object? sender, MouseEventArgs e);

    public abstract class ToolStripItem : Component
    {
        [AllowNull]
        public virtual string Text { get; set; } = "";
        public virtual bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
        public event EventHandler? Click;
    }

    public class ToolStripSeparator : ToolStripItem { }

    public class ToolStripMenuItem : ToolStripItem
    {
        public ToolStripMenuItem() { }
        public ToolStripMenuItem(string? text) => Text = text;
        public ToolStripMenuItem(string? text, Image? image, EventHandler? onClick) => Text = text;
    }

    public class ToolStripItemCollection : List<ToolStripItem>
    {
        public ToolStripItem Add(string? text) => Add(text, null, null);
        public ToolStripItem Add(string? text, Image? image, EventHandler? onClick)
        {
            var item = new ToolStripMenuItem(text, image, onClick);
            base.Add(item);
            return item;
        }
        public new int Add(ToolStripItem value)
        {
            base.Add(value);
            return Count - 1;
        }
    }

    public class ContextMenuStrip : Component
    {
        public ToolStripItemCollection Items { get; } = new();
    }

    public sealed class NotifyIcon : Component
    {
        [AllowNull]
        public string Text { get; set; } = "";
        public Drawing.Icon? Icon { get; set; }
        public ContextMenuStrip? ContextMenuStrip { get; set; }
        public bool Visible { get; set; }
        public event MouseEventHandler? MouseClick;
        public event EventHandler? DoubleClick;
        public void ShowBalloonTip(int timeout) { }
        public void ShowBalloonTip(int timeout, string tipTitle, string tipText, ToolTipIcon tipIcon) { }
    }
}
