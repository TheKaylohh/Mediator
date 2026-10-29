using System.Text.RegularExpressions;
using Mediator.Core.Files;

namespace Mediator.Core.Jobs;

/// <summary>
/// V10: the folder a prompt names ("output the file to C:\Users\me\Documents\Out"). Paths in prompts often run into
/// the next words, so each candidate is shortened word by word from the end. In order of preference:
/// an existing folder; the folder of a file path (existing or with an extension, e.g. "...\F40.SLDPRT");
/// a new folder (up to three new levels, no spaces in them) below an existing one. Protected places (Windows, Program Files,
/// AppData, inside project folders) are never returned.
/// </summary>
public static partial class PromptPaths
{
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\|/)[^""<>|?*\r\n]+")]
    private static partial Regex PathInText();

    public static string? TargetFolder(string prompt, ProtectedPathPolicy? policy = null)
    {
        policy ??= ProtectedPathPolicy.CreateDefault();
        var candidates = new List<string>();
        foreach (Match m in PathInText().Matches(prompt ?? ""))
        {
            string c = m.Value.TrimEnd('.', ',', ';', ' ', '"', '\'');
            for (string? s = c; !string.IsNullOrEmpty(s); s = s.Contains(' ', StringComparison.Ordinal) ? s[..s.LastIndexOf(' ')].TrimEnd('.', ',', ';', ' ') : null)
                candidates.Add(s);
        }
        string? Ok(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                return policy.CheckJobRoot(full) is null ? Path.TrimEndingDirectorySeparator(full) : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        foreach (var c in candidates)
            if (Directory.Exists(c) && Ok(c) is { } dir) return dir;
        foreach (var c in candidates)
            if ((File.Exists(c) || Path.HasExtension(c)) && Path.GetDirectoryName(c) is { } parent && Directory.Exists(parent) && Ok(parent) is { } dir) return dir;
        foreach (var c in candidates)
            if (IsNewFolder(c) && Ok(Path.TrimEndingDirectorySeparator(c)) is { } dir) return dir;
        return null;
    }

    /// <summary>
    /// Existing files and folders the prompt names ("use C:\Parts\wheel.SLDPRT and D:\Logos\logo.dxf"), as the folders the
    /// job may READ: a file's folder, or the folder itself. Protected places are left out. Used as read-only roots, so the job
    /// can open what the user pointed at without being allowed to change it.
    /// </summary>
    public static IReadOnlyList<string> ExistingPaths(string prompt, ProtectedPathPolicy? policy = null)
    {
        policy ??= ProtectedPathPolicy.CreateDefault();
        var found = new List<string>();
        foreach (Match m in PathInText().Matches(prompt ?? ""))
        {
            string c = m.Value.TrimEnd('.', ',', ';', ' ', '"', '\'');
            for (string? s = c; !string.IsNullOrEmpty(s); s = s.Contains(' ', StringComparison.Ordinal) ? s[..s.LastIndexOf(' ')].TrimEnd('.', ',', ';', ' ') : null)
            {
                string? folder;
                try
                {
                    string full = Path.GetFullPath(s);
                    folder = File.Exists(full) ? Path.GetDirectoryName(full) : Directory.Exists(full) ? Path.TrimEndingDirectorySeparator(full) : null;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { folder = null; }
                if (folder is null) continue;
                if (policy.CheckJobRoot(folder) is null && !found.Contains(folder, StringComparer.OrdinalIgnoreCase)) found.Add(folder);
                break; // the longest existing reading wins
            }
        }
        return found;
    }

    /// <summary>New folders a prompt may ask for below an existing one ("Documents\CAD\F40" when only Documents exists).</summary>
    public const int MaxNewLevels = 3;

    /// <summary>
    /// True when <paramref name="path"/> does not exist but an ancestor at most <see cref="MaxNewLevels"/> levels up does, and
    /// every missing level looks like a folder name (no spaces, which would mean the path ran into the sentence; no extension).
    /// </summary>
    private static bool IsNewFolder(string path)
    {
        string? p = Path.TrimEndingDirectorySeparator(path);
        for (int level = 0; level < MaxNewLevels && !string.IsNullOrEmpty(p); level++)
        {
            string leaf = Path.GetFileName(p);
            if (leaf.Length == 0 || leaf.Contains(' ', StringComparison.Ordinal) || Path.HasExtension(leaf)) return false;
            p = Path.GetDirectoryName(p);
            if (p is not null && Directory.Exists(p)) return true;
        }
        return false;
    }
}
