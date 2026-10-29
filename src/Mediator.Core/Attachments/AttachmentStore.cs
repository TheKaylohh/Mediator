using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mediator.Core.Platform;
using Mediator.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace Mediator.Core.Attachments;

public enum AttachmentKind { File, Folder, Image }

/// <param name="Id">Short id the model uses with mediator_read_attachment.</param>
/// <param name="Source">Where it came from (the dropped path, or "clipboard").</param>
/// <param name="GrantedRoot">The folder the job is given read-only: the snapshot's own folder, or the dropped folder itself.</param>
/// <param name="Path">The file to read (snapshot or pasted image), or the folder for folder attachments.</param>
public sealed record Attachment(string Id, AttachmentKind Kind, string Name, string Source, string GrantedRoot, string Path, long Bytes, string? MediaType);

public sealed class AttachmentException(string message) : Exception(message);

/// <summary>
/// Attachments the user dropped, pasted or picked (V7). Rules, so attaching never widens access beyond what the
/// user pointed at:
/// <list type="bullet">
/// <item>A file is copied (a snapshot) into its own folder under <c>&lt;data&gt;/attachments/&lt;id&gt;/</c>, and the
/// job gets only that folder, read-only. The original file and its folder are never granted. The copy reads through
/// a handle opened without following a final link, and the handle's real path must be the path the user gave.</item>
/// <item>A folder is granted in place, read-only (Modify and Create are denied with <c>read_only</c>).</item>
/// <item>A pasted image must be a PNG with a valid header. It is written into its own folder like a snapshot.</item>
/// <item>Links, devices, pipes, sockets and system folders are refused. Size caps apply. Folders older than
/// <see cref="Retention"/> are removed at start-up.</item>
/// </list>
/// </summary>
public sealed class AttachmentStore
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const long MaxImageBytes = 20L * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly string[] SystemPrefixes = ["/proc", "/sys", "/dev", "/run"];

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _sensitive;
    private readonly string _home;
    private readonly long _maxFileBytes;

    /// <param name="sensitive">
    /// Folders never attached, nor any folder that contains one (the Mediator's own data folder and the usual credential
    /// folders by default): a model reading attachments must not be able to read sign-in tokens or keys by accident.
    /// </param>
    public AttachmentStore(string root, TimeProvider? time = null, long maxFileBytes = MaxFileBytes, IEnumerable<string>? sensitive = null, string? home = null)
    {
        _root = System.IO.Path.GetFullPath(root);
        _time = time ?? TimeProvider.System;
        _maxFileBytes = maxFileBytes;
        _home = System.IO.Path.TrimEndingDirectorySeparator(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _sensitive = (sensitive ?? DefaultSensitive(_home).Append(System.IO.Path.GetDirectoryName(_root)!))
            .Select(p => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p))).ToArray();
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Credential folders of the CLIs and common tools under the user's home.</summary>
    public static IEnumerable<string> DefaultSensitive(string home) =>
        new[] { ".claude", ".gemini", ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker", System.IO.Path.Combine(".config", "gcloud"), ".claude.json" }
            .Select(n => System.IO.Path.Combine(home, n));

    /// <summary>Why a path may not be attached (secrets, the whole home folder, a drive root), or null.</summary>
    private string? SensitiveReason(string full, bool isFolder)
    {
        var syntax = PathSyntax.Current;
        if (System.IO.Path.GetPathRoot(full) is { } r && syntax.Comparer.Equals(System.IO.Path.TrimEndingDirectorySeparator(r), full) || full == "/")
            return "a whole drive cannot be attached; attach the folder you mean";
        if (isFolder && _home.Length > 0 && syntax.Comparer.Equals(full, _home))
            return "your whole user folder cannot be attached (it holds sign-in tokens and keys); attach the folder you mean";
        foreach (var s in _sensitive)
        {
            if (syntax.IsSameOrInside(full, s)) return $"it is inside {s}, which holds credentials or the Mediator's own data";
            if (isFolder && syntax.IsSameOrInside(s, full)) return $"it contains {s}, which holds credentials or the Mediator's own data; attach a narrower folder";
        }
        return null;
    }

    /// <summary>Removes attachment folders older than <see cref="Retention"/>. Returns how many were removed.</summary>
    public int Prune()
    {
        int n = 0;
        foreach (var d in new DirectoryInfo(_root).EnumerateDirectories())
        {
            if (_time.GetUtcNow().UtcDateTime - d.CreationTimeUtc < Retention) continue;
            // Snapshots are read-only, which Windows refuses to delete: clear the attribute first, as Discard does.
            if (TryDelete(d.FullName)) n++;
        }
        return n;
    }

    /// <summary>Attaches a path the user dropped or picked: a file is snapshotted, a folder is granted read-only in place.</summary>
    public Attachment AddPath(string raw)
    {
        var syntax = PathSyntax.Current;
        if (syntax.Check(raw) is string bad) throw new AttachmentException($"'{raw}' cannot be attached: {bad}");
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(raw));
        if (OperatingSystem.IsLinux() && SystemPrefixes.Any(p => syntax.IsSameOrInside(full, p)))
            throw new AttachmentException($"'{full}' is a system folder; attach your own files instead.");
        if (syntax.IsSameOrInside(full, _root))
            throw new AttachmentException("That is already an attachment.");
        if (SensitiveReason(full, Directory.Exists(full)) is { } sensitive)
            throw new AttachmentException($"'{full}' cannot be attached: {sensitive}.");
        var info = new FileInfo(full);
        if (info.LinkTarget is not null || new DirectoryInfo(full).LinkTarget is not null)
            throw new AttachmentException($"'{full}' is a link; attach what it points to instead, so you can see what is shared.");
        if (Directory.Exists(full))
        {
            var verdictRoot = new PathAuthorizer([], [full]); // resolves the real path and refuses what a root may not be
            string real = verdictRoot.ReadOnlyRoots.Single();
            if (SensitiveReason(real, isFolder: true) is { } realSensitive) // a link higher up may hide where it really is
                throw new AttachmentException($"'{full}' cannot be attached: {realSensitive}.");
            return new Attachment(NewId(), AttachmentKind.Folder, Name(full), full, real, real, 0, null);
        }
        if (!info.Exists) throw new AttachmentException($"'{full}' does not exist.");
        // The same secret check on the real location: a linked folder higher up must not lead into a credential folder.
        if (System.IO.Path.GetDirectoryName(full) is { } parent && PathAuthorizer.ResolveRootFinalPath(parent) is { } realParent
            && SensitiveReason(System.IO.Path.Combine(realParent, info.Name), isFolder: false) is { } fileSensitive)
            throw new AttachmentException($"'{full}' cannot be attached: {fileSensitive}.");

        string id = NewId();
        string dir = System.IO.Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        string target = System.IO.Path.Combine(dir, SafeName(info.Name));
        try
        {
            long bytes;
            using (var source = OpenSnapshotSource(full))
            using (var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                bytes = CopyBounded(source, dest, _maxFileBytes, full);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            bool image = IsImageName(target);
            return new Attachment(id, image ? AttachmentKind.Image : AttachmentKind.File, info.Name, full, dir, target, bytes, image ? MediaTypeOf(target) : null);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    /// <summary>Stages a pasted screenshot. Only a well-formed PNG under the size cap is accepted.</summary>
    public Attachment AddPng(ReadOnlySpan<byte> png)
    {
        if (png.Length > MaxImageBytes) throw new AttachmentException($"The image is larger than {MaxImageBytes / (1024 * 1024)} MiB.");
        if (png.Length < 33 || !png[..8].SequenceEqual(PngMagic) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new AttachmentException("The clipboard image is not a valid PNG.");
        string id = NewId();
        string dir = System.IO.Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        string name = "pasted-" + _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png";
        string target = System.IO.Path.Combine(dir, name);
        using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write)) fs.Write(png);
        File.SetAttributes(target, FileAttributes.ReadOnly);
        return new Attachment(id, AttachmentKind.Image, name, "clipboard", dir, target, png.Length, "image/png");
    }

    /// <summary>Deletes a snapshot or pasted image (folders attached in place are only forgotten, never touched).</summary>
    public void Discard(Attachment a)
    {
        if (a.Kind == AttachmentKind.Folder) return;
        if (!PathSyntax.Current.IsSameOrInside(a.GrantedRoot, _root) || PathSyntax.Current.Comparer.Equals(a.GrantedRoot, _root)) return;
        TryDelete(a.GrantedRoot);
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Opens the user's file for the snapshot without following a final link, then checks that the handle is a
    /// seekable regular file whose real path is the one the user gave (a link swapped in between is refused).
    /// </summary>
    private static FileStream OpenSnapshotSource(string full)
    {
        SafeFileHandle handle;
        string? real;
        if (OperatingSystem.IsLinux())
        {
            int fd = LinuxFs.OpenNoFollow(full, LinuxFs.O_RDONLY | LinuxFs.O_NONBLOCK, 0, out int err);
            if (fd < 0) throw new AttachmentException(err == LinuxErrno.ELOOP ? $"'{full}' is a link." : $"'{full}' cannot be opened (errno {err}).");
            handle = LinuxFs.Wrap(fd);
            real = LinuxFs.FinalPath(fd);
        }
        else if (OperatingSystem.IsWindows())
        {
            handle = Win32.CreateFile(@"\\?\" + full, Win32.GENERIC_READ, Win32.FILE_SHARE_READ, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new AttachmentException($"'{full}' cannot be opened (Win32 error {err}).");
            }
            real = Win32.FinalPathOf(handle);
        }
        else
        {
            handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            real = full;
        }
        // The expected real path: the parent folder resolved (links above the file are fine, the user can see them),
        // plus the file's own name (a link at the file itself was refused by the open).
        string? parent = System.IO.Path.GetDirectoryName(full);
        string? realParent = parent is null ? null : PathAuthorizer.ResolveRootFinalPath(parent);
        string? expected = realParent is null ? null : System.IO.Path.Combine(realParent, System.IO.Path.GetFileName(full));
        if (real is null || expected is null || !PathSyntax.Current.Comparer.Equals(System.IO.Path.TrimEndingDirectorySeparator(real), expected))
        {
            handle.Dispose();
            throw new AttachmentException($"'{full}' changed while it was being attached (it now resolves to '{real ?? "?"}'); try again.");
        }
        FileStream fs;
        try { fs = new FileStream(handle, FileAccess.Read, bufferSize: 81920); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            handle.Dispose();
            throw new AttachmentException($"'{full}' is not a regular file.");
        }
        if (!fs.CanSeek)
        {
            fs.Dispose();
            throw new AttachmentException($"'{full}' is not a regular file (a pipe, socket or device).");
        }
        return fs;
    }

    private static long CopyBounded(Stream source, Stream dest, long max, string what)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > max) throw new AttachmentException($"'{what}' is larger than the {FormatBytes(max)} limit for a copied attachment; attach its folder instead.");
            dest.Write(buffer, 0, n);
        }
        return total;
    }

    private static string FormatBytes(long b) => b >= 1024 * 1024 ? $"{b / (1024 * 1024)} MiB" : $"{b} bytes";

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    private static string Name(string full) => System.IO.Path.GetFileName(full) is { Length: > 0 } n ? n : full;

    /// <summary>The snapshot keeps the file's name when it is a plain name, else a safe substitute with the same extension.</summary>
    private static string SafeName(string name)
    {
        bool plain = name.Length is > 0 and <= 120 && PathSyntax.Current.Check(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name)) is null
            && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
        return plain ? name : "attachment" + System.IO.Path.GetExtension(name)[..Math.Min(10, System.IO.Path.GetExtension(name).Length)];
    }

    public static bool IsImageName(string path) => MediaTypeOf(path) is not null;

    public static string? MediaTypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };
}
