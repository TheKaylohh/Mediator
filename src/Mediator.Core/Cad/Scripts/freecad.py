# Mediator V17 - FreeCAD connector (run by FreeCADCmd). One function per tool; args come from MEDIATOR_ARGS (JSON),
# the answer goes to MEDIATOR_RESULT (JSON). Units: mm, degrees, N, MPa. FreeCAD is Z-up: Top = XY, Front = XZ, Right = YZ.
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\freecad.py overrides the built-in one.
import json
import math
import os
import sys
import traceback

ARGS = json.load(open(os.environ["MEDIATOR_ARGS"], encoding="utf-8"))
LOG = []


def write_result(obj):
    with open(os.environ["MEDIATOR_RESULT"], "w", encoding="utf-8") as f:
        json.dump(obj, f, default=str)


import FreeCAD as App  # noqa: E402
import Part  # noqa: E402

PLANES = {"top": "XY_Plane", "xy": "XY_Plane", "front": "XZ_Plane", "xz": "XZ_Plane", "right": "YZ_Plane", "yz": "YZ_Plane"}
AXES = {"x": "X_Axis", "y": "Y_Axis", "z": "Z_Axis"}


# -- documents ---------------------------------------------------------------------------------------------------

def open_doc(path):
    if not path:
        raise ValueError("FreeCAD works on files here: give the path of the .FCStd document")
    for d in App.listDocuments().values():
        if os.path.normcase(d.FileName) == os.path.normcase(path):
            return d
    if not os.path.exists(path):
        doc = App.newDocument(os.path.splitext(os.path.basename(path))[0])
        doc.saveAs(path)
        LOG.append("created a new document " + path)
        return doc
    return App.openDocument(path)


def body_of(doc, create=True):
    bodies = [o for o in doc.Objects if o.TypeId == "PartDesign::Body"]
    if bodies:
        return bodies[0]
    if not create:
        return None
    LOG.append("added a Body")
    return doc.addObject("PartDesign::Body", "Body")


def origin_feature(body, role):
    for f in body.Origin.OriginFeatures:
        if f.Role == role:
            return f
    raise ValueError("no origin feature " + role)


def shape_of(doc):
    body = body_of(doc, create=False)
    if body is not None and not body.Shape.isNull():
        return body, body.Shape
    for o in reversed(doc.Objects):
        if hasattr(o, "Shape") and not o.Shape.isNull() and o.Shape.Solids:
            return o, o.Shape
    return None, None


def nearest(subshapes, point, prefix):
    v = Part.Vertex(App.Vector(*point))
    best = min(range(len(subshapes)), key=lambda i: subshapes[i].distToShape(v)[0])
    return prefix + str(best + 1)


def r(x, n=4):
    return round(float(x), n)


def summary(doc):
    obj, shape = shape_of(doc)
    out = {"document": doc.Name, "path": doc.FileName, "objects": len(doc.Objects)}
    if shape is not None:
        bb = shape.BoundBox
        out["sizeMm"] = {"x": r(bb.XLength, 3), "y": r(bb.YLength, 3), "z": r(bb.ZLength, 3)}
        out["solids"] = len(shape.Solids)
        out["valid"] = shape.isValid()
    errors = [o.Label for o in doc.Objects if "Invalid" in o.State or "Error" in getattr(o, "State", [])]
    if errors:
        out["errors"] = errors
    return out


def finish(doc, save, report):
    doc.recompute()
    report.update(summary(doc))
    if save:
        doc.save()
        report["saved"] = doc.FileName
    else:
        report["saved"] = False
        report["note"] = "Not saved: FreeCAD runs on the file here, so changes are kept only with save: true."
    if LOG:
        report["log"] = LOG
    return report


# -- session, documents, export ----------------------------------------------------------------------------------

def session(a):
    v = App.Version()
    return {"running": True, "program": "FreeCAD", "version": ".".join(v[:3]), "build": v[3] if len(v) > 3 else None,
            "mode": "headless (FreeCADCmd): every call names its file", "openDocuments": [d.FileName for d in App.listDocuments().values()]}


def document(a):
    action = a["action"]
    if action == "save_as":
        doc = open_doc(a["path"])
        doc.saveCopy(a["target"])
        return {"savedAs": a["target"], "from": a["path"]}
    doc = open_doc(a.get("path"))
    if action == "open":
        return {"opened": doc.FileName, **summary(doc)}
    if action == "save":
        doc.save()
        return {"saved": doc.FileName}
    return {"closed": True, "note": "nothing stays open between calls in headless FreeCAD"}


