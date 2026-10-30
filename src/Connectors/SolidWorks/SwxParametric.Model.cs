using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Mediator.Core.Cad;

namespace Mediator.Connectors.SolidWorks;

/// <summary>
/// V16: working on any SolidWorks document from a chat, not only parts this builder made: read it (<see cref="Inspect"/>:
/// feature tree, dimensions, equations, custom properties, mass and material, configurations, what the user selected, an
/// assembly's BOM, mates and interferences), change it (<see cref="Edit"/>), make a drawing of it (<see cref="Drawing"/>) and
/// take pictures of it (<see cref="CaptureViews"/>). Every section and operation is tried on its own: one that SolidWorks refuses is
/// reported, the rest still run. Lengths are reported in mm, angles in degrees, mass in kg.
/// </summary>
public sealed partial class SwxParametric
{
    private const int SwSaveAsSilent = 1;
    private const int MaxListed = 300;

    public static JsonObject Inspect(string? path, IReadOnlySet<string> sections, TimeSpan timeout) => Run(p => p.DoInspect(path, sections), timeout);
    public static JsonObject Edit(string? path, JsonArray ops, bool save, TimeSpan timeout) => Run(p => p.DoEdit(path, ops, save), timeout);
    public static JsonObject Drawing(CadDrawingRequest request, TimeSpan timeout) => Run(p => p.DoDrawing(request), timeout);
    public static JsonObject CaptureViews(string? path, IReadOnlyList<string> views, string folder, TimeSpan timeout) => Run(p => p.DoViews(path, views, folder), timeout);

    private JsonArray Log() => new(_log.Select(l => (JsonNode)l).ToArray());

