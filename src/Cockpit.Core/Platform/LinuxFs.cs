using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Core.Platform;

/// <summary>
/// Linux file-system primitives for race-free path resolution.
/// <c>openat2(2)</c> with RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV makes the
/// kernel refuse, atomically with the open, any path that leaves the directory fd, crosses a symlink (in any
/// component) or crosses a mount point (bind mounts included). Available since Linux 5.6; WSL2 kernels qualify.
/// Flag values differ between x86_64 and arm64 and are chosen at run time.
/// </summary>
/// <summary>Linux errno values (identical on x86_64 and arm64).</summary>
internal static class LinuxErrno
{
    internal const int ENOENT = 2, EEXIST = 17, EXDEV = 18, ENOTDIR = 20, EISDIR = 21, ELOOP = 40, ENOSYS = 38, EAGAIN = 11, EPERM = 1, EACCES = 13, ENOSPC = 28;
}

[SupportedOSPlatform("linux")]
internal static partial class LinuxFs
{
    private const long SysOpenat2 = 437; // same number on x86_64 and arm64 (new syscalls use the unified table)
    internal const int ENOENT = LinuxErrno.ENOENT, EEXIST = LinuxErrno.EEXIST, ENOSYS = LinuxErrno.ENOSYS, ENOSPC = LinuxErrno.ENOSPC;

    internal const ulong RESOLVE_NO_XDEV = 0x01, RESOLVE_NO_MAGICLINKS = 0x02, RESOLVE_NO_SYMLINKS = 0x04, RESOLVE_BENEATH = 0x08;
    internal const ulong StrictResolve = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV;

    private static readonly bool Arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
    internal const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, O_CREAT = 0x40, O_EXCL = 0x80;
    internal static int O_DIRECTORY => Arm ? 0x4000 : 0x10000;
    internal static int O_NOFOLLOW => Arm ? 0x8000 : 0x20000;
    internal const int O_CLOEXEC = 0x80000;
    internal const int O_PATH = 0x200000;
    internal const int AT_FDCWD = -100;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall4(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open(byte[] path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static partial int MkdirAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static partial int UnlinkAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static int? _openat2Supported; // null = unknown

    /// <summary>True if this kernel implements openat2.</summary>
    internal static bool Openat2Supported
    {
        get
        {
            if (_openat2Supported is int known) return known == 1;
            int fd = OpenRootDirectory("/", out int err);
            if (fd >= 0) Close(fd);
            _openat2Supported = err == ENOSYS ? 0 : 1;
            return _openat2Supported == 1;
        }
    }

    /// <summary>Opens an absolute directory with O_PATH|O_DIRECTORY (symlinks in the path are followed: roots are resolved once, up front).</summary>
    internal static int OpenRootDirectory(string absolute, out int errno)
    {
        var how = new OpenHow { Flags = (ulong)(O_PATH | O_DIRECTORY | O_CLOEXEC), Mode = 0, Resolve = RESOLVE_NO_MAGICLINKS };
        long r = Syscall4(SysOpenat2, AT_FDCWD, absolute, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return (int)r;
    }

    /// <summary>openat2 relative to <paramref name="dirfd"/> under the strict resolve rules. Returns fd or -1 with errno.</summary>
    internal static int OpenBeneath(int dirfd, string relative, int flags, int mode, out int errno)
    {
        var how = new OpenHow { Flags = (ulong)(flags | O_CLOEXEC), Mode = (ulong)mode, Resolve = StrictResolve };
        long r = Syscall4(SysOpenat2, dirfd, relative.Length == 0 ? "." : relative, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return (int)r;
    }

    /// <summary>mkdir of a single name inside an already verified directory fd (no path resolution at all).</summary>
    internal static int MakeDirectoryIn(int dirfd, string name, out int errno)
    {
        int r = MkdirAt(dirfd, name, 0x1ED /* 0755 */);
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static partial int RenameAt2(int olddirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, int newdirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath, uint flags);

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static partial int ChownNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int owner, int group);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial int GetEuid();

    internal static void Chown(string path, int uid, int gid)
    {
        if (ChownNative(path, uid, gid) < 0) throw new IOException($"chown {path} failed (errno {Marshal.GetLastPInvokeError()})");
    }

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    internal const uint RENAME_NOREPLACE = 1;
    internal const int AT_REMOVEDIR = 0x200;
    internal const int EINVAL = 22, ENOTEMPTY = 39;

    /// <summary>renameat2 between two verified directory fds. flags = RENAME_NOREPLACE refuses to overwrite atomically.</summary>
    internal static int RenameIn(int srcDir, string srcName, int dstDir, string dstName, uint flags, out int errno)
    {
        int r = RenameAt2(srcDir, srcName, dstDir, dstName, flags);
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }

    /// <summary>fsync a directory fd so a rename inside it survives power loss.</summary>
    internal static int SyncFd(int fd, out int errno)
    {
        int r = Fsync(fd);
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }

    internal static int UnlinkIn(int dirfd, string name, int flags, out int errno)
    {
        int r = UnlinkAt(dirfd, name, flags);
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }

    /// <summary>The kernel's current path for an open fd (readlink of /proc/self/fd/N). Null if unavailable.</summary>
    internal static string? FinalPath(int fd)
    {
        try
        {
            string? target = new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
            if (target is null) return null;
            // A deleted file reads as "/path (deleted)"; report it as such so callers fail closed.
            return target;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static SafeFileHandle Wrap(int fd) => new((IntPtr)fd, ownsHandle: true);

    /// <summary>Mount points from /proc/self/mountinfo (field 5, octal escapes decoded).</summary>
    internal static IReadOnlyList<string> MountPoints()
    {
        var list = new List<string>();
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var f = line.Split(' ');
                if (f.Length > 4) list.Add(Unescape(f[4]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal)) return s;
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && IsOct(s, i + 1))
            {
                bytes.Add((byte)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOct(string s, int at) => at + 3 <= s.Length && s[at] is >= '0' and <= '7' && s[at + 1] is >= '0' and <= '7' && s[at + 2] is >= '0' and <= '7';

    /// <summary>Plain open(2) with O_NOFOLLOW, used only when openat2 is unavailable.</summary>
    internal static int OpenNoFollow(string absolute, int flags, int mode, out int errno)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(absolute + "\0");
        int r = Open(bytes, flags | O_NOFOLLOW | O_CLOEXEC, mode);
        errno = r < 0 ? Marshal.GetLastPInvokeError() : 0;
        return r;
    }
}
