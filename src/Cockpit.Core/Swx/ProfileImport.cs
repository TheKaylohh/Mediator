using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Cockpit.Core.Swx;

/// <summary>
/// 2D profiles from DXF and SVG files, turned into the part builder's sketch shapes ({outline}, {spline}, {arc}, {line},
/// {circle}) in millimetres. Connected pieces are chained into closed outlines (so they can be extruded, lofted or cut);
/// curves become splines through sampled points, arcs stay arcs. Supported: DXF LINE, ARC, CIRCLE, LWPOLYLINE (with
/// bulges), POLYLINE/VERTEX, SPLINE, ELLIPSE ($INSUNITS honoured); SVG path (M L H V C S Q T A Z), polygon, polyline,
/// rect, circle, ellipse, line, with transforms. Y points up (SVG is flipped).
/// </summary>
public static partial class ProfileImport
{
    /// <summary>One piece of a contour, ending at its last point: a straight line, a smooth curve (points) or an arc (mid, end).</summary>
    public sealed record Segment(char Kind, List<(double X, double Y)> Points); // 'L' line, 'C' curve, 'A' arc

    public sealed record Contour((double X, double Y) Start, List<Segment> Segments, bool Closed)
    {
        public (double X, double Y) End => Segments.Count == 0 ? Start : Segments[^1].Points[^1];
        public IEnumerable<(double X, double Y)> AllPoints => Segments.SelectMany(s => s.Points).Prepend(Start);
    }

    public sealed record Drawing(List<Contour> Contours, List<(double X, double Y, double R)> Circles, double UnitMm);

    private const int CurveSamples = 8;      // points per Bézier / arc piece
    private const int EllipseSamples = 32;
    private const double SvgPxMm = 25.4 / 96; // CSS pixel

    /// <summary>
    /// The shapes for {import: {path, width?, scale?, center? (true), offset? [x,y]}}: drawn at the drawing's real size (DXF
    /// units, SVG px at 96 dpi) times scale, or scaled so its width is "width" mm; centred on the sketch origin unless center
    /// is false; then moved by offset (mm).
    /// </summary>
    public static JsonArray ToShapes(JsonObject spec, Func<JsonNode?, double> eval)
    {
        string path = (string?)spec["path"] ?? throw new ArgumentException("import needs a path (.dxf or .svg)");
        var drawing = Load(path);
        var pts = drawing.Contours.SelectMany(c => c.AllPoints)
            .Concat(drawing.Circles.SelectMany(c => new[] { (c.X - c.R, c.Y - c.R), (c.X + c.R, c.Y + c.R) })).ToList();
        if (pts.Count == 0) throw new ArgumentException($"import: no usable geometry in {Path.GetFileName(path)}");
        double minX = pts.Min(p => p.Item1), maxX = pts.Max(p => p.Item1), minY = pts.Min(p => p.Item2), maxY = pts.Max(p => p.Item2);
        double s = drawing.UnitMm * (spec["scale"] is null ? 1 : eval(spec["scale"]));
        if (spec["width"] is { } w && maxX > minX) s = eval(w) / (maxX - minX);
        bool center = (bool?)spec["center"] ?? true;
        double ox = (spec["offset"] is JsonArray o ? eval(o[0]) : 0) - (center ? (minX + maxX) / 2 * s : 0);
        double oy = (spec["offset"] is JsonArray o2 ? eval(o2[1]) : 0) - (center ? (minY + maxY) / 2 * s : 0);
        JsonArray P((double X, double Y) p) => new(Math.Round(p.X * s + ox, 4), Math.Round(p.Y * s + oy, 4));

        var shapes = new JsonArray();
        foreach (var c in drawing.Contours)
        {
            if (c.Closed)
            {
                var chain = new JsonArray(P(c.Start));
                foreach (var seg in c.Segments)
                    chain.Add(seg.Kind switch
                    {
                        'L' => P(seg.Points[0]),
                        'A' => new JsonObject { ["arc"] = new JsonArray(P(seg.Points[0]), P(seg.Points[1])) },
                        _ => new JsonObject { ["spline"] = new JsonArray(seg.Points.Select(p => (JsonNode)P(p)).ToArray()) },
                    });
                shapes.Add(new JsonObject { ["outline"] = chain });
                continue;
            }
            var at = c.Start;
            foreach (var seg in c.Segments)
            {
                shapes.Add(seg.Kind switch
                {
                    'L' => new JsonObject { ["line"] = new JsonArray(P(at), P(seg.Points[0])) },
                    'A' => new JsonObject { ["arc"] = new JsonArray(P(at), P(seg.Points[0]), P(seg.Points[1])) },
                    _ => new JsonObject { ["spline"] = new JsonObject { ["points"] = new JsonArray(seg.Points.Prepend(at).Select(p => (JsonNode)P(p)).ToArray()) } },
                });
                at = seg.Points[^1];
            }
        }
        foreach (var (x, y, r) in drawing.Circles)
            shapes.Add(new JsonObject { ["circle"] = new JsonObject { ["cx"] = Math.Round(x * s + ox, 4), ["cy"] = Math.Round(y * s + oy, 4), ["d"] = Math.Round(2 * r * s, 4) } });
        return shapes;
    }