    /// <summary>Late-bound call with by-reference arguments (out/ref): <paramref name="args"/> holds their values afterwards.</summary>
    private static object? CRef(object o, string method, object?[] args, params int[] byRef)
    {
        var mods = new ParameterModifier(args.Length);
        foreach (int i in byRef) mods[i] = true;
        try { return o.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, o, args, [mods], CultureInfo.InvariantCulture, null); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner) { throw new InvalidOperationException($"{method}: {inner.Message}", inner); }
        catch (System.Runtime.InteropServices.COMException ex) { throw new InvalidOperationException($"{method}: {ex.Message}", ex); }
    }

    /// <summary>A SolidWorks call that failed in a way that only affects the current section or operation.</summary>
    private static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or ThreadInterruptedException);

    private static string Message(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

    private static double ToMm(object? metres) => Math.Round(Convert.ToDouble(metres, CultureInfo.InvariantCulture) * 1000, 3);
    private static JsonArray MmArray(IEnumerable<double> metres) => new(metres.Select(v => (JsonNode)ToMm(v)).ToArray());

    // ── Feature tree ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Top-level features in tree order (folders, lights and the origin left out).</summary>
    private IEnumerable<object> TreeFeatures(object doc)
    {
        for (object? f = C(doc, "FirstFeature"); f is not null; f = C(f, "GetNextFeature"))
        {
            string type = C(f, "GetTypeName2") as string ?? "";
            if (type.EndsWith("Folder", StringComparison.Ordinal) || type.EndsWith("Light", StringComparison.Ordinal) || type is "DetailCabinet" or "OriginProfileFeature")
                continue;
            yield return f;
        }
    }

    private static IEnumerable<object> SubFeatures(object feature)
    {
        for (object? s = C(feature, "GetFirstSubFeature"); s is not null; s = C(s, "GetNextSubFeature")) yield return s;
    }

    /// <summary>(error code, warning) of a feature; 0 = rebuilt fine.</summary>
    private static (int Code, bool Warning) FeatureError(object feature)
    {
        try
        {
            var args = new object?[] { false };
            int code = Convert.ToInt32(CRef(feature, "GetErrorCode2", args, 0), CultureInfo.InvariantCulture);
            return (code, args[0] is true);
        }
        catch (InvalidOperationException) { return (Convert.ToInt32(C(feature, "GetErrorCode"), CultureInfo.InvariantCulture), false); }
    }

    private JsonObject Features(object doc)
    {
        var list = new JsonArray();
        int total = 0, errors = 0;
        foreach (var f in TreeFeatures(doc))
        {
            total++;
            var (code, warning) = FeatureError(f);
            if (code != 0 && !warning) errors++;
            if (list.Count >= MaxListed) continue;
            var item = new JsonObject { ["name"] = G(f, "Name") as string, ["type"] = C(f, "GetTypeName2") as string };
            if (C(f, "IsSuppressed") is true) item["suppressed"] = true;
            if (code != 0) item[warning ? "warning" : "error"] = code;
            list.Add(item);
        }
        return new JsonObject { ["count"] = total, ["withErrors"] = errors, ["tree"] = list };
    }

    private JsonArray RebuildErrors(object doc)
    {
        var errors = new JsonArray();
        foreach (var f in TreeFeatures(doc))
        {
            var (code, warning) = FeatureError(f);
            if (code != 0 && !warning) errors.Add($"{G(f, "Name")}: error {code}");
        }
        return errors;
    }

    // ── Dimensions and equations ─────────────────────────────────────────────────────────────────────

    private const int SwAngularDimension = 3; // swDimensionType_e

    private JsonObject Dimensions(object doc)
    {
        var drivenNames = EquationTargets(doc);
        var list = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var f in TreeFeatures(doc))
        {
            foreach (var owner in SubFeatures(f).Prepend(f))
            {
                string featureName = G(owner, "Name") as string ?? "";
                for (object? dd = C(owner, "GetFirstDisplayDimension"); dd is not null; dd = C(owner, "GetNextDisplayDimension", dd))
                {
                    object? dim = C(dd, "GetDimension2", 0);
                    if (dim is null) continue;
                    string name = $"{G(dim, "Name")}@{featureName}";
                    if (!seen.Add(name)) continue;
                    total++;
                    if (list.Count >= MaxListed) continue;
                    bool angular = C(dd, "GetType") is int t && t == SwAngularDimension;
                    double si = Convert.ToDouble(G(dim, "SystemValue"), CultureInfo.InvariantCulture);
                    var item = new JsonObject
                    {
                        ["name"] = name,
                        ["value"] = angular ? Math.Round(si * 180 / Math.PI, 4) : ToMm(si),
                        ["unit"] = angular ? "deg" : "mm",
                    };
                    if (drivenNames.Contains(name)) item["drivenByEquation"] = true;
                    list.Add(item);
                }
            }
        }
        return new JsonObject { ["count"] = total, ["list"] = list };
    }

    /// <summary>Dimension names ("D1@Sketch1") on the left of an equation: setting them directly would be undone by the rebuild.</summary>
    private HashSet<string> EquationTargets(object doc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eqm = C(doc, "GetEquationMgr")!;
        int count = Convert.ToInt32(C(eqm, "GetCount"), CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            string eq = G(eqm, "Equation", i) as string ?? "";
            int q1 = eq.IndexOf('"'), q2 = q1 < 0 ? -1 : eq.IndexOf('"', q1 + 1);
            if (q2 > q1 + 1 && eq[(q1 + 1)..q2] is { } left && left.Contains('@')) names.Add(left);
        }
        return names;
    }

    private static JsonObject Equations(object doc)
    {
        var eqm = C(doc, "GetEquationMgr")!;
        int count = Convert.ToInt32(C(eqm, "GetCount"), CultureInfo.InvariantCulture);
        var globals = new JsonObject();
        var equations = new JsonArray();
        for (int i = 0; i < count && equations.Count < MaxListed; i++)
        {
            string eq = G(eqm, "Equation", i) as string ?? "";
            double value = Math.Round(Convert.ToDouble(G(eqm, "Value", i), CultureInfo.InvariantCulture), 4);
            var m = GlobalRegex().Match(eq);
            if (m.Success) globals[m.Groups[1].Value] = value;
            else equations.Add(new JsonObject { ["equation"] = eq, ["value"] = value });
        }
        return new JsonObject { ["globals"] = globals, ["equations"] = equations };
    }

    // ── Properties, mass, configurations ─────────────────────────────────────────────────────────────

    private string ActiveConfiguration(object doc) => C(doc, "GetActiveConfiguration") is { } cfg ? G(cfg, "Name") as string ?? "" : "";

    private static JsonObject PropertyList(object doc, string configuration)
    {
        var cpm = G(G(doc, "Extension")!, "CustomPropertyManager", configuration)!;
        var result = new JsonObject();
        foreach (var n in Arr(C(cpm, "GetNames")).OfType<string>().Take(MaxListed))
        {
            try
            {
                var args = new object?[] { n, false, "", "" };
                CRef(cpm, "Get4", args, 2, 3);
                string raw = args[2] as string ?? "", resolved = args[3] as string ?? "";
                result[n] = raw == resolved || raw.Length == 0 ? resolved : $"{resolved} (from {raw})";
            }
            catch (InvalidOperationException) { result[n] = C(cpm, "Get", n) as string; }
        }
        return result;
    }

    private JsonObject Properties(object doc)
    {
        string config = ActiveConfiguration(doc);
        var report = new JsonObject { ["file"] = PropertyList(doc, "") };
        if (config.Length > 0 && C(doc, "GetType") is not SwDocDrawing) report["configuration"] = new JsonObject { ["name"] = config, ["properties"] = PropertyList(doc, config) };
        return report;
    }

    private string? Material(object doc)
    {
        if (C(doc, "GetType") is not SwDocPart) return null;
        var args = new object?[] { ActiveConfiguration(doc), "" };
        return CRef(doc, "GetMaterialPropertyName2", args, 1) as string is { Length: > 0 } name ? name : "(none set)";
    }

    private JsonObject Mass(object doc)
    {
        var mp = C(G(doc, "Extension")!, "CreateMassProperty") ?? throw new InvalidOperationException("SolidWorks has no mass properties for this document (no solid body?)");
        var com = Arr(G(mp, "CenterOfMass")).Select(Convert.ToDouble).ToArray();
        var report = new JsonObject
        {
            ["massKg"] = Math.Round(Convert.ToDouble(G(mp, "Mass"), CultureInfo.InvariantCulture), 5),
            ["volumeMm3"] = Math.Round(Convert.ToDouble(G(mp, "Volume"), CultureInfo.InvariantCulture) * 1e9, 2),
            ["surfaceAreaMm2"] = Math.Round(Convert.ToDouble(G(mp, "SurfaceArea"), CultureInfo.InvariantCulture) * 1e6, 2),
            ["centerOfMassMm"] = MmArray(com),
            ["densityKgM3"] = Math.Round(Convert.ToDouble(G(mp, "Density"), CultureInfo.InvariantCulture), 2),
        };
        try { if (Material(doc) is { } material) report["material"] = material; }
        catch (InvalidOperationException ex) { report["material"] = "unknown: " + ex.Message; }
        return report;
    }

    private JsonObject Configurations(object doc) => new()
    {
        ["active"] = ActiveConfiguration(doc),
        ["names"] = new JsonArray(Arr(C(doc, "GetConfigurationNames")).OfType<string>().Select(n => (JsonNode)n).ToArray()),
    };

    /// <summary>Overall size in mm: the part's box, or the assembly's.</summary>
    private static JsonObject? Size(object doc)
    {
        object? box = C(doc, "GetType") switch
        {
            SwDocPart => C(doc, "GetPartBox", true),
            SwDocAssembly => C(doc, "GetBox", 0),
            _ => null,
        };
        var b = Arr(box).Select(Convert.ToDouble).ToArray();
        if (b.Length < 6) return null;
        return new JsonObject
        {
            ["xMm"] = ToMm(b[3] - b[0]), ["yMm"] = ToMm(b[4] - b[1]), ["zMm"] = ToMm(b[5] - b[2]),
        };
    }

    private static readonly string[] LengthUnits = ["mm", "cm", "m", "in", "ft", "ft-in", "Å", "nm", "µm", "mil", "µin"]; // swLengthUnit_e

    // ── Selection ────────────────────────────────────────────────────────────────────────────────────

    private const int SelEdge = 1, SelFace = 2, SelVertex = 3, SelDimension = 14, SelComponent = 20, SelMate = 21, SelBodyFeature = 22;

    private JsonObject Selection(object doc)
    {
        var sm = G(doc, "SelectionManager")!;
        int count = Convert.ToInt32(C(sm, "GetSelectedObjectCount2", -1), CultureInfo.InvariantCulture);
        var items = new JsonArray();
        for (int i = 1; i <= count && items.Count < 20; i++)
        {
            int type = Convert.ToInt32(C(sm, "GetSelectedObjectType3", i, -1), CultureInfo.InvariantCulture);
            object? obj = C(sm, "GetSelectedObject6", i, -1);
            var item = new JsonObject { ["index"] = i };
            try
            {
                switch (type)
                {
                    case SelFace when obj is not null:
                        item["kind"] = "face";
                        item["areaMm2"] = Math.Round(Convert.ToDouble(C(obj, "GetArea"), CultureInfo.InvariantCulture) * 1e6, 2);
                        DescribeSurface(C(obj, "GetSurface")!, item);
                        if (C(obj, "GetFeature") is { } owner) item["feature"] = G(owner, "Name") as string;
                        break;
                    case SelEdge when obj is not null:
                        item["kind"] = "edge";
                        var p = Arr(C(obj, "GetCurveParams2")).Select(Convert.ToDouble).ToArray();
                        if (p.Length >= 8)
                        {
                            item["startMm"] = MmArray(p[..3]);
                            item["endMm"] = MmArray(p[3..6]);
                            item["lengthMm"] = ToMm(C(C(obj, "GetCurve")!, "GetLength3", p[6], p[7]));
                        }
                        break;
                    case SelVertex when obj is not null:
                        item["kind"] = "vertex";
                        item["pointMm"] = MmArray(Arr(C(obj, "GetPoint")).Select(Convert.ToDouble));
                        break;
                    case SelBodyFeature or SelMate when obj is not null:
                        item["kind"] = type == SelMate ? "mate" : "feature";
                        item["name"] = G(obj, "Name") as string;
                        item["type"] = C(obj, "GetTypeName2") as string;
                        break;
                    case SelComponent when obj is not null:
                        item["kind"] = "component";
                        item["name"] = G(obj, "Name2") as string;
                        item["file"] = C(obj, "GetPathName") as string;
                        break;
                    case SelDimension when obj is not null && C(obj, "GetDimension2", 0) is { } dim:
                        item["kind"] = "dimension";
                        item["name"] = G(dim, "FullName") as string;
                        item["systemValue"] = Convert.ToDouble(G(dim, "SystemValue"), CultureInfo.InvariantCulture);
                        break;
                    default:
                        item["kind"] = $"other (swSelectType {type})";
                        break;
                }
            }
            catch (Exception ex) when (Recoverable(ex)) { item["error"] = Message(ex); }
            items.Add(item);
        }
        return new JsonObject { ["count"] = count, ["items"] = items, ["note"] = count == 0 ? "Nothing is selected in SolidWorks." : null };
    }

    private static void DescribeSurface(object surface, JsonObject into)
    {
        if (C(surface, "IsPlane") is true)
        {
            var pp = Arr(G(surface, "PlaneParams")).Select(Convert.ToDouble).ToArray(); // normal xyz, point xyz
            into["surface"] = "plane";
            if (pp.Length >= 6) { into["normal"] = new JsonArray(pp[..3].Select(v => (JsonNode)Math.Round(v, 4)).ToArray()); into["pointMm"] = MmArray(pp[3..6]); }
        }
        else if (C(surface, "IsCylinder") is true)
        {
            var cp = Arr(G(surface, "CylinderParams")).Select(Convert.ToDouble).ToArray(); // origin xyz, axis xyz, radius
            into["surface"] = "cylinder";
            if (cp.Length >= 7)
            {
                into["diameterMm"] = ToMm(2 * cp[6]);
                into["axis"] = new JsonArray(cp[3..6].Select(v => (JsonNode)Math.Round(v, 4)).ToArray());
                into["axisPointMm"] = MmArray(cp[..3]);
            }
        }
        else into["surface"] = C(surface, "IsCone") is true ? "cone" : C(surface, "IsSphere") is true ? "sphere" : "freeform";
    }

    // ── Assemblies ───────────────────────────────────────────────────────────────────────────────────

    private static JsonObject Components(object asm)
    {
        var all = Arr(C(asm, "GetComponents", false));
        var bom = all
            .Where(c => C(c, "IsSuppressed") is not true)
            .GroupBy(c => (File: C(c, "GetPathName") as string ?? "", Config: G(c, "ReferencedConfiguration") as string ?? ""))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.File, StringComparer.OrdinalIgnoreCase)
            .Take(MaxListed)
            .Select(g => (JsonNode)new JsonObject { ["file"] = Path.GetFileName(g.Key.File), ["configuration"] = g.Key.Config, ["quantity"] = g.Count() })
            .ToArray();
        var top = Arr(C(asm, "GetComponents", true)).Take(200).Select(c =>
        {
            var item = new JsonObject { ["name"] = G(c, "Name2") as string, ["file"] = Path.GetFileName(C(c, "GetPathName") as string ?? "") };
            if (C(c, "IsSuppressed") is true) item["suppressed"] = true;
            if (C(c, "IsFixed") is true) item["fixed"] = true;
            return (JsonNode)item;
        }).ToArray();
        return new JsonObject
        {
            ["total"] = all.Length,
            ["suppressed"] = all.Count(c => C(c, "IsSuppressed") is true),
            ["bom"] = new JsonArray(bom),
            ["topLevel"] = new JsonArray(top),
        };
    }

    private JsonObject Mates(object asm)
    {
        var mates = new JsonArray();
        int errors = 0;
        foreach (var group in TreeFeatures(asm).Where(f => C(f, "GetTypeName2") as string == "MateGroup"))
        {
            foreach (var m in SubFeatures(group))
            {
                var (code, warning) = FeatureError(m);
                if (code != 0 && !warning) errors++;
                if (mates.Count >= MaxListed) continue;
                var item = new JsonObject { ["name"] = G(m, "Name") as string, ["type"] = C(m, "GetTypeName2") as string };
                if (C(m, "IsSuppressed") is true) item["suppressed"] = true;
                if (code != 0) item[warning ? "warning" : "error"] = code;
                try
                {
                    if (C(m, "GetSpecificFeature2") is { } mate)
                    {
                        int n = Convert.ToInt32(C(mate, "GetMateEntityCount"), CultureInfo.InvariantCulture);
                        item["components"] = new JsonArray(Enumerable.Range(0, n)
                            .Select(j => G(C(mate, "MateEntity", j)!, "ReferenceComponent") is { } comp ? G(comp, "Name2") as string : null)
                            .Select(s => (JsonNode?)s).ToArray());
                    }
                }
                catch (Exception ex) when (Recoverable(ex)) { }
                mates.Add(item);
            }
        }
        return new JsonObject { ["count"] = mates.Count, ["withErrors"] = errors, ["list"] = mates };
    }

    private static JsonObject Interference(object asm)
    {
        var idm = G(asm, "InterferenceDetectionManager") ?? throw new InvalidOperationException("interference detection is not available");
        foreach (var (name, value) in new[] { ("TreatCoincidenceAsInterference", false), ("TreatSubAssembliesAsComponents", false), ("IncludeMultibodyPartInterferences", false),
                     ("MakeInterferingPartsTransparent", false), ("CreateFastenersFolder", false), ("IgnoreHiddenBodies", true) })
        {
            try { S(idm, name, value); } catch (Exception ex) when (Recoverable(ex)) { }
        }
        try
        {
            int count = Convert.ToInt32(C(idm, "GetInterferenceCount"), CultureInfo.InvariantCulture);
            var list = Arr(C(idm, "GetInterferences")).Take(50).Select(i => (JsonNode)new JsonObject
            {
                ["components"] = new JsonArray(Arr(G(i, "Components")).Select(c => (JsonNode?)(G(c, "Name2") as string)).ToArray()),
                ["volumeMm3"] = Math.Round(Convert.ToDouble(G(i, "Volume"), CultureInfo.InvariantCulture) * 1e9, 3),
            }).ToArray();
            return new JsonObject { ["count"] = count, ["list"] = new JsonArray(list), ["note"] = count == 0 ? "No interferences." : null };
        }
        finally { try { C(idm, "Done"); } catch (Exception ex) when (Recoverable(ex)) { } }
    }

    // ── Inspect ──────────────────────────────────────────────────────────────────────────────────────

    private JsonObject DoInspect(string? path, IReadOnlySet<string> sections)
    {
        Connect();
        var (doc, openedHere) = Target(path);
        _doc = doc;
        try
        {
            int type = Convert.ToInt32(C(doc, "GetType"), CultureInfo.InvariantCulture);
            var report = new JsonObject
            {
                ["title"] = C(doc, "GetTitle") as string,
                ["path"] = C(doc, "GetPathName") as string is { Length: > 0 } p ? p : null,
                ["type"] = TypeName(type),
                ["unsaved"] = C(doc, "GetSaveFlag") is true,
            };
            void Section(string name, Func<JsonNode?> read)
            {
                try { report[name] = read(); }
                catch (Exception ex) when (Recoverable(ex)) { report[name] = new JsonObject { ["error"] = Message(ex) }; }
            }
            Section("units", () => Arr(C(doc, "GetUnits")) is { Length: > 0 } u && Convert.ToInt32(u[0], CultureInfo.InvariantCulture) is var i && i >= 0 && i < LengthUnits.Length ? LengthUnits[i] : null);
            if (type != SwDocDrawing) Section("sizeMm", () => Size(doc));
            bool model = type is SwDocPart or SwDocAssembly, assembly = type == SwDocAssembly;
            var skipped = new List<string>();
            void Want(string name, bool applies, Func<JsonNode?> read)
            {
                if (!sections.Contains(name)) return;
                if (applies) Section(name, read); else skipped.Add(name);
            }
            Want("features", true, () => Features(doc));
            Want("dimensions", model, () => Dimensions(doc));
            Want("equations", model, () => Equations(doc));
            Want("properties", true, () => Properties(doc));
            Want("mass", model, () => Mass(doc));
            Want("configurations", model, () => Configurations(doc));
            Want("selection", true, () => Selection(doc));
            Want("components", assembly, () => Components(doc));
            Want("mates", assembly, () => Mates(doc));
            Want("interference", assembly, () => Interference(doc));
            // Sections that do not apply to this kind of document are named, so "no mates" is not read as "none found".
            if (skipped.Count > 0) report["notApplicable"] = new JsonArray(skipped.Select(s => (JsonNode)s).ToArray());
            if (_log.Count > 0) report["log"] = Log();
            return report;
        }
        finally
        {
            if (openedHere) try { C(_sw, "CloseDoc", (string)C(doc, "GetTitle")!); } catch (Exception ex) when (Recoverable(ex)) { }
        }
    }

    // ── Edit ─────────────────────────────────────────────────────────────────────────────────────────

    private const int SetValueInAllConfigurations = 2;  // swSetValueInConfiguration_e
    private const int SuppressFeature = 0, UnsuppressFeature = 1, ThisConfiguration = 1; // swFeatureSuppressionAction_e, swInConfigurationOpts_e
    private const int ComponentSuppressed = 0, ComponentFullyResolved = 2; // swComponentSuppressionState_e
    private const string MaterialDatabase = "SOLIDWORKS Materials";

    private JsonObject DoEdit(string? path, JsonArray ops, bool save)
    {
        Connect();
        var (doc, _) = Target(path); // stays open: the user reviews the change on screen
        _doc = doc;
        Activate(doc);
        int type = Convert.ToInt32(C(doc, "GetType"), CultureInfo.InvariantCulture);
        if (type == SwDocDrawing) throw new ArgumentException("edit_model changes parts and assemblies; open the model the drawing shows");
        string title = (string)C(doc, "GetTitle")!;
        var results = new JsonArray();
        bool globals = false;
        foreach (var node in ops)
        {
            var op = node!.AsObject();
            string kind = (string?)op["op"] ?? "";
            try
            {
                string detail = ApplyOp(doc, type, kind, op);
                globals |= kind == "set_global";
                results.Add(new JsonObject { ["op"] = kind, ["ok"] = true, ["detail"] = detail });
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                results.Add(new JsonObject { ["op"] = kind, ["ok"] = false, ["error"] = Message(ex) });
            }
        }
        if (globals) C(C(doc, "GetEquationMgr")!, "EvaluateAll");
        C(doc, "ForceRebuild3", false);
        var report = new JsonObject { ["document"] = title, ["results"] = results };
        try { report["rebuildErrors"] = RebuildErrors(doc); } catch (Exception ex) when (Recoverable(ex)) { report["rebuildErrors"] = "unknown: " + Message(ex); }
        try { report["sizeMm"] = Size(doc); } catch (Exception ex) when (Recoverable(ex)) { }
        try { var m = Mass(doc); report["massKg"] = m["massKg"]?.DeepClone(); if (m["material"] is { } mat) report["material"] = mat.DeepClone(); }
        catch (Exception ex) when (Recoverable(ex)) { }
        try { report["configuration"] = ActiveConfiguration(doc); } catch (Exception ex) when (Recoverable(ex)) { }
        if (save && results.OfType<JsonObject>().Any(r => (bool?)r["ok"] == true))
        {
            if (C(doc, "GetPathName") is not string { Length: > 0 } file) throw new ArgumentException($"{title} was never saved; save it with document_ops save_as first");
            SaveDoc(doc, title);
            report["saved"] = file;
        }
        else
        {
            report["saved"] = false;
            report["note"] = "The changes are open in SolidWorks and NOT saved: the user can review them, undo with Ctrl+Z, or save with document_ops.";
        }
        if (_log.Count > 0) report["log"] = Log();
        return report;
    }

    private string ApplyOp(object doc, int type, string kind, JsonObject op)
    {
        string Str(string field) => (string?)op[field] ?? throw new ArgumentException($"{kind} needs {field}");
        switch (kind)
        {
            case "set_dimension":
            {
                string name = Str("name");
                object dim = C(doc, "Parameter", name)
                    ?? (name.Count(c => c == '@') >= 2 ? C(doc, "Parameter", name[..name.LastIndexOf('@')]) : null)
                    ?? throw new ArgumentException($"no dimension named {name} (inspect_model dimensions lists them)");
                double value = (double)op["value"]!;
                string unit = (string?)op["unit"] ?? "mm";
                double si = unit switch { "mm" => value / 1000, "in" => value * 0.0254, "deg" => value * Math.PI / 180, _ => throw new ArgumentException("unit must be mm, in or deg") };
                double before = Convert.ToDouble(G(dim, "SystemValue"), CultureInfo.InvariantCulture);
                try { C(dim, "SetSystemValue3", si, SetValueInAllConfigurations, Array.Empty<string>()); }
                catch (InvalidOperationException) { S(dim, "SystemValue", si); }
                C(doc, "EditRebuild3");
                double after = Convert.ToDouble(G(dim, "SystemValue"), CultureInfo.InvariantCulture);
                if (Math.Abs(after - si) > 1e-9 * Math.Max(1, Math.Abs(si)))
                    throw new InvalidOperationException($"{name} stayed at {Show(after, unit)}: it is driven by an equation or a global (change that instead) or the value is not possible");
                return $"{name}: {Show(before, unit)} → {Show(after, unit)}";
            }
            case "set_global":
            {
                string name = Str("name");
                double value = (double)op["value"]!;
                var eqm = C(doc, "GetEquationMgr")!;
                int count = Convert.ToInt32(C(eqm, "GetCount"), CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    var m = GlobalRegex().Match(G(eqm, "Equation", i) as string ?? "");
                    if (!m.Success || m.Groups[1].Value != name) continue;
                    S(eqm, "Equation", i, $"\"{name}\" = {Num(value)}");
                    return $"global {name} = {Num(value)}";
                }
                throw new ArgumentException($"no global variable named {name} (inspect_model equations lists them)");
            }
            case "suppress" or "unsuppress":
            {
                string name = Str("feature");
                object feature = C(doc, "FeatureByName", name) ?? throw new ArgumentException($"no feature named {name} (inspect_model features lists them)");
                bool suppress = kind == "suppress";
                try { C(feature, "SetSuppression2", suppress ? SuppressFeature : UnsuppressFeature, ThisConfiguration, Array.Empty<string>()); }
                catch (InvalidOperationException)
                {
                    Clear();
                    C(feature, "Select2", false, 0);
                    C(doc, suppress ? "EditSuppress2" : "EditUnsuppress2");
                }
                if ((C(feature, "IsSuppressed") is true) != suppress) throw new InvalidOperationException($"SolidWorks did not {kind} {name}");
                return $"{name} {(suppress ? "suppressed" : "unsuppressed")}";
            }
            case "rename_feature":
            {
                string name = Str("feature"), newName = Str("newName");
                object feature = C(doc, "FeatureByName", name) ?? throw new ArgumentException($"no feature named {name}");
                S(feature, "Name", newName);
                if (G(feature, "Name") as string != newName) throw new InvalidOperationException($"SolidWorks kept the name {G(feature, "Name")} (is {newName} taken?)");
                return $"{name} → {newName}";
            }
            case "set_property":
            {
                string name = Str("name"), config = (string?)op["configuration"] ?? "";
                string value = op["value"] is JsonValue v && v.TryGetValue(out string? text) ? text : op["value"]!.ToJsonString();
                var cpm = G(G(doc, "Extension")!, "CustomPropertyManager", config)!;
                C(cpm, "Add3", name, SwCustomInfoText, value, SwCustomPropertyReplace);
                string? stored = PropertyList(doc, config)[name] is JsonValue s && s.TryGetValue(out string? got) ? got : null;
                if (stored is null) throw new InvalidOperationException($"SolidWorks did not store the property {name}");
                return $"{name} = \"{value}\"" + (config.Length > 0 ? $" in {config}" : "");
            }
            case "delete_property":
            {
                string name = Str("name"), config = (string?)op["configuration"] ?? "";
                var cpm = G(G(doc, "Extension")!, "CustomPropertyManager", config)!;
                try { C(cpm, "Delete2", name); } catch (InvalidOperationException) { C(cpm, "Delete", name); }
                if (PropertyList(doc, config).ContainsKey(name)) throw new InvalidOperationException($"SolidWorks kept the property {name}");
                return $"deleted {name}";
            }
            case "set_material":
            {
                if (type != SwDocPart) throw new ArgumentException("materials are set on parts (open the part)");
                string material = Str("material");
                var configs = Arr(C(doc, "GetConfigurationNames")).OfType<string>().ToList();
                foreach (var config in configs) C(doc, "SetMaterialPropertyName2", config, MaterialDatabase, material);
                string? now = Material(doc);
                if (!string.Equals(now, material, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"\"{material}\" was not found in {MaterialDatabase} (the part still has {now}); use the exact name shown in SolidWorks' material library");
                return $"material {material} in {configs.Count} configuration(s)";
            }
            case "activate_configuration":
            {
                string name = Str("name");
                if (C(doc, "ShowConfiguration2", name) is not true) throw new ArgumentException($"no configuration named {name} (inspect_model configurations lists them)");
                return $"active configuration: {name}";
            }
            case "add_configuration":
            {
                string name = Str("name");
                if (Arr(C(doc, "GetConfigurationNames")).OfType<string>().Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"a configuration named {name} exists already");
                if (C(doc, "AddConfiguration3", name, (string?)op["description"] ?? "", "", 0) is null or false) throw new InvalidOperationException($"SolidWorks could not add the configuration {name}");
                return $"added configuration {name} (now active: {ActiveConfiguration(doc)})";
            }
            case "suppress_component" or "unsuppress_component":
            {
                if (type != SwDocAssembly) throw new ArgumentException("components are suppressed in assemblies");
                string name = Str("component");
                object comp = C(doc, "GetComponentByName", name) ?? throw new ArgumentException($"no component named {name} (inspect_model components lists them, e.g. Bolt-2)");
                bool suppress = kind == "suppress_component";
                C(comp, "SetSuppression2", suppress ? ComponentSuppressed : ComponentFullyResolved);
                if ((C(comp, "IsSuppressed") is true) != suppress) throw new InvalidOperationException($"SolidWorks did not {(suppress ? "suppress" : "unsuppress")} {name}");
                return $"{name} {(suppress ? "suppressed" : "unsuppressed")}";
            }
            case "rebuild":
                return "rebuilt";
            default:
                return ApplyMoreOps(doc, kind, op) ?? throw new ArgumentException($"unknown op {kind}");
        }
    }

    private static string Show(double si, string unit) => unit switch
    {
        "deg" => Num(si * 180 / Math.PI) + "°",
        "in" => Num(si / 0.0254) + " in",
        _ => Num(si * 1000) + " mm",
    };

    // ── Drawings ─────────────────────────────────────────────────────────────────────────────────────

    private const int SwDefaultTemplateDrawing = 10; // swUserPreferenceStringValue_e
    private const int SwInsertDimensionsMarkedForDrawing = 0x8000, SwImportModelItemsFromEntireModel = 0; // swInsertAnnotation_e, swImportModelItemsSource_e

    /// <summary>Sheet name → swDwgPaperSizes_e.</summary>
    public static int PaperSize(string sheet) => sheet.ToUpperInvariant() switch
    {
        "A" => 0, "B" => 2, "C" => 3, "D" => 4, "E" => 5, "A4" => 6, "A3" => 8, "A2" => 9, "A1" => 10, "A0" => 11,
        _ => throw new ArgumentException($"unknown sheet {sheet}"),
    };

    private JsonObject DoDrawing(CadDrawingRequest r)
    {
        Connect();
        var (model, _) = Target(r.ModelPath);
        int modelType = Convert.ToInt32(C(model, "GetType"), CultureInfo.InvariantCulture);
        if (modelType is not (SwDocPart or SwDocAssembly)) throw new ArgumentException("a drawing is made of a part or an assembly");
        string modelPath = C(model, "GetPathName") as string is { Length: > 0 } mp ? mp
            : throw new ArgumentException($"{C(model, "GetTitle")} was never saved: a drawing refers to the model's file, so save it first");
        string template = C(_sw, "GetUserPreferenceStringValue", SwDefaultTemplateDrawing) as string ?? "";
        if (template.Length == 0 || !File.Exists(template))
            throw new InvalidOperationException("SolidWorks has no default drawing template (Tools > Options > Default Templates)");
        _doc = C(_sw, "NewDocument", template, PaperSize(r.Sheet), 0.0, 0.0) ?? throw new InvalidOperationException("SolidWorks could not create a drawing from " + template);
        try
        {
            if (C(_doc, r.FirstAngle ? "Create1stAngleViews2" : "Create3rdAngleViews2", modelPath) is not true)
                _log.Add("the standard views could not be placed");
            if (r.Isometric)
            {
                double w = 0.42, h = 0.297; // A3 when the sheet does not say
                try
                {
                    var props = Arr(C(C(_doc, "GetCurrentSheet")!, "GetProperties2")).Select(Convert.ToDouble).ToArray();
                    if (props.Length >= 7 && props[5] > 0 && props[6] > 0) (w, h) = (props[5], props[6]);
                }
                catch (Exception ex) when (Recoverable(ex)) { }
                if (C(_doc, "CreateDrawViewFromModelView3", modelPath, "*Isometric", w * 0.78, h * 0.72, 0.0) is null) _log.Add("the isometric view could not be placed");
            }
            if (r.Dimensions)
            {
                try
                {
                    int n = Arr(C(_doc, "InsertModelAnnotations3", SwImportModelItemsFromEntireModel, SwInsertDimensionsMarkedForDrawing, true, false, false, false)).Length;
                    _log.Add($"{n} model dimension(s) inserted (check their placement)");
                }
                catch (Exception ex) when (Recoverable(ex)) { _log.Add("model dimensions could not be inserted: " + Message(ex)); }
            }
            int views = 0;
            try { views = Arr(C(C(_doc, "GetCurrentSheet")!, "GetViews")).Length; } catch (Exception ex) when (Recoverable(ex)) { }
            C(_doc, "ViewZoomtofit2");
            int err = (int)C(_doc, "SaveAs3", r.OutputPath, 0, SwSaveAsSilent)!;
            if (err != 0 || !File.Exists(r.OutputPath)) throw new InvalidOperationException($"the drawing could not be saved (error {err})");
            var report = new JsonObject { ["saved"] = r.OutputPath, ["model"] = modelPath, ["sheet"] = r.Sheet, ["views"] = views };
            if (r.ExportPdf)
            {
                string pdf = Path.ChangeExtension(r.OutputPath, ".pdf");
                int pdfErr = (int)C(_doc, "SaveAs3", pdf, 0, SwSaveAsCopy)!;
                report["pdf"] = pdfErr == 0 && File.Exists(pdf) ? pdf : $"PDF export failed (error {pdfErr})";
            }
            report["log"] = Log();
            return report;
        }
        catch
        {
            DiscardUnsaved();
            throw;
        }
    }

    // ── Pictures ─────────────────────────────────────────────────────────────────────────────────────

    private static string NamedView(string view) => view switch
    {
        "iso" => "*Isometric", "trimetric" => "*Trimetric", "front" => "*Front", "back" => "*Back", "left" => "*Left",
        "right" => "*Right", "top" => "*Top", "bottom" => "*Bottom",
        _ => throw new ArgumentException($"unknown view {view}"),
    };

    private JsonObject DoViews(string? path, IReadOnlyList<string> views, string folder)
    {
        Connect();
        var (doc, openedHere) = Target(path);
        _doc = doc;
        try
        {
            Activate(doc);
            bool drawing = C(doc, "GetType") is SwDocDrawing;
            var images = new JsonArray();
            int i = 0;
            foreach (var view in views)
            {
                if (drawing && view != "current") { _log.Add($"{view}: drawings only have the current view"); continue; }
                try
                {
                    if (view != "current")
                    {
                        C(doc, "ShowNamedView2", NamedView(view), -1);
                        C(doc, "ViewZoomtofit2");
                    }
                    string png = Path.Combine(folder, $"{++i:00}-{view}.png");
                    int err = (int)C(doc, "SaveAs3", png, 0, SwSaveAsCopy)!;
                    if (err == 0 && File.Exists(png)) images.Add(png);
                    else _log.Add($"{view}: the picture could not be saved (error {err})");
                }
                catch (Exception ex) when (Recoverable(ex)) { _log.Add($"{view}: {Message(ex)}"); }
            }
            return new JsonObject { ["document"] = C(doc, "GetTitle") as string, ["views"] = new JsonArray(views.Select(v => (JsonNode)v).ToArray()), ["images"] = images, ["log"] = Log() };
        }
        finally
        {
            if (openedHere) try { C(_sw, "CloseDoc", (string)C(doc, "GetTitle")!); } catch (Exception ex) when (Recoverable(ex)) { }
        }
    }
}
