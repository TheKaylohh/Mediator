using System.Runtime.InteropServices;

namespace Mediator.SwxWorker;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────
//  COM plumbing: ROT lookup without Marshal.GetActiveObject (removed in .NET Core), and a message filter.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
    [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

internal static class NativeMethods
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CLSIDFromProgID(string lpszProgID, out Guid pclsid);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    internal static extern int GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoRegisterMessageFilter(IOleMessageFilter? lpMessageFilter, out IOleMessageFilter? lplpMessageFilter);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int length, out int returnLength);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenElevation = 20;
}

/// <summary>Running Object Table lookup, replacing Marshal.GetActiveObject which does not exist in .NET 5+.</summary>
public static class PInvokeRot
{
    public const int MK_E_UNAVAILABLE = unchecked((int)0x800401E3);
    public const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);

    public static bool TryGetActiveObject(string progId, out object? instance, out int hresult)
    {
        if (TryOne(progId, out instance, out hresult)) return true;
        // SolidWorks registers in the ROT under its versioned CLSID (SldWorks.Application.33 = 2025); the version-independent
        // ProgID can point at a stale CLSID from an older install.
        for (int v = 44; v >= 20; v--)
            if (TryOne($"{progId}.{v}", out instance, out int hr)) { hresult = hr; return true; }
        return false;
    }

    private static bool TryOne(string progId, out object? instance, out int hresult)
    {
        instance = null;
        hresult = NativeMethods.CLSIDFromProgID(progId, out Guid clsid);
        if (hresult < 0) return false;
        hresult = NativeMethods.GetActiveObject(ref clsid, IntPtr.Zero, out instance);
        return hresult >= 0 && instance is not null;
    }

    public static object GetActiveObject(string progId)
    {
        if (TryGetActiveObject(progId, out var instance, out int hr)) return instance!;
        throw Marshal.GetExceptionForHR(hr) ?? new COMException($"GetActiveObject('{progId}') failed.", hr);
    }
}

/// <summary>
/// Retries calls that SolidWorks rejects with SERVERCALL_RETRYLATER (surfacing as RPC_E_SERVERCALL_RETRYLATER,
/// 0x8001010A) while it rebuilds or shows a modal dialog. Without it, those calls throw immediately.
/// Must be registered on the STA thread that makes the calls.
/// </summary>
[ComVisible(true)]
public sealed class ComMessageFilter : IOleMessageFilter
{
    private const int SERVERCALL_ISHANDLED = 0;
    private const int SERVERCALL_RETRYLATER = 2;
    private const int PENDINGMSG_WAITDEFPROCESS = 2;
    private const int RetryDelayMs = 250;   // values >= 100 mean "wait this many ms, then retry"
    private const int CancelCall = -1;      // caller then sees RPC_E_CALL_REJECTED

    private readonly int _maxRetryMs;
    private static long s_retries;

    private ComMessageFilter(TimeSpan maxRetry) => _maxRetryMs = (int)Math.Min(int.MaxValue, maxRetry.TotalMilliseconds);

    public static long RetryCount => Interlocked.Read(ref s_retries);

    public static void Register(TimeSpan maxRetry)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("CoRegisterMessageFilter only works on an STA thread.");
        Marshal.ThrowExceptionForHR(NativeMethods.CoRegisterMessageFilter(new ComMessageFilter(maxRetry), out _));
    }

    public static void Revoke() => NativeMethods.CoRegisterMessageFilter(null, out _);

    int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) =>
        SERVERCALL_ISHANDLED;

    int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        if (dwRejectType == SERVERCALL_RETRYLATER && dwTickCount < _maxRetryMs)
        {
            Interlocked.Increment(ref s_retries);
            return RetryDelayMs;
        }
        return CancelCall;
    }

    int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) =>
        PENDINGMSG_WAITDEFPROCESS;
}

/// <summary>Detects the elevation mismatch that makes a running SolidWorks invisible to the ROT.</summary>
public static class ProcessIntegrity
{
    /// <summary>True if the process runs elevated while we do not, or we cannot even inspect it (another user, higher integrity).</summary>
    public static bool IsInaccessibleOrMoreElevated(int pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return true;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out IntPtr token)) return true;
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out int elevated, sizeof(int), out _)) return false;
                return elevated != 0 && !Environment.IsPrivilegedProcess;
            }
            finally { NativeMethods.CloseHandle(token); }
        }
        finally { NativeMethods.CloseHandle(process); }
    }
}
