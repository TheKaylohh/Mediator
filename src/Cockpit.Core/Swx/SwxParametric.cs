using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Swx;

/// <summary>
/// Native, equation-driven SolidWorks parts (late-bound IDispatch; Windows only). A recipe holds global variables (mm)
/// and features: sketches of rectangles, circles and polygons on a plane or a face, extruded, cut, or filleted. Every
/// sketch dimension and feature depth is linked to the globals with SolidWorks equations, so changing a global in
/// SolidWorks (Tools > Equations) or with <see cref="SetGlobals"/> rebuilds the part with its design intent.
/// Sketch coordinates: Top plane x = model X, y = -model Z; Front plane x = X, y = Y; Right plane x = -Z, y = Y.
/// IDispatch through InvokeMember is used (not <c>dynamic</c>): it works even when the SolidWorks type library is not registered.
/// </summary>
public sealed partial class SwxParametric
{
    private const double Mm = 0.001;
    private readonly Dictionary<string, double> _g = new(StringComparer.Ordinal);
    private readonly List<(string Dim, string Expr)> _pending = []; // dims of the open sketch, "D1" → expr
    private readonly List<string> _log = [];
    private bool _lock; // sketch lock:true: entities are fixed, so no driving dimensions (they would over-define it)
    private readonly HashSet<string> _cuts = new(StringComparer.Ordinal); // cut features: their faces are hole walls
    private readonly Dictionary<string, double> _extrudeDepth = new(StringComparer.Ordinal); // feature → thickness (mm)
    private object _sw = null!;
    private object _doc = null!;

    // ── Late-bound calls ──────────────────────────────────────────────────────────────────────────────────

