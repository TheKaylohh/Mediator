using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Core.Platform;

[SupportedOSPlatform("windows")]
internal static partial class Win32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    /// <summary>The mount point of the volume holding the path (e.g. "D:\" or "D:\Archive\" for a mounted disk).</summary>
    internal static string? VolumeOf(string path)
    {
        var buffer = new char[1024];
        if (!GetVolumePathName(path, buffer, (uint)buffer.Length)) return null;
        int len = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, len < 0 ? buffer.Length : len);
    }

    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE: every process assigned to it dies when the Core exits or
/// crashes, so no orphaned bridge, CLI or COM worker survives. SolidWorks itself is never assigned: it is
/// the user's application, activated by COM rather than spawned by us.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private readonly SafeFileHandle? _job;

    public ChildProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateWindowsJob();
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle CreateWindowsJob()
    {
        var job = Win32.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Win32.SetInformationJobObject(job, Win32.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
        {
            int err = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(err, "SetInformationJobObject failed");
        }
        return job;
    }

    /// <summary>Assigns a freshly started process. No-op on non-Windows.</summary>
    public void Assign(Process process)
    {
        if (_job is null || !OperatingSystem.IsWindows()) return;
        if (!Win32.AssignProcessToJobObject(_job, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"AssignProcessToJobObject failed for PID {process.Id}");
    }

    /// <summary>Kill switch: terminates every process in the job.</summary>
    public void TerminateAll(uint exitCode = 1)
    {
        if (_job is null || !OperatingSystem.IsWindows()) return;
        Win32.TerminateJobObject(_job, exitCode);
    }

    public void Dispose() => _job?.Dispose();
}

public static class PipeSecurityFactory
{
    /// <summary>
    /// Current user only, and an explicit deny for network logons so the bus is never reachable from
    /// another machine even though named pipes are remotable by default.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CurrentUserLocalOnly()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID for the current process.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    [SupportedOSPlatform("windows")]
    public static int? GetClientProcessId(NamedPipeServerStream pipe) =>
        Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;
}
