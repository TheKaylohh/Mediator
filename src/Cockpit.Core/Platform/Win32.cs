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

    // ── Handle-based path verification (PathAuthorizer) ──────────────────────────────────────────────
    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80;
    internal const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    internal const uint OPEN_EXISTING = 3, CREATE_NEW = 1;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FILE_NAME_NORMALIZED = 0, VOLUME_NAME_DOS = 0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO { public byte DeleteFile; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FILE_DISPOSITION_INFO info, uint size);

    /// <summary>The normalized DOS path of an open handle, without the \\?\ prefix. Null on failure.</summary>
    internal static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        uint n = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (n == 0 || n >= buffer.Length) return null;
        string p = new(buffer, 0, (int)n);
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p[8..];
        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    /// <summary>Marks the file behind an open handle (opened with DELETE access) for deletion: race-free, by identity not name.</summary>
    internal static bool DeleteByHandle(SafeFileHandle handle)
    {
        var info = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        return SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref info, 1);
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