def export(a):
    import Mesh
    files, problems = [], []
    for path in a["paths"]:
        doc = open_doc(path)
        obj, shape = shape_of(doc)
        if obj is None:
            problems.append(os.path.basename(path) + ": no solid to export")
            continue
        stem = os.path.join(a["folder"], os.path.splitext(os.path.basename(path))[0])
        for f in a["formats"]:
            target = stem + f["ext"]
            if os.path.exists(target):
                problems.append(os.path.basename(target) + " already exists: not replaced")
                continue
            try:
                if f["name"] in ("step", "iges", "brep"):
                    Part.export([obj], target)
                elif f["name"] in ("stl", "obj", "3mf"):
                    Mesh.export([obj], target)
                elif f["name"] == "fcstd":
                    doc.saveCopy(target)
                files.append(target)
            except Exception as e:  # noqa: BLE001
                problems.append("%s -> %s: %s" % (os.path.basename(path), f["name"], e))
    return {"files": files, "problems": problems}


# -- inspect -----------------------------------------------------------------------------------------------------

LENGTH_PROPS = ("Length", "Length2", "Radius", "Radius2", "Size", "Size2", "Depth", "Diameter", "Thickness", "Value", "Height", "Width", "Angle", "Occurrences")


def inspect(a):
    doc = open_doc(a.get("path"))
    doc.recompute()
    sections = set(a.get("sections") or [])
    out = summary(doc)
    if "features" in sections:
        out["features"] = [{"name": o.Name, "label": o.Label, "type": o.TypeId, "state": list(o.State)} for o in doc.Objects][:300]
    if "dimensions" in sections or "parameters" in sections or "equations" in sections:
        dims = []
        for o in doc.Objects:
            if o.TypeId == "Sketcher::SketchObject":
                for i, c in enumerate(o.Constraints):
                    if c.Name and c.Type in ("Distance", "DistanceX", "DistanceY", "Radius", "Diameter", "Angle"):
                        val = math.degrees(c.Value) if c.Type == "Angle" else c.Value
                        dims.append({"name": o.Name + "." + c.Name, "value": r(val), "unit": "deg" if c.Type == "Angle" else "mm"})
            for p in LENGTH_PROPS:
                if p in o.PropertiesList and o.TypeId.startswith("PartDesign::"):
                    v = getattr(o, p)
                    val = getattr(v, "Value", v)
                    if isinstance(val, (int, float)):
                        dims.append({"name": o.Name + "." + p, "value": r(val), "unit": "deg" if p == "Angle" else ("" if p == "Occurrences" else "mm")})
        out["dimensions"] = dims[:400]
        sheets = []
        for o in doc.Objects:
            if o.TypeId == "Spreadsheet::Sheet":
                for cell in o.getUsedCells():
                    alias = o.getAlias(cell)
                    if alias:
                        sheets.append({"name": alias, "sheet": o.Name, "value": str(o.get(cell))})
        out["globals"] = sheets
        out["expressions"] = [{"object": o.Name, "property": p, "expression": e} for o in doc.Objects for (p, e) in getattr(o, "ExpressionEngine", [])][:200]
    if "properties" in sections:
        out["properties"] = dict(doc.Meta) if hasattr(doc, "Meta") else {}
    if "mass" in sections:
        obj, shape = shape_of(doc)
        if shape is not None and shape.Solids:
            vol = shape.Volume
            density = 7.85e-6  # kg/mm3 (steel) unless the part has a material
            mat = getattr(obj, "ShapeMaterial", None)
            try:
                if mat is not None and "Density" in mat.PhysicalProperties:
                    density = App.Units.Quantity(mat.PhysicalProperties["Density"]).getValueAs("kg/mm^3").Value
            except Exception:  # noqa: BLE001
                pass
            com = sum((s.CenterOfMass * s.Volume for s in shape.Solids), App.Vector()) * (1.0 / vol)
            out["mass"] = {"volumeMm3": r(vol, 2), "surfaceAreaMm2": r(shape.Area, 2), "massKg": r(vol * density, 5),
                           "centerOfMassMm": [r(com.x, 3), r(com.y, 3), r(com.z, 3)], "densityKgM3": r(density * 1e9, 1),
                           "material": getattr(mat, "Name", None) or "(none set: steel density assumed)"}
    if "bodies" in sections:
        obj, shape = shape_of(doc)
        out["bodies"] = None if shape is None else {"solids": len(shape.Solids), "faces": len(shape.Faces), "edges": len(shape.Edges),
                                                    "closed": shape.isClosed(), "valid": shape.isValid()}
    if "components" in sections:
        links = [o for o in doc.Objects if o.TypeId == "App::Link"]
        if links:
            out["components"] = [{"name": o.Name, "file": getattr(getattr(o, "LinkedObject", None), "Document", None) and o.LinkedObject.Document.FileName,
                                  "placementMm": [r(o.Placement.Base.x, 3), r(o.Placement.Base.y, 3), r(o.Placement.Base.z, 3)]} for o in links]
    return out


