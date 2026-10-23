using System.Runtime.InteropServices;
using System.Text;

namespace Cockpit.SwxWorker;

/// <summary>A dialog that SolidWorks is showing while one of our COM calls is blocked.</summary>
public sealed record BlockingDialogInfo(string Title, string Text, IReadOnlyList<string> Buttons, string ClassName);

/// <summary>Finds a modal dialog owned by a process. Abstracted so the watchdog can be tested without Windows.</summary>
public interface ISwxDialogDetector
{
    BlockingDialogInfo? FindBlockingDialog(int processId);
}

/// <summary>
/// Raised to the caller when a COM call has been blocked for a few seconds and SolidWorks is showing a
/// modal dialog. The call itself is still running: when the user closes the dialog it continues or fails.
/// </summary>
public sealed class SwxBlockedByModalDialogException : StaQueueException
{
    public const string Code = "MODAL_DIALOG_BLOCKING";
    public BlockingDialogInfo Dialog { get; }

    public SwxBlockedByModalDialogException(BlockingDialogInfo dialog)
        : base($"SolidWorks is waiting on a dialog: \"{dialog.Title}\"" +
               (dialog.Text.Length > 0 ? $" ({dialog.Text})" : "") +
               (dialog.Buttons.Count > 0 ? $" [buttons: {string.Join(", ", dialog.Buttons)}]" : "") +
               ". Answer it in SolidWorks; the blocked operation will then continue or fail. Check the result before retrying.")
        => Dialog = dialog;
}

/// <summary>
/// Win32 detector: a visible top-level window of the SolidWorks process with the standard dialog class
/// "#32770", or any visible window of that process while its main window is disabled (the sign of a modal
/// loop). Reads the title, static text and button captions so the user is told what SolidWorks is asking.
/// </summary>
public sealed class Win32DialogDetector : ISwxDialogDetector
{
    private const string DialogClass = "#32770";

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    public BlockingDialogInfo? FindBlockingDialog(int processId)
    {
        if (processId <= 0) return null;
        var windows = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == processId && IsWindowVisible(h)) windows.Add(h);
            return true;
        }, IntPtr.Zero);
        if (windows.Count == 0) return null;

        IntPtr dialog = windows.FirstOrDefault(h => ClassOf(h) == DialogClass);
        if (dialog == IntPtr.Zero)
        {
            // Some SolidWorks dialogs use their own window classes; a disabled main window still gives the modal state away.
            IntPtr main;
            using (var p = System.Diagnostics.Process.GetProcessById(processId)) main = p.MainWindowHandle;
            if (main == IntPtr.Zero || IsWindowEnabled(main)) return null;
            dialog = windows.FirstOrDefault(h => h != main && TextOf(h).Length > 0);
            if (dialog == IntPtr.Zero) return null;
        }

        var statics = new List<string>();
        var buttons = new List<string>();
        EnumChildWindows(dialog, (h, _) =>
        {
            string cls = ClassOf(h);
            string text = TextOf(h).Replace("&", "", StringComparison.Ordinal).Trim();
            if (text.Length == 0 || !IsWindowVisible(h)) return true;
            if (cls == "Static") statics.Add(text);
            else if (cls == "Button") buttons.Add(text);
            return true;
        }, IntPtr.Zero);

        string body = string.Join(" | ", statics);
        if (body.Length > 500) body = body[..500] + "…";
        return new BlockingDialogInfo(TextOf(dialog), body, buttons, ClassOf(dialog));
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string TextOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
