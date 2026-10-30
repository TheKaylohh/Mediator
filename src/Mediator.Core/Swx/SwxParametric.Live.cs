using System.Globalization;
using System.Text.Json.Nodes;

namespace Mediator.Core.Swx;

/// <summary>
/// V17: SolidWorks operations on the document open on screen (or one given by path), as a user would do them by hand:
/// one sketch with constraints and dimensions (<see cref="SketchOn"/>), features added to an existing part
/// (<see cref="AddFeatures"/>: the recipe ops plus standard holes, linear and circular patterns and delete), assembly work
/// (<see cref="AssemblyOps"/>: insert, mate, fix/float, move, replace, delete, copies) and drawing annotation
/// (<see cref="Annotate"/>: projected, section and detail views, model dimensions, notes, BOM table, balloons).
/// Each operation is tried on its own; lengths mm, angles degrees.
/// </summary>
public sealed partial class SwxParametric
{
    public static JsonObject SketchOn(string? path, JsonObject spec, bool save, TimeSpan timeout) => Run(p => p.DoSketchOn(path, spec, save), timeout);
    public static JsonObject AddFeatures(string? path, JsonArray ops, bool save, TimeSpan timeout) => Run(p => p.DoAddFeatures(path, ops, save), timeout);
    public static JsonObject AssemblyOps(string? path, JsonArray ops, bool save, TimeSpan timeout) => Run(p => p.DoAssemblyOps(path, ops, save), timeout);
    public static JsonObject Annotate(string? path, JsonArray ops, bool save, TimeSpan timeout) => Run(p => p.DoAnnotate(path, ops, save), timeout);

    /// <summary>Opens (or takes) the target document, checks its kind and brings it to the front.</summary>
    private object Live(string? path, int kind, string what)
    {
        Connect();
        var (doc, _) = Target(path);
        _doc = doc;
        if (C(doc, "GetType") is not int t || t != kind) throw new ArgumentException($"{what} works on {TypeName(kind)}s; the document is a {TypeName(C(doc, "GetType"))}");
        Activate(doc);
        return doc;
    }

    /// <summary>Saves (or explains it did not) and adds the rebuild state; the common end of every live call.</summary>
    private JsonObject FinishLive(JsonObject report, bool save)
    {
        try { C(_doc, "ForceRebuild3", false); } catch (Exception ex) when (Recoverable(ex)) { _log.Add("rebuild: " + Message(ex)); }
        try { report["rebuildErrors"] = RebuildErrors(_doc); } catch (Exception ex) when (Recoverable(ex)) { }
        try { if (C(_doc, "GetType") is not SwDocDrawing) report["sizeMm"] = Size(_doc); } catch (Exception ex) when (Recoverable(ex)) { }
        string title = (string)C(_doc, "GetTitle")!;
        if (save)
        {
            if (C(_doc, "GetPathName") is not string { Length: > 0 } file) throw new ArgumentException($"{title} was never saved; save it with document_ops save_as first");
            SaveDoc(_doc, title);
            report["saved"] = file;
        }
        else
        {
            report["saved"] = false;
            report["note"] = "Not saved: review it in SolidWorks (Ctrl+Z undoes), or save with document_ops.";
        }
        if (_log.Count > 0) report["log"] = Log();
        return report;
    }

    private JsonArray RunEach(JsonArray ops, Func<JsonObject, string> apply)
    {
        var results = new JsonArray();
        foreach (var node in ops)
        {
            var op = node!.AsObject();
            try { results.Add(new JsonObject { ["op"] = (string?)op["op"], ["ok"] = true, ["detail"] = apply(op) }); }
            catch (Exception ex) when (Recoverable(ex))
            {
                results.Add(new JsonObject { ["op"] = (string?)op["op"], ["ok"] = false, ["error"] = Message(ex) });
                try { if (G(Sm, "ActiveSketch") is not null) C(Sm, "InsertSketch", true); } catch (Exception closeEx) when (Recoverable(closeEx)) { }
            }
        }
        return results;
    }