# -- edit --------------------------------------------------------------------------------------------------------

def set_named(doc, name, value, unit):
    obj_name, _, prop = name.partition(".")
    obj = doc.getObject(obj_name) or (doc.getObjectsByLabel(obj_name) or [None])[0]
    if obj is None:
        raise ValueError("no object " + obj_name)
    if obj.TypeId == "Sketcher::SketchObject" and prop not in obj.PropertiesList:
        q = App.Units.Quantity("%s deg" % value) if unit == "deg" else App.Units.Quantity("%s mm" % (value * (25.4 if unit == "in" else 1)))
        obj.setDatum(prop, q)
        return "%s = %s" % (name, q.UserString)
    old = getattr(obj, prop)
    setattr(obj, prop, value * (25.4 if unit == "in" else 1))
    return "%s: %s -> %s" % (name, getattr(old, "UserString", old), getattr(getattr(obj, prop), "UserString", getattr(obj, prop)))


def find_obj(doc, name):
    obj = doc.getObject(name) or (doc.getObjectsByLabel(name) or [None])[0]
    if obj is None:
        raise ValueError("no object named " + name + " (inspect_model features lists them)")
    return obj


def edit_op(doc, op):
    kind = op["op"]
    if kind == "set_dimension":
        return set_named(doc, op["name"], float(op["value"]), op.get("unit", "mm"))
    if kind in ("set_global", "add_global"):
        sheet = next((o for o in doc.Objects if o.TypeId == "Spreadsheet::Sheet"), None)
        if sheet is None:
            sheet = doc.addObject("Spreadsheet::Sheet", "Parameters")
        cell = sheet.getCellFromAlias(op["name"]) if hasattr(sheet, "getCellFromAlias") else None
        if not cell:
            if kind == "set_global":
                raise ValueError("no parameter named " + op["name"] + " (add_global creates one)")
            row = len([c for c in sheet.getUsedCells() if c.startswith("B")]) + 1
            sheet.set("A%d" % row, op["name"])
            cell = "B%d" % row
            sheet.setAlias(cell, op["name"])
        sheet.set(cell, str(op["value"]))
        return "%s = %s" % (op["name"], op["value"])
    if kind in ("suppress", "unsuppress"):
        obj = find_obj(doc, op["feature"])
        if "Suppressed" not in obj.PropertiesList:
            raise ValueError(op["feature"] + " cannot be suppressed (FreeCAD 1.0 suppresses PartDesign features)")
        obj.Suppressed = kind == "suppress"
        return op["feature"] + " " + kind + "ed"
    if kind == "rename_feature":
        find_obj(doc, op["feature"]).Label = op["newName"]
        return op["feature"] + " -> " + op["newName"]
    if kind == "delete_feature":
        doc.removeObject(find_obj(doc, op["feature"]).Name)
        return "deleted " + op["feature"]
    if kind == "set_property":
        meta = dict(doc.Meta)
        meta[op["name"]] = str(op["value"])
        doc.Meta = meta
        return "%s = %s" % (op["name"], op["value"])
    if kind == "delete_property":
        meta = dict(doc.Meta)
        meta.pop(op["name"], None)
        doc.Meta = meta
        return "deleted " + op["name"]
    if kind == "set_material":
        import Materials
        mats = Materials.MaterialManager().Materials
        found = next((m for m in mats.values() if m.Name.lower() == op["material"].lower()), None)
        if found is None:
            raise ValueError("no material '%s' in the library (e.g. %s)" % (op["material"], ", ".join(sorted(m.Name for m in mats.values())[:12])))
        obj, _ = shape_of(doc)
        obj.ShapeMaterial = found
        return "material " + found.Name
    if kind == "set_color":
        rgb = tuple(c / 255.0 for c in op["rgb"])
        obj = find_obj(doc, op["feature"]) if op.get("feature") else shape_of(doc)[0]
        if hasattr(obj, "ViewObject") and obj.ViewObject is not None:
            obj.ViewObject.ShapeColor = rgb
            return "colour set"
        raise ValueError("colours need the FreeCAD GUI (headless documents keep no view data)")
    if kind == "rebuild":
        doc.recompute()
        return "recomputed"
    raise ValueError("unknown op " + kind)


