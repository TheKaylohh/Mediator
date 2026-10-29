using System.Diagnostics;
using System.Runtime.InteropServices;
using Mediator.Core.Runtime;

namespace Mediator.Core.Swx;

/// <summary>
/// Starts SolidWorks when a job needs it and it is not running. COM activation (CoCreateInstance) is not registered on
/// every install, so SLDWORKS.exe is started directly and the caller attaches through the Running Object Table.
/// One SolidWorks only: every start goes through an in-process lock and a session-wide named mutex (the overlay, the
/// CLI host and the part builder can all ask at once), and the check for a running copy happens inside that lock.
/// </summary>
public static partial class SolidWorksLauncher
{
    public const string ProgId = "SldWorks.Application";
    public const string ProcessName = "SLDWORKS";
    /// <summary>Held while one caller checks for and starts SolidWorks. "Local\": per Windows session, like SolidWorks itself.</summary>
    public const string LaunchMutexName = @"Local\Mediator-SolidWorks-Launch";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    /// <summary>SolidWorks registers in the ROT before its add-ins and UI have finished loading.</summary>
    private static readonly TimeSpan SettleAfterStart = TimeSpan.FromSeconds(3);
    private static readonly object s_launchLock = new();

    /// <summary>True when a SolidWorks process exists in this session (it may still be starting).</summary>
    public static bool IsRunning()
    {
        var running = Process.GetProcessesByName(ProcessName);
        try { return running.Length > 0; }
        finally { foreach (var p in running) p.Dispose(); }
    }

    /// <summary>The running SolidWorks session (from the ROT), starting SLDWORKS.exe first if needed. Windows only.</summary>
    public static object Attach(TimeSpan wait)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SolidWorks needs Windows.");
        if (TryGetActive(out var sw)) return sw!;
        EnsureRunning(wait);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < wait)
        {
            if (TryGetActive(out sw)) return sw!;
            Thread.Sleep(PollInterval);
        }
        throw new TimeoutException("SolidWorks is running but did not become available for automation (still starting, or a dialog is open?).");
    }

    /// <summary>
    /// Makes sure exactly one SolidWorks is running and accepts automation. Starts SLDWORKS.exe only when no SolidWorks
    /// process exists; a copy that is still starting is waited for, never duplicated. Returns true when this call started it.
    /// </summary>
    public static bool EnsureRunning(TimeSpan wait)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (TryGetActive(out _)) return false;
        lock (s_launchLock)
        {
            using var mutex = new Mutex(initiallyOwned: false, LaunchMutexName);
            bool owned;
            try { owned = mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { owned = true; } // a previous holder crashed; the lock is ours now
            if (!owned) throw new TimeoutException("Another Mediator window is still starting SolidWorks.");
            try
            {
                if (TryGetActive(out _)) return false; // started by whoever held the lock before us
                bool started = false;
                if (!IsRunning())
                {
                    var install = DesktopAppLocator.Default.Find(DesktopApp.SolidWorks)
                        ?? throw new InvalidOperationException("SolidWorks is not installed (no SLDWORKS.exe found).");
                    using var process = Process.Start(new ProcessStartInfo(install.Executable) { UseShellExecute = true });
                    started = true;
                }
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < wait)
                {
                    if (TryGetActive(out _))
                    {
                        if (started) Thread.Sleep(SettleAfterStart);
                        return started;
                    }
                    Thread.Sleep(PollInterval);
                }
                throw new TimeoutException($"SolidWorks did not finish starting within {wait.TotalSeconds:0} s (a start-up dialog may be waiting for you).");
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// SolidWorks registers in the ROT under its versioned CLSID (SldWorks.Application.33 for 2025). The version-independent
    /// ProgID can point at a stale CLSID left by an older install, so the versioned ones are tried too.
    /// </summary>
    public static IEnumerable<string> ProgIds() => new[] { ProgId }.Concat(Enumerable.Range(20, 25).Reverse().Select(v => $"{ProgId}.{v}"));

    private static bool TryGetActive(out object? instance)
    {
        instance = null;
        if (!OperatingSystem.IsWindows()) return false;
        foreach (string progId in ProgIds())
        {
            if (CLSIDFromProgID(progId, out var clsid) != 0) continue;
            if (GetActiveObject(ref clsid, IntPtr.Zero, out instance) == 0 && instance is not null) return true;
        }
        return false;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}
