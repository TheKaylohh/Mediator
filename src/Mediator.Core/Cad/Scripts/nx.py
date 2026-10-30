# Mediator V17 - Siemens NX connector: an NX Open Python journal run in batch by run_journal.exe, on part files.
# One function per tool; args come from MEDIATOR_ARGS (JSON), the answer goes to MEDIATOR_RESULT (JSON).
# A copy of this file in %LOCALAPPDATA%\Mediator\cad-scripts\nx.py overrides the built-in one.
import json
import os
import traceback

ARGS = json.load(open(os.environ["MEDIATOR_ARGS"], encoding="utf-8"))


def write_result(obj):
    with open(os.environ["MEDIATOR_RESULT"], "w", encoding="utf-8") as f:
        json.dump(obj, f, default=str)


import NXOpen  # noqa: E402

session = NXOpen.Session.GetSession()


def open_part(path):
    if not path:
        raise ValueError("NX works on part files here: give the path of the .prt")
    for p in session.Parts:
        if os.path.normcase(p.FullPath) == os.path.normcase(path):
            session.Parts.SetDisplay(p, False, True)
            return p
    part, status = session.Parts.OpenBaseDisplay(path)
    try:
        status.Dispose()
    except Exception:  # noqa: BLE001
        pass
    return part


def update():
    mark = session.SetUndoMark(NXOpen.Session.MarkVisibility.Visible, "Mediator")
    return session.UpdateManager.DoUpdate(mark)


def save(part):
    part.Save(NXOpen.BasePart.SaveComponents.TrueValue, NXOpen.BasePart.CloseAfterSave.FalseValue)


def session_info(a):
    return {"running": True, "program": "Siemens NX", "release": session.GetEnvironmentVariableValue("UGII_VERSION") or None,
            "mode": "batch journal (run_journal): every call names its part file"}


def document(a):
    part = open_part(a.get("path"))
    action = a["action"]
    if action == "save_as":
        part.SaveAs(a["target"])
        return {"savedAs": a["target"], "from": a["path"]}
    if action == "save":
        save(part)
        return {"saved": part.FullPath}
    if action == "open":
        return {"opened": part.FullPath, "bodies": len(list(part.Bodies)), "features": len(list(part.Features))}
    return {"closed": True, "note": "nothing stays open between batch calls"}


def export(a):
    files, problems = [], []
    for path in a["paths"]:
        part = open_part(path)
        stem = os.path.join(a["folder"], os.path.splitext(os.path.basename(path))[0])
        for f in a["formats"]:
            target = stem + f["ext"]
            if os.path.exists(target):
                problems.append(os.path.basename(target) + " already exists: not replaced")
                continue
            try:
                if f["name"] == "step":
                    sc = session.DexManager.CreateStepCreator()
                    sc.ExportAs = NXOpen.StepCreator.ExportAsOption.Ap214
                    sc.InputFile = part.FullPath
                    sc.OutputFile = target
                    sc.FileSaveFlag = False
                    sc.ExportSelectionBlock.SelectionScope = NXOpen.ObjectSelector.Scope.EntirePart
                    sc.Commit()
                    sc.Destroy()
                elif f["name"] == "parasolid":
                    import NXOpen.UF
                    NXOpen.UF.UFSession.GetUFSession().Ps.ExportData([b.Tag for b in part.Bodies], target)
                else:
                    raise ValueError("format %s: use run_script" % f["name"])
                files.append(target)
            except Exception as e:  # noqa: BLE001
                problems.append("%s -> %s: %s" % (os.path.basename(path), f["name"], e))
    return {"files": files, "problems": problems}