    public static Drawing Load(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"import: {path} does not exist");
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dxf" => ParseDxf(File.ReadAllText(path)),
            ".svg" => ParseSvg(File.ReadAllText(path)),
            _ => throw new ArgumentException("import: only .dxf and .svg profiles are supported"),
        };
    }

    // ── DXF ───────────────────────────────────────────────────────────────────────────────────────────

    public static Drawing ParseDxf(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim('\r', ' ', '\t')).ToArray();
        var pairs = new List<(int Code, string Value)>();
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) pairs.Add((code, lines[i + 1]));

        double unitMm = 1;
        for (int i = 0; i + 1 < pairs.Count; i++)
            if (pairs[i] is (9, "$INSUNITS") && pairs[i + 1].Code == 70)
                unitMm = int.Parse(pairs[i + 1].Value, CultureInfo.InvariantCulture) switch { 1 => 25.4, 2 => 304.8, 5 => 10, 6 => 1000, _ => 1 };

        // Entities: from "ENTITIES" to "ENDSEC"; each starts with group 0.
        int start = pairs.FindIndex(p => p is (2, "ENTITIES"));
        var pieces = new List<Contour>();
        var circles = new List<(double, double, double)>();
        if (start < 0) return new Drawing(pieces, circles, unitMm);
        List<(double X, double Y, double Bulge)>? polyVerts = null;
        bool polyClosed = false;
        for (int i = start + 1; i < pairs.Count; i++)
        {
            if (pairs[i].Code != 0) continue;
            string type = pairs[i].Value;
            if (type == "ENDSEC") break;
            int end = i + 1;
            while (end < pairs.Count && pairs[end].Code != 0) end++;
            var g = pairs.Skip(i + 1).Take(end - i - 1).ToList();
            double D(int code, double def = 0) => g.FirstOrDefault(p => p.Code == code) is { Value: { } v } && g.Any(p => p.Code == code) ? Num(v) : def;
            List<double> All(int code) => g.Where(p => p.Code == code).Select(p => Num(p.Value)).ToList();
            switch (type)
            {
                case "LINE":
                    pieces.Add(new Contour((D(10), D(20)), [new Segment('L', [(D(11), D(21))])], false));
                    break;
                case "CIRCLE":
                    circles.Add((D(10), D(20), D(40)));
                    break;
                case "ARC":
                {
                    double cx = D(10), cy = D(20), r = D(40), a1 = D(50), a2 = D(51);
                    if (a2 <= a1) a2 += 360;
                    (double, double) At(double deg) => (cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
                    pieces.Add(new Contour(At(a1), [new Segment('A', [At((a1 + a2) / 2), At(a2)])], false));
                    break;
                }
                case "LWPOLYLINE":
                {
                    var verts = new List<(double X, double Y, double Bulge)>();
                    foreach (var p in g)
                    {
                        if (p.Code == 10) verts.Add((Num(p.Value), 0, 0));
                        else if (p.Code == 20 && verts.Count > 0) verts[^1] = (verts[^1].X, Num(p.Value), verts[^1].Bulge);
                        else if (p.Code == 42 && verts.Count > 0) verts[^1] = (verts[^1].X, verts[^1].Y, Num(p.Value));
                    }
                    if (verts.Count >= 2) pieces.Add(Polyline(verts, ((int)D(70) & 1) == 1));
                    break;
                }
                case "POLYLINE":
                    polyVerts = [];
                    polyClosed = ((int)D(70) & 1) == 1;
                    break;
                case "VERTEX" when polyVerts is not null:
                    polyVerts.Add((D(10), D(20), D(42)));
                    break;
                case "SEQEND" when polyVerts is not null:
                    if (polyVerts.Count >= 2) pieces.Add(Polyline(polyVerts, polyClosed));
                    polyVerts = null;
                    break;
                case "SPLINE":
                {
                    // Fit points lie on the curve; without them the control polygon approximates it.
                    var (fx, fy) = (All(11), All(21));
                    var (px, py) = fx.Count >= 2 ? (fx, fy) : (All(10), All(20));
                    var pts = px.Zip(py, (x, y) => (x, y)).ToList();
                    if (pts.Count < 2) break;
                    bool closed = ((int)D(70) & 1) == 1;
                    if (closed && pts[0] != pts[^1]) pts.Add(pts[0]);
                    pieces.Add(new Contour(pts[0], [new Segment('C', pts.Skip(1).ToList())], closed));
                    break;
                }
                case "ELLIPSE":
                {
                    double cx = D(10), cy = D(20), mx = D(11), my = D(21), ratio = D(40, 1), t1 = D(41, 0), t2 = D(42, 2 * Math.PI);
                    if (t2 <= t1) t2 += 2 * Math.PI;
                    double nx = -my * ratio, ny = mx * ratio; // minor axis
                    var pts = Enumerable.Range(0, EllipseSamples + 1).Select(k => t1 + (t2 - t1) * k / EllipseSamples)
                        .Select(t => (cx + mx * Math.Cos(t) + nx * Math.Sin(t), cy + my * Math.Cos(t) + ny * Math.Sin(t))).ToList();
                    bool full = Math.Abs(t2 - t1 - 2 * Math.PI) < 1e-6;
                    pieces.Add(new Contour(pts[0], [new Segment('C', pts.Skip(1).ToList())], full));
                    break;
                }
            }
            i = end - 1;
        }
        return new Drawing(Chain(pieces), circles, unitMm);
    }

    private static Contour Polyline(List<(double X, double Y, double Bulge)> v, bool closed)
    {
        var segs = new List<Segment>();
        int n = closed ? v.Count : v.Count - 1;
        for (int k = 0; k < n; k++)
        {
            var a = v[k];
            var b = v[(k + 1) % v.Count];
            if (Math.Abs(a.Bulge) < 1e-9) { segs.Add(new Segment('L', [(b.X, b.Y)])); continue; }
            // Bulge = tan(angle/4); positive turns counter-clockwise, so the arc's middle lies right of the chord.
            double dx = b.X - a.X, dy = b.Y - a.Y, chord = Math.Sqrt(dx * dx + dy * dy);
            double sag = a.Bulge * chord / 2;
            var mid = ((a.X + b.X) / 2 + dy / chord * sag, (a.Y + b.Y) / 2 - dx / chord * sag);
            segs.Add(new Segment('A', [mid, (b.X, b.Y)]));
        }
        return new Contour((v[0].X, v[0].Y), segs, closed);
    }

    /// <summary>Joins open pieces whose ends meet (DXF lines and arcs drawn one by one) into longer, possibly closed, contours.</summary>
    public static List<Contour> Chain(List<Contour> pieces)
    {
        var all = pieces.SelectMany(p => p.AllPoints).ToList();
        double extent = all.Count == 0 ? 1 : Math.Max(all.Max(p => p.X) - all.Min(p => p.X), all.Max(p => p.Y) - all.Min(p => p.Y));
        double tol = Math.Max(extent * 1e-6, 1e-9);
        bool Near((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;

        var open = pieces.Where(p => !p.Closed).ToList();
        var done = pieces.Where(p => p.Closed).ToList();
        while (open.Count > 0)
        {
            var cur = open[0];
            open.RemoveAt(0);
            bool grew = true;
            while (grew && !(cur.Segments.Count > 0 && Near(cur.Start, cur.End)))
            {
                grew = false;
                for (int k = 0; k < open.Count; k++)
                {
                    var o = open[k];
                    Contour? joined = Near(cur.End, o.Start) ? Join(cur, o)
                        : Near(cur.End, o.End) ? Join(cur, Reverse(o))
                        : Near(cur.Start, o.End) ? Join(o, cur)
                        : Near(cur.Start, o.Start) ? Join(Reverse(o), cur) : null;
                    if (joined is null) continue;
                    cur = joined;
                    open.RemoveAt(k);
                    grew = true;
                    break;
                }
            }
            bool closed = cur.Segments.Count > 1 && Near(cur.Start, cur.End);
            if (closed) cur.Segments[^1].Points[^1] = cur.Start; // exact closure
            done.Add(cur with { Closed = closed });
        }
        return done;
    }

    private static Contour Join(Contour a, Contour b) => new(a.Start, [.. a.Segments, .. b.Segments], false);

    public static Contour Reverse(Contour c)
    {
        var nodes = c.Segments.Select(s => s.Points[^1]).Prepend(c.Start).ToList();
        var segs = new List<Segment>();
        for (int k = c.Segments.Count - 1; k >= 0; k--)
        {
            var s = c.Segments[k];
            var to = nodes[k];
            segs.Add(s.Kind switch
            {
                'C' => new Segment('C', s.Points.Take(s.Points.Count - 1).Reverse().Append(to).ToList()),
                'A' => new Segment('A', [s.Points[0], to]),
                _ => new Segment('L', [to]),
            });
        }
        return new Contour(nodes[^1], segs, c.Closed);
    }

    // ── SVG ───────────────────────────────────────────────────────────────────────────────────────────

    public static Drawing ParseSvg(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(text), settings)) doc = XDocument.Load(reader);
        var contours = new List<Contour>();
        var circles = new List<(double, double, double)>();
        foreach (var el in doc.Descendants())
        {
            if (el.Ancestors().Any(a => a.Name.LocalName is "defs" or "clipPath" or "mask" or "symbol")) continue;
            var m = el.AncestorsAndSelf().Reverse().Aggregate(Affine.Identity, (acc, e) => acc * Affine.Parse((string?)e.Attribute("transform")));
            double A(string n) => Num((string?)el.Attribute(n) ?? "0");
            switch (el.Name.LocalName)
            {
                case "path":
                    contours.AddRange(ParsePath((string?)el.Attribute("d") ?? "").Select(c => m.Apply(c)));
                    break;
                case "polygon" or "polyline":
                {
                    var nums = Numbers((string?)el.Attribute("points") ?? "");
                    var pts = Enumerable.Range(0, nums.Count / 2).Select(k => (nums[2 * k], nums[2 * k + 1])).ToList();
                    if (pts.Count < 2) break;
                    bool closed = el.Name.LocalName == "polygon";
                    var segs = pts.Skip(1).Select(p => new Segment('L', [p])).ToList();
                    if (closed && pts[0] != pts[^1]) segs.Add(new Segment('L', [pts[0]]));
                    contours.Add(m.Apply(new Contour(pts[0], segs, closed)));
                    break;
                }
                case "rect":
                {
                    double x = A("x"), y = A("y"), w = A("width"), h = A("height");
                    if (w <= 0 || h <= 0) break;
                    contours.Add(m.Apply(new Contour((x, y), [new Segment('L', [(x + w, y)]), new Segment('L', [(x + w, y + h)]), new Segment('L', [(x, y + h)]), new Segment('L', [(x, y)])], true)));
                    break;
                }
                case "line":
                    contours.Add(m.Apply(new Contour((A("x1"), A("y1")), [new Segment('L', [(A("x2"), A("y2"))])], false)));
                    break;
                case "circle" when m.IsSimilarity:
                {
                    var c = m.Apply((A("cx"), A("cy")));
                    circles.Add((c.X, c.Y, A("r") * m.Scale));
                    break;
                }
                case "circle" or "ellipse":
                {
                    double cx = A("cx"), cy = A("cy"), rx = el.Name.LocalName == "circle" ? A("r") : A("rx"), ry = el.Name.LocalName == "circle" ? A("r") : A("ry");
                    var pts = Enumerable.Range(0, EllipseSamples + 1).Select(k => 2 * Math.PI * k / EllipseSamples).Select(t => (cx + rx * Math.Cos(t), cy + ry * Math.Sin(t))).ToList();
                    contours.Add(m.Apply(new Contour(pts[0], [new Segment('C', pts.Skip(1).ToList())], true)));
                    break;
                }
            }
        }
        // SVG's y axis points down: flip so the sketch matches what the user sees.
        var flipped = contours.Select(c => Map(c, p => (p.X, -p.Y))).ToList();
        return new Drawing(Chain(flipped), circles.Select(c => (c.Item1, -c.Item2, c.Item3)).ToList(), SvgPxMm);
    }

    private static Contour Map(Contour c, Func<(double X, double Y), (double X, double Y)> f) =>
        new(f(c.Start), c.Segments.Select(s => new Segment(s.Kind, s.Points.Select(f).ToList())).ToList(), c.Closed);

    /// <summary>Path data → contours (one per subpath). Curves are sampled; consecutive curves merge into one smooth piece.</summary>
    public static List<Contour> ParsePath(string d)
    {
        var result = new List<Contour>();
        var tokens = PathToken().Matches(d).Select(t => t.Value).ToList();
        int i = 0;
        char cmd = 'M';
        (double X, double Y) cur = (0, 0), start = (0, 0), lastCtrl = (0, 0);
        char lastCmd = ' ';
        List<Segment>? segs = null;

        void Flush(bool closed)
        {
            if (segs is { Count: > 0 }) result.Add(new Contour(start, MergeCurves(segs), closed));
            segs = null;
        }
        double N() => Num(tokens[i++]);
        bool HasNum() => i < tokens.Count && !char.IsLetter(tokens[i][0]);

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
            else if (cmd is 'M') cmd = 'L';
            else if (cmd is 'm') cmd = 'l';
            bool rel = char.IsLower(cmd);
            (double X, double Y) P(double x, double y) => rel ? (cur.X + x, cur.Y + y) : (x, y);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Flush(false);
                    cur = P(N(), N());
                    start = cur;
                    segs = [];
                    break;
                case 'Z':
                    if (segs is not null && cur != start) segs.Add(new Segment('L', [start]));
                    cur = start;
                    Flush(true);
                    segs = [];
                    break;
                case 'L':
                    cur = P(N(), N());
                    (segs ??= []).Add(new Segment('L', [cur]));
                    break;
                case 'H':
                    cur = (rel ? cur.X + N() : N(), cur.Y);
                    (segs ??= []).Add(new Segment('L', [cur]));
                    break;
                case 'V':
                    cur = (cur.X, rel ? cur.Y + N() : N());
                    (segs ??= []).Add(new Segment('L', [cur]));
                    break;
                case 'C' or 'S':
                {
                    var c1 = char.ToUpperInvariant(cmd) == 'S'
                        ? (char.ToUpperInvariant(lastCmd) is 'C' or 'S' ? (2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur)
                        : P(N(), N());
                    var c2 = P(N(), N());
                    var to = P(N(), N());
                    (segs ??= []).Add(new Segment('C', Bezier(cur, c1, c2, to)));
                    lastCtrl = c2;
                    cur = to;
                    break;
                }
                case 'Q' or 'T':
                {
                    var q = char.ToUpperInvariant(cmd) == 'T'
                        ? (char.ToUpperInvariant(lastCmd) is 'Q' or 'T' ? (2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur)
                        : P(N(), N());
                    var to = P(N(), N());
                    var c1 = (cur.X + 2.0 / 3 * (q.Item1 - cur.X), cur.Y + 2.0 / 3 * (q.Item2 - cur.Y));
                    var c2 = (to.Item1 + 2.0 / 3 * (q.Item1 - to.Item1), to.Item2 + 2.0 / 3 * (q.Item2 - to.Item2));
                    (segs ??= []).Add(new Segment('C', Bezier(cur, c1, c2, to)));
                    lastCtrl = q;
                    cur = to;
                    break;
                }
                case 'A':
                {
                    double rx = N(), ry = N(), rot = N(), large = N(), sweep = N();
                    var to = P(N(), N());
                    (segs ??= []).Add(new Segment('C', SvgArc(cur, rx, ry, rot, large != 0, sweep != 0, to)));
                    cur = to;
                    break;
                }
                default:
                    i++; // unknown command: skip a token
                    break;
            }
            lastCmd = cmd;
            if (char.ToUpperInvariant(cmd) is 'M' or 'L' or 'H' or 'V' or 'C' or 'S' or 'Q' or 'T' or 'A' && !HasNum() && i < tokens.Count && !char.IsLetter(tokens[i][0])) i++;
        }
        Flush(false);
        return result;
    }

    private static List<Segment> MergeCurves(List<Segment> segs)
    {
        var merged = new List<Segment>();
        foreach (var s in segs)
        {
            if (s.Kind == 'C' && merged.Count > 0 && merged[^1].Kind == 'C') merged[^1].Points.AddRange(s.Points);
            else merged.Add(new Segment(s.Kind, [.. s.Points]));
        }
        return merged;
    }

    private static List<(double X, double Y)> Bezier((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3) =>
        Enumerable.Range(1, CurveSamples).Select(k => (double)k / CurveSamples).Select(t =>
        {
            double u = 1 - t;
            return (u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                    u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
        }).ToList();

    /// <summary>SVG elliptical arc (endpoint form, SVG spec F.6.5) sampled into points after the start.</summary>
    private static List<(double X, double Y)> SvgArc((double X, double Y) p0, double rx, double ry, double rotDeg, bool large, bool sweep, (double X, double Y) p1)
    {
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        if (rx < 1e-12 || ry < 1e-12) return [p1];
        double phi = rotDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1, den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
        double cx1 = coef * rx * y1 / ry, cy1 = -coef * ry * x1 / rx;
        double cx = cos * cx1 - sin * cy1 + (p0.X + p1.X) / 2, cy = sin * cx1 + cos * cy1 + (p0.Y + p1.Y) / 2;
        static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        double t1 = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        double dt = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI;
        else if (sweep && dt < 0) dt += 2 * Math.PI;
        int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 8)));
        var pts = Enumerable.Range(1, n).Select(k => t1 + dt * k / n)
            .Select(t => (cos * rx * Math.Cos(t) - sin * ry * Math.Sin(t) + cx, sin * rx * Math.Cos(t) + cos * ry * Math.Sin(t) + cy)).ToList();
        pts[^1] = p1;
        return pts;
    }

    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);
        public static Affine operator *(Affine m, Affine n) => new(
            m.A * n.A + m.C * n.B, m.B * n.A + m.D * n.B, m.A * n.C + m.C * n.D, m.B * n.C + m.D * n.D,
            m.A * n.E + m.C * n.F + m.E, m.B * n.E + m.D * n.F + m.F);
        public (double X, double Y) Apply((double X, double Y) p) => (A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
        public Contour Apply(Contour c) => Map(c, Apply);
        public bool IsSimilarity => Math.Abs(A - D) < 1e-9 && Math.Abs(B + C) < 1e-9;
        public double Scale => Math.Sqrt(A * D - B * C);

        public static Affine Parse(string? transform)
        {
            var m = Identity;
            if (string.IsNullOrWhiteSpace(transform)) return m;
            foreach (Match t in TransformPart().Matches(transform))
            {
                var v = Numbers(t.Groups[2].Value);
                double V(int k, double def = 0) => k < v.Count ? v[k] : def;
                m *= t.Groups[1].Value switch
                {
                    "matrix" when v.Count >= 6 => new Affine(v[0], v[1], v[2], v[3], v[4], v[5]),
                    "translate" => new Affine(1, 0, 0, 1, V(0), V(1)),
                    "scale" => new Affine(V(0, 1), 0, 0, V(1, V(0, 1)), 0, 0),
                    "rotate" => Rotate(V(0), V(1), V(2)),
                    _ => Identity,
                };
            }
            return m;
        }

        private static Affine Rotate(double deg, double cx, double cy)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            return new Affine(1, 0, 0, 1, cx, cy) * new Affine(c, s, -s, c, 0, 0) * new Affine(1, 0, 0, 1, -cx, -cy);
        }
    }

    private static double Num(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static List<double> Numbers(string s) => NumberToken().Matches(s).Select(m => Num(m.Value)).ToList();

    [GeneratedRegex(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")] private static partial Regex PathToken();
    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")] private static partial Regex NumberToken();
    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")] private static partial Regex TransformPart();
}
