using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mediator.Core.Blender;

/// <summary>Validated parameters of the car scene. Units: metres, seconds, km/h.</summary>
public sealed record CarSceneParameters
{
    public double DurationSec { get; init; } = 8;
    public int Fps { get; init; } = 24;
    /// <summary>EEVEE (mapped to BLENDER_EEVEE_NEXT on Blender 4.2–4.x, BLENDER_EEVEE on 5.x), CYCLES or WORKBENCH.</summary>
    public string Engine { get; init; } = "EEVEE";
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public IReadOnlyList<double[]> RoadPoints { get; init; } = [[0, 0], [40, 12], [80, -10], [120, 4], [170, 20], [220, 0]];
    public double RoadWidthM { get; init; } = 9;
    public double CarSpeedKmh { get; init; } = 60;
    /// <summary>Chase camera in the car's frame: metres behind (negative X), to the side (Y), above (Z).</summary>
    public double[] CameraOffset { get; init; } = [-9, 3.5, 3.2];
    public int BuildingCount { get; init; } = 60;
    public int Seed { get; init; } = 7;
    /// <summary>"night" (emissive windows, street lights, dark sky) or "day".</summary>
    public string Style { get; init; } = "night";

    public static readonly string[] Engines = ["EEVEE", "CYCLES", "WORKBENCH"];

    public int Frames => (int)Math.Round(DurationSec * Fps);

    /// <summary>Reads tool arguments over the defaults; throws <see cref="FormatException"/> with a model-readable reason.</summary>
    public static CarSceneParameters From(JsonElement a)
    {
        var p = new CarSceneParameters();
        double Num(string name, double fallback, double min, double max)
        {
            if (!a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return fallback;
            double d = v.GetDouble();
            if (double.IsNaN(d) || d < min || d > max) throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max}."));
            return d;
        }
        string engine = a.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()!.ToUpperInvariant() : p.Engine;
        if (engine.StartsWith("BLENDER_EEVEE", StringComparison.Ordinal)) engine = "EEVEE";
        if (engine == "BLENDER_WORKBENCH") engine = "WORKBENCH";
        if (!Engines.Contains(engine)) throw new FormatException("engine must be EEVEE, CYCLES or WORKBENCH.");
        string style = a.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.ToLowerInvariant() : p.Style;
        if (style is not ("night" or "day")) throw new FormatException("style must be night or day.");

        var road = p.RoadPoints;
        if (a.TryGetProperty("roadPoints", out var rp) && rp.ValueKind == JsonValueKind.Array)
        {
            road = rp.EnumerateArray().Select(pt => pt.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList();
            if (road.Count is < 2 or > 64 || road.Any(pt => pt.Length != 2 || pt.Any(c => double.IsNaN(c) || Math.Abs(c) > 5000)))
                throw new FormatException("roadPoints needs 2-64 [x, y] points within ±5000 m.");
        }
        double[] cam = p.CameraOffset;
        if (a.TryGetProperty("cameraOffset", out var co) && co.ValueKind == JsonValueKind.Array)
        {
            cam = co.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (cam.Length != 3 || cam.Any(c => Math.Abs(c) > 100)) throw new FormatException("cameraOffset is [behind, side, up] in metres, each within ±100.");
        }
        int fps = (int)Num("fps", p.Fps, 12, 60);
        return p with
        {
            DurationSec = Num("durationSec", p.DurationSec, 1, 120),
            Fps = fps,
            Engine = engine,
            Width = (int)Num("width", p.Width, 320, 7680),
            Height = (int)Num("height", p.Height, 240, 4320),
            RoadPoints = road,
            RoadWidthM = Num("roadWidthM", p.RoadWidthM, 3, 40),
            CarSpeedKmh = Num("carSpeedKmh", p.CarSpeedKmh, 1, 400),
            CameraOffset = cam,
            BuildingCount = (int)Num("buildingCount", p.BuildingCount, 0, 400),
            Seed = (int)Num("seed", p.Seed, 0, int.MaxValue),
            Style = style,
        };
    }

    public JsonObject ToJson() => new()
    {
        ["durationSec"] = DurationSec, ["fps"] = Fps, ["frames"] = Frames, ["engine"] = Engine, ["width"] = Width, ["height"] = Height,
        ["roadPoints"] = new JsonArray(RoadPoints.Select(pt => (JsonNode)new JsonArray(pt[0], pt[1])).ToArray()),
        ["roadWidthM"] = RoadWidthM, ["carSpeedKmh"] = CarSpeedKmh,
        ["cameraOffset"] = new JsonArray(CameraOffset[0], CameraOffset[1], CameraOffset[2]),
        ["buildingCount"] = BuildingCount, ["seed"] = Seed, ["style"] = Style,
    };
}

/// <summary>
/// The deterministic bpy script behind blender_build_car_scene. Parameters are embedded as JSON (no model text is
/// ever spliced into code). Targets Blender 4.2 through 5.x: the EEVEE engine id is picked from what the running
/// Blender offers, material inputs are looked up by name, and keyframes use the linear-interpolation preference
/// instead of the F-curve API that changed with layered actions in 4.4/5.0.
/// </summary>
public static class CarSceneScript
{
    public static string Render(CarSceneParameters p, string blendPath)
    {
        var config = p.ToJson();
        config["blendPath"] = blendPath;
        string json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return Template.Replace("__CONFIG_JSON__", JsonSerializer.Serialize(json), StringComparison.Ordinal);
    }

