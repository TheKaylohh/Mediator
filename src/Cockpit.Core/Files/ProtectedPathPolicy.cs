using System.Collections.Concurrent;

namespace Cockpit.Core.Files;

/// <summary>
/// Decides which paths an automated organizer must never touch. A path is protected if:
///  - any segment is a system or VCS folder ($Recycle.Bin, System Volume Information, .git, ...),
///  - it lies under a protected root (Windows, Program Files, ProgramData, AppData, cloud-sync roots),
///  - it is a well-known system file (pagefile.sys, desktop.ini, ...),
///  - an ancestor folder is a git repository (contains .git as a folder or file),
///  - an ancestor folder is a project folder: it contains a file whose extension is a project marker
///    (by default .sldasm and .slddrw, whose internal references break if parts move),
///  - an ancestor folder is a junction or symbolic link (could escape the job's roots).
/// Folder checks are cached per directory, so planning across a whole drive stays fast.
/// </summary>
public sealed class ProtectedPathPolicy
{
    private static readonly StringComparison PathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private readonly string[] _protectedRoots;
    private readonly HashSet<string> _protectedSegments;
    private readonly HashSet<string> _protectedFileNames;
    private readonly HashSet<string> _projectMarkers;
    private readonly ConcurrentDictionary<string, string?> _folderVerdicts = new(PathComparer);

    public ProtectedPathPolicy(IEnumerable<string> protectedRoots, IEnumerable<string> projectMarkerExtensions,
        IEnumerable<string>? protectedSegments = null, IEnumerable<string>? protectedFileNames = null)
    {
        _protectedRoots = protectedRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(NormalizeDir).Distinct(PathComparer).ToArray();
        _projectMarkers = new HashSet<string>(projectMarkerExtensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        _protectedSegments = new HashSet<string>(protectedSegments ??
            ["$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "Config.Msi", "$SysReset", ".git", ".svn", ".hg"],
            StringComparer.OrdinalIgnoreCase);
        _protectedFileNames = new HashSet<string>(protectedFileNames ??
            ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "desktop.ini", "bootmgr", "BOOTNXT"],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Default policy for the current Windows user, plus any extra roots and project markers.</summary>
    public static ProtectedPathPolicy CreateDefault(IEnumerable<string>? extraProtectedRoots = null, IEnumerable<string>? extraProjectMarkers = null)
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        // Cloud-sync roots: moving files there triggers uploads; moving placeholders out triggers downloads.
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od) roots.Add(od);
        if (extraProtectedRoots is not null) roots.AddRange(extraProtectedRoots);

        var markers = new List<string> { ".sldasm", ".slddrw" };
        if (extraProjectMarkers is not null) markers.AddRange(extraProjectMarkers);
        return new ProtectedPathPolicy(roots, markers);
    }

    /// <summary>Returns why the file may not be moved, or null if it may.</summary>
    public string? CheckSource(string fullPath)
    {
        if (CheckPathShape(fullPath) is string shape) return shape;
        FileInfo info;
        try { info = new FileInfo(fullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return $"cannot inspect file: {ex.Message}"; }
        if (!info.Exists) return "file does not exist";
        var attrs = info.Attributes;
        if ((attrs & FileAttributes.ReparsePoint) != 0) return "symbolic link or reparse point";
        if ((attrs & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            return "cloud placeholder (Files On-Demand); moving it would download it";
        if ((attrs & FileAttributes.System) != 0) return "system file";
        return CheckAncestors(Path.GetDirectoryName(fullPath));
    }

    /// <summary>Returns why a destination path is unacceptable, or null. The destination need not exist yet.</summary>
    public string? CheckDestination(string fullPath)
    {
        if (CheckPathShape(fullPath) is string shape) return shape;
        // Walk up to the nearest existing folder and judge from there.
        string? dir = Path.GetDirectoryName(fullPath);
        while (dir is not null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        return CheckAncestors(dir);
    }

    /// <summary>V10: why a folder may not be a job's root (Windows, Program Files, AppData, protected names), or null.
    /// Project markers (.sldasm) do not count: a CAD job is meant to work in its project folder.</summary>
    public string? CheckJobRoot(string fullPath)
    {
        string full = Path.TrimEndingDirectorySeparator(fullPath);
        // The temp folder lives under AppData on Windows; job scratch folders there are fine.
        if ((full + Path.DirectorySeparatorChar).StartsWith(Path.GetTempPath(), PathCmp)) return null;
        return CheckPathShape(full);
    }

    private string? CheckPathShape(string fullPath)
    {
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            if (_protectedSegments.Contains(segment)) return $"inside a protected folder ({segment})";
        if (_protectedFileNames.Contains(Path.GetFileName(fullPath))) return "protected system file name";
        string withSep = fullPath + Path.DirectorySeparatorChar;
        foreach (var root in _protectedRoots)
            if (withSep.StartsWith(root, PathCmp)) return $"under protected location {root}";
        return null;
    }

    private string? CheckAncestors(string? dir)
    {
        while (dir is not null)
        {
            if (FolderVerdict(dir) is string reason) return reason;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private string? FolderVerdict(string dir) => _folderVerdicts.GetOrAdd(dir, d =>
    {
        try
        {
            var info = new DirectoryInfo(d);
            if (!info.Exists) return null;
            bool isDriveRoot = info.Parent is null;
            if (!isDriveRoot && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return $"path passes through a junction or link ({d})";
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git")))
                return $"inside git repository {d}";
            if (_projectMarkers.Count > 0)
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    if (_projectMarkers.Contains(Path.GetExtension(f)))
                        return $"inside project folder {d} (contains {Path.GetFileName(f)}; moving files would break its references)";
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"cannot inspect folder {d}: {ex.Message}";
        }
    });

    /// <summary>Forget cached folder verdicts (call after the file system changed, e.g. between plan and execute).</summary>
    public void ClearCache() => _folderVerdicts.Clear();

    private static string NormalizeDir(string path)
    {
        string full = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
