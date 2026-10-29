using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mediator.Core.Swx;

namespace Mediator.Core.CadFiles;

/// <summary>
/// V15: a plain-language summary of a CAD file for the model and the user, without a CAD kernel:
/// meshes (STL, OBJ, PLY, 3MF): size, triangles, area, volume, closed/open, shells, print hints;
/// STEP (.step/.stp) and IGES (.iges/.igs): who wrote it, units, parts, solids/surfaces, an approximate size;
/// 2D profiles (DXF, SVG): outlines, circles and the drawing's size. Native SolidWorks files are pointed at SolidWorks.
/// </summary>
public static partial class CadFileInspector
{
    public static readonly string[] StepExtensions = [".step", ".stp"];
    public static readonly string[] IgesExtensions = [".iges", ".igs"];
    public static readonly string[] ProfileExtensions = [".dxf", ".svg"];
    public static readonly string[] NativeExtensions = [".sldprt", ".sldasm", ".slddrw"];
    private const long MaxTextBytes = 300L * 1024 * 1024;

    public static IEnumerable<string> AllExtensions => Mesh.Extensions.Concat(StepExtensions).Concat(IgesExtensions).Concat(ProfileExtensions).Concat(NativeExtensions);

    public static bool IsCadFile(string path) => AllExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The summary of the file at <paramref name="path"/> (any supported kind).</summary>
    public static string Describe(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant(), name = Path.GetFileName(path);
        if (Mesh.Extensions.Contains(ext)) return DescribeMesh(Mesh.Load(path));
        if (NativeExtensions.Contains(ext))
            return $"{name} is a native SolidWorks file ({new FileInfo(path).Length / 1024:N0} KiB). Its features and dimensions are read by opening it in SolidWorks " +
                "(swx_session / swx_document_ops) or exporting it to STEP/STL (swx_export_neutral); Mediator does not parse the binary format itself.";
        if (new FileInfo(path).Length > MaxTextBytes) throw new CadFileException($"{name} is over {MaxTextBytes / (1024 * 1024)} MB.");
        if (StepExtensions.Contains(ext)) return DescribeStep(File.ReadAllText(path, Encoding.Latin1), name);
        if (IgesExtensions.Contains(ext)) return DescribeIges(File.ReadAllLines(path, Encoding.Latin1), name);
        if (ProfileExtensions.Contains(ext)) return DescribeProfile(path);
        throw new CadFileException($"{name}: not a CAD file Mediator reads ({string.Join(", ", AllExtensions)}).");
    }

    private static string Mm(double v) => v.ToString(Math.Abs(v) >= 100 ? "0.#" : "0.###", CultureInfo.InvariantCulture);

