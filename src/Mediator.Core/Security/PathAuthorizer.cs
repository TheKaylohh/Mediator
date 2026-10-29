using System.Runtime.InteropServices;
using Mediator.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace Mediator.Core.Security;

/// <summary>What the caller is about to do with a path. Writes are stricter than reads.</summary>
public enum PathAccess
{
    /// <summary>Open an existing file for reading.</summary>
    Read,
    /// <summary>Enumerate an existing folder (the root itself is allowed).</summary>
    List,
    /// <summary>Create a file that must not already exist.</summary>
    CreateFile,
    /// <summary>Create a folder (and missing parents) beneath a root.</summary>
    CreateDirectory,
    /// <summary>Move, rename, replace or delete an existing entry. Never allowed on a root itself.</summary>
    Modify,
}

/// <summary>Result of an authorization check. <see cref="FullPath"/> is the normalized path to use from now on.</summary>
public sealed record PathVerdict(bool Allowed, string FullPath, string? Root, string Code, string? Reason)
{
    public static PathVerdict Deny(string path, string code, string reason) => new(false, path, null, code, reason);
    /// <summary>Message for a tool result: names the rule so the audit trail shows why.</summary>
    public string Message => Allowed ? "allowed" : $"[path:{Code}] {FullPath}: {Reason}";
}

public sealed class PathDeniedException : UnauthorizedAccessException
{
    public PathVerdict Verdict { get; }
    public PathDeniedException(PathVerdict verdict) : base(verdict.Message) => Verdict = verdict;
}

/// <summary>
/// The single place that decides whether a path may be touched by a job. Fail-closed throughout: any error while
/// inspecting the file system is a denial.
///
/// Rules, in order:
///  1. Lexical (<see cref="PathSyntax"/>) on the raw string: device paths, UNC, ADS, trailing dots/spaces,
///     reserved names, 8.3 short names, '..' segments.
///  2. Containment by path <em>component</em> against roots that were resolved to their final, real paths
///     when the grant was issued (so "C:\Proj" and "C:\Project" can never be confused).
///  3. No component strictly below the root may be a symbolic link, junction, mount point or other reparse
///     point, whether intermediate or final.
///  4. No mount point may lie between the root and the target (Linux bind mounts are not reparse points; on
///     Windows mounted folders are reparse points and caught by rule 3, and the volume is compared too).
///  5. The root itself may be read and listed, never modified, replaced or deleted.
///
/// The check-then-use gap (TOCTOU) is closed differently per OS:
///  - Linux / WSL: opens go through openat2 with RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_XDEV relative
///    to a root directory fd, so the kernel enforces rules 2–4 atomically with the open.
///  - Windows: the opened handle's final path (GetFinalPathNameByHandle) is re-checked against the root. A file
///    created through a swapped junction is deleted by handle and the call fails. This detects rather than
///    prevents; see docs/v6/FILESYSTEM_SECURITY.md.
/// Callers that pass a path to an external program cannot hand over a handle; they call
/// <see cref="VerifyExisting"/> immediately before launch (a residual window documented in the same file).
/// </summary>
public sealed class PathAuthorizer
{
    private sealed record RootEntry(string Lexical, string Final, bool ReadOnly);

    /// <summary>Test hook: forces the portable fallback (no openat2) to prove it also fails closed.</summary>
    internal static bool ForceFallback { get; set; }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    private static bool UseKernelResolve => OperatingSystem.IsLinux() && !ForceFallback && LinuxFs.Openat2Supported;

    private volatile RootEntry[] _roots;
    private readonly object _rootsLock = new();
    private readonly PathSyntax _syntax;

    /// <summary>Every root (writable and read-only), as real paths.</summary>
    public IReadOnlyList<string> Roots => _roots.Select(r => r.Final).ToArray();
    /// <summary>Roots that may be read and listed but never changed (V7: attachments the user dropped or pasted).</summary>
    public IReadOnlyList<string> ReadOnlyRoots => _roots.Where(r => r.ReadOnly).Select(r => r.Final).ToArray();
    public PathSyntax Syntax => _syntax;

