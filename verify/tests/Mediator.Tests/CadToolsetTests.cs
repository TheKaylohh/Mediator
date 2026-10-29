using System.Text.Json.Nodes;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Core.Swx;

namespace Mediator.Tests;

/// <summary>
/// V10: SolidWorks toolset upgrades: DXF/SVG profile import, curve3d and sketch lock in the recipe; session, document and
/// neutral-export tools in every CAD job; files a prompt names become read-only folders.
/// </summary>
static class CadToolsetTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static double Eval(JsonNode? n) => double.Parse(n!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public static void DxfAndSvgProfiles()
    {
        // Four separate LINEs and an ARC drawn one by one (any order, one reversed) chain into one closed outline; a CIRCLE stays a circle.
        string dxf = string.Join("\n",
            "0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", "4", "0", "ENDSEC",
            "0", "SECTION", "2", "ENTITIES",
            "0", "LINE", "8", "0", "10", "0", "20", "0", "11", "100", "21", "0",
            "0", "LINE", "8", "0", "10", "100", "20", "50", "11", "100", "21", "0",   // reversed
            "0", "ARC", "8", "0", "10", "50", "20", "50", "40", "50", "50", "0", "51", "180",
            "0", "CIRCLE", "8", "0", "10", "50", "20", "25", "40", "10",
            "0", "LINE", "8", "0", "10", "0", "20", "50", "11", "0", "21", "0",
            "0", "ENDSEC", "0", "EOF");
        var d = ProfileImport.ParseDxf(dxf);
        Check.True(d.Contours.Count == 1 && d.Contours[0].Closed && d.Contours[0].Segments.Count == 4, "DXF pieces chain into one closed loop: " + d.Contours.Count);
        Check.True(d.Contours[0].Segments.Count(s => s.Kind == 'A') == 1 && d.Circles.Count == 1 && d.UnitMm == 1, "the arc stays an arc, the circle a circle, mm units");

        // LWPOLYLINE with a bulge (a half circle) and inch units.
        var poly = ProfileImport.ParseDxf(string.Join("\n", "0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", "1", "0", "ENDSEC",
            "0", "SECTION", "2", "ENTITIES", "0", "LWPOLYLINE", "90", "2", "70", "1", "10", "0", "20", "0", "42", "1", "10", "2", "20", "0", "0", "ENDSEC", "0", "EOF"));
        var arc = poly.Contours.Single().Segments[0];
        Check.True(poly.UnitMm == 25.4 && arc.Kind == 'A' && Math.Abs(arc.Points[0].Y + 1) < 1e-9, "bulge 1 = half circle (mid 1 below the chord), inches → 25.4: " + arc.Points[0]);

        // SVG: a path with a curve and Z, a rect inside a translate group, y flipped, px → mm.
        string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
              <path d="M 0 0 L 96 0 C 96 48, 48 96, 0 96 Z"/>
              <g transform="translate(10,10)"><rect x="0" y="0" width="96" height="48"/></g>
              <defs><rect width="5" height="5"/></defs>
            </svg>
            """;
        var s = ProfileImport.ParseSvg(svg);
        Check.True(s.Contours.Count == 2 && s.Contours.All(c => c.Closed), "path and rect closed, defs ignored: " + s.Contours.Count);
        Check.True(s.Contours[0].Segments.Any(x => x.Kind == 'C') && s.Contours[0].AllPoints.All(p => p.Y <= 1e-9), "the curve is sampled; y is flipped (SVG y points down)");
        Check.True(Math.Abs(s.UnitMm * 96 - 25.4) < 1e-9, "96 px = 1 inch");

        // The shapes the builder draws: width scaling and centring.
        string folder = TempDir("cad-import");
        string file = Path.Combine(folder, "plate.dxf");
        File.WriteAllText(file, dxf);
        var shapes = ProfileImport.ToShapes(new JsonObject { ["path"] = file, ["width"] = 50 }, Eval);
        var outline = shapes.OfType<JsonObject>().Single(o => o["outline"] is not null)["outline"]!.AsArray();
        var xs = outline.OfType<JsonArray>().Select(p => (double)p[0]!).ToList();
        Check.True(Math.Abs(xs.Max() - xs.Min() - 50) < 1e-6 && Math.Abs(xs.Max() + xs.Min()) < 1e-6, "width 50 mm, centred on the origin: " + string.Join(",", xs));
        Check.True(outline.OfType<JsonObject>().Any(o => o["arc"] is JsonArray { Count: 2 }) && shapes.Any(o => o!["circle"] is JsonObject c && Math.Abs((double)c["d"]! - 10) < 1e-6),
            "an outline arc is {arc:[mid,end]}; the circle scales with the drawing (d 20 → 10)");

        // A profile file outside the job's folders is refused before SolidWorks sees it.
        var recipe = new JsonObject { ["features"] = new JsonArray(new JsonObject { ["op"] = "extrude", ["profile"] = new JsonArray(new JsonObject { ["import"] = new JsonObject { ["path"] = file } }) }) };
        Check.True(SwxPartToolProvider.AuthorizeImports(recipe, new PathAuthorizer([TempDir("cad-other")])) is { } no && no.Contains("outside_roots", StringComparison.Ordinal), "import outside the roots refused");
        Check.True(SwxPartToolProvider.AuthorizeImports(recipe, new PathAuthorizer([], [folder])) is null, "a read-only folder is enough to import");
    }

    public static void ToolsetAndReadRoots()
    {
        foreach (var t in new[] { SwxPartToolProvider.SessionTool, SwxPartToolProvider.DocumentTool, SwxPartToolProvider.ExportTool })
            Check.True(JobSpecFactory.CadTools.Contains(t) && SwxPartToolProvider.ToolNames.Contains(t), t + " is in every CAD job and warms SolidWorks up");
        var tools = new SwxPartToolProvider().Tools.ToDictionary(t => t.Name);
        Check.True(tools[SwxPartToolProvider.SessionTool].Risk == Mediator.Core.Bus.ToolRisk.ReadOnly && tools[SwxPartToolProvider.ExportTool].Risk == Mediator.Core.Bus.ToolRisk.Write, "session reads; export writes");
        Check.True(tools[SwxPartToolProvider.BuildTool].InputSchema.ToJsonString().Contains("\"curve3d\"", StringComparison.Ordinal)
            && tools[SwxPartToolProvider.BuildTool].Description.Contains("import:", StringComparison.Ordinal), "the build schema offers curve3d and import");
        Check.True(SwxParametric.ExportFormats.Keys.Order().SequenceEqual(["iges", "parasolid", "pdf", "step", "stl"]), "export formats");
        Check.True(SwxParametric.DocType(@"C:\a\b.SLDASM") == 2, "doc type by extension");

        string parts = TempDir("cad-named");
        string wheel = Path.Combine(parts, "wheel.SLDPRT");
        File.WriteAllText(wheel, "x");
        var named = PromptPaths.ExistingPaths($"use {wheel} and mate it, then save to C:\\NoSuchPlace\\Out");
        Check.True(named.SequenceEqual([parts], StringComparer.OrdinalIgnoreCase), "a named existing file → its folder, readable; missing paths ignored: " + string.Join("; ", named));
        if (OperatingSystem.IsWindows())
            Check.True(PromptPaths.ExistingPaths(@"look at C:\Windows\System32\drivers").Count == 0, "protected places are never offered");
        var grant = new PathAuthorizer([], named);
        Check.True(grant.Check(wheel, PathAccess.Read).Allowed && !grant.Check(wheel, PathAccess.Modify).Allowed, "the job may read the named file, not change it");
    }
}
