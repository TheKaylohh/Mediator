using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mediator.Connectors.SolidWorks;

/// <summary>
/// Design iterations for <see cref="SwxParametric"/>: rendered views the model checks its work against, proportions against
/// the real object's size, and clean-up of older versions ("F40-v4" saved → "F40-v1".."-v3", their STLs, views, backups and
/// timestamped copies are deleted). Only files this builder wrote are ever deleted: each folder keeps a list of them.
/// </summary>
public sealed partial class SwxParametric
{
    /// <summary>Files this builder wrote in a folder (hidden). Clean-up never touches a file that is not listed.</summary>
    public const string ManifestName = ".mediator-parts.json";
    /// <summary>Views rendered after a build, as SolidWorks names them, with the short name used in file names.</summary>
    public static readonly (string View, string Short)[] Views = [("*Front", "front"), ("*Right", "right"), ("*Top", "top"), ("*Isometric", "iso")];
    /// <summary>Proportions within this much of the reference count as matching.</summary>
    public const double ProportionTolerancePct = 5;

    private const int SwSaveAsCopy = 2; // swSaveAsOptions_Copy: the open document stays the .SLDPRT

    /// <summary>
    /// The design a file belongs to and its version: "F40-v3" → (F40, 3); "F40" → (F40, 0). Backups (".previous"), timestamped
    /// copies ("-20261028-082158") and rendered views (".view-front") belong to the same version as their part.
    /// </summary>
    public static (string Base, int Version) IterationKey(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        stem = ViewSuffix().Replace(stem, "");
        stem = stem.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase) ? stem[..^PreviousSuffix.Length] : stem;
        stem = StampSuffix().Replace(stem, "");
        var m = VersionSuffix().Match(stem);
        return m.Success && m.Groups[1].Length > 0
            ? (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : (stem.ToUpperInvariant(), 0);
    }

    /// <summary>
    /// Which files to delete once <paramref name="current"/> is saved: same design, built by Mediator (listed in the manifest, or
    /// the ".previous" backup of a listed part), and an older version, or the same version's backups and copies.
    /// </summary>
    public static List<string> OlderIterations(string current, IEnumerable<string> folderFiles, IReadOnlySet<string> manifest)
    {
        var (key, version) = IterationKey(current);
        string currentStem = Path.GetFileNameWithoutExtension(current);
        var old = new List<string>();
        foreach (string file in folderFiles)
        {
            if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) continue;
            string dir = Path.GetDirectoryName(file) ?? "";
            string stem = Path.GetFileNameWithoutExtension(file);
            bool ours = manifest.Contains(file)
                || (stem.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase)
                    && manifest.Contains(Path.Combine(dir, stem[..^PreviousSuffix.Length] + Path.GetExtension(file))));
            if (!ours) continue;
            var (k, v) = IterationKey(file);
            if (k != key || v > version) continue;
            // This version's own STL and views stay; its backups and timestamped copies go.
            bool sameOutput = v == version && string.Equals(ViewSuffix().Replace(stem, ""), currentStem, StringComparison.OrdinalIgnoreCase);
            if (!sameOutput) old.Add(file);
        }
        return old;
    }

    /// <summary>
    /// Built size against the real object's size (any units, any axis order): both sorted and divided by their largest side,
    /// so a car's length : width : height can be compared however it was oriented or scaled.
    /// </summary>
    public static JsonObject Proportions(double[] builtMm, double[] referenceMm)
    {
        double[] b = builtMm.OrderByDescending(x => x).ToArray(), r = referenceMm.OrderByDescending(x => x).ToArray();
        var dev = new JsonArray();
        bool ok = true;
        for (int i = 0; i < 3; i++)
        {
            double bi = b[i] / b[0], ri = r[i] / r[0];
            double pct = Math.Round((bi - ri) / ri * 100, 1);
            ok &= Math.Abs(pct) <= ProportionTolerancePct;
            dev.Add(pct);
        }
        return new JsonObject
        {
            ["built"] = new JsonArray(b.Select(x => (JsonNode)Math.Round(x / b[0], 3)).ToArray()),
            ["reference"] = new JsonArray(r.Select(x => (JsonNode)Math.Round(x / r[0], 3)).ToArray()),
            ["deviationPct"] = dev,
            ["matches"] = ok,
            ["note"] = ok ? $"overall proportions within {ProportionTolerancePct}% of the reference"
                          : $"proportions off by more than {ProportionTolerancePct}% (sides sorted longest first): fix them in the next version",
        };
    }

    /// <summary>Front, side, top and iso pictures of the part next to it, for the model to compare with the real object.</summary>
    private JsonArray RenderViews(string partPath)
    {
        var saved = new JsonArray();
        string stem = Path.Combine(Path.GetDirectoryName(partPath)!, Path.GetFileNameWithoutExtension(partPath));
        foreach (var (view, shortName) in Views)
        {
            string png = $"{stem}.view-{shortName}.png";
            try
            {
                C(_doc, "ShowNamedView2", view, -1);
                C(_doc, "ViewZoomtofit2");
                int err = (int)C(_doc, "SaveAs3", png, 0, SwSaveAsCopy)!;
                if (err == 0 && File.Exists(png)) { saved.Add(png); Remember(png); }
                else _log.Add($"view {shortName}: could not save the picture (error {err})");
            }
            catch (InvalidOperationException ex) { _log.Add($"view {shortName}: {ex.Message}"); }
        }
        C(_doc, "ShowNamedView2", "*Isometric", 7);
        C(_doc, "ViewZoomtofit2");
        return saved;
    }

    /// <summary>Adds a file this builder wrote to its folder's manifest.</summary>
    private void Remember(string file)
    {
        string folder = Path.GetDirectoryName(file)!;
        var set = ReadManifest(folder);
        if (!set.Add(file)) return;
        WriteManifest(folder, set);
    }

    private static HashSet<string> ReadManifest(string folder)
    {
        try
        {
            string path = Path.Combine(folder, ManifestName);
            if (File.Exists(path) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) is { } list)
                return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void WriteManifest(string folder, HashSet<string> set)
    {
        string path = Path.Combine(folder, ManifestName);
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, JsonSerializer.Serialize(set.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
            File.SetAttributes(path, FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Add("could not update " + ManifestName + ": " + ex.Message); }
    }

    /// <summary>Deletes older iterations of the part just saved (closing them in SolidWorks first). Returns what was deleted.</summary>
    private JsonArray PruneIterations(string partPath)
    {
        string folder = Path.GetDirectoryName(partPath)!;
        var manifest = ReadManifest(folder);
        var deleted = new JsonArray();
        foreach (string file in OlderIterations(partPath, Directory.EnumerateFiles(folder), manifest))
        {
            if (file.EndsWith(PartExtension, StringComparison.OrdinalIgnoreCase)) CloseIfOpen(file, onlyIfSaved: false);
            try { File.Delete(file); deleted.Add(Path.GetFileName(file)); manifest.Remove(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Add($"could not delete old iteration {Path.GetFileName(file)}: {ex.Message}"); }
        }
        if (deleted.Count > 0) WriteManifest(folder, manifest);
        return deleted;
    }

    [GeneratedRegex(@"\.view-[a-z]+$", RegexOptions.IgnoreCase)] private static partial Regex ViewSuffix();
    [GeneratedRegex(@"-\d{8}-\d{6}$")] private static partial Regex StampSuffix();
    [GeneratedRegex(@"^(.*?)[-_ ]?v(\d+)$", RegexOptions.IgnoreCase)] private static partial Regex VersionSuffix();
}