    /// <summary>Raised for every denial (wired to the audit log by the bus).</summary>
    public event Action<PathVerdict>? Denied;

    /// <param name="roots">Existing folders. A root that does not exist, or cannot be resolved, is a configuration error.</param>
    /// <param name="readOnlyRoots">Existing folders the job may only read and list (Modify and Create are denied with code read_only).</param>
    public PathAuthorizer(IEnumerable<string> roots, IEnumerable<string>? readOnlyRoots = null)
    {
        _syntax = PathSyntax.Current;
        var list = roots.Select(r => Resolve(r, readOnly: false)).Concat((readOnlyRoots ?? []).Select(r => Resolve(r, readOnly: true))).ToList();
        _roots = Order(list);
    }

    private RootEntry Resolve(string raw, bool readOnly)
    {
        if (_syntax.Check(raw) is string bad) throw new ArgumentException($"Root '{raw}' is not acceptable: {bad}");
        string lexical = Trim(Path.GetFullPath(raw));
        if (!Directory.Exists(lexical)) throw new ArgumentException($"Root '{lexical}' does not exist or is not a folder.");
        string final = Trim(ResolveRootFinalPath(lexical) ?? throw new ArgumentException($"Root '{lexical}' could not be resolved to a real path."));
        if (_syntax.Check(final) is string badFinal) throw new ArgumentException($"Root '{lexical}' resolves to '{final}', which is not acceptable: {badFinal}");
        return new RootEntry(lexical, final, readOnly);
    }

    /// <summary>
    /// Most specific first, so a nested root wins. A read-only root at or inside a writable root is dropped: it is
    /// already readable there, and attaching something must never take write access away from a running job.
    /// </summary>
    private RootEntry[] Order(IEnumerable<RootEntry> list)
    {
        var all = list.ToList();
        var writable = all.Where(r => !r.ReadOnly).ToList();
        return all.Where(r => !r.ReadOnly || !writable.Any(w => _syntax.IsSameOrInside(r.Final, w.Final)))
            .GroupBy(r => r.Final, _syntax.Comparer).Select(g => g.First())
            .OrderByDescending(r => _syntax.Components(r.Final).Length).ToArray();
    }

    /// <summary>
    /// Adds a read-only root to a live grant (V7: the user attached a folder to a running job). Only ever adds read
    /// access; the caller audits it. Returns the root's real path.
    /// </summary>
    public string AddReadOnlyRoot(string raw)
    {
        var entry = Resolve(raw, readOnly: true);
        lock (_rootsLock) _roots = Order(_roots.Append(entry));
        return entry.Final;
    }

    /// <summary>
    /// Adds a writable root to a live grant (V10: the user allowed a folder for a running job, or picked Save to). The
    /// caller checks it against the protected-path policy and audits it. Returns the root's real path.
    /// </summary>
    public string AddRoot(string raw)
    {
        var entry = Resolve(raw, readOnly: false);
        lock (_rootsLock) _roots = Order(_roots.Where(r => !_syntax.Comparer.Equals(r.Final, entry.Final)).Append(entry));
        return entry.Final;
    }

    /// <summary>How many allowed folders a refusal lists (the rest are counted).</summary>
    private const int ListedRoots = 4;

    /// <summary>The refusal for a path outside every root: which folders are allowed, and how the user can allow another.</summary>
    private string OutsideRootsReason()
    {
        var writable = _roots.Where(r => !r.ReadOnly).Select(r => r.Final).ToList();
        string list = writable.Count == 0 ? "none" : string.Join("; ", writable.Take(ListedRoots)) + (writable.Count > ListedRoots ? $" and {writable.Count - ListedRoots} more" : "");
        return $"outside this job's allowed folders (allowed: {list}). Use an allowed folder, or ask the user to click Allow on " +
               "the folder request in Mediator (or pick it with Allow folder / Save to), then try again";
    }

    private static string Trim(string p) => p.Length > 1 && Path.EndsInDirectorySeparator(p) && Path.GetPathRoot(p) != p ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;