    // ── Sketch ───────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> PlaneAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = "Front", ["xy"] = "Front", ["top"] = "Top", ["xz"] = "Top", ["right"] = "Right", ["yz"] = "Right",
    };

    private static readonly Dictionary<string, string> Relations = new(StringComparer.Ordinal)
    {
        ["horizontal"] = "sgHORIZONTAL2D", ["vertical"] = "sgVERTICAL2D", ["tangent"] = "sgTANGENT", ["parallel"] = "sgPARALLEL",
        ["perpendicular"] = "sgPERPENDICULAR", ["equal"] = "sgSAMELENGTH", ["concentric"] = "sgCONCENTRIC", ["collinear"] = "sgCOLINEAR", ["fix"] = "sgFIXED",
    };

    private const int SwFullyConstrained = 3; // swConstrainedStatus_e

    private void SelectSketchPlane(JsonNode? on)
    {
        Clear();
        bool ok = on is JsonArray p
            ? SelectId("", "FACE", Eval(p[0]) * Mm, Eval(p[1]) * Mm, Eval(p[2]) * Mm)
            : SelectId(PlaneId(PlaneAliases.TryGetValue(((string?)on ?? "").Replace(" Plane", "", StringComparison.OrdinalIgnoreCase), out var n) ? n : (string?)on ?? ""), "PLANE");
        if (!ok) throw new InvalidOperationException($"could not select the sketch plane/face {on?.ToJsonString()}");
    }

    private object Draw(JsonObject e)
    {
        (double X, double Y) P(JsonNode? n) => (Eval(n![0]), Eval(n[1]));
        switch ((string?)e["type"])
        {
            case "line": { var (a, b) = (P(e["a"]), P(e["b"])); return C(Sm, "CreateLine", a.X * Mm, a.Y * Mm, 0.0, b.X * Mm, b.Y * Mm, 0.0)!; }
            case "centerline": { var (a, b) = (P(e["a"]), P(e["b"])); return C(Sm, "CreateCenterLine", a.X * Mm, a.Y * Mm, 0.0, b.X * Mm, b.Y * Mm, 0.0)!; }
            case "circle": { double x = Eval(e["cx"]), y = Eval(e["cy"]), d = Eval(e["d"]); return C(Sm, "CreateCircleByRadius", x * Mm, y * Mm, 0.0, d / 2 * Mm)!; }
            case "arc": return Arc3([P(e["a"]), P(e["mid"]), P(e["b"])]);
            case "point": { var a = P(e["at"]); return C(Sm, "CreatePoint", a.X * Mm, a.Y * Mm, 0.0)!; }
            case "spline": return Spline(e);
            case "rect":
            {
                double x = Eval(e["cx"]), y = Eval(e["cy"]), w = Eval(e["w"]), h = Eval(e["h"]);
                var segs = Arr(C(Sm, "CreateCenterRectangle", x * Mm, y * Mm, 0.0, (x + w / 2) * Mm, (y + h / 2) * Mm, 0.0));
                return segs.FirstOrDefault(s => G(s, "ConstructionGeometry") is false) ?? throw new InvalidOperationException("rectangle not created");
            }
            case "polygon":
            {
                var pts = (e["points"] as JsonArray ?? throw new ArgumentException("polygon needs points")).Select(P).ToList();
                var lines = new List<object>();
                for (int i = 0; i < pts.Count; i++)
                    lines.Add(C(Sm, "CreateLine", pts[i].X * Mm, pts[i].Y * Mm, 0.0, pts[(i + 1) % pts.Count].X * Mm, pts[(i + 1) % pts.Count].Y * Mm, 0.0)!);
                for (int i = 0; i < lines.Count; i++)
                {
                    Clear();
                    C(C(lines[i], "GetEndPoint2")!, "Select4", false, null);
                    C(C(lines[(i + 1) % lines.Count], "GetStartPoint2")!, "Select4", true, null);
                    C(_doc, "SketchAddConstraints", "sgMERGEPOINTS");
                }
                return lines[0];
            }
            case "slot":
            {
                var (a, b) = (P(e["a"]), P(e["b"]));
                double w = Eval(e["width"]) / 2, len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                double nx = -(b.Y - a.Y) / len * w, ny = (b.X - a.X) / len * w;
                var l1 = C(Sm, "CreateLine", (a.X + nx) * Mm, (a.Y + ny) * Mm, 0.0, (b.X + nx) * Mm, (b.Y + ny) * Mm, 0.0)!;
                C(Sm, "CreateLine", (b.X - nx) * Mm, (b.Y - ny) * Mm, 0.0, (a.X - nx) * Mm, (a.Y - ny) * Mm, 0.0);
                C(Sm, "CreateArc", b.X * Mm, b.Y * Mm, 0.0, (b.X + nx) * Mm, (b.Y + ny) * Mm, 0.0, (b.X - nx) * Mm, (b.Y - ny) * Mm, 0.0, (short)-1);
                C(Sm, "CreateArc", a.X * Mm, a.Y * Mm, 0.0, (a.X - nx) * Mm, (a.Y - ny) * Mm, 0.0, (a.X + nx) * Mm, (a.Y + ny) * Mm, 0.0, (short)-1);
                return l1;
            }
            default: throw new ArgumentException($"unknown entity type {(string?)e["type"]}");
        }
    }

    private JsonObject DoSketchOn(string? path, JsonObject spec, bool save)
    {
        Live(path, SwDocPart, "sketch");
        SelectSketchPlane(spec["on"]);
        C(Sm, "InsertSketch", true);
        var made = new List<object>();
        var problems = new JsonArray();
        S(Sm, "AddToDB", true);
        try
        {
            foreach (var e in spec["entities"] as JsonArray ?? []) made.Add(Draw(e!.AsObject()));
        }
        finally { S(Sm, "AddToDB", false); }
        object Ent(JsonNode? i) => made[(int)i!];
        foreach (var c in (spec["constraints"] as JsonArray ?? []).OfType<JsonObject>())
        {
            try
            {
                var ids = c["entities"] as JsonArray ?? throw new ArgumentException("constraint needs entities");
                string type = (string?)c["type"] ?? "";
                Clear();
                if (type == "coincident")
                {
                    C(C(Ent(ids[0]), "GetEndPoint2")!, "Select4", false, null);
                    C(C(Ent(ids[1]), "GetStartPoint2")!, "Select4", true, null);
                    C(_doc, "SketchAddConstraints", "sgMERGEPOINTS");
                    continue;
                }
                if (!Relations.TryGetValue(type, out var rel)) throw new ArgumentException($"unknown constraint {type}");
                bool first = true;
                foreach (var i in ids) { C(Ent(i), "Select4", !first, null); first = false; }
                C(_doc, "SketchAddConstraints", rel);
            }
            catch (Exception ex) when (Recoverable(ex)) { problems.Add($"constraint {c.ToJsonString()}: {Message(ex)}"); }
        }
        foreach (var d in (spec["dimensions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            try
            {
                Clear();
                var targets = d["entity"] is { } one ? [Ent(one)] : (d["entities"] as JsonArray ?? throw new ArgumentException("dimension needs entity or entities")).Select(Ent).ToList();
                for (int i = 0; i < targets.Count; i++) C(targets[i], "Select4", i > 0, null);
                var display = C(_doc, "AddDimension2", 0.01, 0.01, 0.0) ?? throw new InvalidOperationException("SolidWorks refused the dimension (over-defined?)");
                double v = Eval(d["value"]);
                double si = (string?)d["type"] == "angle" ? v * Math.PI / 180 : v * Mm;
                S(C(display, "GetDimension2", 0)!, "SystemValue", si);
            }
            catch (Exception ex) when (Recoverable(ex)) { problems.Add($"dimension {d.ToJsonString()}: {Message(ex)}"); }
        }
        C(Sm, "InsertSketch", true);
        var feature = C(_doc, "FeatureByPositionReverse", 0)!;
        string name = Rename(feature, spec["name"]);
        int status = 0;
        try { status = Convert.ToInt32(C(C(feature, "GetSpecificFeature2")!, "GetConstrainedStatus"), CultureInfo.InvariantCulture); } catch (Exception ex) when (Recoverable(ex)) { }
        var report = new JsonObject { ["sketch"] = name, ["entities"] = made.Count, ["fullyDefined"] = status == SwFullyConstrained };
        if (problems.Count > 0) report["problems"] = problems;
        return FinishLive(report, save);
    }

    // ── Features on an existing part ─────────────────────────────────────────────────────────────────

    private JsonObject DoAddFeatures(string? path, JsonArray ops, bool save)
    {
        Live(path, SwDocPart, "model_features");
        // Expressions may use the part's global variables (and new dimensions are linked to them).
        var eqm = Eqm;
        int count = Convert.ToInt32(C(eqm, "GetCount"), CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
            if (GlobalRegex().Match(G(eqm, "Equation", i) as string ?? "") is { Success: true } m)
                _g[m.Groups[1].Value] = Convert.ToDouble(G(eqm, "Value", i), CultureInfo.InvariantCulture);
        var results = RunEach(ops, f =>
        {
            int before = _log.Count;
            RunFeature(f);
            return _log.Count > before ? string.Join("; ", _log.Skip(before)) : (string?)f["op"] ?? "";
        });
        C(eqm, "EvaluateAll");
        return FinishLive(new JsonObject { ["results"] = results }, save);
    }

    /// <summary>ISO metric sizes: tap drill, clearance (ISO 273 medium), counterbore diameter and depth (ISO 4762 cap screws), mm.</summary>
    private static readonly Dictionary<string, (double Tap, double Clear, double Cbore, double CboreDepth)> MetricHoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["M2"] = (1.6, 2.4, 4.4, 2.2), ["M2.5"] = (2.05, 2.9, 5.5, 2.7), ["M3"] = (2.5, 3.4, 6.5, 3.3), ["M4"] = (3.3, 4.5, 8.0, 4.4),
        ["M5"] = (4.2, 5.5, 9.5, 5.4), ["M6"] = (5.0, 6.6, 11.0, 6.4), ["M8"] = (6.8, 9.0, 14.5, 8.6), ["M10"] = (8.5, 11.0, 17.5, 10.8),
        ["M12"] = (10.2, 13.5, 20.0, 13.0), ["M14"] = (12.0, 15.5, 24.0, 15.0), ["M16"] = (14.0, 17.5, 26.0, 17.5), ["M20"] = (17.5, 22.0, 33.0, 21.5),
        ["M24"] = (21.0, 26.0, 40.0, 25.5),
    };

    /// <summary>
    /// {op:"hole", on, points, size, type, depth}: standard holes as cuts with the standard's diameters (tap drill for tapped,
    /// ISO 273 medium clearance, ISO 4762 counterbores; a countersink is a clearance hole plus a 90° chamfer at the top).
    /// </summary>
    private void Hole(JsonObject f)
    {
        string size = ((string?)f["size"] ?? "").ToUpperInvariant();
        if (!MetricHoles.TryGetValue(size, out var std)) throw new ArgumentException($"unknown size {size} (M2 to M24)");
        string type = (string?)f["type"] ?? "clearance";
        var points = f["points"] as JsonArray ?? throw new ArgumentException("hole needs points");
        JsonObject CutAt(double d, JsonNode? depth) => new()
        {
            ["op"] = "cut", ["on"] = f["on"]?.DeepClone(), ["depth"] = depth?.DeepClone() ?? "through_all", ["equal"] = true,
            ["profile"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject { ["circle"] = new JsonObject { ["cx"] = p![0]!.DeepClone(), ["cy"] = p[1]!.DeepClone(), ["d"] = d } }).ToArray()),
        };
        double main = type == "tapped" ? std.Tap : std.Clear;
        var cut = CutAt(main, f["depth"]);
        SketchFor(cut);
        Extrude(cut, cut: true);
        if (type == "counterbore")
        {
            var bore = CutAt(std.Cbore, std.CboreDepth);
            SketchFor(bore);
            Extrude(bore, cut: true);
        }
        _log.Add($"{points.Count} × {size} {type} hole(s), Ø{main}" + (type == "tapped" ? " tap drill (add cosmetic threads in SolidWorks if the drawing needs them)" : "") +
            (type == "countersink" ? "; countersink: add a 90° chamfer on the hole edges (chamfer with edgesNear)" : ""));
    }

    /// <summary>A reference axis along X, Y or Z (two default planes intersecting), made once per part and reused by name.</summary>
    private string EnsureAxis(string axis)
    {
        string a = axis.ToUpperInvariant(), name = $"Axis {a} (Mediator)";
        if (C(_doc, "FeatureByName", name) is not null) return name;
        (string P1, string P2) planes = a switch
        {
            "X" => ("Top", "Front"), "Y" => ("Front", "Right"), "Z" => ("Top", "Right"),
            _ => throw new ArgumentException("axis/direction must be x, y or z"),
        };
        Clear();
        if (!SelectId(PlaneId(planes.P1), "PLANE") || !SelectId(PlaneId(planes.P2), "PLANE", append: true)) throw new InvalidOperationException("could not select the default planes");
        C(_doc, "InsertAxis2", true);
        S(C(_doc, "FeatureByPositionReverse", 0)!, "Name", name);
        Clear();
        return name;
    }

    private void SelectFeatures(JsonNode? names, int mark)
    {
        foreach (var n in names as JsonArray ?? throw new ArgumentException("features: a list of feature names is required"))
            if (!SelectMarked((string?)n ?? "", "BODYFEATURE", mark)) throw new ArgumentException($"no feature named {n}");
    }

    private void LinearPattern(JsonObject f)
    {
        string dir1 = EnsureAxis((string?)f["direction"] ?? "x");
        string? dir2 = (string?)f["direction2"] is { } d2 ? EnsureAxis(d2) : null;
        Clear();
        SelectMarked(dir1, "AXIS", 1, append: false);
        if (dir2 is not null) SelectMarked(dir2, "AXIS", 2);
        SelectFeatures(f["features"], 4);
        int n1 = (int)Eval(f["count"]), n2 = f["count2"] is null ? 1 : (int)Eval(f["count2"]);
        var feat = C(Fm, "FeatureLinearPattern4", n1, Eval(f["spacing"]) * Mm, n2, f["spacing2"] is null ? 0.01 : Eval(f["spacing2"]) * Mm,
            false, false, "", "", false, false) ?? throw new InvalidOperationException("linear pattern failed (count ≥ 2? instances outside the part?)");
        _log.Add($"{Rename(feat, f["name"])}: {n1}" + (n2 > 1 ? $" × {n2}" : "") + " instances");
    }

    private void CircularPattern(JsonObject f)
    {
        string axis = EnsureAxis((string?)f["axis"] ?? "y");
        Clear();
        SelectMarked(axis, "AXIS", 1, append: false);
        SelectFeatures(f["features"], 4);
        int n = (int)Eval(f["count"]);
        double angle = f["angle"] is null ? 360 : Eval(f["angle"]);
        var feat = C(Fm, "FeatureCircularPattern4", n, angle * Math.PI / 180, false, "", false, true, false)
            ?? throw new InvalidOperationException("circular pattern failed");
        _log.Add($"{Rename(feat, f["name"])}: {n} instances over {Num(angle)}°");
    }

    private void DeleteFeature(string name)
    {
        Clear();
        if (!SelectId(name, "BODYFEATURE") && !SelectId(name, "SKETCH") && !SelectId(name, "PLANE")) throw new ArgumentException($"no feature named {name}");
        if (C(Ext, "DeleteSelection2", 1) is not true) throw new InvalidOperationException($"SolidWorks did not delete {name}");
        _log.Add("deleted " + name);
    }

    // ── Assemblies ───────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, int> MateTypes = new(StringComparer.Ordinal)
    {
        ["coincident"] = 0, ["concentric"] = 1, ["perpendicular"] = 2, ["parallel"] = 3, ["tangent"] = 4, ["distance"] = 5, ["angle"] = 6,
    };

    private object Component(string name) => C(_doc, "GetComponentByName", name) ?? throw new ArgumentException($"no component named {name} (inspect_model components lists them)");

    private void SelectComponent(string name, bool append = false)
    {
        if (!append) Clear();
        if (C(Component(name), "Select4", append, null) is not true) throw new InvalidOperationException($"could not select {name}");
    }

    private void SelectMateEntity(JsonObject r, bool append)
    {
        if (r["near"] is JsonArray p)
        {
            if (!(bool)C(Ext, "SelectByID2", "", "FACE", Eval(p[0]) * Mm, Eval(p[1]) * Mm, Eval(p[2]) * Mm, append, 1, null, 0)!)
                throw new InvalidOperationException($"no face at {p.ToJsonString()} (the point must lie on the face, in assembly coordinates, mm)");
            return;
        }
        string comp = (string?)r["component"] ?? throw new ArgumentException("a mate entity needs component + plane, or near");
        string plane = PlaneAliases.TryGetValue((string?)r["plane"] ?? "", out var pl) ? pl : throw new ArgumentException("plane must be front, top or right");
        string asm = Path.GetFileNameWithoutExtension((string)C(_doc, "GetTitle")!);
        if (!(bool)C(Ext, "SelectByID2", $"{plane} Plane@{comp}@{asm}", "PLANE", 0.0, 0.0, 0.0, append, 1, null, 0)!)
            throw new InvalidOperationException($"could not select the {plane} plane of {comp}");
    }

    private string AssemblyOp(JsonObject op)
    {
        switch ((string?)op["op"])
        {
            case "insert":
            {
                string file = (string)op["path"]!;
                var asm = _doc;
                OpenOrGet(file); // AddComponent5 needs the part loaded
                _doc = asm;
                Activate(asm);
                var pos = op["position"] as JsonArray;
                double X(int i) => pos is { Count: 3 } ? Eval(pos[i]) * Mm : 0.0;
                var comp = C(_doc, "AddComponent5", file, 0, "", op["configuration"] is not null, (string?)op["configuration"] ?? "", X(0), X(1), X(2))
                    ?? throw new InvalidOperationException($"could not insert {Path.GetFileName(file)}");
                if ((string?)op["name"] is { Length: > 0 } wanted) try { S(comp, "Name2", wanted); } catch (Exception ex) when (Recoverable(ex)) { _log.Add($"could not rename it {wanted}"); }
                string name = (string)G(comp, "Name2")!;
                if ((bool?)op["fixed"] == true) { Clear(); C(comp, "Select4", false, null); C(_doc, "FixComponent"); }
                return $"inserted {name}";
            }
            case "mate":
            {
                string type = (string?)op["type"] ?? "";
                if (!MateTypes.TryGetValue(type, out int mt)) throw new ArgumentException($"mate type must be one of {string.Join(", ", MateTypes.Keys)}");
                Clear();
                SelectMateEntity(op["a"]!.AsObject(), append: false);
                SelectMateEntity(op["b"]!.AsObject(), append: true);
                int align = (string?)op["align"] switch { "aligned" => 0, "anti_aligned" => 1, _ => 2 };
                double v = op["value"] is null ? 0 : Eval(op["value"]);
                double dist = type == "distance" ? v * Mm : 0, angle = type == "angle" ? v * Math.PI / 180 : 0;
                var args = new object?[] { mt, align, (bool?)op["flip"] ?? false, dist, dist, dist, 1.0, 1.0, angle, angle, angle, false, false, 0, 0 };
                var mate = CRef(_doc, "AddMate5", args, 14);
                int status = Convert.ToInt32(args[14], CultureInfo.InvariantCulture);
                Clear();
                if (mate is null || status != 1) throw new InvalidOperationException($"SolidWorks refused the mate (status {status}: wrong entity types, or over-defined?)");
                return $"{type} mate {G(mate, "Name")}";
            }
            case "fix": SelectComponent((string)op["component"]!); C(_doc, "FixComponent"); return $"{op["component"]} fixed";
            case "float": SelectComponent((string)op["component"]!); C(_doc, "UnfixComponent"); return $"{op["component"]} floating";
            case "move":
            {
                var comp = Component((string)op["component"]!);
                var p = op["position"] as JsonArray ?? throw new ArgumentException("move needs position");
                var data = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, Eval(p[0]) * Mm, Eval(p[1]) * Mm, Eval(p[2]) * Mm, 1, 0, 0, 0 };
                S(comp, "Transform2", C(C(_sw, "GetMathUtility")!, "CreateTransform", data)!);
                return $"{op["component"]} moved (mates may move it back on rebuild)";
            }
            case "replace":
                SelectComponent((string)op["component"]!);
                if (C(_doc, "ReplaceComponents2", (string)op["path"]!, "", false, 0, true) is not true) throw new InvalidOperationException("SolidWorks did not replace it");
                return $"{op["component"]} replaced by {Path.GetFileName((string)op["path"]!)}";
            case "delete":
                SelectComponent((string)op["component"]!);
                C(Ext, "DeleteSelection2", 0);
                return $"deleted {op["component"]}";
            case "pattern_linear":
            {
                var src = Component((string)op["component"]!);
                string file = (string)C(src, "GetPathName")!;
                var t = Arr(G(G(src, "Transform2")!, "ArrayData")).Select(Convert.ToDouble).ToArray();
                double s = Eval(op["spacing"]) * Mm;
                var (dx, dy, dz) = ((string?)op["direction"] ?? "x").ToLowerInvariant() switch { "y" => (0.0, 1.0, 0.0), "z" => (0.0, 0.0, 1.0), _ => (1.0, 0.0, 0.0) };
                var names = new List<string>();
                for (int i = 1; i < (int)Eval(op["count"]); i++)
                {
                    var c = C(_doc, "AddComponent5", file, 0, "", false, "", t[9] + dx * s * i, t[10] + dy * s * i, t[11] + dz * s * i);
                    if (c is not null) names.Add((string)G(c, "Name2")!);
                }
                return "copies: " + string.Join(", ", names);
            }
            default: throw new ArgumentException($"unknown op {(string?)op["op"]}");
        }
    }

    private JsonObject DoAssemblyOps(string? path, JsonArray ops, bool save)
    {
        Live(path, SwDocAssembly, "assembly_ops");
        return FinishLive(new JsonObject { ["results"] = RunEach(ops, AssemblyOp) }, save);
    }

    // ── Drawings ─────────────────────────────────────────────────────────────────────────────────────

    private object SelectView(string name)
    {
        Clear();
        if (!SelectId(name, "DRAWINGVIEW")) throw new ArgumentException($"no view named {name} (inspect_model lists the drawing's views)");
        return C(G(_doc, "SelectionManager")!, "GetSelectedObject6", 1, -1)!;
    }

    private static (double X, double Y) SheetPoint(JsonNode? n) => n is JsonArray { Count: >= 2 } a
        ? ((double)a[0]! * Mm, (double)a[1]! * Mm)
        : throw new ArgumentException("a sheet point [x, y] in mm is required");

    private string AnnotateOp(JsonObject op)
    {
        switch ((string?)op["op"])
        {
            case "projected_view":
            {
                var view = SelectView((string)op["view"]!);
                var pos = Arr(G(view, "Position")).Select(Convert.ToDouble).ToArray();
                const double step = 0.12;
                var (x, y) = op["at"] is { } at ? SheetPoint(at) : ((string?)op["direction"]) switch
                {
                    "left" => (pos[0] - step, pos[1]), "top" => (pos[0], pos[1] + step), "bottom" => (pos[0], pos[1] - step), _ => (pos[0] + step, pos[1]),
                };
                var made = C(_doc, "CreateUnfoldedViewAt3", x, y, 0.0, false) ?? throw new InvalidOperationException("the projected view could not be placed");
                return $"view {G(made, "Name")}";
            }
            case "section_view":
            {
                string viewName = (string)op["view"]!;
                C(_doc, "ActivateView", viewName);
                var (x1, y1) = SheetPoint(op["from"]);
                var (x2, y2) = SheetPoint(op["to"]);
                var line = C(Sm, "CreateLine", x1, y1, 0.0, x2, y2, 0.0) ?? throw new InvalidOperationException("could not draw the cut line");
                Clear();
                C(line, "Select4", false, null);
                var (x, y) = SheetPoint(op["at"]);
                var made = C(_doc, "CreateSectionViewAt5", x, y, 0.0, (string?)op["label"] ?? "A", 0, null, 0.0) ?? throw new InvalidOperationException("the section view could not be made");
                return $"section view {G(made, "Name")}";
            }
            case "detail_view":
            {
                C(_doc, "ActivateView", (string)op["view"]!);
                var (cx, cy) = SheetPoint(op["center"]);
                double r = (double)op["radius"]! * Mm;
                var circle = C(Sm, "CreateCircle", cx, cy, 0.0, cx + r, cy, 0.0) ?? throw new InvalidOperationException("could not draw the detail circle");
                Clear();
                C(circle, "Select4", false, null);
                var (x, y) = SheetPoint(op["at"]);
                double scale = op["scale"] is null ? 2 : (double)op["scale"]!;
                var made = C(_doc, "CreateDetailViewAt4", x, y, 0.0, 0, scale, 1.0, (string?)op["label"] ?? "B", 0, false, false, false, 5)
                    ?? throw new InvalidOperationException("the detail view could not be made");
                return $"detail view {G(made, "Name")}";
            }
            case "model_dimensions":
            {
                bool all = op["view"] is null;
                if (!all) SelectView((string)op["view"]!);
                int n = Arr(C(_doc, "InsertModelAnnotations3", SwImportModelItemsFromEntireModel, SwInsertDimensionsMarkedForDrawing, all, false, false, false)).Length;
                return $"{n} dimension(s) inserted";
            }
            case "note":
            {
                Clear();
                var note = C(_doc, "InsertNote", (string)op["text"]!) ?? throw new InvalidOperationException("the note could not be inserted");
                var (x, y) = SheetPoint(op["at"]);
                C(C(note, "GetAnnotation")!, "SetPosition2", x, y, 0.0);
                return "note placed";
            }
            case "bom_table":
            {
                var view = op["view"] is { } v ? SelectView((string)v!) : C(_doc, "GetFirstView") is { } sheet ? C(sheet, "GetNextView") ?? throw new ArgumentException("the sheet has no model view") : throw new ArgumentException("no sheet");
                var (x, y) = SheetPoint(op["at"]);
                string template = Path.Combine(Path.GetDirectoryName((string)C(_sw, "GetExecutablePath")!) ?? "", "lang", "english", "bom-standard.sldbomtbt");
                var table = C(view, "InsertBomTable4", false, x, y, 1, 1, "", template, false, 0, false) ?? throw new InvalidOperationException("the BOM table could not be inserted");
                return "BOM table inserted";
            }
            case "balloons":
            {
                if (op["view"] is { } bv) SelectView((string)bv!);
                else { Clear(); C(_doc, "ActivateSheet", (string)G(C(_doc, "GetCurrentSheet")!, "GetName")!); }
                var opts = C(_doc, "CreateAutoBalloonOptions")!;
                int n = Arr(C(_doc, "AutoBalloon5", opts)).Length;
                return $"{n} balloon(s)";
            }
            default: throw new ArgumentException($"unknown op {(string?)op["op"]}");
        }
    }

    private JsonObject DoAnnotate(string? path, JsonArray ops, bool save)
    {
        Live(path, SwDocDrawing, "annotate_drawing");
        return FinishLive(new JsonObject { ["results"] = RunEach(ops, AnnotateOp) }, save);
    }

    // ── Extra edit operations ────────────────────────────────────────────────────────────────────────

    /// <summary>Edit ops added in V17 (the rest are in <c>ApplyOp</c>); null when <paramref name="kind"/> is not one of them.</summary>
    private string? ApplyMoreOps(object doc, string kind, JsonObject op)
    {
        switch (kind)
        {
            case "add_global":
            {
                string name = (string?)op["name"] ?? throw new ArgumentException("add_global needs name");
                if (!IdentRegex().IsMatch(name)) throw new ArgumentException("a global name is letters, digits and _");
                C(C(doc, "GetEquationMgr")!, "Add2", -1, $"\"{name}\" = {Num((double)op["value"]!)}", true);
                return $"added global {name}";
            }
            case "delete_feature":
                DeleteFeature((string?)op["feature"] ?? throw new ArgumentException("delete_feature needs feature"));
                return $"deleted {op["feature"]}";
            case "undo":
                C(doc, "EditUndo2", op["steps"] is null ? 1 : (int)(double)op["steps"]!);
                return "undone";
            case "set_color":
            {
                var rgb = (op["rgb"] as JsonArray ?? throw new ArgumentException("rgb: [r, g, b]")).Select(c => (double)c! / 255).ToArray();
                var values = new double[] { rgb[0], rgb[1], rgb[2], 0.5, 0.8, 0.4, 0.3, 0, 0 }; // RGB, ambient, diffuse, specular, shininess, transparency, emission
                if ((string?)op["component"] is { } comp) S(Component(comp), "MaterialPropertyValues", values);
                else if ((string?)op["feature"] is { } feat)
                    C(C(doc, "FeatureByName", feat) ?? throw new ArgumentException($"no feature named {feat}"), "SetMaterialPropertyValues2", values, 2, null);
                else S(doc, "MaterialPropertyValues", values);
                C(doc, "GraphicsRedraw2");
                return "colour set";
            }
            default: return null;
        }
    }
}
