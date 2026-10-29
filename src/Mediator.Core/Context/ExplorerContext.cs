using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Mediator.Core.Context;

public sealed record ContextSnapshot(
    string ProcessName,
    string WindowTitle,
    string? ExplorerFolder,
    IReadOnlyList<string> SelectedPaths,
    bool AmbiguousExplorerTab);

/// <summary>
/// Captures what the user is looking at when the hotkey is pressed: foreground process and title, and for
/// File Explorer the current folder and selection (Shell.Application / SHDocVw ShellWindows via late-bound COM).
/// Costs zero tokens. Must run on an STA thread (the Shell objects are apartment-threaded).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class ExplorerContext
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr hWnd, [Out] char[] className, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static ContextSnapshot Capture()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("ExplorerContext.Capture must run on an STA thread.");

        IntPtr hwnd = GetForegroundWindow();
        string title = Text(hwnd, GetWindowText);
        string cls = Text(hwnd, GetClassName);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string process;
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
        catch (ArgumentException) { process = "unknown"; }

        if (cls != "CabinetWClass") // not a File Explorer window
            return new ContextSnapshot(process, title, null, [], false);

        var candidates = new List<(string Folder, string Location, List<string> Selection)>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return new ContextSnapshot(process, title, null, [], false);
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    if (w is null || (long)w.HWND != hwnd.ToInt64()) continue;
                    string folder = w.Document.Folder.Self.Path;
                    string location = w.LocationName;
                    var selection = new List<string>();
                    dynamic items = w.Document.SelectedItems();
                    int n = items.Count;
                    for (int j = 0; j < n && j < 500; j++) selection.Add((string)items.Item(j).Path);
                    candidates.Add((folder, location, selection));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    // Non-folder views (Home, search results, legacy IE windows) do not expose these members.
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        if (candidates.Count == 1)
            return new ContextSnapshot(process, title, candidates[0].Folder, candidates[0].Selection, false);

        // Windows 11 Explorer tabs share one top-level window handle; the title names the active tab.
        var byTitle = candidates.Where(c => title.StartsWith(c.Location, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (byTitle.Count == 1)
            return new ContextSnapshot(process, title, byTitle[0].Folder, byTitle[0].Selection, false);

        // Still ambiguous (two tabs on folders with the same name): let the overlay ask instead of guessing.
        return new ContextSnapshot(process, title, null, [], candidates.Count > 1);
    }

    private static string Text(IntPtr hwnd, Func<IntPtr, char[], int, int> read)
    {
        var buffer = new char[512];
        int n = read(hwnd, buffer, buffer.Length);
        return n > 0 ? new string(buffer, 0, n) : "";
    }
}