    private static object? C(object o, string method, params object?[] args)
    {
        // A null IDispatch argument (SelectData, Callout, optional arrays) must be passed as VT_DISPATCH null, not VT_EMPTY.
        if (OperatingSystem.IsWindows())
            for (int i = 0; i < args.Length; i++) args[i] ??= new System.Runtime.InteropServices.DispatchWrapper(null);
        try { return o.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, o, args, CultureInfo.InvariantCulture); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new InvalidOperationException($"{method}: {inner.Message}", inner); }
        catch (System.Runtime.InteropServices.COMException ex) { throw new InvalidOperationException($"{method}: {ex.Message}", ex); }
    }
    private static object? G(object o, string property, params object?[] args) =>
        o.GetType().InvokeMember(property, BindingFlags.GetProperty, null, o, args, CultureInfo.InvariantCulture);
    private static void S(object o, string property, params object?[] args) =>
        o.GetType().InvokeMember(property, BindingFlags.SetProperty, null, o, args, CultureInfo.InvariantCulture);
    private static object[] Arr(object? v) => v as object[] ?? (v is Array a ? a.Cast<object>().ToArray() : []);

    private object Ext => G(_doc, "Extension")!;
    private object Sm => G(_doc, "SketchManager")!;
    private object Eqm => C(_doc, "GetEquationMgr")!;
    private void Clear() => C(_doc, "ClearSelection2", true);
    private bool SelectId(string name, string type, double x = 0, double y = 0, double z = 0, bool append = false) =>
        (bool)C(Ext, "SelectByID2", name, type, x, y, z, append, 0, null, 0)!;

    // ── Public entry points (each runs on its own STA thread) ──────────────────────────────────────────

    public static JsonObject Build(JsonObject recipe, TimeSpan timeout) => RunSta(() => new SwxParametric().DoBuild(recipe), timeout);

    public static JsonObject SetGlobals(string? partPath, JsonObject values, TimeSpan timeout) => RunSta(() => new SwxParametric().DoSet(partPath, values), timeout);

    private static JsonObject RunSta(Func<JsonObject> work, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SolidWorks parts need Windows.");
        JsonObject? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (TargetInvocationException ex) { error = ex.InnerException ?? ex; }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(timeout)) throw new TimeoutException($"SolidWorks did not finish within {timeout.TotalMinutes:0} min.");
        if (error is not null) throw new InvalidOperationException(error.Message, error);
        return result!;
    }

    private void Connect()
    {
        _sw = SolidWorksLauncher.Attach(TimeSpan.FromMinutes(3));
        S(_sw, "Visible", true);
        C(_sw, "SetUserPreferenceToggle", 10, false); // swInputDimValOnCreate: no "Modify" box per dimension
    }

    // ── Build ───────────────────────────────────────────────────────────────────────────────────────────

    private JsonObject DoBuild(JsonObject recipe)
    {
        string name = Safe((string?)recipe["name"] ?? "Part");
        string folder = (string?)recipe["outputFolder"] ?? throw new ArgumentException("outputFolder is required.");
        foreach (var (k, v) in recipe["globals"]?.AsObject() ?? [])
        {
            if (!IdentRegex().IsMatch(k)) throw new ArgumentException($"Bad global name '{k}' (letters, digits, _).");
            _g[k] = (double)v!;
        }

        Connect();
        string template = (string)C(_sw, "GetUserPreferenceStringValue", 8)!; // swDefaultTemplatePart
        _doc = C(_sw, "NewDocument", template, 0, 0.0, 0.0) ?? throw new InvalidOperationException("SolidWorks could not create a part from " + template);
        try
        {
            C(Ext, "SetUserPreferenceInteger", 263, 0, 5); // swUnitSystem = MMGS, so equations read in mm
            foreach (var (k, v) in _g) C(Eqm, "Add2", -1, $"\"{k}\" = {Num(v)}", true);

            int n = 0;
            foreach (var node in recipe["features"]?.AsArray() ?? [])
            {
                var f = node!.AsObject();
                string op = (string?)f["op"] ?? "";
                n++;
                try
                {
                    switch (op)
                    {
                        case "extrude" when (bool?)f["surface"] == true: SurfaceExtrude(f); break;
                        case "extrude": SketchFor(f); Extrude(f, cut: false); break;
                        case "cut": SketchFor(f); Extrude(f, cut: true); break;
                        case "fillet": Fillet(f); break;
                        case "chamfer": Chamfer(f); break;
                        case "plane": Plane(f); break;
                        case "sketch": NamedSketch(f); break;
                        case "curve3d": Curve3d(f); break;
                        case "loft": Loft(f); break;
                        case "sweep": Sweep(f); break;
                        case "revolve": Revolve(f); break;
                        case "knit": Knit(f); break;
                        case "thicken": Thicken(f); break;
                        case "shell": Shell(f); break;
                        case "mirror": Mirror(f); break;
                        default: throw new ArgumentException($"unknown op '{op}' ({string.Join(", ", Ops)})");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    string msg = ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
                    _log.Add($"feature {n} ({op}) failed: {msg}");
                    try { if (G(Sm, "ActiveSketch") is not null) C(Sm, "InsertSketch", true); } catch (Exception) { }
                    break;
                }
            }

            C(Eqm, "EvaluateAll");
            C(_doc, "ForceRebuild3", false);
            if (recipe["fitBoxMm"] is JsonArray { Count: 3 } fit)
            {
                try { FitToBox(fit); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { _log.Add("fitBoxMm failed: " + ex.Message); }
            }
            Directory.CreateDirectory(folder);
            string path = ReplaceTarget(folder, name);
            MarkBuilt();
            int saveErr = (int)C(_doc, "SaveAs3", path, 0, 1)!;
            if (saveErr == 0) CloseFinishedParts(path);
            C(_doc, "ShowNamedView2", "*Isometric", 7);
            C(_doc, "ViewZoomtofit2");
            var report = Report();
            report["saved"] = saveErr == 0 && File.Exists(path) ? path : $"save failed (error {saveErr})";
            if ((bool?)recipe["exportStl"] == true)
            {
                string stl = Path.ChangeExtension(path, ".STL");
                int stlErr = (int)C(_doc, "SaveAs3", stl, 0, 2)!; // swSaveAsOptions_Copy: the open document stays the .SLDPRT
                report["stl"] = stlErr == 0 && File.Exists(stl) ? stl : $"STL export failed (error {stlErr})";
                if (stlErr == 0) Remember(stl);
            }
            if (saveErr != 0) return report;
            Remember(path);
            if (recipe["referenceMm"] is JsonArray { Count: 3 } refSize && report["boundingBoxMm"] is JsonObject bb)
            {
                double Side(string axis) => (double)bb[axis]![1]! - (double)bb[axis]![0]!;
                report["proportions"] = Proportions([Side("x"), Side("y"), Side("z")], refSize.Select(v => Eval(v)).ToArray());
            }
            if ((bool?)recipe["views"] != false) report["views"] = RenderViews(path);
            if ((bool?)recipe["keepIterations"] != true && PruneIterations(path) is { Count: > 0 } pruned) report["deletedOlderIterations"] = pruned;
            return report;
        }
        catch
        {
            DiscardUnsaved();
            throw;
        }
    }

    /// <summary>Every feature op a recipe may use (the tool schema lists the same).</summary>
    public static readonly string[] Ops = ["extrude", "cut", "revolve", "loft", "sweep", "fillet", "chamfer", "shell", "mirror", "plane", "sketch", "curve3d", "knit", "thicken"];

    // ── Versions and windows ───────────────────────────────────────────────────────────────────────────

    /// <summary>Custom property marking parts this builder made (only those are closed automatically).</summary>
    public const string BuiltProperty = "AiCockpitBuilt";
    private const int SwDocPart = 1, SwCustomInfoText = 30, SwCustomPropertyReplace = 2;
    private const int ReplaceAttempts = 10;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(300);
    public const string PreviousSuffix = ".previous";
    private const string PartExtension = ".SLDPRT";

    /// <summary>
    /// Where a rebuild of <paramref name="name"/> is saved: the same file, not a new timestamped copy. The window holding the
    /// old version is closed first (SolidWorks cannot save over an open file) and the old file is kept as
    /// "name.previous.SLDPRT" (one backup, replaced each time). A copy that cannot be moved (locked) gets a timestamped name.
    /// </summary>
    private string ReplaceTarget(string folder, string name)
    {
        string path = Path.GetFullPath(Path.Combine(folder, name + PartExtension));
        if (!File.Exists(path)) return path;
        CloseIfOpen(path, onlyIfSaved: false);
        // SolidWorks releases the file a moment after CloseDoc returns.
        for (int attempt = 1; ; attempt++)
        try
        {
            File.Move(path, Path.Combine(folder, name + PreviousSuffix + PartExtension), overwrite: true);
            return path;
        }
        catch (IOException) when (attempt < ReplaceAttempts)
        {
            Thread.Sleep(ReplaceRetryDelay);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Add($"could not replace {Path.GetFileName(path)} ({ex.Message}); saved as a new copy");
            return Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}{PartExtension}");
        }
    }

    /// <summary>Tags the part as built by Cockpit (a custom property saved in the file), so any later build can tidy it up.</summary>
    private void MarkBuilt()
    {
        try { C(G(Ext, "CustomPropertyManager", "")!, "Add3", BuiltProperty, SwCustomInfoText, "1", SwCustomPropertyReplace); }
        catch (InvalidOperationException ex) { _log.Add("could not tag the part: " + ex.Message); }
    }

    /// <summary>
    /// Once a part is saved, closes every other finished Cockpit part in SolidWorks (tagged, saved, no unsaved edits), from this
    /// job or earlier ones, so only the newest result stays open for review. The user's own documents are never touched.
    /// </summary>
    private void CloseFinishedParts(string keep)
    {
        int closed = 0;
        foreach (var d in Arr(C(_sw, "GetDocuments")))
        {
            try
            {
                if (C(d, "GetType") is not int type || type != SwDocPart) continue;
                if (C(d, "GetPathName") is not string path || path.Length == 0 || string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (C(d, "GetSaveFlag") is true) continue; // unsaved edits: leave it for the user
                var props = G(G(d, "Extension")!, "CustomPropertyManager", "")!;
                if (C(props, "Get", BuiltProperty) as string != "1") continue;
                C(_sw, "CloseDoc", (string)C(d, "GetTitle")!);
                closed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TargetInvocationException) { }
        }
        if (closed > 0) _log.Add($"closed {closed} finished part{(closed == 1 ? "" : "s")} (saved; only the newest stays open)");
    }

    /// <summary>A build that failed before saving: its untitled document is scratch, so it is closed without saving.</summary>
    private void DiscardUnsaved()
    {
        try { if (_doc is not null && C(_doc, "GetPathName") as string is null or "") C(_sw, "CloseDoc", (string)C(_doc, "GetTitle")!); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or TargetInvocationException) { }
    }

    /// <summary>Closes the window of <paramref name="path"/>. With <paramref name="onlyIfSaved"/>, a part with unsaved edits stays open.</summary>
    private void CloseIfOpen(string path, bool onlyIfSaved)
    {
        try
        {
            // GetOpenDocumentByName misses documents through late binding; match on the full path instead.
            var doc = Arr(C(_sw, "GetDocuments")).FirstOrDefault(d =>
                string.Equals(C(d, "GetPathName") as string, path, StringComparison.OrdinalIgnoreCase));
            if (doc is null) return;
            if (onlyIfSaved && C(doc, "GetSaveFlag") is true) { _log.Add($"left {Path.GetFileName(path)} open: it has unsaved changes"); return; }
            C(_sw, "CloseDoc", (string)C(doc, "GetTitle")!);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidOperationException)
        {
            _log.Add($"could not close {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private void Sketch(JsonObject f)
    {
        _pending.Clear();
        Clear();
        var on = f["on"] ?? "Top";
        bool selected = on is JsonArray p
            ? SelectId("", "FACE", Eval(p[0]) * Mm, Eval(p[1]) * Mm, Eval(p[2]) * Mm)
            : SelectId(PlaneId((string)on!), "PLANE");
        if (!selected) throw new InvalidOperationException($"could not select the sketch plane/face {on.ToJsonString()}");
        C(Sm, "InsertSketch", true);
        S(Sm, "AddToDB", true);
        try
        {
            _lock = (bool?)f["lock"] ?? false;
            object? firstCircle = null;
            bool equal = (bool?)f["equal"] ?? false;
            void Draw(JsonArray profile)
            {
            foreach (var s in profile)
            {
                var shape = s!.AsObject();
                if (shape["import"] is JsonObject imp) Draw(ProfileImport.ToShapes(imp, Eval));
                else if (shape["circle"] is JsonObject c) { var made = Circle(c, equal ? firstCircle : null); firstCircle ??= made; }
                else if (shape["rect"] is JsonObject r) Rect(r);
                else if (shape["polygon"] is JsonArray poly) Polygon(poly);
                else if (shape["spline"] is JsonObject sp) Spline(sp);
                else if (shape["outline"] is JsonArray chain) Outline(chain);
                else if (shape["arc"] is JsonArray arc) Arc3(Points(arc, "arc (start, mid, end)", 3));
                else if (shape["line"] is JsonArray line) { var l = Points(line, "line", 2); C(Sm, "CreateLine", l[0].X * Mm, l[0].Y * Mm, 0.0, l[1].X * Mm, l[1].Y * Mm, 0.0); }
                else if (shape["centerline"] is JsonArray cl) { var l = Points(cl, "centerline", 2); C(Sm, "CreateCenterLine", l[0].X * Mm, l[0].Y * Mm, 0.0, l[1].X * Mm, l[1].Y * Mm, 0.0); }
                else throw new ArgumentException("shape must be circle, rect, polygon, spline, outline, arc, line, centerline or import");
            }
            }
            Draw(f["profile"]?.AsArray() ?? throw new ArgumentException("profile is required"));
            if (_lock) LockSketch();
        }
        finally { S(Sm, "AddToDB", false); _lock = false; }
        C(Sm, "InsertSketch", true); // close
        string sketch = (string)G(C(_doc, "FeatureByPositionReverse", 0)!, "Name")!;
        Link(sketch);
        Clear();
        SelectId(sketch, "SKETCH");
    }

    /// <summary>sketch lock:true: fixes every entity so the profile keeps its exact (e.g. imported) shape.</summary>
    private void LockSketch()
    {
        try
        {
            Clear();
            C(G(_doc, "Extension")!, "SelectAll");
            C(_doc, "SketchAddConstraints", "sgFIXED");
            Clear();
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidOperationException)
        {
            _log.Add($"could not lock the sketch: {ex.Message}");
        }
    }

    private object Circle(JsonObject c, object? equalTo)
    {
        double x = Eval(c["cx"]), y = Eval(c["cy"]), d = Eval(c["d"]);
        var seg = C(Sm, "CreateCircleByRadius", x * Mm, y * Mm, 0.0, d / 2 * Mm) ?? throw new InvalidOperationException("circle not created");
        Clear();
        C(seg, "Select4", false, null);
        if (equalTo is not null)
        {
            C(equalTo, "Select4", true, null);
            C(_doc, "SketchAddConstraints", "sgSAMELENGTH");
        }
        else if (!_lock) Dim(C(_doc, "AddDimension2", (x + d) * Mm, (y + d) * Mm, 0.0), Expr(c["d"]));
        Locate(C(seg, "GetCenterPoint2")!, c["cx"], c["cy"]);
        return seg;
    }

    private void Rect(JsonObject r)
    {
        double x = Eval(r["cx"]), y = Eval(r["cy"]), w = Eval(r["w"]), h = Eval(r["h"]);
        var segs = Arr(C(Sm, "CreateCenterRectangle", x * Mm, y * Mm, 0.0, (x + w / 2) * Mm, (y + h / 2) * Mm, 0.0));
        object? top = null, side = null;
        foreach (var s in segs)
        {
            if ((bool)G(s, "ConstructionGeometry")! || (int)C(s, "GetType")! != 0) continue; // real lines only
            object a = C(s, "GetStartPoint2")!, b = C(s, "GetEndPoint2")!;
            double ax = (double)G(a, "X")!, ay = (double)G(a, "Y")!, bx = (double)G(b, "X")!, by = (double)G(b, "Y")!;
            if (Math.Abs(ay - by) < 1e-9 && ay > y * Mm) top = s;
            if (Math.Abs(ax - bx) < 1e-9 && ax > x * Mm) side = s;
        }
        if (top is null || side is null) throw new InvalidOperationException("rectangle edges not found");
        Clear(); C(top, "Select4", false, null);
        if (!_lock) Dim(C(_doc, "AddDimension2", x * Mm, (y + h / 2 + 10) * Mm, 0.0), Expr(r["w"]));
        Clear(); C(side, "Select4", false, null);
        if (!_lock) Dim(C(_doc, "AddDimension2", (x + w / 2 + 10) * Mm, y * Mm, 0.0), Expr(r["h"]));
        Locate(FindPoint(x, y) ?? throw new InvalidOperationException("rectangle centre point not found"), r["cx"], r["cy"]);
    }

    private void Polygon(JsonArray pts)
    {
        var xy = pts.Select(p => (X: Eval(p![0]), Y: Eval(p[1]), Ex: p[0], Ey: p[1])).ToList();
        if (xy.Count < 3) throw new ArgumentException("polygon needs 3+ points");
        var lines = new List<object>();
        for (int i = 0; i < xy.Count; i++)
        {
            var (a, b) = (xy[i], xy[(i + 1) % xy.Count]);
            lines.Add(C(Sm, "CreateLine", a.X * Mm, a.Y * Mm, 0.0, b.X * Mm, b.Y * Mm, 0.0) ?? throw new InvalidOperationException("line not created"));
        }
        for (int i = 0; i < lines.Count; i++)
        {
            Clear();
            C(C(lines[i], "GetEndPoint2")!, "Select4", false, null);
            C(C(lines[(i + 1) % lines.Count], "GetStartPoint2")!, "Select4", true, null);
            C(_doc, "SketchAddConstraints", "sgMERGEPOINTS");
        }
        foreach (var p in xy) Locate(FindPoint(p.X, p.Y) ?? throw new InvalidOperationException($"polygon point ({p.X},{p.Y}) not found"), p.Ex, p.Ey);
    }

    /// <summary>Ties a sketch point to the origin: dimensions linked to the expressions, or a relation when a coordinate is 0.</summary>
    private void Locate(object pt, JsonNode? ex, JsonNode? ey)
    {
        double x = Eval(ex), y = Eval(ey);
        bool zx = Math.Abs(x) < 1e-9, zy = Math.Abs(y) < 1e-9;
        if (zx && zy) { SelectWithOrigin(pt); C(_doc, "SketchAddConstraints", "sgCOINCIDENT"); return; }
        SelectWithOrigin(pt);
        if (zx) C(_doc, "SketchAddConstraints", "sgVERTICALPOINTS2D");
        else if (!_lock) Dim(C(_doc, "AddHorizontalDimension2", x / 2 * Mm, (y + 8) * Mm, 0.0), Signed(ex, x));
        SelectWithOrigin(pt);
        if (zy) C(_doc, "SketchAddConstraints", "sgHORIZONTALPOINTS2D");
        else if (!_lock) Dim(C(_doc, "AddVerticalDimension2", (x + 8) * Mm, y / 2 * Mm, 0.0), Signed(ey, y));
    }

    private void SelectWithOrigin(object pt)
    {
        Clear();
        C(pt, "Select4", false, null);
        SelectId("Point1@Origin", "EXTSKETCHPOINT", append: true);
    }

    private object? FindPoint(double x, double y)
    {
        foreach (var p in Arr(C(G(Sm, "ActiveSketch")!, "GetSketchPoints2")))
            if (Math.Abs((double)G(p, "X")! - x * Mm) < 1e-7 && Math.Abs((double)G(p, "Y")! - y * Mm) < 1e-7) return p;
        return null;
    }

    private void Dim(object? displayDim, string expr)
    {
        if (displayDim is null) throw new InvalidOperationException("SolidWorks refused a dimension (over-defined sketch?)");
        _pending.Add(((string)G(C(displayDim, "GetDimension2", 0)!, "Name")!, expr));
    }

    private void Link(string feature)
    {
        foreach (var (dim, expr) in _pending) C(Eqm, "Add2", -1, $"\"{dim}@{feature}\" = {expr}", true);
        _pending.Clear();
    }

    private void Extrude(JsonObject f, bool cut)
    {
        var fm = G(_doc, "FeatureManager")!;
        bool through = f["depth"] is JsonValue v && v.TryGetValue(out string? s) && s == "through_all";
        bool mid = (bool?)f["midPlane"] ?? false, flip = (bool?)f["reverse"] ?? false;
        double depth = through ? 0.01 : Eval(f["depth"]);
        int t1 = through ? 1 : mid ? 6 : 0, t2 = through ? 1 : 0;
        object? feat = cut
            ? C(fm, "FeatureCut4", !through, flip, false, t1, t2, depth * Mm, depth * Mm, false, false, false, false, 0.0, 0.0,
                false, false, false, false, false, true, true, false, false, false, 0, 0.0, false, false)
            : C(fm, "FeatureExtrusion2", !through, flip, false, t1, t2, depth * Mm, depth * Mm, false, false, false, false, 0.0, 0.0,
                false, false, false, false, true, true, true, 0, 0.0, false);
        if (feat is null) throw new InvalidOperationException((cut ? "cut" : "extrude") + " failed (open or self-intersecting profile, or nothing to cut?)");
        string name = (string)G(feat, "Name")!;
        if (!through) { _pending.Add(("D1", Expr(f["depth"]))); Link(name); }
        if (!cut && !through) _extrudeDepth[name] = depth;
        if (cut) _cuts.Add(name);
        _log.Add(name);
    }

    private void Fillet(JsonObject f)
    {
        double r = Eval(f["radius"]);
        int count = SelectEdges(f, r, out var thin);
        object? fillet = C(Fm, "FeatureFillet3", 195, r * Mm, 0.0, 0.0, 0, 0, 0, null, null, null, null, null, null, null);
        if (fillet is null) throw new InvalidOperationException($"fillet R{r} failed on {count} edges (a wall thinner than 2×R, or a radius larger than the curvature?)");
        string name = Rename(fillet, f["name"]);
        _pending.Add(("D1", Expr(f["radius"])));
        Link(name);
        _log.Add($"{name} on {count} edges" + (thin.Count > 0 ? $"; edges of {string.Join(", ", thin)} left sharp (thinner than 2×R)" : ""));
    }

    // ── Change globals (the design-intent test) ─────────────────────────────────────────────────────────

    private JsonObject DoSet(string? partPath, JsonObject values)
    {
        Connect();
        _doc = (partPath is null ? G(_sw, "ActiveDoc") : C(_sw, "OpenDoc", partPath, 1))
            ?? throw new InvalidOperationException(partPath is null ? "No part is open in SolidWorks." : $"could not open {partPath}");
        if (partPath is not null) C(_sw, "ActivateDoc", Path.GetFileName(partPath));
        var eqm = Eqm;
        var changed = new List<string>();
        int count = (int)C(eqm, "GetCount")!;
        for (int i = 0; i < count; i++)
        {
            var m = GlobalRegex().Match((string)G(eqm, "Equation", i)!);
            if (!m.Success || values[m.Groups[1].Value] is not { } v) continue;
            S(eqm, "Equation", i, $"\"{m.Groups[1].Value}\" = {Num((double)v)}");
            changed.Add($"{m.Groups[1].Value} = {Num((double)v)}");
        }
        foreach (var (k, _) in values) if (!changed.Any(c => c.StartsWith(k + " ", StringComparison.Ordinal))) _log.Add($"no global named {k}");
        C(eqm, "EvaluateAll");
        C(_doc, "ForceRebuild3", false);
        string? path = (string?)C(_doc, "GetPathName");
        if (!string.IsNullOrEmpty(path)) C(_doc, "SaveAs3", path, 0, 1);
        C(_doc, "ViewZoomtofit2");
        var report = Report();
        report["changed"] = new JsonArray(changed.Select(c => (JsonNode)c).ToArray());
        report["part"] = path;
        return report;
    }

    // ── Report: rebuild errors and measured geometry, so the result can be checked ─────────────────────

    private JsonObject Report()
    {
        var errors = new JsonArray();
        for (object? f = C(_doc, "FirstFeature"); f is not null; f = C(f, "GetNextFeature"))
        {
            int code = (int)C(f, "GetErrorCode")!;
            if (code != 0) errors.Add($"{G(f, "Name")}: error {code}");
        }
        var report = new JsonObject { ["rebuildErrors"] = errors, ["log"] = new JsonArray(_log.Select(l => (JsonNode)l).ToArray()) };
        var eqm = Eqm;
        var globals = new JsonObject();
        int count = (int)C(eqm, "GetCount")!;
        for (int i = 0; i < count; i++)
        {
            var m = GlobalRegex().Match((string)G(eqm, "Equation", i)!);
            if (m.Success) globals[m.Groups[1].Value] = Math.Round((double)G(eqm, "Value", i)!, 4);
        }
        report["globals"] = globals;
        var bodies = Arr(C(_doc, "GetBodies2", 0, true));
        report["bodies"] = bodies.Length;
        report["surfaceBodies"] = Arr(C(_doc, "GetBodies2", 1, true)).Length;
        if (bodies.Length == 0) return report;
        var box = Arr(C(bodies[0], "GetBodyBox")).Select(Convert.ToDouble).ToArray();
        static double R(double m) => Math.Round(m / Mm, 2);
        report["boundingBoxMm"] = new JsonObject
        {
            ["x"] = new JsonArray(R(box[0]), R(box[3])), ["y"] = new JsonArray(R(box[1]), R(box[4])), ["z"] = new JsonArray(R(box[2]), R(box[5])),
        };
        var cyl = new Dictionary<string, JsonObject>();
        foreach (var face in Arr(C(bodies[0], "GetFaces")))
        {
            object surf = C(face, "GetSurface")!;
            if (!(bool)C(surf, "IsCylinder")!) continue;
            var c = Arr(G(surf, "CylinderParams")).Select(Convert.ToDouble).ToArray(); // origin xyz, axis xyz, radius
            if (Math.Abs(c[4]) < 0.99) continue;           // vertical axes only (holes, bore, boss)
            double d = R(2 * c[6]), x = R(c[0]), z = R(c[2]);
            string key = $"{d}|{x}|{z}";
            if (cyl.ContainsKey(key)) continue;
            cyl[key] = new JsonObject
            {
                ["diameter"] = d, ["x"] = x, ["z"] = z,
                ["toEdgeX"] = Math.Round(Math.Min(x - R(box[0]), R(box[3]) - x), 2),
                ["toEdgeZ"] = Math.Round(Math.Min(z - R(box[2]), R(box[5]) - z), 2),
            };
        }
        report["verticalCylinders"] = new JsonArray(cyl.Values.OrderByDescending(h => (double)h["diameter"]!).ThenBy(h => (double)h["x"]!).Cast<JsonNode>().ToArray());
        report["boxCentreXZ"] = new JsonArray(R((box[0] + box[3]) / 2), R((box[2] + box[5]) / 2));
        return report;
    }

    // ── Expressions: numbers, globals, + - * / ( ) ──────────────────────────────────────────────────────

    private string Expr(JsonNode? n) => n switch
    {
        null => "0",
        JsonValue v when v.TryGetValue(out double d) => Num(d),
        _ => QuoteGlobals((string)n!),
    };

    private string Signed(JsonNode? n, double value) => value < 0 ? $"-({Expr(n)})" : Expr(n);

    private string QuoteGlobals(string e)
    {
        if (!ExprRegex().IsMatch(e)) throw new ArgumentException($"bad expression '{e}'");
        return IdentRegex2().Replace(e, m => _g.ContainsKey(m.Value) ? $"\"{m.Value}\"" : throw new ArgumentException($"unknown global '{m.Value}' in '{e}'"));
    }

    private double Eval(JsonNode? n)
    {
        if (n is null) return 0;
        if (n is JsonValue v && v.TryGetValue(out double d)) return d;
        string s = (string)n!;
        if (!ExprRegex().IsMatch(s)) throw new ArgumentException($"bad expression '{s}'");
        int i = 0;
        double r = Sum(s, ref i);
        if (Peek(s, ref i) != '\0') throw new ArgumentException($"bad expression '{s}'");
        return r;
    }

    private double Sum(string s, ref int i)
    {
        double v = Prod(s, ref i);
        while (Peek(s, ref i) is '+' or '-') { char op = s[i++]; double r = Prod(s, ref i); v = op == '+' ? v + r : v - r; }
        return v;
    }

    private double Prod(string s, ref int i)
    {
        double v = Unary(s, ref i);
        while (Peek(s, ref i) is '*' or '/') { char op = s[i++]; double r = Unary(s, ref i); v = op == '*' ? v * r : v / r; }
        return v;
    }

    private double Unary(string s, ref int i)
    {
        char c = Peek(s, ref i);
        if (c == '-') { i++; return -Unary(s, ref i); }
        if (c == '(') { i++; double v = Sum(s, ref i); if (Peek(s, ref i) != ')') throw new ArgumentException($"missing ) in '{s}'"); i++; return v; }
        int start = i;
        if (char.IsDigit(c) || c == '.')
        {
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return double.Parse(s[start..i], CultureInfo.InvariantCulture);
        }
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        string id = s[start..i];
        return _g.TryGetValue(id, out double g) ? g : throw new ArgumentException($"unknown global '{id}' in '{s}'");
    }

    private static char Peek(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
        return i < s.Length ? s[i] : '\0';
    }

    private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Safe(string n) => string.Concat(n.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ')).Trim() is { Length: > 0 } s ? s : "Part";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex IdentRegex();
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")] private static partial Regex IdentRegex2();
    [GeneratedRegex(@"^[A-Za-z0-9_+\-*/(). ]+$")] private static partial Regex ExprRegex();
    [GeneratedRegex("^\\s*\"([A-Za-z_][A-Za-z0-9_]*)\"\\s*=")] private static partial Regex GlobalRegex();
}