    public static string DescribeMesh(Mesh mesh)
    {
        var r = mesh.Analyze();
        double unit = mesh.UnitMm ?? 1;
        var size = r.Size * (float)unit;
        var sb = new StringBuilder();
        sb.Append($"{mesh.Name}: {mesh.Format} mesh, {r.Triangles:N0} triangles, {r.Vertices:N0} vertices.\n");
        sb.Append($"Size: {Mm(size.X)} × {Mm(size.Y)} × {Mm(size.Z)} mm{(mesh.UnitMm is null ? " (if the file is in millimetres, as STL/OBJ/PLY usually are)" : "")}; ");
        sb.Append($"from ({Mm(r.Min.X * unit)}, {Mm(r.Min.Y * unit)}, {Mm(r.Min.Z * unit)}) to ({Mm(r.Max.X * unit)}, {Mm(r.Max.Y * unit)}, {Mm(r.Max.Z * unit)}).\n");
        sb.Append($"Surface area: {Mm(r.Area * unit * unit)} mm².");
        if (r.Watertight) sb.Append($" Volume: {Mm(r.Volume * unit * unit * unit / 1000)} cm³ (closed mesh).\n");
        else sb.Append(" Volume: not reliable (the mesh is not closed).\n");
        sb.Append(r.Watertight ? "Closed (watertight): every edge is shared by exactly two triangles." : $"NOT closed: {r.BoundaryEdges:N0} open edge(s), {r.NonManifoldEdges:N0} non-manifold edge(s).");
        sb.Append($" Shells: {r.Shells}.");
        if (r.Degenerate > 0) sb.Append($" {r.Degenerate:N0} degenerate (zero-area) triangle(s).");
        if (r.InsideOut) sb.Append(" The normals point inward (inside out): flip them before printing.");
        sb.Append('\n');
        double longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (mesh.UnitMm is null && longest is > 0 and < 2) sb.Append("Hint: under 2 mm across: the file may be in metres or inches (scale by 1000 or 25.4).\n");
        if (mesh.UnitMm is null && longest > 2000) sb.Append("Hint: over 2 m across: the file may be in micrometres or a different unit.\n");
        if (!r.Watertight) sb.Append("For printing, repair the open edges first (most slicers can; or re-export from CAD as a solid).\n");
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"#\d+\s*=\s*([A-Z][A-Z0-9_]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex StepEntity();

    [GeneratedRegex(@"CARTESIAN_POINT\s*\(\s*'[^']*'\s*,\s*\(\s*([-+0-9.EeDd]+)\s*,\s*([-+0-9.EeDd]+)\s*(?:,\s*([-+0-9.EeDd]+))?\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex StepPoint();

    [GeneratedRegex(@"\bPRODUCT\s*\(\s*'((?:[^']|'')*)'", RegexOptions.CultureInvariant)]
    private static partial Regex StepProduct();

    [GeneratedRegex(@"FILE_NAME\s*\(\s*'((?:[^']|'')*)'\s*,\s*'([^']*)'\s*,\s*\(([^)]*)\)\s*,\s*\(([^)]*)\)\s*,\s*'((?:[^']|'')*)'\s*,\s*'((?:[^']|'')*)'", RegexOptions.CultureInvariant)]
    private static partial Regex StepFileName();

    [GeneratedRegex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex StepSchema();

    public static string DescribeStep(string text, string name)
    {
        if (!text.TrimStart().StartsWith("ISO-10303-21", StringComparison.Ordinal)) throw new CadFileException($"{name} is not a STEP file (no ISO-10303-21 header).");
        var sb = new StringBuilder();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in StepEntity().Matches(text)) counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
        int Count(string e) => counts.GetValueOrDefault(e);
        string schema = StepSchema().Match(text) is { Success: true } s ? s.Groups[1].Value : "unknown";
        string protocol = schema.Contains("AP214", StringComparison.OrdinalIgnoreCase) || schema.Contains("AUTOMOTIVE", StringComparison.OrdinalIgnoreCase) ? "AP214"
            : schema.Contains("AP242", StringComparison.OrdinalIgnoreCase) || schema.Contains("MANAGED_MODEL_BASED", StringComparison.OrdinalIgnoreCase) ? "AP242"
            : schema.Contains("CONFIG_CONTROL", StringComparison.OrdinalIgnoreCase) || schema.Contains("AP203", StringComparison.OrdinalIgnoreCase) ? "AP203" : schema;
        sb.Append($"{name}: STEP file ({protocol}, {text.Length / 1024:N0} KiB, {counts.Values.Sum():N0} entities).\n");
        if (StepFileName().Match(text) is { Success: true } f)
        {
            string system = f.Groups[6].Value.Length > 0 ? f.Groups[6].Value : f.Groups[5].Value;
            sb.Append($"Written {f.Groups[2].Value} by {(system.Length > 0 ? system : "an unnamed system")}{(f.Groups[5].Value.Length > 0 && f.Groups[5].Value != system ? " (" + f.Groups[5].Value + ")" : "")}.\n");
        }
        string units = text.Contains("CONVERSION_BASED_UNIT('INCH'", StringComparison.OrdinalIgnoreCase) ? "inch"
            : text.Contains("SI_UNIT(.MILLI.,.METRE.)", StringComparison.Ordinal) ? "millimetre"
            : text.Contains("SI_UNIT(.CENTI.,.METRE.)", StringComparison.Ordinal) ? "centimetre"
            : text.Contains("SI_UNIT($,.METRE.)", StringComparison.Ordinal) ? "metre" : "not stated";
        sb.Append($"Length unit: {units}.\n");
        var products = StepProduct().Matches(text).Select(m => m.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal)).Where(p => p.Length > 0).Distinct().ToList();
        if (products.Count > 0) sb.Append($"Parts/products ({products.Count}): {string.Join(", ", products.Take(30))}{(products.Count > 30 ? ", …" : "")}.\n");
        int assemblyLinks = Count("NEXT_ASSEMBLY_USAGE_OCCURRENCE");
        if (assemblyLinks > 0) sb.Append($"Assembly: {assemblyLinks} component placement(s).\n");
        int solids = Count("MANIFOLD_SOLID_BREP") + Count("BREP_WITH_VOIDS"), faces = Count("ADVANCED_FACE"), surfaceModels = Count("SHELL_BASED_SURFACE_MODEL"), meshes = Count("TRIANGULATED_FACE_SET") + Count("TESSELLATED_SHELL");
        sb.Append($"Geometry: {solids} solid body(ies), {faces:N0} B-rep face(s)");
        if (surfaceModels > 0) sb.Append($", {surfaceModels} surface model(s)");
        if (meshes > 0) sb.Append($", {meshes} tessellated (mesh) part(s)");
        sb.Append(".\n");
        if (solids == 0 && surfaceModels > 0) sb.Append("No closed solids: SolidWorks will import surfaces (knit them to make a solid).\n");
        var surfaceKinds = new[] { ("PLANE", "planes"), ("CYLINDRICAL_SURFACE", "cylinders"), ("CONICAL_SURFACE", "cones"), ("SPHERICAL_SURFACE", "spheres"), ("TOROIDAL_SURFACE", "tori"), ("B_SPLINE_SURFACE_WITH_KNOTS", "B-spline surfaces") }
            .Where(k => Count(k.Item1) > 0).Select(k => $"{Count(k.Item1):N0} {k.Item2}").ToList();
        if (surfaceKinds.Count > 0) sb.Append("Surfaces: ").Append(string.Join(", ", surfaceKinds)).Append(".\n");
        // Approximate size: control and placement points can stick out a little past the real shape.
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        int points = 0;
        foreach (Match m in StepPoint().Matches(text))
        {
            if (!TryNumber(m.Groups[1].Value, out double x) || !TryNumber(m.Groups[2].Value, out double y)) continue;
            double z = m.Groups[3].Success && TryNumber(m.Groups[3].Value, out double zz) ? zz : 0;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            points++;
        }
        if (points > 0 && assemblyLinks == 0)
        {
            double k = units switch { "inch" => 25.4, "centimetre" => 10, "metre" => 1000, _ => 1 };
            sb.Append($"Approximate size: {Mm((maxX - minX) * k)} × {Mm((maxY - minY) * k)} × {Mm((maxZ - minZ) * k)} mm (from {points:N0} points; exact size after import).\n");
        }
        else if (assemblyLinks > 0) sb.Append("Size: not estimated for an assembly (parts are placed by transforms); import it to measure.\n");
        sb.Append("To edit it: swx_import_step opens it as a SolidWorks part; to mesh it for printing, import and export with swx_export_neutral (STL).");
        return sb.ToString();
    }

    private static bool TryNumber(string s, out double v) =>
        double.TryParse(s.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static readonly Dictionary<int, string> IgesEntities = new()
    {
        [100] = "circular arcs", [102] = "composite curves", [104] = "conic arcs", [106] = "copious data", [108] = "planes", [110] = "lines", [112] = "parametric splines",
        [116] = "points", [118] = "ruled surfaces", [120] = "surfaces of revolution", [122] = "tabulated cylinders", [124] = "transformation matrices",
        [126] = "B-spline curves", [128] = "B-spline surfaces", [141] = "boundaries", [142] = "curves on surfaces", [143] = "bounded surfaces",
        [144] = "trimmed surfaces", [186] = "manifold solids", [190] = "plane surfaces", [192] = "cylindrical surfaces", [196] = "spherical surfaces",
        [308] = "subfigure definitions", [314] = "colours", [402] = "associativities", [406] = "properties", [408] = "subfigure instances",
        [502] = "vertex lists", [504] = "edge lists", [508] = "loops", [510] = "faces", [514] = "shells",
    };

    public static string DescribeIges(string[] lines, string name)
    {
        var record = lines.Where(l => l.Length >= 73).ToList();
        if (record.Count == 0 || !record.Any(l => l[72] == 'G')) throw new CadFileException($"{name} is not an IGES file (no 80-column G section).");
        string global = string.Concat(record.Where(l => l[72] == 'G').Select(l => l[..72]));
        char pd = global.Length > 2 && global[0] == '1' && global[1] == 'H' ? global[2] : ',';
        char rd = ';';
        var fields = SplitIges(global, pd, rd);
        string Field(int i) => i < fields.Count ? fields[i] : "";
        int unitFlag = int.TryParse(Field(13), NumberStyles.Integer, CultureInfo.InvariantCulture, out int uf) ? uf : 0;
        string unit = unitFlag switch { 1 => "inch", 2 => "millimetre", 4 => "foot", 5 => "mile", 6 => "metre", 7 => "kilometre", 8 => "mil", 9 => "micron", 10 => "centimetre", 11 => "microinch", _ => Field(14) is { Length: > 0 } u ? u : "not stated" };
        var counts = new Dictionary<int, int>();
        var directory = record.Where(l => l[72] == 'D').ToList();
        for (int i = 0; i < directory.Count; i += 2)
            if (int.TryParse(directory[i][..8].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int type)) counts[type] = counts.GetValueOrDefault(type) + 1;
        var sb = new StringBuilder();
        sb.Append($"{name}: IGES file, {counts.Values.Sum():N0} entities.\n");
        if (Field(3).Length > 0 || Field(4).Length > 0) sb.Append($"File name in header: {Field(3)}; written by {Field(4)} {Field(5)}.\n");
        if (Field(17).Length > 0) sb.Append($"Date: {Field(17)}.\n");
        sb.Append($"Length unit: {unit}.\n");
        sb.Append("Contents: ").Append(string.Join(", ", counts.OrderByDescending(c => c.Value).Take(14)
            .Select(c => $"{c.Value:N0} {IgesEntities.GetValueOrDefault(c.Key, "type " + c.Key)}"))).Append(".\n");
        bool solids = counts.ContainsKey(186);
        sb.Append(solids ? "Contains solid B-rep bodies." : "Surfaces/curves only (no solid bodies): SolidWorks imports surfaces to knit.");
        return sb.ToString();
    }

    /// <summary>Splits the IGES global section, honouring Hollerith strings ("5HHello").</summary>
    private static List<string> SplitIges(string s, char pd, char rd)
    {
        var result = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == 'H' && i > start && int.TryParse(s[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                int textStart = i + 1;
                string text = textStart + n <= s.Length ? s.Substring(textStart, n) : s[textStart..];
                result.Add(text);
                i = Math.Min(s.Length, textStart + n);
            }
            else
            {
                i = start;
                while (i < s.Length && s[i] != pd && s[i] != rd) i++;
                result.Add(s[start..i].Trim());
            }
            if (i < s.Length && s[i] == rd) break;
            i++; // the delimiter
        }
        return result;
    }

    public static string DescribeProfile(string path)
    {
        string name = Path.GetFileName(path);
        ProfileImport.Drawing d;
        try { d = ProfileImport.Load(path); }
        catch (ArgumentException ex) { throw new CadFileException($"{name}: {ex.Message}"); }
        var points = d.Contours.SelectMany(c => c.AllPoints).Concat(d.Circles.SelectMany(c => new[] { (c.X - c.R, c.Y - c.R), (c.X + c.R, c.Y + c.R) })).ToList();
        var sb = new StringBuilder($"{name}: 2D {(path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "SVG" : "DXF")} profile, {d.Contours.Count} outline(s) ({d.Contours.Count(c => c.Closed)} closed), {d.Circles.Count} circle(s).\n");
        if (points.Count > 0)
        {
            double w = (points.Max(p => p.Item1) - points.Min(p => p.Item1)) * d.UnitMm, h = (points.Max(p => p.Item2) - points.Min(p => p.Item2)) * d.UnitMm;
            sb.Append($"Size: {Mm(w)} × {Mm(h)} mm.\n");
        }
        if (d.Circles.Count > 0)
            sb.Append("Hole/circle diameters (mm): ").Append(string.Join(", ", d.Circles.Select(c => Mm(2 * c.R * d.UnitMm)).GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).Take(20))).Append(".\n");
        sb.Append("Use it in a SolidWorks part with swx_build_parametric_part (a sketch {import: {path}} extruded or cut).");
        return sb.ToString();
    }
}
