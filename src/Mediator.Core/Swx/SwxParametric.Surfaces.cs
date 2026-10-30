using System.Text.Json.Nodes;

namespace Mediator.Core.Swx;

/// <summary>
/// Curves and surfacing for <see cref="SwxParametric"/>: splines and mixed outlines in sketches, named sketches and
/// offset planes to hang sections on, lofts and sweeps (solid or surface, with guide curves), revolves, surface
/// extrudes, knit, thicken, shell, chamfer, mirror and fit-to-box scaling. Nothing here is specific to one product:
/// a recipe composes these into any shape (a car body is section sketches on offset planes, lofted, mirrored, filleted).
/// Signatures follow the SolidWorks 2025 interop (IFeatureManager unless noted).
/// </summary>
public sealed partial class SwxParametric
{
    // SolidWorks enum values used below (swconst), named so the calls read as what they do.
    private const int SwRefPlaneDistance = 8, SwRefPlaneFlip = 256;              // swRefPlaneReferenceConstraints_e
    private const int SwSheetBody = 1, SwSolidBody = 0;                          // swBodyType_e
    private const int SwEndBlind = 0;                                            // swEndConditions_e
    private const int MarkProfile = 1, MarkGuide = 2, MarkPath = 4, MarkBody = 256; // selection marks loft/sweep/mirror/thicken expect
    private const int SwChamferAngleDistance = 1;                                // swChamferType_e
    private const int SwScaleAboutCentroid = 0;                                  // swScaleType_e
    private const double KnitToleranceM = 0.0001;

    private static readonly string[] StandardPlanes = ["Top", "Front", "Right"];
    private readonly Dictionary<string, string> _sketchNames = new(StringComparer.Ordinal); // recipe name → feature name
    private object Fm => G(_doc, "FeatureManager")!;

    // ── Selection helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>"Top" / "Front" / "Right" are the default planes; any other name is a plane made with the "plane" op.</summary>
    private static string PlaneId(string name) => StandardPlanes.Contains(name, StringComparer.Ordinal) ? name + " Plane" : name;

    private bool SelectMarked(string name, string type, int mark, bool append = true) =>
        (bool)C(Ext, "SelectByID2", name, type, 0.0, 0.0, 0.0, append, mark, null, 0)!;

    private string SketchRef(JsonNode? n, string role)
    {
        string recipeName = (string?)n ?? throw new ArgumentException($"{role} sketch name is required");
        if (_sketchNames.TryGetValue(recipeName, out var feature)) return feature;
        // V17: on an open part, a sketch already in the feature tree (e.g. made with the sketch tool) is used by its name.
        return C(_doc, "FeatureByName", recipeName) is not null ? recipeName
            : throw new ArgumentException($"unknown sketch '{recipeName}' for {role} (make it first with op \"sketch\" or the sketch tool)");
    }

    private void SelectBody(object body, int mark, bool append)
    {
        var data = C(G(_doc, "SelectionManager")!, "CreateSelectData")!;
        S(data, "Mark", mark);
        C(body, "Select2", append, data);
    }

    private object[] Bodies(int type) => Arr(C(_doc, "GetBodies2", type, true));