def run_ops(doc, ops, handler):
    results = []
    for op in ops:
        try:
            results.append({"op": op["op"], "ok": True, "detail": handler(doc, op)})
            doc.recompute()
        except Exception as e:  # noqa: BLE001
            results.append({"op": op["op"], "ok": False, "error": str(e)})
    return results


def edit(a):
    doc = open_doc(a.get("path"))
    return finish(doc, a.get("save"), {"results": run_ops(doc, a["ops"], edit_op)})


# -- sketch ------------------------------------------------------------------------------------------------------

def attach(doc, body, sketch, on):
    support_prop = "AttachmentSupport" if "AttachmentSupport" in sketch.PropertiesList else "Support"
    if isinstance(on, list):
        _, shape = shape_of(doc)
        if shape is None:
            raise ValueError("no solid to sketch on yet: use a plane (Top/Front/Right)")
        face = nearest(shape.Faces, on, "Face")
        setattr(sketch, support_prop, [(body.Tip or body, face)])
    else:
        key = str(on).lower().replace(" plane", "")
        if key not in PLANES:
            plane = find_obj(doc, on)
            setattr(sketch, support_prop, [(plane, "")])
        else:
            setattr(sketch, support_prop, [(origin_feature(body, PLANES[key]), "")])
    sketch.MapMode = "FlatFace"


def V(p):
    return App.Vector(float(p[0]), float(p[1]), 0)


def add_entity(sketch, e):
    import Sketcher
    t = e["type"]
    if t in ("line", "centerline"):
        return [sketch.addGeometry(Part.LineSegment(V(e["a"]), V(e["b"])), t == "centerline")]
    if t == "circle":
        return [sketch.addGeometry(Part.Circle(V([e["cx"], e["cy"]]), App.Vector(0, 0, 1), float(e["d"]) / 2), False)]
    if t == "arc":
        return [sketch.addGeometry(Part.Arc(V(e["a"]), V(e["mid"]), V(e["b"])), False)]
    if t == "point":
        return [sketch.addGeometry(Part.Point(V(e["at"])), False)]
    if t == "spline":
        pts = [V(p) for p in e["points"]]
        bs = Part.BSplineCurve()
        bs.interpolate(pts, PeriodicFlag=bool(e.get("closed")))
        return [sketch.addGeometry(bs, False)]
    if t in ("rect", "polygon"):
        if t == "rect":
            cx, cy, w, h = (float(e[k]) for k in ("cx", "cy", "w", "h"))
            pts = [(cx - w / 2, cy - h / 2), (cx + w / 2, cy - h / 2), (cx + w / 2, cy + h / 2), (cx - w / 2, cy + h / 2)]
        else:
            pts = [tuple(p) for p in e["points"]]
        ids = [sketch.addGeometry(Part.LineSegment(V(pts[i]), V(pts[(i + 1) % len(pts)])), False) for i in range(len(pts))]
        for i in range(len(ids)):
            sketch.addConstraint(Sketcher.Constraint("Coincident", ids[i], 2, ids[(i + 1) % len(ids)], 1))
        if t == "rect":
            sketch.addConstraint(Sketcher.Constraint("Horizontal", ids[0]))
            sketch.addConstraint(Sketcher.Constraint("Horizontal", ids[2]))
            sketch.addConstraint(Sketcher.Constraint("Vertical", ids[1]))
            sketch.addConstraint(Sketcher.Constraint("Vertical", ids[3]))
        return ids
    if t == "slot":
        a, b, w = V(e["a"]), V(e["b"]), float(e["width"]) / 2
        d = b - a
        n = App.Vector(-d.y, d.x, 0).normalize() * w
        l1 = sketch.addGeometry(Part.LineSegment(a + n, b + n), False)
        l2 = sketch.addGeometry(Part.LineSegment(b - n, a - n), False)
        ang = math.atan2(d.y, d.x)
        c1 = sketch.addGeometry(Part.ArcOfCircle(Part.Circle(b, App.Vector(0, 0, 1), w), ang - math.pi / 2, ang + math.pi / 2), False)
        c2 = sketch.addGeometry(Part.ArcOfCircle(Part.Circle(a, App.Vector(0, 0, 1), w), ang + math.pi / 2, ang + 3 * math.pi / 2), False)
        return [l1, l2, c1, c2]
    raise ValueError("unknown entity type " + t)


