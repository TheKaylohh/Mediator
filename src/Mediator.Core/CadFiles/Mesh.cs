using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace Mediator.Core.CadFiles;

/// <summary>A CAD file Mediator could not read or write. The message is for the user.</summary>
public sealed class CadFileException(string message) : Exception(message);

/// <summary>
/// V15: a triangle mesh (STL, OBJ, PLY, 3MF) as shared vertices and index triangles, with the checks a 3D-print or CAD
/// user asks first: size, triangle count, surface area, volume, closed or not, separate shells, bad triangles.
/// Units are not stored by STL/OBJ/PLY (millimetres by convention); 3MF says them.
/// </summary>
public sealed class Mesh
{
    public List<Vector3> Vertices { get; } = new();
    public List<(int A, int B, int C)> Triangles { get; } = new();
    /// <summary>Length of one unit in millimetres when the file says it (3MF), else null (assume mm).</summary>
    public double? UnitMm { get; set; }
    public string Format { get; set; } = "";
    public string? Name { get; set; }

    public static readonly string[] Extensions = [".stl", ".obj", ".ply", ".3mf"];
    private const long MaxBytes = 500L * 1024 * 1024;
    private const int MaxTriangles = 20_000_000;

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────

    public static Mesh Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) throw new CadFileException($"{info.Name} is over {MaxBytes / (1024 * 1024)} MB.");
        using var fs = File.OpenRead(path);
        return Load(fs, Path.GetExtension(path), info.Name);
    }

    public static Mesh Load(Stream stream, string extension, string name)
    {
        var mesh = extension.ToLowerInvariant() switch
        {
            ".stl" => ReadStl(stream),
            ".obj" => ReadObj(stream),
            ".ply" => ReadPly(stream),
            ".3mf" => Read3mf(stream),
            _ => throw new CadFileException($"{name}: meshes are read from .stl, .obj, .ply and .3mf files."),
        };
        mesh.Name = name;
        if (mesh.Triangles.Count == 0) throw new CadFileException($"{name} has no triangles.");
        return mesh;
    }

    private static Mesh ReadStl(Stream s)
    {
        byte[] all = ReadAll(s);
        // Binary: 80-byte header, uint32 count, 50 bytes per triangle. ASCII files begin with "solid" but so do some binary ones.
        if (all.Length >= 84)
        {
            uint count = BitConverter.ToUInt32(all, 80);
            if (count <= MaxTriangles && 84L + 50L * count == all.Length) return ReadBinaryStl(all, (int)count);
        }
        string text = Encoding.ASCII.GetString(all);
        if (!text.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase)) throw new CadFileException("Not an STL file (neither binary nor ASCII STL).");
        var mesh = new Mesh { Format = "STL (ASCII)" };
        var weld = new Dictionary<Vector3, int>();
        var corner = new List<int>(3);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) throw new CadFileException("A vertex line in the STL has fewer than 3 numbers.");
            corner.Add(mesh.Weld(weld, new Vector3(F(p[1]), F(p[2]), F(p[3]))));
            if (corner.Count == 3) { mesh.Triangles.Add((corner[0], corner[1], corner[2])); corner.Clear(); }
        }
        return mesh;
    }

    private static Mesh ReadBinaryStl(byte[] b, int count)
    {
        var mesh = new Mesh { Format = "STL (binary)" };
        var weld = new Dictionary<Vector3, int>();
        for (int i = 0; i < count; i++)
        {
            int o = 84 + i * 50 + 12; // skip the normal
            int a = mesh.Weld(weld, new Vector3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8)));
            int c1 = mesh.Weld(weld, new Vector3(BitConverter.ToSingle(b, o + 12), BitConverter.ToSingle(b, o + 16), BitConverter.ToSingle(b, o + 20)));
            int c2 = mesh.Weld(weld, new Vector3(BitConverter.ToSingle(b, o + 24), BitConverter.ToSingle(b, o + 28), BitConverter.ToSingle(b, o + 32)));
            mesh.Triangles.Add((a, c1, c2));
        }
        return mesh;
    }

    /// <summary>STL repeats each corner: identical coordinates become one vertex, so edges can be matched.</summary>
    private int Weld(Dictionary<Vector3, int> map, Vector3 v)
    {
        if (map.TryGetValue(v, out int i)) return i;
        i = Vertices.Count;
        Vertices.Add(v);
        map[v] = i;
        return i;
    }

    private static Mesh ReadObj(Stream s)
    {
        var mesh = new Mesh { Format = "OBJ" };
        using var reader = new StreamReader(s);
        string? line;
        int lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "v" && p.Length >= 4) mesh.Vertices.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
            else if (p[0] == "f" && p.Length >= 4)
            {
                var idx = p.Skip(1).Select(t => ObjIndex(t, mesh.Vertices.Count, lineNo)).ToArray();
                for (int k = 1; k + 1 < idx.Length; k++) mesh.Triangles.Add((idx[0], idx[k], idx[k + 1])); // fan
            }
            else if (p[0] == "o" && p.Length > 1 && mesh.Name is null) mesh.Name = string.Join(' ', p.Skip(1));
            if (mesh.Triangles.Count > MaxTriangles) throw new CadFileException("The OBJ has more than 20 million triangles.");
        }
        return mesh;
    }

    private static int ObjIndex(string token, int count, int line)
    {
        string first = token.Split('/')[0];
        if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) || i == 0) throw new CadFileException($"Bad face index '{token}' on OBJ line {line}.");
        int index = i > 0 ? i - 1 : count + i;
        if (index < 0 || index >= count) throw new CadFileException($"Face index {i} on OBJ line {line} refers to a vertex that does not exist.");
        return index;
    }

    private static Mesh ReadPly(Stream s)
    {
        var mesh = new Mesh { Format = "PLY" };
        // Header lines are ASCII up to "end_header\n".
        var header = new List<string>();
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n') { string l = sb.ToString().Trim(); header.Add(l); sb.Clear(); if (l == "end_header") break; if (header.Count > 500) break; }
            else sb.Append((char)b);
        }
        if (header.Count == 0 || header[0] != "ply" || header[^1] != "end_header") throw new CadFileException("Not a PLY file (no ply … end_header header).");
        string format = header.FirstOrDefault(h => h.StartsWith("format", StringComparison.Ordinal))?.Split(' ').ElementAtOrDefault(1) ?? "";
        if (format is not ("ascii" or "binary_little_endian")) throw new CadFileException($"PLY format '{format}' is not supported (ascii and binary_little_endian are).");
        mesh.Format = "PLY (" + format + ")";
        // Elements in order, with their properties.
        var elements = new List<(string Name, int Count, List<(string Type, string Name, string? CountType)> Props)>();
        foreach (var h in header)
        {
            var p = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3 && p[0] == "element") elements.Add((p[1], int.Parse(p[2], CultureInfo.InvariantCulture), new()));
            else if (p.Length >= 3 && p[0] == "property" && elements.Count > 0)
                elements[^1].Props.Add(p[1] == "list" && p.Length >= 5 ? (p[3], p[4], p[2]) : (p[1], p[2], null));
        }
        if (format == "ascii")
        {
            using var reader = new StreamReader(s);
            foreach (var (name, count, props) in elements)
                for (int i = 0; i < count; i++)
                {
                    var tok = (reader.ReadLine() ?? throw new CadFileException("The PLY ends early.")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (name == "vertex") mesh.Vertices.Add(new Vector3(F(tok[props.FindIndex(p => p.Name == "x")]), F(tok[props.FindIndex(p => p.Name == "y")]), F(tok[props.FindIndex(p => p.Name == "z")])));
                    else if (name == "face") AddFace(mesh, tok.Skip(1).Take(int.Parse(tok[0], CultureInfo.InvariantCulture)).Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToArray());
                }
        }
        else
        {
            using var br = new BinaryReader(s);
            foreach (var (name, count, props) in elements)
                for (int i = 0; i < count; i++)
                {
                    float x = 0, y = 0, z = 0;
                    foreach (var (type, prop, countType) in props)
                    {
                        if (countType is not null)
                        {
                            int n = (int)ReadNumber(br, countType);
                            var idx = new int[n];
                            for (int k = 0; k < n; k++) idx[k] = (int)ReadNumber(br, type);
                            if (name == "face") AddFace(mesh, idx);
                            continue;
                        }
                        double v = ReadNumber(br, type);
                        if (name == "vertex") { if (prop == "x") x = (float)v; else if (prop == "y") y = (float)v; else if (prop == "z") z = (float)v; }
                    }
                    if (name == "vertex") mesh.Vertices.Add(new Vector3(x, y, z));
                }
        }
        return mesh;
    }

    private static double ReadNumber(BinaryReader br, string type) => type switch
    {
        "char" or "int8" => br.ReadSByte(),
        "uchar" or "uint8" => br.ReadByte(),
        "short" or "int16" => br.ReadInt16(),
        "ushort" or "uint16" => br.ReadUInt16(),
        "int" or "int32" => br.ReadInt32(),
        "uint" or "uint32" => br.ReadUInt32(),
        "float" or "float32" => br.ReadSingle(),
        "double" or "float64" => br.ReadDouble(),
        _ => throw new CadFileException($"PLY property type '{type}' is not supported."),
    };

    private static void AddFace(Mesh mesh, int[] idx)
    {
        foreach (int i in idx) if (i < 0 || i >= mesh.Vertices.Count) throw new CadFileException($"A PLY face refers to vertex {i}, which does not exist.");
        for (int k = 1; k + 1 < idx.Length; k++) mesh.Triangles.Add((idx[0], idx[k], idx[k + 1]));
    }

    private static readonly XNamespace Core3mf = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    private static Mesh Read3mf(Stream s)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new CadFileException("Not a 3MF file (not a zip package)."); }
        using (zip)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) && e.FullName.StartsWith("3D/", StringComparison.OrdinalIgnoreCase))
                ?? throw new CadFileException("The 3MF has no 3D/*.model part.");
            XDocument doc;
            using (var es = entry.Open()) doc = XDocument.Load(es);
            var mesh = new Mesh { Format = "3MF" };
            mesh.UnitMm = ((string?)doc.Root!.Attribute("unit") ?? "millimeter") switch
            {
                "micron" => 0.001, "millimeter" => 1, "centimeter" => 10, "inch" => 25.4, "foot" => 304.8, "meter" => 1000, _ => 1,
            };
            foreach (var m in doc.Descendants(Core3mf + "mesh"))
            {
                int offset = mesh.Vertices.Count;
                foreach (var v in m.Element(Core3mf + "vertices")?.Elements(Core3mf + "vertex") ?? [])
                    mesh.Vertices.Add(new Vector3(F((string?)v.Attribute("x")), F((string?)v.Attribute("y")), F((string?)v.Attribute("z"))));
                foreach (var t in m.Element(Core3mf + "triangles")?.Elements(Core3mf + "triangle") ?? [])
                {
                    int a = offset + (int)t.Attribute("v1")!, b = offset + (int)t.Attribute("v2")!, c = offset + (int)t.Attribute("v3")!;
                    if (Math.Max(a, Math.Max(b, c)) >= mesh.Vertices.Count) throw new CadFileException("A 3MF triangle refers to a vertex that does not exist.");
                    mesh.Triangles.Add((a, b, c));
                }
            }
            return mesh;
        }
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static float F(string? s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : throw new CadFileException($"'{s}' is not a number.");

    // ── Analysis ─────────────────────────────────────────────────────────────────────────────────────

    public sealed record Report(int Vertices, int Triangles, Vector3 Min, Vector3 Max, double Area, double Volume, int BoundaryEdges, int NonManifoldEdges,
        int Degenerate, int Shells, bool Watertight, bool InsideOut)
    {
        public Vector3 Size => Max - Min;
    }

    public Report Analyze()
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (int i in Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct())
        {
            min = Vector3.Min(min, Vertices[i]);
            max = Vector3.Max(max, Vertices[i]);
        }
        double area = 0, volume = 0;
        int degenerate = 0;
        var edges = new Dictionary<(int, int), int>();
        var parent = Enumerable.Range(0, Vertices.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
        foreach (var (a, b, c) in Triangles)
        {
            Vector3 p = Vertices[a], q = Vertices[b], r = Vertices[c];
            double twice = Vector3.Cross(q - p, r - p).Length();
            if (twice < 1e-12 || a == b || b == c || a == c) degenerate++;
            area += twice / 2;
            volume += Vector3.Dot(p, Vector3.Cross(q, r)) / 6.0;
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = u < v ? (u, v) : (v, u);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
            Union(a, b); Union(b, c);
        }
        int boundary = edges.Values.Count(n => n == 1), nonManifold = edges.Values.Count(n => n > 2);
        int shells = Triangles.Select(t => Find(t.A)).Distinct().Count();
        bool closed = boundary == 0 && nonManifold == 0;
        return new Report(Vertices.Count, Triangles.Count, min, max, area, Math.Abs(volume), boundary, nonManifold, degenerate, shells, closed, closed && volume < 0);
    }

    // ── Changing and writing ─────────────────────────────────────────────────────────────────────────

    /// <summary>Scales every vertex, then (when <paramref name="center"/>) moves the part so its base is centred on the origin at z = 0.</summary>
    public void Transform(double scale, bool center)
    {
        for (int i = 0; i < Vertices.Count; i++) Vertices[i] *= (float)scale;
        if (!center || Vertices.Count == 0) return;
        var r = Analyze();
        var shift = new Vector3((r.Min.X + r.Max.X) / 2, (r.Min.Y + r.Max.Y) / 2, r.Min.Z);
        for (int i = 0; i < Vertices.Count; i++) Vertices[i] -= shift;
    }

    public static readonly string[] WritableExtensions = [".stl", ".obj", ".ply", ".3mf"];

    public void Write(Stream output, string extension, bool asciiStl = false)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".stl" when asciiStl: WriteAsciiStl(output); break;
            case ".stl": WriteBinaryStl(output); break;
            case ".obj": WriteText(output, w => { w.Write("# written by Mediator\n"); foreach (var v in Vertices) w.Write($"v {N(v.X)} {N(v.Y)} {N(v.Z)}\n"); foreach (var (a, b, c) in Triangles) w.Write($"f {a + 1} {b + 1} {c + 1}\n"); }); break;
            case ".ply":
                WriteText(output, w =>
                {
                    w.Write($"ply\nformat ascii 1.0\ncomment written by Mediator\nelement vertex {Vertices.Count}\nproperty float x\nproperty float y\nproperty float z\nelement face {Triangles.Count}\nproperty list uchar int vertex_indices\nend_header\n");
                    foreach (var v in Vertices) w.Write($"{N(v.X)} {N(v.Y)} {N(v.Z)}\n");
                    foreach (var (a, b, c) in Triangles) w.Write($"3 {a} {b} {c}\n");
                });
                break;
            case ".3mf": Write3mf(output); break;
            default: throw new CadFileException("Meshes can be written as .stl, .obj, .ply or .3mf.");
        }
    }

    private static string N(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    private static void WriteText(Stream output, Action<StreamWriter> body)
    {
        using var w = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        body(w);
    }

    private Vector3 Normal((int A, int B, int C) t)
    {
        var n = Vector3.Cross(Vertices[t.B] - Vertices[t.A], Vertices[t.C] - Vertices[t.A]);
        float len = n.Length();
        return len > 0 ? n / len : Vector3.Zero;
    }

    private void WriteBinaryStl(Stream output)
    {
        using var bw = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("Mediator binary STL").CopyTo(header, 0);
        bw.Write(header);
        bw.Write((uint)Triangles.Count);
        foreach (var t in Triangles)
        {
            var n = Normal(t);
            foreach (var v in new[] { n, Vertices[t.A], Vertices[t.B], Vertices[t.C] }) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); }
            bw.Write((ushort)0);
        }
    }

    private void WriteAsciiStl(Stream output) => WriteText(output, w =>
    {
        string name = new((Name ?? "mediator").Where(char.IsLetterOrDigit).ToArray());
        w.Write($"solid {name}\n");
        foreach (var t in Triangles)
        {
            var n = Normal(t);
            w.Write($"  facet normal {N(n.X)} {N(n.Y)} {N(n.Z)}\n    outer loop\n");
            foreach (var v in new[] { Vertices[t.A], Vertices[t.B], Vertices[t.C] }) w.Write($"      vertex {N(v.X)} {N(v.Y)} {N(v.Z)}\n");
            w.Write("    endloop\n  endfacet\n");
        }
        w.Write($"endsolid {name}\n");
    });

    private void Write3mf(Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, Action<StreamWriter> body)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            body(w);
        }
        Add("[Content_Types].xml", w => w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>"));
        Add("_rels/.rels", w => w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>"));
        Add("3D/3dmodel.model", w =>
        {
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources><object id=\"1\" type=\"model\"><mesh><vertices>");
            foreach (var v in Vertices) w.Write($"<vertex x=\"{N(v.X)}\" y=\"{N(v.Y)}\" z=\"{N(v.Z)}\"/>");
            w.Write("</vertices><triangles>");
            foreach (var (a, b, c) in Triangles) w.Write($"<triangle v1=\"{a}\" v2=\"{b}\" v3=\"{c}\"/>");
            w.Write("</triangles></mesh></object></resources><build><item objectid=\"1\"/></build></model>");
        });
    }
}