    /// <summary>The face or edge closest to a model point (metres); the point need not lie exactly on it.</summary>
    private static object? Nearest(IEnumerable<object> entities, double x, double y, double z)
    {
        object? best = null;
        double bestD = double.MaxValue;
        foreach (var e in entities)
        {
            var c = Arr(C(e, "GetClosestPointOn", x, y, z)).Select(Convert.ToDouble).ToArray();
            if (c.Length < 3) continue;
            double d = (c[0] - x) * (c[0] - x) + (c[1] - y) * (c[1] - y) + (c[2] - z) * (c[2] - z);
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    private int BodyCount() => Bodies(SwSolidBody).Length + Bodies(SwSheetBody).Length;

    /// <summary>
    /// Some feature calls create the feature but return null through late binding (surface extrude, sweep surface).
    /// The call counts as made when the body count changed; then the newest feature is the one it made.
    /// </summary>
    private object? Made(object? feature, int bodiesBefore) =>
        feature ?? (BodyCount() != bodiesBefore ? C(_doc, "FeatureByPositionReverse", 0) : null);

    /// <summary>Gives the newest feature the recipe's name when one is set (names must be unique in the part).</summary>
    private string Rename(object? feature, JsonNode? wanted)
    {
        feature ??= C(_doc, "FeatureByPositionReverse", 0);
        if (feature is null) throw new InvalidOperationException("feature not created");
        if ((string?)wanted is { Length: > 0 } name)
        {
            try { S(feature, "Name", name); } catch (Exception ex) when (ex is InvalidOperationException or System.Reflection.TargetInvocationException) { _log.Add($"could not name a feature '{name}'"); }
        }
        return (string)G(feature, "Name")!;
    }

    // ── Sketch shapes: curves ───────────────────────────────────────────────────────────────────────────

    private static double[] Xyz(IEnumerable<(double X, double Y)> pts) => pts.SelectMany(p => new[] { p.X * Mm, p.Y * Mm, 0.0 }).ToArray();

    private List<(double X, double Y)> Points(JsonNode? n, string what, int min) =>
        (n as JsonArray ?? throw new ArgumentException($"{what}: a list of [x, y] points is required")) is { Count: var c } a && c >= min
            ? a.Select(p => (Eval(p![0]), Eval(p[1]))).ToList()
            : throw new ArgumentException($"{what} needs at least {min} points");

    /// <summary>{spline: {points: [[x,y],...], closed}}: a smooth curve through the points (a closed one returns to the start).</summary>
    private object Spline(JsonObject s)
    {
        var pts = Points(s["points"], "spline", 2);
        if ((bool?)s["closed"] == true && pts[0] != pts[^1]) pts.Add(pts[0]);
        return C(Sm, "CreateSpline2", Xyz(pts), true) ?? throw new InvalidOperationException("spline not created");
    }

    /// <summary>{arc: [[x1,y1],[xMid,yMid],[x2,y2]]}: a circular arc through three points.</summary>
    private object Arc3(List<(double X, double Y)> p) =>
        C(Sm, "Create3PointArc", p[0].X * Mm, p[0].Y * Mm, 0.0, p[2].X * Mm, p[2].Y * Mm, 0.0, p[1].X * Mm, p[1].Y * Mm, 0.0)
        ?? throw new InvalidOperationException("arc not created");

    /// <summary>
    /// {outline: [[x,y], {line: [x,y]}, {spline: [[x,y],...]}, {arc: [[xMid,yMid],[x,y]]}, ...]}: one closed profile drawn
    /// as a chain from the first point; each segment ends where the next starts, and the last returns to the start
    /// (a straight closing line is added if needed). Mixes straight and curved edges: a flat floor under a curved roof.
    /// </summary>
    private void Outline(JsonArray chain)
    {
        if (chain.Count < 2 || chain[0] is not JsonArray first) throw new ArgumentException("outline: start with an [x, y] point, then segments");
        var start = (X: Eval(first[0]), Y: Eval(first[1]));
        var at = start;
        var segs = new List<object>();
        foreach (var node in chain.Skip(1))
        {
            switch (node)
            {
                case JsonArray p: // a bare point is a straight line to it
                case JsonObject o when o["line"] is JsonArray:
                {
                    var arr = node as JsonArray ?? (JsonArray)((JsonObject)node)["line"]!;
                    var to = (Eval(arr[0]), Eval(arr[1]));
                    segs.Add(C(Sm, "CreateLine", at.X * Mm, at.Y * Mm, 0.0, to.Item1 * Mm, to.Item2 * Mm, 0.0) ?? throw new InvalidOperationException("line not created"));
                    at = to;
                    break;
                }
                case JsonObject o when o["spline"] is JsonArray sp:
                {
                    var pts = new List<(double X, double Y)> { at };
                    pts.AddRange(Points(sp, "outline spline", 1));
                    segs.Add(C(Sm, "CreateSpline2", Xyz(pts), true) ?? throw new InvalidOperationException("spline not created"));
                    at = pts[^1];
                    break;
                }
                case JsonObject o when o["arc"] is JsonArray ar:
                {
                    var p = Points(ar, "outline arc (mid point, end point)", 2);
                    segs.Add(Arc3([at, p[0], p[1]]));
                    at = p[1];
                    break;
                }
                default: throw new ArgumentException("outline segment must be [x,y], {line}, {spline} or {arc}");
            }
        }
        if (Math.Abs(at.X - start.X) > 1e-9 || Math.Abs(at.Y - start.Y) > 1e-9)
            segs.Add(C(Sm, "CreateLine", at.X * Mm, at.Y * Mm, 0.0, start.X * Mm, start.Y * Mm, 0.0) ?? throw new InvalidOperationException("closing line not created"));
        // Make the chain one closed loop: join each segment's end to the next one's start.
        for (int i = 0; i < segs.Count; i++)
        {
            Clear();
            if (EndPoint(segs[i], end: true) is { } a && EndPoint(segs[(i + 1) % segs.Count], end: false) is { } b && !ReferenceEquals(a, b))
            {
                C(a, "Select4", false, null);
                C(b, "Select4", true, null);
                C(_doc, "SketchAddConstraints", "sgMERGEPOINTS");
            }
        }
    }

    private static object? EndPoint(object segment, bool end)
    {
        try { return C(segment, end ? "GetEndPoint2" : "GetStartPoint2"); }
        catch (InvalidOperationException) { return null; } // a closed spline has no free ends
    }

    // ── Reference geometry and named sketches ───────────────────────────────────────────────────────────

    /// <summary>{op: "plane", name, base: "Front" | plane name, offset}: a plane parallel to base (negative offset = other side).</summary>
    private void Plane(JsonObject f)
    {
        string name = (string?)f["name"] ?? throw new ArgumentException("plane needs a name");
        Clear();
        string baseName = (string?)f["base"] ?? "Front";
        if (!SelectId(PlaneId(baseName), "PLANE")) throw new InvalidOperationException($"could not select plane {baseName}");
        double offset = Eval(f["offset"]);
        int constraint = SwRefPlaneDistance | (offset < 0 ? SwRefPlaneFlip : 0);
        var plane = C(Fm, "InsertRefPlane", constraint, Math.Abs(offset) * Mm, 0, 0.0, 0, 0.0);
        string made = Rename(plane, name);
        _pending.Add(("D1", offset < 0 ? $"-({Expr(f["offset"])})" : Expr(f["offset"])));
        if (offset >= 0) Link(made); else _pending.Clear(); // a flipped plane's distance is stored positive
        _log.Add($"plane {made} at {Num(offset)} mm from {baseName}");
    }

    /// <summary>{op: "sketch", name, on, profile}: a sketch kept for a later loft, sweep, guide or path.</summary>
    private void NamedSketch(JsonObject f)
    {
        string name = (string?)f["name"] ?? throw new ArgumentException("sketch needs a name");
        Sketch(f);
        string feature = Rename(C(_doc, "FeatureByPositionReverse", 0), name);
        _sketchNames[name] = feature;
        Clear();
        _log.Add($"sketch {feature}");
    }

    /// <summary>
    /// {op: "curve3d", name, points: [[x,y,z],...] (model mm), closed?}: a 3D sketch spline through the points, kept by name for a
    /// sweep path or a loft/sweep guide.
    /// </summary>
    private void Curve3d(JsonObject f)
    {
        string name = (string?)f["name"] ?? throw new ArgumentException("curve3d needs a name");
        var pts = (f["points"] as JsonArray ?? throw new ArgumentException("curve3d needs points [[x,y,z],...]"))
            .Select(p => (Eval(p![0]), Eval(p[1]), Eval(p[2]))).ToList();
        if (pts.Count < 2) throw new ArgumentException("curve3d needs at least 2 points");
        if ((bool?)f["closed"] == true && pts[0] != pts[^1]) pts.Add(pts[0]);
        _pending.Clear();
        Clear();
        C(Sm, "Insert3DSketch", true);
        S(Sm, "AddToDB", true);
        try
        {
            _ = C(Sm, "CreateSpline2", pts.SelectMany(p => new[] { p.Item1 * Mm, p.Item2 * Mm, p.Item3 * Mm }).ToArray(), true)
                ?? throw new InvalidOperationException("3D spline not created");
        }
        finally { S(Sm, "AddToDB", false); }
        C(Sm, "Insert3DSketch", true); // close
        string feature = Rename(C(_doc, "FeatureByPositionReverse", 0), name);
        _sketchNames[name] = feature;
        Clear();
        _log.Add($"3D curve {feature} ({pts.Count} points)");
    }

    /// <summary>Selects the sketch a feature consumes: a named one ({sketch: "side"}) or a new one from on/profile.</summary>
    private void SketchFor(JsonObject f)
    {
        if (f["sketch"] is { } named)
        {
            Clear();
            if (!SelectId(SketchRef(named, "the feature"), "SKETCH")) throw new InvalidOperationException($"could not select sketch {named}");
        }
        else Sketch(f);
    }

    // ── Solid and surface features ──────────────────────────────────────────────────────────────────────

    /// <summary>{op: "loft", profiles: [sketch names, in order], guides?: [...], surface?, closed?}: blends section to section.</summary>
    private void Loft(JsonObject f)
    {
        var profiles = f["profiles"] as JsonArray ?? throw new ArgumentException("loft needs profiles (2+ sketch names)");
        if (profiles.Count < 2) throw new ArgumentException("loft needs at least 2 profiles");
        Clear();
        foreach (var p in profiles) if (!SelectMarked(SketchRef(p, "loft profile"), "SKETCH", MarkProfile)) throw new InvalidOperationException($"could not select profile {p}");
        foreach (var g in f["guides"] as JsonArray ?? []) if (!SelectMarked(SketchRef(g, "loft guide"), "SKETCH", MarkGuide)) throw new InvalidOperationException($"could not select guide {g}");
        bool closed = (bool?)f["closed"] ?? false;
        object? feat;
        if ((bool?)f["surface"] == true)
        {
            int before = BodyCount();
            C(_doc, "InsertLoftRefSurface2", closed, true, false, 1.0, (short)0, (short)0);
            feat = Made(null, before);
        }
        else
        {
            feat = C(Fm, "InsertProtrusionBlend2", closed, true, false, 1.0, (short)0, (short)0, 1.0, 1.0, false, false,
                false, 0.0, 0.0, (short)0, true, true, true, 0);
        }
        if (feat is null) throw new InvalidOperationException("loft failed (profiles crossing, or open profiles for a solid loft?)");
        _log.Add($"{Rename(feat, f["name"])}: loft through {profiles.Count} sections" + ((bool?)f["surface"] == true ? " (surface)" : ""));
    }

    /// <summary>{op: "sweep", profile, path, guides?, surface?}: moves a section along a path curve.</summary>
    private void Sweep(JsonObject f)
    {
        Clear();
        if (!SelectMarked(SketchRef(f["profile"], "sweep profile"), "SKETCH", MarkProfile)) throw new InvalidOperationException("could not select the sweep profile");
        if (!SelectMarked(SketchRef(f["path"], "sweep path"), "SKETCH", MarkPath)) throw new InvalidOperationException("could not select the sweep path");
        foreach (var g in f["guides"] as JsonArray ?? []) if (!SelectMarked(SketchRef(g, "sweep guide"), "SKETCH", MarkGuide)) throw new InvalidOperationException($"could not select guide {g}");
        int before = BodyCount();
        object? feat = Made((bool?)f["surface"] == true
            ? C(Fm, "InsertSweepSurface3", false, 0, false, false, 0, 0, 0, true, true, 0.0, true, false, 0.0, 0)
            : C(Fm, "InsertProtrusionSwept4", false, false, 0, false, false, 0, 0, false, 0.0, 0.0, 0, 0, true, true, true, 0.0, true, false, 0.0, 0), before);
        if (feat is null) throw new InvalidOperationException("sweep failed (path not touching the profile plane, or self-intersecting?)");
        _log.Add($"{Rename(feat, f["name"])}: sweep" + ((bool?)f["surface"] == true ? " (surface)" : ""));
    }

    /// <summary>{op: "revolve", on/profile with a {centerline} (or sketch), angle (deg, default 360), surface?, cut?}.</summary>
    private void Revolve(JsonObject f)
    {
        SketchFor(f);
        double angle = f["angle"] is null ? 360 : Eval(f["angle"]);
        bool surface = (bool?)f["surface"] == true, cut = (bool?)f["cut"] == true;
        var feat = C(Fm, "FeatureRevolve2", true, !surface, false, cut, (bool?)f["reverse"] ?? false, false, SwEndBlind, SwEndBlind,
            angle * Math.PI / 180, 0.0, false, false, 0.0, 0.0, 0, 0.0, 0.0, true, true, true)
            ?? throw new InvalidOperationException("revolve failed (needs one centerline in the sketch, profile on one side of it)");
        string name = Rename(feat, f["name"]);
        if (cut) _cuts.Add(name);
        _log.Add($"{name}: revolve {Num(angle)}°" + (surface ? " (surface)" : cut ? " (cut)" : ""));
    }

    /// <summary>{op: "extrude", surface: true, ...}: an extruded surface (open sketches allowed, e.g. a side-profile curve).</summary>
    private void SurfaceExtrude(JsonObject f)
    {
        SketchFor(f);
        bool mid = (bool?)f["midPlane"] ?? false;
        double depth = Eval(f["depth"]);
        int before = BodyCount();
        var feat = Made(C(Fm, "FeatureExtruRefSurface3", true, (bool?)f["reverse"] ?? false, 0, 0.0, mid ? 6 : SwEndBlind, SwEndBlind,
            depth * Mm, depth * Mm, false, false, false, false, 0.0, 0.0, false, false, false, false, false, false, false, false), before)
            ?? throw new InvalidOperationException("surface extrude failed");
        string name = Rename(feat, f["name"]);
        _pending.Add(("D1", Expr(f["depth"])));
        Link(name);
        _log.Add($"{name}: surface extrude {Num(depth)} mm");
    }

    /// <summary>{op: "knit", solid?: true}: joins every surface body; a closed set becomes a solid.</summary>
    private void Knit(JsonObject f)
    {
        var sheets = Bodies(SwSheetBody);
        if (sheets.Length == 0) throw new InvalidOperationException("knit: there are no surface bodies");
        Clear();
        for (int i = 0; i < sheets.Length; i++) SelectBody(sheets[i], MarkProfile, append: i > 0);
        var feat = C(Fm, "InsertSewRefSurface", false, (bool?)f["solid"] ?? true, true, KnitToleranceM, 0.0)
            ?? throw new InvalidOperationException("knit failed (surfaces do not meet edge to edge?)");
        _log.Add($"{Rename(feat, f["name"])}: knit {sheets.Length} surfaces");
    }

    /// <summary>{op: "thicken", thickness, side?: "out" | "in" | "both"}: gives the (first) surface body a wall thickness.</summary>
    private void Thicken(JsonObject f)
    {
        var sheets = Bodies(SwSheetBody);
        if (sheets.Length == 0) throw new InvalidOperationException("thicken: there is no surface body");
        Clear();
        // FeatureBossThicken only takes the surface body selected with mark 1 (unmarked, or a face, returns null).
        string body = (string)G(sheets[0], "Name")!;
        if (!SelectMarked(body, "SURFACEBODY", MarkProfile, append: false)) throw new InvalidOperationException($"thicken: could not select surface {body}");
        int dir = ((string?)f["side"]) switch { "in" => 1, "both" => 2, _ => 0 };
        int before = BodyCount();
        var feat = Made(C(Fm, "FeatureBossThicken", Eval(f["thickness"]) * Mm, dir, 0, false, true, true, true), before)
            ?? throw new InvalidOperationException("thicken failed (thickness larger than the surface curvature allows?)");
        _log.Add($"{Rename(feat, f["name"])}: thicken {Num(Eval(f["thickness"]))} mm");
    }

    /// <summary>{op: "shell", thickness, openAt?: [x,y,z] (a face to remove)}: hollows the solid.</summary>
    private void Shell(JsonObject f)
    {
        Clear();
        if (f["openAt"] is JsonArray p)
        {
            var faces = Bodies(SwSolidBody).SelectMany(b => Arr(C(b, "GetFaces")));
            var face = Nearest(faces, Eval(p[0]) * Mm, Eval(p[1]) * Mm, Eval(p[2]) * Mm)
                ?? throw new InvalidOperationException($"shell: no face near {p.ToJsonString()}");
            C(face, "Select4", false, null);
        }
        C(_doc, "InsertFeatureShell", Eval(f["thickness"]) * Mm, (bool?)f["outward"] ?? false);
        _log.Add($"{Rename(null, f["name"])}: shell {Num(Eval(f["thickness"]))} mm");
    }

    /// <summary>{op: "chamfer", distance, angle? (deg, 45), edgesNear?}: bevels the edges (all outside edges by default).</summary>
    private void Chamfer(JsonObject f)
    {
        int count = SelectEdges(f, Eval(f["distance"]), out _);
        double angle = (f["angle"] is null ? 45 : Eval(f["angle"])) * Math.PI / 180;
        var feat = C(Fm, "InsertFeatureChamfer", 0, SwChamferAngleDistance, Eval(f["distance"]) * Mm, angle, 0.0, 0.0, 0.0, 0.0)
            ?? throw new InvalidOperationException("chamfer failed");
        _log.Add($"{Rename(feat, f["name"])}: chamfer {Num(Eval(f["distance"]))} mm on {count} edges");
    }

    /// <summary>{op: "mirror", plane: "Right"}: mirrors the solid about a plane and merges it (build half, mirror the rest).</summary>
    private void Mirror(JsonObject f)
    {
        var solids = Bodies(SwSolidBody);
        if (solids.Length == 0) throw new InvalidOperationException("mirror: there is no solid body");
        Clear();
        string plane = PlaneId((string?)f["plane"] ?? "Right");
        if (!SelectMarked(plane, "PLANE", MarkGuide, append: false)) throw new InvalidOperationException($"could not select plane {plane}");
        foreach (var b in solids) SelectBody(b, MarkBody, append: true);
        var feat = C(Fm, "InsertMirrorFeature2", true, false, true, false, 0)
            ?? throw new InvalidOperationException("mirror failed (the body does not touch the mirror plane, so it cannot merge?)");
        _log.Add($"{Rename(feat, f["name"])}: mirror about {plane}");
    }

    /// <summary>
    /// Selects the edges a fillet or chamfer acts on: those nearest the given model points (edgesNear: [[x,y,z],...]),
    /// else every outside edge (hole walls and walls thinner than 2×size skipped). Returns how many were selected.
    /// </summary>
    private int SelectEdges(JsonObject f, double size, out HashSet<string> thin)
    {
        thin = new HashSet<string>(StringComparer.Ordinal);
        Clear();
        var solids = Bodies(SwSolidBody);
        if (f["edgesNear"] is JsonArray near)
        {
            // The edge closest to each point (a point need not lie exactly on the edge, which SelectByID2 would require).
            var edges = solids.SelectMany(b => Arr(C(b, "GetEdges"))).ToList();
            var picked = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var p in near)
            {
                if (p is not JsonArray xyz || xyz.Count < 3) { _log.Add($"edgesNear: {p?.ToJsonString()} is not [x, y, z]"); continue; }
                var best = Nearest(edges, Eval(xyz[0]) * Mm, Eval(xyz[1]) * Mm, Eval(xyz[2]) * Mm);
                if (best is null) _log.Add($"no edge near {xyz.ToJsonString()}");
                else if (picked.Add(best)) C(best, "Select4", true, null);
            }
            return picked.Count;
        }
        if (solids.Length == 0) throw new InvalidOperationException("there is no solid body to round");
        int sel = 0;
        foreach (var edge in Arr(C(solids[0], "GetEdges")))
        {
            bool skip = false;
            foreach (var face in Arr(C(edge, "GetTwoAdjacentFaces2")))
            {
                if (face is null || C(face, "GetFeature") is not { } feat) continue;
                string fname = (string)G(feat, "Name")!;
                if (_cuts.Contains(fname)) skip = true;
                if (_extrudeDepth.TryGetValue(fname, out double t) && t <= 2 * size + 1e-6) { skip = true; thin.Add(fname); }
            }
            // Each edge is selected on its own (an object[] of COM objects is not marshalled as SolidWorks expects).
            if (!skip && C(edge, "Select4", true, null) is true) sel++;
        }
        return sel;
    }