def sketch(a):
    import Sketcher
    doc = open_doc(a.get("path"))
    spec = a["sketch"]
    body = body_of(doc)
    sk = body.newObject("Sketcher::SketchObject", spec.get("name") or "Sketch")
    attach(doc, body, sk, spec["on"])
    ids, problems = [], []
    for e in spec["entities"]:
        ids.append(add_entity(sk, e))
    names = {"horizontal": "Horizontal", "vertical": "Vertical", "tangent": "Tangent", "parallel": "Parallel",
             "perpendicular": "Perpendicular", "equal": "Equal", "fix": "Block"}
    for c in spec.get("constraints") or []:
        try:
            ent = [ids[i][0] for i in c["entities"]]
            t = c["type"]
            if t in names:
                sk.addConstraint(Sketcher.Constraint(names[t], *ent))
            elif t == "coincident":
                sk.addConstraint(Sketcher.Constraint("Coincident", ent[0], 2, ent[1], 1))
            elif t == "concentric":
                sk.addConstraint(Sketcher.Constraint("Coincident", ent[0], 3, ent[1], 3))
            elif t == "collinear":
                sk.addConstraint(Sketcher.Constraint("Parallel", ent[0], ent[1]))
                sk.addConstraint(Sketcher.Constraint("PointOnObject", ent[1], 1, ent[0]))
            else:
                raise ValueError("unknown constraint " + t)
        except Exception as e:  # noqa: BLE001
            problems.append("constraint %s: %s" % (c, e))
    for d in spec.get("dimensions") or []:
        try:
            value = float(d["value"])
            if "entity" in d:
                gid = ids[d["entity"]][0]
                geo = sk.Geometry[gid]
                kind = "Diameter" if isinstance(geo, (Part.Circle, Part.ArcOfCircle)) else "Distance"
                sk.addConstraint(Sketcher.Constraint(kind, gid, value))
            else:
                g1, g2 = ids[d["entities"][0]][0], ids[d["entities"][1]][0]
                if d.get("type") == "angle":
                    sk.addConstraint(Sketcher.Constraint("Angle", g1, g2, math.radians(value)))
                else:
                    sk.addConstraint(Sketcher.Constraint("Distance", g1, 1, g2, value))
        except Exception as e:  # noqa: BLE001
            problems.append("dimension %s: %s" % (d, e))
    doc.recompute()
    report = {"sketch": sk.Name, "entities": len(sk.Geometry), "constraints": len(sk.Constraints)}
    if hasattr(sk, "FullyConstrained"):
        report["fullyDefined"] = bool(sk.FullyConstrained)
    if problems:
        report["problems"] = problems
    return finish(doc, a.get("save"), report)


# -- features ----------------------------------------------------------------------------------------------------

CLEARANCE = {"M2": 2.4, "M2.5": 2.9, "M3": 3.4, "M4": 4.5, "M5": 5.5, "M6": 6.6, "M8": 9.0, "M10": 11.0, "M12": 13.5, "M14": 15.5, "M16": 17.5, "M20": 22.0, "M24": 26.0}


def profile_for(doc, body, f, n):
    if f.get("sketch"):
        return find_obj(doc, f["sketch"])
    if f.get("on") is not None and f.get("profile"):
        ents = []
        for shape in f["profile"]:
            if "rect" in shape:
                ents.append(dict(type="rect", **shape["rect"]))
            elif "circle" in shape:
                ents.append(dict(type="circle", **shape["circle"]))
            elif "polygon" in shape:
                ents.append({"type": "polygon", "points": shape["polygon"]})
            elif "centerline" in shape:
                ents.append({"type": "centerline", "a": shape["centerline"][0], "b": shape["centerline"][1]})
            else:
                ents.append(shape)
        sk = body.newObject("Sketcher::SketchObject", "Sketch")
        attach(doc, body, sk, f["on"])
        for e in ents:
            add_entity(sk, e)
        doc.recompute()
        return sk
    raise ValueError("give sketch (a sketch name) or on + profile")


