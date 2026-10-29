using System.Text.RegularExpressions;

namespace Mediator.Core.Security;

/// <summary>
/// Lexical rules applied to a path string <em>before</em> any normalization. These rules have to run on the raw
/// input: on Windows, <c>Path.GetFullPath</c> silently strips trailing dots and spaces ("a.txt." → "a.txt"), so
/// two different strings would refer to one file and a later comparison could be fooled.
/// The Windows rule set can be selected on any OS so it is unit-tested on Linux CI too.
/// </summary>
public sealed partial class PathSyntax
{
    public static readonly PathSyntax Windows = new(isWindows: true);
    public static readonly PathSyntax Posix = new(isWindows: false);
    public static PathSyntax Current => OperatingSystem.IsWindows() ? Windows : Posix;

    public bool IsWindows { get; }
    public StringComparison Comparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public StringComparer Comparer => IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private PathSyntax(bool isWindows) => IsWindows = isWindows;

    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    // An 8.3 short name ("PROGRA~1", "REPORT~2.PDF"). Short names alias long names, so a check on one spelling can
    // be bypassed with the other. Mediator refuses the short shape rather than resolving it.
    [GeneratedRegex(@"^[^.~]{1,6}~[0-9][^.]{0,1}(\.[^.]{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNameShape();

    /// <summary>Returns why the raw path string is unacceptable, or null. Does not touch the file system.</summary>
    public string? Check(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "empty path";
        if (raw.Length > 4096) return "path longer than 4096 characters";
        foreach (char c in raw)
        {
            if (c == '\0') return "path contains a NUL character";
            if (c < 0x20) return "path contains a control character";
        }

        if (IsWindows)
        {
            string p = raw.Replace('/', '\\');
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
                return "device or extended-length path (\\\\?\\, \\\\.\\, \\??\\) is not accepted";
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return "network (UNC) path is not accepted";
            if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
                return "path must be absolute with a drive letter (C:\\...)";
            if (p.IndexOf(':', 2) >= 0) return "path contains ':' after the drive (alternate data stream or device syntax)";
            foreach (char c in p) if (c is '<' or '>' or '"' or '|' or '?' or '*') return $"path contains the reserved character '{c}'";
            foreach (var seg in p[3..].Split('\\'))
            {
                if (seg.Length == 0) continue; // doubled separators are harmless and normalized
                if (seg is "." or "..") return "relative segments ('.' or '..') are not accepted";
                if (seg.EndsWith('.') || seg.EndsWith(' ')) return $"segment '{seg}' ends with a dot or space (Windows strips these, so it would alias another name)";
                if (seg.StartsWith(' ')) return $"segment '{seg}' starts with a space";
                string stem = seg.Split('.')[0].TrimEnd(' ');
                if (ReservedDosNames.Contains(stem)) return $"segment '{seg}' is a reserved device name";
                if (ShortNameShape().IsMatch(seg)) return $"segment '{seg}' looks like an 8.3 short name; use the long name";
            }
        }
        else
        {
            if (raw[0] != '/') return "path must be absolute";
            foreach (var seg in raw.Split('/'))
                if (seg is "." or "..") return "relative segments ('.' or '..') are not accepted";
        }
        return null;
    }

    /// <summary>Splits a normalized absolute path into components ("C:" or "" for the root, then names).</summary>
    public string[] Components(string fullPath)
    {
        char sep = IsWindows ? '\\' : '/';
        string p = IsWindows ? fullPath.Replace('/', '\\') : fullPath;
        var parts = p.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        return IsWindows ? parts : ["", .. parts];
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="root"/> or lies beneath it, compared by component.</summary>
    public bool IsSameOrInside(string path, string root) => RelativeDepth(path, root) >= 0;

    /// <summary>Number of components below the root (0 = the root itself), or -1 if outside.</summary>
    public int RelativeDepth(string path, string root)
    {
        var a = Components(path);
        var r = Components(root);
        if (a.Length < r.Length) return -1;
        for (int i = 0; i < r.Length; i++)
            if (!string.Equals(a[i], r[i], Comparison)) return -1;
        return a.Length - r.Length;
    }
}