def inspect(a):
    part = open_part(a.get("path"))
    sections = set(a.get("sections") or [])
    out = {"title": part.Leaf, "path": part.FullPath, "units": str(part.PartUnits), "bodies": len(list(part.Bodies))}
    if sections & {"parameters", "equations", "dimensions"}:
        out["expressions"] = [{"name": e.Name, "formula": e.RightHandSide, "value": e.Value, "units": e.Units.Name if e.Units else None}
                              for e in list(part.Expressions)[:400]]
    if "features" in sections:
        out["features"] = [{"name": f.JournalIdentifier, "type": f.FeatureType, "suppressed": f.Suppressed} for f in list(part.Features)[:300]]
    if "properties" in sections:
        out["properties"] = {att.Title: att.StringValue for att in part.GetUserAttributes() if att.Type == NXOpen.NXObject.AttributeType.String}
    if "mass" in sections:
        try:
            uc = part.UnitCollection
            units = [uc.GetBase("Area"), uc.GetBase("Volume"), uc.GetBase("Mass"), uc.GetBase("Length")]
            m = part.MeasureManager.NewMassProperties(units, 0.99, list(part.Bodies))
            c = m.Centroid
            out["mass"] = {"mass": m.Mass, "volume": m.Volume, "area": m.Area, "centroid": [c.X, c.Y, c.Z], "units": "part base units"}
        except Exception as e:  # noqa: BLE001
            out["mass"] = {"error": str(e)}
    return out


def find_feature(part, name):
    for f in part.Features:
        if f.JournalIdentifier == name or f.Name == name:
            return f
    raise ValueError("no feature named " + name)


def edit(a):
    part = open_part(a.get("path"))
    results = []
    for op in a["ops"]:
        try:
            kind = op["op"]
            if kind in ("set_global", "set_dimension"):
                e = part.Expressions.FindObject(op["name"])
                old = e.RightHandSide
                part.Expressions.Edit(e, str(op["value"]))
                detail = "%s: %s -> %s" % (op["name"], old, op["value"])
            elif kind == "add_global":
                unit = part.UnitCollection.FindObject("MilliMeter")
                part.Expressions.CreateWithUnits("%s=%s" % (op["name"], op["value"]), unit)
                detail = "added " + op["name"]
            elif kind in ("suppress", "unsuppress"):
                feat = find_feature(part, op["feature"])
                if kind == "suppress":
                    part.Features.SuppressFeatures([feat])
                else:
                    part.Features.UnsuppressFeatures([feat])
                detail = op["feature"] + " " + kind + "ed"
            elif kind == "set_property":
                part.SetUserAttribute(op["name"], -1, str(op["value"]), NXOpen.Update.Option.Now)
                detail = "%s = %s" % (op["name"], op["value"])
            elif kind == "rebuild":
                detail = "updated"
            else:
                raise ValueError("unknown op " + kind)
            update()
            results.append({"op": kind, "ok": True, "detail": detail})
        except Exception as e:  # noqa: BLE001
            results.append({"op": op.get("op"), "ok": False, "error": str(e)})
    report = {"results": results}
    if a.get("save"):
        save(part)
        report["saved"] = part.FullPath
    else:
        report["saved"] = False
        report["note"] = "Not saved: NX runs on the file here, so changes are kept only with save: true."
    return report


def script(a):
    import contextlib
    import io
    part = open_part(a["path"]) if a.get("path") else None
    out = {}
    ns = {"NXOpen": NXOpen, "session": session, "part": part, "args": a, "result": out.update, "save": (lambda: save(part)) if part else None}
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        exec(compile(a["code"], "user_journal.py", "exec"), ns)  # noqa: S102 - the user approved this code
    out.setdefault("ok", True)
    if buf.getvalue().strip():
        out["output"] = buf.getvalue()[-4000:]
    return out


VERBS = {"session": session_info, "document": document, "export": export, "inspect": inspect, "edit": edit, "script": script}

try:
    verb = ARGS["verb"]
    if verb not in VERBS:
        raise ValueError("NX cannot do '%s' here" % verb)
    write_result(VERBS[verb](ARGS))
except Exception as exc:  # noqa: BLE001
    write_result({"error": "%s: %s" % (type(exc).__name__, exc), "trace": traceback.format_exc()[-3000:]})