def set_extent(feat, f):
    depth = f.get("depth", 10)
    if depth == "through_all":
        feat.Type = "ThroughAll"
    else:
        feat.Type = "Length"
        feat.Length = float(depth)
    if f.get("midPlane"):
        feat.Midplane = True
    if f.get("reverse"):
        feat.Reversed = True


def edges_near(shape, pts):
    return [nearest(shape.Edges, p, "Edge") for p in pts]


def feature_op(doc, f):
    body = body_of(doc)
    op = f["op"]
    tip = body.Tip
    if op in ("extrude", "cut"):
        sk = profile_for(doc, body, f, op)
        feat = body.newObject("PartDesign::Pad" if op == "extrude" else "PartDesign::Pocket", f.get("name") or ("Pad" if op == "extrude" else "Pocket"))
        feat.Profile = sk
        set_extent(feat, f)
        if f.get("draft") and "TaperAngle" in feat.PropertiesList:
            feat.TaperAngle = float(f["draft"])
        return feat.Name
    if op == "revolve":
        sk = profile_for(doc, body, f, op)
        feat = body.newObject("PartDesign::Groove" if f.get("cut") else "PartDesign::Revolution", f.get("name") or "Revolution")
        feat.Profile = sk
        feat.ReferenceAxis = (sk, ["V_Axis"])
        feat.Angle = float(f.get("angle", 360))
        return feat.Name
    if op == "sweep":
        feat = body.newObject("PartDesign::AdditivePipe", f.get("name") or "Sweep")
        feat.Profile = find_obj(doc, f["profile"])
        path = find_obj(doc, f["path"])
        feat.Spine = (path, ["Edge%d" % (i + 1) for i in range(len(path.Shape.Edges))])
        return feat.Name
    if op == "loft":
        names = f["profiles"]
        feat = body.newObject("PartDesign::AdditiveLoft", f.get("name") or "Loft")
        feat.Profile = find_obj(doc, names[0])
        feat.Sections = [find_obj(doc, n) for n in names[1:]]
        return feat.Name
    if op in ("fillet", "chamfer"):
        doc.recompute()
        shape = body.Shape
        edges = edges_near(shape, f["edgesNear"]) if f.get("edgesNear") else ["Edge%d" % (i + 1) for i in range(len(shape.Edges))]
        feat = body.newObject("PartDesign::Fillet" if op == "fillet" else "PartDesign::Chamfer", f.get("name") or op.capitalize())
        feat.Base = (tip, edges)
        if op == "fillet":
            feat.Radius = float(f["radius"])
        else:
            feat.Size = float(f["distance"])
        return "%s on %d edges" % (feat.Name, len(edges))
    if op == "shell":
        doc.recompute()
        faces = [nearest(body.Shape.Faces, f["openAt"], "Face")] if f.get("openAt") else []
        feat = body.newObject("PartDesign::Thickness", f.get("name") or "Shell")
        feat.Base = (tip, faces)
        feat.Value = float(f["thickness"])
        return feat.Name
    if op == "hole":
        import Sketcher  # noqa: F401
        sk = body.newObject("Sketcher::SketchObject", "HoleSketch")
        attach(doc, body, sk, f["on"])
        size = str(f["size"]).upper()
        for p in f["points"]:
            add_entity(sk, {"type": "circle", "cx": p[0], "cy": p[1], "d": CLEARANCE.get(size, 6.6)})
        doc.recompute()
        hole = body.newObject("PartDesign::Hole", f.get("name") or "Hole")
        hole.Profile = sk
        kind = f.get("type", "clearance")
        if size in CLEARANCE:
            hole.ThreadType = "ISOMetricProfile"
            hole.ThreadSize = size
        hole.Threaded = kind == "tapped"
        if kind == "clearance":
            hole.ThreadType = "None"
            hole.Diameter = CLEARANCE.get(size, 6.6)
        hole.HoleCutType = {"counterbore": "Counterbore", "countersink": "Countersink"}.get(kind, "None")
        depth = f.get("depth", "through_all")
        if depth == "through_all":
            hole.DepthType = "ThroughAll"
        else:
            hole.DepthType = "Dimension"
            hole.Depth = float(depth)
        return "%s: %d x %s %s" % (hole.Name, len(f["points"]), size, kind)
    if op in ("pattern_linear", "pattern_circular", "mirror"):
        originals = [find_obj(doc, n) for n in (f.get("features") or [tip.Name])]
        if op == "pattern_linear":
            feat = body.newObject("PartDesign::LinearPattern", f.get("name") or "LinearPattern")
            feat.Originals = originals
            feat.Direction = (origin_feature(body, AXES[f["direction"].lower()]), [""])
            feat.Length = float(f["spacing"]) * (int(f["count"]) - 1)
            feat.Occurrences = int(f["count"])
        elif op == "pattern_circular":
            feat = body.newObject("PartDesign::PolarPattern", f.get("name") or "PolarPattern")
            feat.Originals = originals
            feat.Axis = (origin_feature(body, AXES[f["axis"].lower()]), [""])
            feat.Angle = float(f.get("angle", 360))
            feat.Occurrences = int(f["count"])
        else:
            feat = body.newObject("PartDesign::Mirrored", f.get("name") or "Mirrored")
            feat.Originals = originals
            key = str(f["plane"]).lower().replace(" plane", "")
            feat.MirrorPlane = (origin_feature(body, PLANES[key]) if key in PLANES else find_obj(doc, f["plane"]), [""])
        return feat.Name
    if op == "plane":
        key = str(f["base"]).lower().replace(" plane", "")
        base = origin_feature(body, PLANES[key]) if key in PLANES else find_obj(doc, f["base"])
        pl = body.newObject("PartDesign::Plane", f["name"])
        support_prop = "AttachmentSupport" if "AttachmentSupport" in pl.PropertiesList else "Support"
        setattr(pl, support_prop, [(base, "")])
        pl.MapMode = "FlatFace"
        pl.AttachmentOffset = App.Placement(App.Vector(0, 0, float(f["offset"])), App.Rotation())
        return pl.Name
    if op == "sketch":
        sk = profile_for(doc, body, {"on": f["on"], "profile": f["profile"]}, op)
        sk.Label = f["name"]
        return sk.Name
    if op == "boolean":
        kinds = {"union": "Part::MultiFuse", "subtract": "Part::Cut", "intersect": "Part::MultiCommon"}
        base, tools = find_obj(doc, f["base"]), [find_obj(doc, n) for n in f["tools"]]
        obj = doc.addObject(kinds[f["operation"]], f.get("name") or f["operation"].capitalize())
        if f["operation"] == "subtract":
            obj.Base, obj.Tool = base, tools[0]
        else:
            obj.Shapes = [base] + tools
        return obj.Name
    if op in ("delete",):
        doc.removeObject(find_obj(doc, f["feature"]).Name)
        return "deleted " + f["feature"]
    raise ValueError("FreeCAD has no op " + op + " here; use run_script")