    /// <summary>fitBoxMm: [x, y, z]: scales the finished part uniformly (about its centroid) so it fits inside the box.</summary>
    private void FitToBox(JsonArray box)
    {
        var solids = Bodies(SwSolidBody);
        var all = solids.Length > 0 ? solids : Bodies(SwSheetBody);
        if (all.Length == 0) { _log.Add("fitBoxMm: nothing to scale"); return; }
        var b = Arr(C(all[0], "GetBodyBox")).Select(Convert.ToDouble).ToArray();
        double[] size = [(b[3] - b[0]) / Mm, (b[4] - b[1]) / Mm, (b[5] - b[2]) / Mm];
        double[] limit = [Eval(box[0]), Eval(box[1]), Eval(box[2])];
        double factor = Enumerable.Range(0, 3).Min(i => size[i] > 1e-9 ? limit[i] / size[i] : double.MaxValue);
        if (factor >= 0.9999 && factor <= 1.0001) return;
        if (factor > 1) { _log.Add($"fits the {Num(limit[0])}×{Num(limit[1])}×{Num(limit[2])} mm box already (not enlarged)"); return; }
        var feat = C(Fm, "InsertScale", (short)SwScaleAboutCentroid, true, factor, factor, factor)
            ?? throw new InvalidOperationException("scale to the box failed");
        _log.Add($"{Rename(feat, null)}: scaled ×{factor:0.####} to fit {Num(limit[0])}×{Num(limit[1])}×{Num(limit[2])} mm");
    }
}
