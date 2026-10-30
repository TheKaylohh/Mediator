namespace Mediator.Core.Cad;

/// <summary>One operation of an ops-taking CAD tool: its required fields and what it does (shown to the model).</summary>
public sealed record CadOp(string Name, string[] Required, string Help);

/// <summary>
/// The CAD-neutral operation vocabulary (V17). Every program uses the same names, fields and units, so the model learns
/// one language for SolidWorks, FreeCAD, AutoCAD, Inventor, NX, Creo and CATIA; a program lists the subset it supports
/// (<see cref="ICadApp.Ops"/>). Units: lengths mm, angles degrees, forces N, pressures MPa. Points are [x, y, z] in the
/// model (or assembly) frame unless a field says sheet [x, y] (drawings, mm from the sheet's lower-left corner).
/// </summary>
public static class CadOpCatalog
{
    private static CadOp Op(string name, string help, params string[] required) => new(name, required, help);

    public static readonly IReadOnlyList<CadOp> Edit =
    [
        Op("set_dimension", "{name:\"D1@Boss-Extrude1\", value, unit?: mm|in|deg} (names from inspect_model dimensions)", "name", "value"),
        Op("set_global", "{name:\"W\", value}: a global variable / user parameter (document units, mm for Mediator parts)", "name", "value"),
        Op("add_global", "{name, value}: a new global variable / user parameter", "name", "value"),
        Op("suppress", "{feature}", "feature"),
        Op("unsuppress", "{feature}", "feature"),
        Op("rename_feature", "{feature, newName}", "feature", "newName"),
        Op("delete_feature", "{feature}: deletes it (and what depends on it)", "feature"),
        Op("set_property", "{name, value, configuration?}: custom property / iProperty / attribute (configuration \"\" or none = the file)", "name", "value"),
        Op("delete_property", "{name, configuration?}", "name"),
        Op("set_material", "{material}: a name from the program's material library", "material"),
        Op("set_color", "{rgb:[r,g,b] 0-255, feature?|component?}: appearance of the part, a feature or a component", "rgb"),
        Op("activate_configuration", "{name}", "name"),
        Op("add_configuration", "{name, description?}", "name"),
        Op("suppress_component", "{component:\"Bolt-2\"}", "component"),
        Op("unsuppress_component", "{component}", "component"),
        Op("set_layer", "{handle|layer:name, on?, frozen?, locked?, color?:index}: layer state, or move entity {handle} to {layer}"),
        Op("transform", "{handle, move?:[dx,dy,dz], rotate?:deg, about?:[x,y,z], scale?, copy?}: moves/rotates/scales a drawing entity", "handle"),
        Op("delete_entity", "{handle}", "handle"),
        Op("set_attribute", "{handle, tag, value}: a block reference's attribute", "handle", "tag", "value"),
        Op("run_command", "{command}: a command-line command of the program, as typed (e.g. \"_PURGE _ALL * _N \")", "command"),
        Op("undo", "{steps?}: undoes the last changes"),
        Op("rebuild", "{}: rebuilds / recomputes / regenerates"),
    ];

    public static readonly IReadOnlyList<CadOp> Feature =
    [
        Op("extrude", "{sketch:name | on + profile, depth | \"through_all\", midPlane?, reverse?, draft?:deg}: adds material"),
        Op("cut", "{sketch | on + profile, depth | \"through_all\", reverse?}: removes material"),
        Op("revolve", "{sketch | on + profile (with a centerline), angle?:360, cut?}"),
        Op("sweep", "{profile:sketch, path:sketch, guides?}", "profile", "path"),
        Op("loft", "{profiles:[sketches in order], guides?}", "profiles"),
        Op("fillet", "{radius, edgesNear?:[[x,y,z],...]} (no edgesNear = all edges)", "radius"),
        Op("chamfer", "{distance, angle?:45, edgesNear?}", "distance"),
        Op("shell", "{thickness, openAt?:[x,y,z] (a face to remove)}", "thickness"),
        Op("hole", "{on:plane|[x,y,z] face point, points:[[x,y],...] sketch coords, size:\"M6\", type?: clearance|tapped|counterbore|countersink, depth? | \"through_all\"}: standard holes", "on", "points", "size"),
        Op("pattern_linear", "{features:[names], direction: x|y|z, count, spacing, direction2?, count2?, spacing2?}", "features", "direction", "count", "spacing"),
        Op("pattern_circular", "{features:[names], axis: x|y|z, count, angle?:360}", "features", "axis", "count"),
        Op("mirror", "{plane: Front|Top|Right|name, features?:[names]} (no features = the whole body)", "plane"),
        Op("plane", "{name, base: Front|Top|Right|name, offset}: a reference plane", "name", "base", "offset"),
        Op("sketch", "{name, on, profile}: a named sketch for loft/sweep/later features", "name", "on", "profile"),
        Op("curve3d", "{name, points:[[x,y,z],...], closed?}: a 3D spline (sweep path, loft guide)", "name", "points"),
        Op("thicken", "{thickness, side?: out|in|both}: turns a surface into a solid", "thickness"),
        Op("knit", "{solid?}: joins surfaces"),
        Op("boolean", "{operation: union|subtract|intersect, base, tools:[names]}: combines bodies", "operation", "base", "tools"),
        Op("delete", "{feature}: removes a feature", "feature"),
    ];

