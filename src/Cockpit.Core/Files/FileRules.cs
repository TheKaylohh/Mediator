using System.Globalization;
using System.IO.Enumeration;
using System.Text.Json;

namespace Cockpit.Core.Files;

/// <summary>
/// One organizing rule. The model turns the user's criteria into rules; the Core expands them over the file
/// system deterministically, so a 100,000-file drive never passes through the model.
/// First matching rule wins; files no rule matches are left where they are.
/// </summary>
public sealed record FileRule(
    IReadOnlyList<string>? Extensions,  // ".pdf", "jpg" (case-insensitive)
    string? NameGlob,                   // "*invoice*" (Windows-style * and ?)
    int? OlderThanDays,
    int? NewerThanDays,
    long? MinBytes,
    long? MaxBytes,
    string Destination);                // template: {ext} {yyyy} {mm} {dd} {parent}

public sealed record RuleExpansion(IReadOnlyList<MoveRequest> Moves, int Scanned, int Unmatched, IReadOnlyDictionary<int, int> MatchesPerRule, bool Truncated);

public static class FileRules
{
    public static IReadOnlyList<FileRule> Parse(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Array) throw new JournalException("rules must be an array.");
        var list = new List<FileRule>();
        foreach (var r in rules.EnumerateArray())
        {
            string dest = r.TryGetProperty("destination", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()! : throw new JournalException("Every rule needs a destination.");
            list.Add(new FileRule(
                r.TryGetProperty("extensions", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Select(x => x.StartsWith('.') ? x : "." + x).ToList()
                    : null,
                r.TryGetProperty("nameGlob", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null,
                Int(r, "olderThanDays"), Int(r, "newerThanDays"), Long(r, "minBytes"), Long(r, "maxBytes"), dest));
        }
        return list;
    }

    /// <summary>Applies rules to files under <paramref name="sourceFolder"/>. Skips files already at their destination.</summary>
    public static RuleExpansion Expand(string sourceFolder, bool recursive, IReadOnlyList<FileRule> rules, int maxMoves, DateTime nowUtc, CancellationToken ct)
    {
        var moves = new List<MoveRequest>();
        var perRule = new Dictionary<int, int>();
        int scanned = 0, unmatched = 0;
        bool truncated = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            // Never descend into links or system/hidden trees; the policy re-checks every file anyway.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
        };

        string baseFolder = Path.GetFullPath(sourceFolder);
        foreach (var path in Directory.EnumerateFiles(baseFolder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            var info = new FileInfo(path);
            int ruleIndex = -1;
            for (int i = 0; i < rules.Count; i++)
            {
                if (Matches(rules[i], info, nowUtc)) { ruleIndex = i; break; }
            }
            if (ruleIndex < 0) { unmatched++; continue; }

            string destDir = Render(rules[ruleIndex].Destination, info, baseFolder);
            string dest = Path.Combine(destDir, info.Name);
            if (string.Equals(Path.GetFullPath(dest), info.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue; // already organized

            perRule[ruleIndex] = perRule.GetValueOrDefault(ruleIndex) + 1;
            if (moves.Count >= maxMoves) { truncated = true; break; }
            moves.Add(new MoveRequest(info.FullName, dest));
        }
        return new RuleExpansion(moves, scanned, unmatched, perRule, truncated);
    }

    private static bool Matches(FileRule r, FileInfo f, DateTime nowUtc)
    {
        if (r.Extensions is { Count: > 0 } exts && !exts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return false;
        if (r.NameGlob is { Length: > 0 } glob && !FileSystemName.MatchesSimpleExpression(glob, f.Name, ignoreCase: true)) return false;
        double ageDays = (nowUtc - f.LastWriteTimeUtc).TotalDays;
        if (r.OlderThanDays is int older && ageDays < older) return false;
        if (r.NewerThanDays is int newer && ageDays > newer) return false;
        if (r.MinBytes is long min && f.Length < min) return false;
        if (r.MaxBytes is long max && f.Length > max) return false;
        return true;
    }

    /// <summary>Relative destinations are relative to the source folder (never to the Cockpit's working directory).</summary>
    private static string Render(string template, FileInfo f, string baseFolder)
    {
        var t = f.LastWriteTime; // local time: folder names should match what the user sees in Explorer
        string ext = f.Extension.TrimStart('.').ToLowerInvariant();
        string rendered = template
            .Replace("{ext}", ext.Length == 0 ? "no-extension" : ext, StringComparison.OrdinalIgnoreCase)
            .Replace("{yyyy}", t.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{mm}", t.Month.ToString("D2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{dd}", t.Day.ToString("D2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{parent}", f.Directory?.Name ?? "", StringComparison.OrdinalIgnoreCase);
        if (rendered.Contains('{') || rendered.Contains('}'))
            throw new JournalException($"Unknown placeholder in destination '{template}'. Use {{ext}} {{yyyy}} {{mm}} {{dd}} {{parent}}.");
        return Path.GetFullPath(rendered, baseFolder);
    }

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