    private const string Template = """
# Generated by Mediator (blender_build_car_scene). Run: blender -b --factory-startup -P build_scene.py --python-exit-code 1
import bpy, bmesh, json, math, os, random, sys
from mathutils import Vector, Matrix
from mathutils.geometry import interpolate_bezier

P = json.loads(__CONFIG_JSON__)
if os.path.exists(P["blendPath"]):
    print(json.dumps({"ok": False, "error": "exists: " + P["blendPath"]}))
    sys.exit(3)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
frames = int(P["frames"])
scene.render.fps = int(P["fps"])
scene.frame_start, scene.frame_end = 1, frames
scene.render.resolution_x, scene.render.resolution_y = int(P["width"]), int(P["height"])
scene.render.resolution_percentage = 100
bpy.context.preferences.edit.keyframe_new_interpolation_type = 'LINEAR'

engines = [i.identifier for i in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
want = P["engine"]
if want == "EEVEE":
    engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
elif want == "CYCLES":
    engine = "CYCLES"
else:
    engine = "BLENDER_WORKBENCH"
scene.render.engine = engine
if engine == "CYCLES":
    scene.cycles.samples = 64
night = P["style"] == "night"

def link(obj, collection=None):
    (collection or scene.collection).objects.link(obj)
    return obj

def material(name, color, roughness=0.5, emission=None, strength=0.0, metallic=0.0):
    m = bpy.data.materials.new(name)
    if m.node_tree is None:  # Blender < 5 creates materials without nodes; 5.x always has them (use_nodes is deprecated)
        m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    if emission is not None:
        key = "Emission Color" if "Emission Color" in bsdf.inputs else "Emission"
        bsdf.inputs[key].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = strength
    return m

def mesh_object(name, verts, faces, mat=None):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    obj = bpy.data.objects.new(name, me)
    if mat is not None:
        me.materials.append(mat)
    return link(obj)

def box(name, size, mat=None):
    sx, sy, sz = size[0] / 2, size[1] / 2, size[2] / 2
    v = [(-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz), (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz)]
    f = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    return mesh_object(name, v, f, mat)

# ── road path (Bezier, auto handles) and its polyline ──
pts = [Vector((x, y, 0.0)) for x, y in P["roadPoints"]]
curve = bpy.data.curves.new("RoadPath", "CURVE")
curve.dimensions = "3D"
spline = curve.splines.new("BEZIER")
spline.bezier_points.add(len(pts) - 1)
for bp, co in zip(spline.bezier_points, pts):
    bp.co = co
    bp.handle_left_type = bp.handle_right_type = "AUTO"
curve.use_path = True
curve.path_duration = frames
path = link(bpy.data.objects.new("RoadPath", curve))
path.hide_render = True

poly = []
bps = spline.bezier_points
for a, b in zip(bps[:-1], bps[1:]):
    seg = interpolate_bezier(a.co, a.handle_right, b.handle_left, b.co, 24)
    poly.extend(seg if not poly else seg[1:])
road_length = sum((q - p).length for p, q in zip(poly[:-1], poly[1:]))

# ── road surface: a flat strip following the polyline ──
half = P["roadWidthM"] / 2
asphalt = material("Asphalt", (0.03, 0.03, 0.035), roughness=0.35 if night else 0.8)
verts, faces = [], []
for i, p in enumerate(poly):
    t = (poly[min(i + 1, len(poly) - 1)] - poly[max(i - 1, 0)]).normalized()
    n = Vector((-t.y, t.x, 0.0))
    verts += [tuple(p + n * half), tuple(p - n * half)]
    if i:
        faces.append((2 * i - 2, 2 * i - 1, 2 * i + 1, 2 * i))
mesh_object("Road", verts, faces, asphalt)
ground = mesh_object("Ground", [(-5000, -5000, -0.05), (5000, -5000, -0.05), (5000, 5000, -0.05), (-5000, 5000, -0.05)], [(0, 1, 2, 3)],
                     material("Ground", (0.02, 0.025, 0.02) if night else (0.25, 0.3, 0.22), roughness=0.9))

# ── low-poly car: chassis + cabin + 4 wheels under one rig that follows the road ──
rig = link(bpy.data.objects.new("CarRig", None))
follow = rig.constraints.new("FOLLOW_PATH")
follow.target = path
follow.use_curve_follow = True
follow.use_fixed_location = True
follow.forward_axis = "FORWARD_X"
paint = material("CarPaint", (0.75, 0.05, 0.08), roughness=0.25, metallic=0.6)
glass = material("Glass", (0.02, 0.03, 0.05), roughness=0.05)
chassis = box("Chassis", (4.3, 1.85, 0.65), paint); chassis.parent = rig; chassis.location = (0, 0, 0.75)
cabin = box("Cabin", (2.2, 1.6, 0.55), glass); cabin.parent = rig; cabin.location = (-0.3, 0, 1.35)
lamp_mat = material("Headlight", (1, 1, 0.9), emission=(1.0, 0.95, 0.8), strength=40.0 if night else 2.0)
for side in (-0.65, 0.65):
    lamp = box("Headlight", (0.1, 0.35, 0.18), lamp_mat); lamp.parent = rig; lamp.location = (2.16, side, 0.85)

radius, wheel_w = 0.36, 0.28
rubber = material("Tyre", (0.02, 0.02, 0.02), roughness=0.9)
bm = bmesh.new()
bmesh.ops.create_cone(bm, cap_ends=True, segments=20, radius1=radius, radius2=radius, depth=wheel_w)
bmesh.ops.rotate(bm, verts=bm.verts, cent=(0, 0, 0), matrix=Matrix.Rotation(math.pi / 2, 3, "X"))  # axle along Y
wheel_mesh = bpy.data.meshes.new("Wheel")
bm.to_mesh(wheel_mesh); bm.free()
wheel_mesh.materials.append(rubber)
distance = min(P["carSpeedKmh"] / 3.6 * P["durationSec"], road_length * 0.98)
wheels = []
for x in (1.35, -1.35):
    for y in (0.95, -0.95):
        w = link(bpy.data.objects.new("Wheel", wheel_mesh))  # linked duplicates share one mesh
        w.parent = rig; w.location = (x, y, radius)
        wheels.append(w)

# Motion: offset along the path, and wheel spin = distance / radius (rolls without slipping).
for frame, frac in ((1, 0.0), (frames, distance / road_length)):
    follow.offset_factor = frac
    follow.keyframe_insert("offset_factor", frame=frame)
    for w in wheels:
        w.rotation_euler = (0.0, frac * road_length / radius, 0.0)
        w.keyframe_insert("rotation_euler", index=1, frame=frame)

# ── city blocks: one shared mesh, scaled instances, emissive windows from a brick pattern in world space ──
random.seed(int(P["seed"]))
facade = bpy.data.materials.new("Facade")
if facade.node_tree is None:
    facade.use_nodes = True
nt = facade.node_tree
bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
bsdf.inputs["Base Color"].default_value = (0.05, 0.055, 0.07, 1) if night else (0.45, 0.45, 0.48, 1)
geo = nt.nodes.new("ShaderNodeNewGeometry")
sep = nt.nodes.new("ShaderNodeSeparateXYZ")
add = nt.nodes.new("ShaderNodeMath"); add.operation = "ADD"
comb = nt.nodes.new("ShaderNodeCombineXYZ")
brick = nt.nodes.new("ShaderNodeTexBrick")
brick.inputs["Scale"].default_value = 0.35
brick.inputs["Color1"].default_value = (1.0, 0.75, 0.35, 1)
brick.inputs["Color2"].default_value = (0.25, 0.45, 1.0, 1)
brick.inputs["Mortar"].default_value = (0, 0, 0, 1)
brick.inputs["Mortar Size"].default_value = 0.09
nt.links.new(geo.outputs["Position"], sep.inputs[0])
nt.links.new(sep.outputs["X"], add.inputs[0]); nt.links.new(sep.outputs["Y"], add.inputs[1])
nt.links.new(add.outputs[0], comb.inputs["X"]); nt.links.new(sep.outputs["Z"], comb.inputs["Y"])
nt.links.new(comb.outputs[0], brick.inputs["Vector"])
key = "Emission Color" if "Emission Color" in bsdf.inputs else "Emission"
nt.links.new(brick.outputs["Color"], bsdf.inputs[key])
bsdf.inputs["Emission Strength"].default_value = 2.5 if night else 0.0

block_mesh = bpy.data.meshes.new("Block")
bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0); bmesh.ops.translate(bm, verts=bm.verts, vec=(0, 0, 0.5)); bm.to_mesh(block_mesh); bm.free()
block_mesh.materials.append(facade)
city = bpy.data.collections.new("City"); scene.collection.children.link(city)
placed = 0
for i in range(int(P["buildingCount"])):
    s = random.random()
    k = min(int(s * (len(poly) - 1)), len(poly) - 2)
    p, q = poly[k], poly[k + 1]
    t = (q - p).normalized(); n = Vector((-t.y, t.x, 0.0))
    side = 1 if i % 2 == 0 else -1
    wdt, dep, hgt = random.uniform(8, 18), random.uniform(8, 18), random.uniform(10, 70)
    centre = p + n * side * (half + 6 + dep / 2 + random.uniform(0, 6))
    if any((centre - r).length < half + dep / 2 + 2 for r in poly[::4]):
        continue  # would overlap the road where it curves
    b = link(bpy.data.objects.new("Block", block_mesh), city)
    b.location = centre
    b.scale = (wdt, dep, hgt)
    b.rotation_euler = (0, 0, math.atan2(t.y, t.x))
    placed += 1

# ── light and sky ──
world = bpy.data.worlds.new("Sky"); scene.world = world
if world.node_tree is None:
    world.use_nodes = True
bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
bg.inputs["Color"].default_value = (0.005, 0.008, 0.02, 1) if night else (0.5, 0.65, 0.9, 1)
bg.inputs["Strength"].default_value = 1.0
sun_data = bpy.data.lights.new("Moon" if night else "Sun", "SUN")
sun_data.energy = 0.3 if night else 4.0
sun = link(bpy.data.objects.new(sun_data.name, sun_data)); sun.rotation_euler = (math.radians(50), 0, math.radians(30))
if night:
    lamp_data = bpy.data.lights.new("StreetLight", "POINT"); lamp_data.energy = 450; lamp_data.color = (1.0, 0.7, 0.4)
    step = max(1, len(poly) // 40)
    for i in range(0, len(poly), step):
        p = poly[i]; t = (poly[min(i + 1, len(poly) - 1)] - poly[max(i - 1, 0)]).normalized()
        light = link(bpy.data.objects.new("StreetLight", lamp_data))
        light.location = p + Vector((-t.y, t.x, 0)) * (half + 1) + Vector((0, 0, 7))

# ── chase camera: rides with the car rig, always aimed at it ──
cam_data = bpy.data.cameras.new("ChaseCam"); cam_data.lens = 32
cam = link(bpy.data.objects.new("ChaseCam", cam_data))
cam.parent = rig
cam.location = tuple(P["cameraOffset"])
aim = cam.constraints.new("TRACK_TO"); aim.target = chassis; aim.track_axis = "TRACK_NEGATIVE_Z"; aim.up_axis = "UP_Y"
scene.camera = cam

bpy.ops.wm.save_as_mainfile(filepath=P["blendPath"])
print(json.dumps({"ok": True, "blend": P["blendPath"], "engine": engine, "frames": frames, "roadLengthM": round(road_length, 2),
                  "carDistanceM": round(distance, 2), "buildings": placed, "blender": bpy.app.version_string}))
""";
}