    public static readonly IReadOnlyList<CadOp> Assembly =
    [
        Op("insert", "{path, position?:[x,y,z] mm, fixed?, configuration?, name?}: adds a part or sub-assembly", "path"),
        Op("mate", "{type: coincident|concentric|parallel|perpendicular|distance|angle|tangent, a:{component, plane: front|top|right} | {near:[x,y,z]}, b:{...}, value?: mm|deg, flip?, align?: aligned|anti_aligned}", "type", "a", "b"),
        Op("fix", "{component}: grounds it", "component"),
        Op("float", "{component}", "component"),
        Op("move", "{component, position:[x,y,z] mm}", "component", "position"),
        Op("replace", "{component, path}: swaps in another file", "component", "path"),
        Op("delete", "{component}", "component"),
        Op("pattern_linear", "{component, direction: x|y|z, count, spacing}: inserts copies at a spacing", "component", "direction", "count", "spacing"),
    ];

    public static readonly IReadOnlyList<CadOp> Annotate =
    [
        Op("projected_view", "{view, direction: left|right|top|bottom, at?:[x,y]}", "view", "direction"),
        Op("section_view", "{view, from:[x,y], to:[x,y] (cut line, sheet mm), at:[x,y], label?}", "view", "from", "to", "at"),
        Op("detail_view", "{view, center:[x,y], radius, at:[x,y], scale?:2, label?}", "view", "center", "radius", "at"),
        Op("model_dimensions", "{view?}: inserts the model's dimensions"),
        Op("dimension", "{type: linear|aligned|radial|diameter|angular, points:[[x,y],...], at:[x,y], text?}: a drafting dimension", "type", "points", "at"),
        Op("note", "{text, at:[x,y], height?}: text / note", "text", "at"),
        Op("leader", "{text, points:[[x,y],...]}: a note with an arrow", "text", "points"),
        Op("bom_table", "{at:[x,y], view?}: bill of materials / parts list", "at"),
        Op("balloons", "{view?}: automatic balloons"),
        Op("table", "{at:[x,y], rows:[[cell,...],...], title?}: a table", "at", "rows"),
        Op("insert_block", "{name|path, at:[x,y], scale?, rotation?, attributes?:{tag:value}}: a block / symbol", "at"),
        Op("hatch", "{boundary:[[x,y],...], pattern?:\"ANSI31\", scale?}: hatches a closed boundary", "boundary"),
        Op("layer", "{name, color?:index, linetype?, current?}: creates or sets the current layer", "name"),
    ];

    public static readonly IReadOnlyList<CadOp> Simulate =
    [
        Op("material", "{name}: the material for the analysis (else the part's)", "name"),
        Op("fixed", "{faces:[\"Face3\",...] | facesNear:[[x,y,z],...]}: fixed support"),
        Op("force", "{faces | facesNear, newtons, direction?:[x,y,z] (default: normal)}", "newtons"),
        Op("pressure", "{faces | facesNear, mpa}", "mpa"),
        Op("mesh", "{size?: mm}"),
        Op("solve", "{}: runs a linear static analysis and reports max von Mises stress, max displacement and the safety factor"),
    ];

    /// <summary>The sketch tool's fields (not an ops list): what the description shows.</summary>
    public const string SketchEntities =
        "on: \"Front\"|\"Top\"|\"Right\" (or XY/XZ/YZ) or a point [x,y,z] on a planar face; name?; entities: [{type:\"line\", a:[x,y], b:[x,y]} | " +
        "{type:\"centerline\", a, b} | {type:\"rect\", cx, cy, w, h} | {type:\"circle\", cx, cy, d} | {type:\"arc\", a, mid, b} | {type:\"polygon\", points:[[x,y],...]} | " +
        "{type:\"slot\", a, b, width} | {type:\"spline\", points, closed?} | {type:\"point\", at}] (sketch coordinates, mm); constraints?: [{type: horizontal|vertical|" +
        "coincident|tangent|parallel|perpendicular|equal|concentric|collinear|fix, entities:[i, j?]} (indexes into entities)]; dimensions?: [{entity:i, value} " +
        "(line length, circle/arc diameter) | {entities:[i,j], value} (distance; angle in deg between two lines)]";

    public static IReadOnlyList<CadOp> For(CadCapability c) => c switch
    {
        CadCapability.Edit => Edit,
        CadCapability.Feature => Feature,
        CadCapability.Assembly => Assembly,
        CadCapability.Annotate => Annotate,
        CadCapability.Simulate => Simulate,
        _ => [],
    };

    public static IReadOnlyList<string> Names(CadCapability c) => For(c).Select(o => o.Name).ToList();

    public static CadOp? Find(CadCapability c, string name) => For(c).FirstOrDefault(o => o.Name == name);
}