def model_features(a):
    doc = open_doc(a.get("path"))
    return finish(doc, a.get("save"), {"results": run_ops(doc, a["ops"], feature_op)})


# -- simulation (FEM, CalculiX) ----------------------------------------------------------------------------------

def simulate(a):
    import ObjectsFem
    doc = open_doc(a.get("path"))
    obj, shape = shape_of(doc)
    if shape is None:
        raise ValueError("no solid to analyse")
    analysis = ObjectsFem.makeAnalysis(doc, "Analysis")
    solver = ObjectsFem.makeSolverCalculixCcxTools(doc, "CalculiX")
    analysis.addObject(solver)
    results = []

    def faces_of(op):
        if op.get("faces"):
            return op["faces"]
        return [nearest(shape.Faces, p, "Face") for p in op.get("facesNear") or []]

    material = {"Name": "Steel", "YoungsModulus": "210000 MPa", "PoissonRatio": "0.30", "Density": "7900 kg/m^3"}
    mesh_size = None
    for op in a["ops"]:
        try:
            kind = op["op"]
            if kind == "material":
                known = {"steel": ("210000 MPa", "0.30", "7900 kg/m^3"), "aluminium": ("70000 MPa", "0.33", "2700 kg/m^3"),
                         "aluminum": ("70000 MPa", "0.33", "2700 kg/m^3"), "abs": ("2300 MPa", "0.35", "1050 kg/m^3"),
                         "pla": ("3500 MPa", "0.36", "1240 kg/m^3"), "titanium": ("114000 MPa", "0.34", "4430 kg/m^3")}
                e, nu, rho = known.get(op["name"].lower(), known["steel"])
                material = {"Name": op["name"], "YoungsModulus": e, "PoissonRatio": nu, "Density": rho}
                detail = "material " + op["name"] + ("" if op["name"].lower() in known else " (unknown: steel values used)")
            elif kind == "fixed":
                c = ObjectsFem.makeConstraintFixed(doc, "Fixed")
                c.References = [(obj, faces_of(op))]
                analysis.addObject(c)
                detail = "fixed " + ", ".join(faces_of(op))
            elif kind == "force":
                c = ObjectsFem.makeConstraintForce(doc, "Force")
                c.References = [(obj, faces_of(op))]
                c.Force = App.Units.Quantity("%s N" % float(op["newtons"]))
                analysis.addObject(c)
                detail = "%s N on %s" % (op["newtons"], ", ".join(faces_of(op)))
            elif kind == "pressure":
                c = ObjectsFem.makeConstraintPressure(doc, "Pressure")
                c.References = [(obj, faces_of(op))]
                c.Pressure = App.Units.Quantity("%s MPa" % float(op["mpa"]))
                analysis.addObject(c)
                detail = "%s MPa on %s" % (op["mpa"], ", ".join(faces_of(op)))
            elif kind == "mesh":
                mesh_size = op.get("size")
                detail = "mesh size %s" % (mesh_size or "auto")
            elif kind == "solve":
                mat = ObjectsFem.makeMaterialSolid(doc, "Material")
                m = mat.Material
                m.update(material)
                mat.Material = m
                analysis.addObject(mat)
                mesh = ObjectsFem.makeMeshGmsh(doc, "Mesh")
                mesh.Shape = obj
                if mesh_size:
                    mesh.CharacteristicLengthMax = float(mesh_size)
                analysis.addObject(mesh)
                doc.recompute()
                from femmesh.gmshtools import GmshTools
                GmshTools(mesh).create_mesh()
                from femtools import ccxtools
                fea = ccxtools.FemToolsCcx(analysis, solver)
                fea.update_objects()
                fea.setup_working_dir()
                fea.setup_ccx()
                problems = fea.check_prerequisites()
                if problems:
                    raise ValueError(problems)
                fea.purge_results()
                fea.write_inp_file()
                fea.ccx_run()
                fea.load_results()
                res = [o for o in analysis.Group if o.isDerivedFrom("Fem::FemResultObject")]
                if not res:
                    raise ValueError("CalculiX produced no results")
                vm = max(res[0].vonMises)
                disp = max(res[0].DisplacementLengths)
                yield_mpa = {"steel": 250, "aluminium": 240, "aluminum": 240, "abs": 40, "pla": 50, "titanium": 880}.get(material["Name"].lower())
                summary_ = {"maxVonMisesMPa": r(vm, 3), "maxDisplacementMm": r(disp, 5), "nodes": len(res[0].NodeNumbers)}
                if yield_mpa and vm > 0:
                    summary_["safetyFactor"] = r(yield_mpa / vm, 2)
                    summary_["yieldMPaAssumed"] = yield_mpa
                detail = summary_
            else:
                raise ValueError("unknown op " + kind)
            results.append({"op": kind, "ok": True, "detail": detail})
        except Exception as e:  # noqa: BLE001
            results.append({"op": op.get("op"), "ok": False, "error": str(e)})
    return finish(doc, a.get("save"), {"results": results})


# -- the escape hatch --------------------------------------------------------------------------------------------

def script(a):
    import io
    import contextlib
    doc = open_doc(a["path"]) if a.get("path") else None
    out = {}
    ns = {"App": App, "FreeCAD": App, "Part": Part, "doc": doc, "args": a, "result": out.update,
          "save": (lambda: doc.save()) if doc else None}
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        exec(compile(a["code"], "user_script.py", "exec"), ns)  # noqa: S102 - the user approved this code
    out.setdefault("ok", True)
    if buf.getvalue().strip():
        out["output"] = buf.getvalue()[-4000:]
    return out


VERBS = {"session": session, "document": document, "export": export, "inspect": inspect, "edit": edit, "sketch": sketch,
         "model_features": model_features, "simulate": simulate, "script": script}

try:
    verb = ARGS["verb"]
    if verb not in VERBS:
        raise ValueError("FreeCAD cannot do '%s' here" % verb)
    write_result(VERBS[verb](ARGS))
except Exception as exc:  # noqa: BLE001
    write_result({"error": "%s: %s" % (type(exc).__name__, exc), "trace": traceback.format_exc()[-3000:], "log": LOG})
finally:
    sys.stdout.flush()
    os._exit(0)