    // ── Checks ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Authorizes a path for an access without opening it. Use the returned FullPath afterwards.</summary>
    public PathVerdict Check(string? raw, PathAccess access)
    {
        var v = Evaluate(raw, access);
        if (!v.Allowed) Denied?.Invoke(v);
        return v;
    }

    /// <summary>Like <see cref="Check"/> but throws <see cref="PathDeniedException"/>.</summary>
    public string Require(string? raw, PathAccess access)
    {
        var v = Check(raw, access);
        return v.Allowed ? v.FullPath : throw new PathDeniedException(v);
    }

    private PathVerdict Evaluate(string? raw, PathAccess access)
    {
        if (_syntax.Check(raw) is string syntax) return PathVerdict.Deny(raw ?? "", "syntax", syntax);
        string full;
        try { full = Trim(Path.GetFullPath(raw!)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return PathVerdict.Deny(raw!, "syntax", ex.Message);
        }

        // Map a lexical alias of a root (one that itself resolved elsewhere) onto the real root.
        foreach (var r in _roots)
        {
            if (!_syntax.Comparer.Equals(r.Lexical, r.Final) && _syntax.IsSameOrInside(full, r.Lexical))
            {
                string rel = Path.GetRelativePath(r.Lexical, full);
                full = rel == "." ? r.Final : Trim(Path.Join(r.Final, rel));
                break;
            }
        }

        RootEntry? root = _roots.FirstOrDefault(r => _syntax.IsSameOrInside(full, r.Final));
        if (root is null) return PathVerdict.Deny(full, "outside_roots", OutsideRootsReason());
        int depth = _syntax.RelativeDepth(full, root.Final);
        if (root.ReadOnly && access is PathAccess.Modify or PathAccess.CreateFile or PathAccess.CreateDirectory)
            return PathVerdict.Deny(full, "read_only", "this folder was attached read-only: it can be read but nothing in it can be changed or created");
        if (depth == 0 && access is PathAccess.Modify or PathAccess.CreateFile or PathAccess.CreateDirectory)
            return PathVerdict.Deny(full, "root_itself", "an allowed folder itself can be read but never moved, replaced or deleted");

        // Rule 3: every existing component strictly below the root must be a plain file or folder.
        var parts = _syntax.Components(full);
        int rootLen = _syntax.Components(root.Final).Length;
        string current = root.Final;
        bool exists = true;
        for (int i = rootLen; i < parts.Length; i++)
        {
            current = Path.Join(current, parts[i]);
            FileSystemInfo? info;
            try
            {
                info = Directory.Exists(current) || IsLink(current) ? new DirectoryInfo(current) : File.Exists(current) ? new FileInfo(current) : null;
                if (info is not null && (info.Attributes & FileAttributes.ReparsePoint) != 0 || info?.LinkTarget is not null)
                    return PathVerdict.Deny(full, "reparse_point", $"'{current}' is a symbolic link, junction or other reparse point");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PathVerdict.Deny(full, "inspect_failed", $"cannot inspect '{current}': {ex.Message}");
            }
            if (info is null)
            {
                exists = false;
                break;
            }
            if (i < parts.Length - 1 && info is FileInfo)
                return PathVerdict.Deny(full, "not_a_folder", $"'{current}' is a file, not a folder");
        }

        // Rule 4: no mount point between the root and the target.
        if (CrossesMount(full, root.Final) is string mount)
            return PathVerdict.Deny(full, "crosses_mount", $"path crosses the mount point '{mount}'");

        switch (access)
        {
            case PathAccess.Read when !exists || !File.Exists(full):
                return PathVerdict.Deny(full, "not_found", "file does not exist");
            case PathAccess.List when !exists || !Directory.Exists(full):
                return PathVerdict.Deny(full, "not_found", "folder does not exist");
            case PathAccess.Modify when !exists:
                return PathVerdict.Deny(full, "not_found", "nothing exists at this path");
            case PathAccess.CreateFile when exists:
                return PathVerdict.Deny(full, "exists", "a file or folder already exists here; it is never overwritten");
        }
        return new PathVerdict(true, full, root.Final, "ok", null);
    }

    private static bool IsLink(string p)
    {
        try { return new FileInfo(p).LinkTarget is not null; }
        catch (IOException) { return false; }
    }

    private string? CrossesMount(string full, string root)
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var m in LinuxFs.MountPoints())
            {
                if (_syntax.RelativeDepth(m, root) > 0 && _syntax.IsSameOrInside(full, m)) return m;
            }
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            string? vr = Win32.VolumeOf(root + Path.DirectorySeparatorChar);
            string nearest = full;
            while (!File.Exists(nearest) && !Directory.Exists(nearest)) nearest = Path.GetDirectoryName(nearest) ?? root;
            string? vt = Win32.VolumeOf(Directory.Exists(nearest) ? nearest + Path.DirectorySeparatorChar : nearest);
            if (vr is null || vt is null) return "(volume could not be determined)";
            return _syntax.Comparer.Equals(vr, vt) ? null : vt;
        }
        return null;
    }

    // ── Handle-based operations ──────────────────────────────────────────────────────────────────────

    /// <summary>Opens an existing file for reading, verified by handle. The caller reads from this stream, not the path.</summary>
    public FileStream OpenRead(string raw)
    {
        var v = Check(raw, PathAccess.Read);
        if (!v.Allowed) throw new PathDeniedException(v);
        var handle = OpenVerified(v, create: false);
        return new FileStream(handle, FileAccess.Read, bufferSize: 81920);
    }

    /// <summary>Creates a new file (never overwrites), verified by handle. The parent folder must already exist.</summary>
    public FileStream CreateNew(string raw)
    {
        var v = Check(raw, PathAccess.CreateFile);
        if (!v.Allowed) throw new PathDeniedException(v);
        var handle = OpenVerified(v, create: true);
        return new FileStream(handle, FileAccess.Write, bufferSize: 81920);
    }

    /// <summary>Writes a new UTF-8 file through <see cref="CreateNew"/>.</summary>
    public void WriteNewText(string raw, string content)
    {
        using var fs = CreateNew(raw);
        using var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false));
        w.Write(content);
    }

    /// <summary>
    /// Re-verifies an existing file or folder by opening it (for paths handed to external programs). Call right
    /// before the launch; returns the verdict (denied if the handle resolves outside the root).
    /// </summary>
    public PathVerdict VerifyExisting(string raw, PathAccess access)
    {
        var v = Check(raw, access);
        if (!v.Allowed) return v;
        try
        {
            bool dir = Directory.Exists(v.FullPath);
            using var h = dir ? OpenDirectoryVerified(v) : OpenVerified(v, create: false);
            return v;
        }
        catch (PathDeniedException ex) { return ex.Verdict; }
    }

    /// <summary>Creates a folder and any missing parents beneath a root, one component at a time, each verified.</summary>
    public string EnsureDirectory(string raw)
    {
        var v = Check(raw, PathAccess.CreateDirectory);
        if (!v.Allowed) throw new PathDeniedException(v);
        var parts = _syntax.Components(v.FullPath);
        int rootLen = _syntax.Components(v.Root!).Length;
        string current = v.Root!;
        for (int i = rootLen; i < parts.Length; i++)
        {
            string next = Path.Join(current, parts[i]);
            if (!Directory.Exists(next))
            {
                if (UseKernelResolve)
                    MakeDirectoryLinux(v, current, parts[i]);
                else
                    Directory.CreateDirectory(next);
            }
            current = next;
            var check = Check(current, PathAccess.List);
            if (!check.Allowed) throw new PathDeniedException(check with { Code = "race_detected", Reason = "folder changed while it was being created: " + check.Reason });
        }
        using (OpenDirectoryVerified(Check(v.FullPath, PathAccess.List) is { Allowed: true } ok ? ok : throw new PathDeniedException(v))) { }
        return v.FullPath;
    }

    /// <summary>
    /// Renames an existing file to a new name without ever overwriting. Both paths must be authorized (source for
    /// Modify, destination for CreateFile) and on the same mount. Linux: renameat2(RENAME_NOREPLACE) between parent
    /// folders opened with openat2 beneath their roots, then both folders are fsynced. Windows: MoveFileEx without
    /// REPLACE_EXISTING, then the destination handle is verified against its root.
    /// </summary>
    public void MoveNoReplace(string srcRaw, string dstRaw)
    {
        var s = Check(srcRaw, PathAccess.Modify);
        if (!s.Allowed) throw new PathDeniedException(s);
        var d = Check(dstRaw, PathAccess.CreateFile);
        if (!d.Allowed) throw new PathDeniedException(d);
        if (UseKernelResolve)
        {
            MoveLinux(s, d);
            return;
        }
        File.Move(s.FullPath, d.FullPath, overwrite: false);
        var after = VerifyExisting(d.FullPath, Directory.Exists(d.FullPath) ? PathAccess.List : PathAccess.Read);
        if (!after.Allowed)
            throw Fail(d, "race_detected", "after the move the destination resolved outside the allowed folders (a link was swapped in); stop and inspect " + d.FullPath);
    }

    /// <summary>Removes an empty folder that lies strictly beneath a root. Returns false if it is not empty.</summary>
    public bool RemoveEmptyDirectory(string raw)
    {
        var v = Check(raw, PathAccess.Modify);
        if (!v.Allowed) throw new PathDeniedException(v);
        if (!Directory.Exists(v.FullPath)) throw Fail(v, "not_a_folder", "not a folder");
        if (UseKernelResolve)
        {
            int parent = OpenParentLinux(v, out string name);
            try
            {
                if (LinuxFs.UnlinkIn(parent, name, LinuxFs.AT_REMOVEDIR, out int err) < 0)
                {
                    if (err == LinuxFs.ENOTEMPTY || err == LinuxErrno.EEXIST) return false;
                    throw new IOException($"rmdir '{v.FullPath}' failed (errno {err})");
                }
                LinuxFs.SyncFd(parent, out _);
                return true;
            }
            finally { LinuxFs.Close(parent); }
        }
        if (Directory.EnumerateFileSystemEntries(v.FullPath).Any()) return false;
        Directory.Delete(v.FullPath, recursive: false);
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private int OpenParentLinux(PathVerdict v, out string name)
    {
        name = Path.GetFileName(v.FullPath);
        string parentRel = Path.GetRelativePath(v.Root!, Path.GetDirectoryName(v.FullPath)!);
        int rootFd = LinuxFs.OpenRootDirectory(v.Root!, out int e0);
        if (rootFd < 0) throw Fail(v, "open_failed", $"cannot open root (errno {e0})");
        try
        {
            int fd = LinuxFs.OpenBeneath(rootFd, parentRel == "." ? "" : parentRel, LinuxFs.O_RDONLY | LinuxFs.O_DIRECTORY, 0, out int err);
            if (fd < 0) throw Fail(v, Errno(err), $"the kernel refused the parent folder beneath the root (errno {err})");
            return fd;
        }
        finally { LinuxFs.Close(rootFd); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void MoveLinux(PathVerdict s, PathVerdict d)
    {
        int sp = OpenParentLinux(s, out string sName);
        try
        {
            int dp = OpenParentLinux(d, out string dName);
            try
            {
                if (LinuxFs.RenameIn(sp, sName, dp, dName, LinuxFs.RENAME_NOREPLACE, out int err) < 0)
                {
                    if (err == LinuxErrno.EEXIST) throw Fail(d, "exists", "destination appeared before the move; nothing was overwritten");
                    if (err == LinuxErrno.EXDEV) throw Fail(d, "crosses_mount", "source and destination are on different mounts; a move would be copy+delete");
                    if (err == LinuxErrno.ENOENT) throw Fail(s, "not_found", "source disappeared before the move");
                    if (err == LinuxErrno.ENOSPC) throw new IOException("No space left on device", unchecked((int)0x80070070));
                    if (err == LinuxFs.EINVAL)
                        throw new IOException("this file system does not support atomic no-replace renames (RENAME_NOREPLACE); refusing to move without that guarantee");
                    throw new IOException($"rename failed (errno {err})", err);
                }
                // Durability: the rename lives in the directory entries; flush both folders.
                LinuxFs.SyncFd(dp, out _);
                if (sp != dp) LinuxFs.SyncFd(sp, out _);
            }
            finally { LinuxFs.Close(dp); }
        }
        finally { LinuxFs.Close(sp); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void MakeDirectoryLinux(PathVerdict v, string parent, string name)
    {
        string rel = Path.GetRelativePath(v.Root!, parent);
        int rootFd = LinuxFs.OpenRootDirectory(v.Root!, out int e0);
        if (rootFd < 0) throw Fail(v, "open_failed", $"cannot open root (errno {e0})");
        try
        {
            int dirFd = LinuxFs.OpenBeneath(rootFd, rel == "." ? "" : rel, LinuxFs.O_PATH | LinuxFs.O_DIRECTORY, 0, out int e1);
            if (dirFd < 0) throw Fail(v, Errno(e1), $"cannot open parent folder beneath the root (errno {e1})");
            try
            {
                if (LinuxFs.MakeDirectoryIn(dirFd, name, out int e2) < 0 && e2 != LinuxFs.EEXIST)
                    throw new IOException($"mkdir '{name}' failed (errno {e2})");
            }
            finally { LinuxFs.Close(dirFd); }
        }
        finally { LinuxFs.Close(rootFd); }
    }

    private SafeFileHandle OpenVerified(PathVerdict v, bool create)
    {
        if (UseKernelResolve)
        {
            int rootFd = LinuxFs.OpenRootDirectory(v.Root!, out int e0);
            if (rootFd < 0) throw Fail(v, "open_failed", $"cannot open root (errno {e0})");
            try
            {
                string rel = Path.GetRelativePath(v.Root!, v.FullPath);
                int flags = create ? LinuxFs.O_WRONLY | LinuxFs.O_CREAT | LinuxFs.O_EXCL : LinuxFs.O_RDONLY;
                // openat2 rejects a non-zero mode without O_CREAT (EINVAL).
                int fd = LinuxFs.OpenBeneath(rootFd, rel, flags, create ? 0x1A4 /* 0644 */ : 0, out int err);
                if (fd < 0)
                {
                    if (err == LinuxFs.EEXIST) throw Fail(v, "exists", "a file appeared here before it could be created; not overwritten");
                    if (err == LinuxFs.ENOSPC) throw new IOException("No space left on device", unchecked((int)0x80070070));
                    throw Fail(v, Errno(err), $"the kernel refused the open beneath the root (errno {err})");
                }
                var handle = LinuxFs.Wrap(fd);
                if (!create && Directory.Exists(LinuxFs.FinalPath(fd) ?? ""))
                {
                    handle.Dispose();
                    throw Fail(v, "not_a_file", "path is a folder");
                }
                return handle;
            }
            finally { LinuxFs.Close(rootFd); }
        }

        SafeFileHandle h;
        if (OperatingSystem.IsWindows())
        {
            uint access = create ? Win32.GENERIC_WRITE | Win32.DELETE : Win32.GENERIC_READ;
            h = Win32.CreateFile(@"\\?\" + v.FullPath, access, Win32.FILE_SHARE_READ, IntPtr.Zero,
                create ? Win32.CREATE_NEW : Win32.OPEN_EXISTING, create ? 0 : Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                if (err == 80 /* ERROR_FILE_EXISTS */) throw Fail(v, "exists", "a file appeared here before it could be created; not overwritten");
                throw new IOException($"CreateFile failed for '{v.FullPath}' (Win32 error {err})", err);
            }
            string? final = Win32.FinalPathOf(h);
            if (final is null || !_syntax.IsSameOrInside(final, v.Root!))
            {
                if (create) Win32.DeleteByHandle(h);
                h.Dispose();
                throw Fail(v, "race_detected", $"the opened handle resolves to '{final ?? "?"}', outside the root; a link was swapped in after the check");
            }
            return h;
        }

        // Fallback (Linux without openat2, other Unix): O_NOFOLLOW on the last component, then /proc check.
        h = File.OpenHandle(v.FullPath, create ? FileMode.CreateNew : FileMode.Open, create ? FileAccess.Write : FileAccess.Read, FileShare.Read);
        string? fp = OperatingSystem.IsLinux() ? LinuxFs.FinalPath((int)h.DangerousGetHandle()) : v.FullPath;
        if (fp is null || !_syntax.IsSameOrInside(fp, v.Root!))
        {
            h.Dispose();
            if (create && fp is not null) { try { File.Delete(fp); } catch (IOException) { } }
            throw Fail(v, "race_detected", $"the opened handle resolves to '{fp ?? "?"}', outside the root");
        }
        return h;
    }

    private SafeFileHandle OpenDirectoryVerified(PathVerdict v)
    {
        if (UseKernelResolve)
        {
            int rootFd = LinuxFs.OpenRootDirectory(v.Root!, out int e0);
            if (rootFd < 0) throw Fail(v, "open_failed", $"cannot open root (errno {e0})");
            try
            {
                string rel = Path.GetRelativePath(v.Root!, v.FullPath);
                int fd = LinuxFs.OpenBeneath(rootFd, rel == "." ? "" : rel, LinuxFs.O_PATH | LinuxFs.O_DIRECTORY, 0, out int err);
                if (fd < 0) throw Fail(v, Errno(err), $"the kernel refused the folder beneath the root (errno {err})");
                return LinuxFs.Wrap(fd);
            }
            finally { LinuxFs.Close(rootFd); }
        }
        if (OperatingSystem.IsWindows())
        {
            var h = Win32.CreateFile(@"\\?\" + v.FullPath, Win32.FILE_READ_ATTRIBUTES, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
                IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new IOException($"Cannot open folder '{v.FullPath}' (Win32 error {err})", err);
            }
            string? final = Win32.FinalPathOf(h);
            if (final is null || !_syntax.IsSameOrInside(final, v.Root!))
            {
                h.Dispose();
                throw Fail(v, "race_detected", $"the folder handle resolves to '{final ?? "?"}', outside the root");
            }
            return h;
        }
        // No portable directory handle in .NET elsewhere; the component walk already ran.
        return new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
    }

    private static string Errno(int err) => err switch
    {
        LinuxErrno.ELOOP => "reparse_point",
        LinuxErrno.EXDEV => "crosses_mount",
        LinuxErrno.ENOENT => "not_found",
        LinuxErrno.ENOTDIR => "not_a_folder",
        LinuxErrno.EAGAIN => "race_detected",
        LinuxErrno.EACCES or LinuxErrno.EPERM => "permission_denied",
        _ => "open_failed",
    };

    private PathDeniedException Fail(PathVerdict v, string code, string reason)
    {
        var d = v with { Allowed = false, Code = code, Reason = reason };
        Denied?.Invoke(d);
        return new PathDeniedException(d);
    }

    // ── Root resolution ──────────────────────────────────────────────────────────────────────────────

    internal static string? ResolveRootFinalPath(string lexical)
    {
        if (OperatingSystem.IsLinux())
        {
            int fd = LinuxFs.OpenRootDirectory(lexical, out int err);
            if (fd < 0)
            {
                // No openat2: fall back to resolving links in .NET.
                return err == LinuxFs.ENOSYS ? ResolveManually(lexical) : null;
            }
            try { return LinuxFs.FinalPath(fd); }
            finally { LinuxFs.Close(fd); }
        }
        if (OperatingSystem.IsWindows())
        {
            using var h = Win32.CreateFile(@"\\?\" + lexical, Win32.FILE_READ_ATTRIBUTES, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
                IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            return h.IsInvalid ? null : Win32.FinalPathOf(h);
        }
        return ResolveManually(lexical);
    }

    private static string ResolveManually(string path)
    {
        var info = new DirectoryInfo(path);
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
    }
}
