using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Mediator.Core.Cad;

namespace Mediator.Core.Swx;

/// <summary>
/// Document handling in SolidWorks: what is open (<see cref="Session"/>), open/save/save as/close (<see cref="Document"/>) and
/// exports (<see cref="Export"/>). Paths are checked by the tool provider (<see cref="CadAppToolProvider"/>) before they reach these.
/// </summary>
public sealed partial class SwxParametric
{
    private const int SwDocAssembly = 2, SwDocDrawing = 3;
    private const int SwOpenSilent = 1; // swOpenDocOptions_Silent

    /// <summary>What SaveAs3 writes, by file extension: models to neutral CAD and mesh files, drawings to PDF/DXF/DWG, both to a picture.</summary>
    public static readonly IReadOnlyList<CadExportFormat> ExportFormats =
    [
        new("step", ".step", true, false), new("iges", ".igs", true, false), new("parasolid", ".x_t", true, false),
        new("stl", ".stl", true, false), new("3mf", ".3mf", true, false),
        new("pdf", ".pdf", false, true), new("dxf", ".dxf", false, true), new("dwg", ".dwg", false, true),
        new("png", ".png", true, true),
    ];

    public static int DocType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sldprt" => SwDocPart, ".sldasm" => SwDocAssembly, ".slddrw" => SwDocDrawing,
        _ => throw new ArgumentException($"{Path.GetFileName(path)}: not a SolidWorks part, assembly or drawing"),
    };

    private static string TypeName(object? t) => t switch { SwDocPart => "part", SwDocAssembly => "assembly", SwDocDrawing => "drawing", _ => "other" };

    public static JsonObject Session(TimeSpan timeout) => Run(p => p.DoSession(), timeout);
    public static JsonObject Document(CadDocumentRequest request, TimeSpan timeout) => Run(p => p.DoDocument(request), timeout);

    /// <summary>The active document's file, or null (nothing open, or never saved).</summary>
    public static string? ActivePath(TimeSpan timeout) => (string?)Run(p =>
    {
        p.Connect();
        return new JsonObject { ["path"] = C(p._sw, "ActiveDoc") is { } d && C(d, "GetPathName") is string { Length: > 0 } path ? path : null };
    }, timeout)["path"];
    public static JsonObject Export(IReadOnlyList<string> paths, IReadOnlyList<string> formats, string outputFolder, TimeSpan timeout) =>
        Run(p => p.DoExport(paths, formats, outputFolder), timeout);

    private JsonObject DoSession()
    {
        Connect();
        var docs = new JsonArray();
        foreach (var d in Arr(C(_sw, "GetDocuments")))
        {
            try
            {
                docs.Add(new JsonObject
                {
                    ["title"] = C(d, "GetTitle") as string,
                    ["path"] = C(d, "GetPathName") as string is { Length: > 0 } p ? p : null,
                    ["type"] = TypeName(C(d, "GetType")),
                    ["unsaved"] = C(d, "GetSaveFlag") is true,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TargetInvocationException) { }
        }
        var active = C(_sw, "ActiveDoc");
        return new JsonObject
        {
            ["running"] = true,
            ["version"] = C(_sw, "RevisionNumber") as string,
            ["active"] = active is null ? null : C(active, "GetTitle") as string,
            ["documents"] = docs,
        };
    }

    private object? OpenDoc(string path) => Arr(C(_sw, "GetDocuments"))
        .FirstOrDefault(d => string.Equals(C(d, "GetPathName") as string, path, StringComparison.OrdinalIgnoreCase));

    private object OpenOrGet(string path)
    {
        if (OpenDoc(path) is { } open) return open;
        if (!File.Exists(path)) throw new ArgumentException($"{path} does not exist");
        int err = 0, warn = 0;
        var args = new object?[] { path, DocType(path), SwOpenSilent, "", err, warn };
        var mods = new ParameterModifier(6);
        mods[4] = true; mods[5] = true;
        var doc = _sw.GetType().InvokeMember("OpenDoc6", BindingFlags.InvokeMethod, null, _sw, args, [mods], null, null);
        return doc ?? throw new InvalidOperationException($"SolidWorks could not open {Path.GetFileName(path)} (error {args[4]})");
    }

    /// <summary>Brings <paramref name="doc"/> to the front (pictures and the user's review need it active).</summary>
    private void Activate(object doc)
    {
        var act = new object?[] { (string)C(doc, "GetTitle")!, false, 0, 0 };
        var actMods = new ParameterModifier(4);
        actMods[3] = true; // ref int Errors
        try { _sw.GetType().InvokeMember("ActivateDoc3", BindingFlags.InvokeMethod, null, _sw, act, [actMods], null, null); }
        catch (Exception ex) when (ex is TargetInvocationException or COMException) { _log.Add("could not bring it to the front: " + ex.Message); }
    }

    /// <summary>
    /// The document a call works on: <paramref name="path"/> (opened silently if needed) or the active one.
    /// OpenedHere tells the caller to close it again when it only read it.
    /// </summary>
    private (object Doc, bool OpenedHere) Target(string? path)
    {
        if (path is null) return (C(_sw, "ActiveDoc") ?? throw new ArgumentException("no document is open in SolidWorks; give a path"), false);
        bool openedHere = OpenDoc(path) is null;
        return (OpenOrGet(path), openedHere);
    }

    private JsonObject DoDocument(CadDocumentRequest request)
    {
        var (action, path, save, discard, target) = request;
        Connect();
        object? doc = path is null ? C(_sw, "ActiveDoc") : action is "open" or "save_as" ? OpenOrGet(path) : OpenDoc(path);
        if (doc is null)
            return action == "close" && path is not null
                ? new JsonObject { ["closed"] = false, ["note"] = $"{Path.GetFileName(path)} is not open" }
                : throw new ArgumentException(path is null ? "no document is active in SolidWorks; give a path" : $"{Path.GetFileName(path)} is not open");
        string title = (string)C(doc, "GetTitle")!;
        string? docPath = C(doc, "GetPathName") as string is { Length: > 0 } p ? p : null;
        bool dirty = C(doc, "GetSaveFlag") is true;
        switch (action)
        {
            case "open":
                Activate(doc);
                return new JsonObject { ["opened"] = docPath, ["title"] = title, ["type"] = TypeName(C(doc, "GetType")), ["unsaved"] = dirty, ["log"] = Log() };
            case "save_as":
                if (target is null) throw new ArgumentException("save_as needs targetPath");
                if (DocType(target) != (int)C(doc, "GetType")!) throw new ArgumentException($"{Path.GetFileName(target)} must have the same type as {title}");
                int err = (int)C(doc, "SaveAs3", target, 0, SwSaveAsSilent)!;
                if (err != 0 || !File.Exists(target)) throw new InvalidOperationException($"saving {title} as {Path.GetFileName(target)} failed (error {err})");
                return new JsonObject { ["savedAs"] = target, ["from"] = docPath, ["note"] = $"The open document is now {Path.GetFileName(target)}." };
            case "save":
                if (docPath is null) throw new ArgumentException($"{title} was never saved: build or export it to a folder instead");
                SaveDoc(doc, title);
                return new JsonObject { ["saved"] = docPath };
            case "close":
                if (dirty && !save && !discard)
                    throw new ArgumentException($"{title} has unsaved changes: close it with save: true (keep them) or discard: true (lose them)");
                if (dirty && save)
                {
                    if (docPath is null) throw new ArgumentException($"{title} was never saved: use discard: true or build/export it first");
                    SaveDoc(doc, title);
                }
                C(_sw, "CloseDoc", title);
                return new JsonObject { ["closed"] = true, ["title"] = title, ["saved"] = dirty && save, ["discarded"] = dirty && discard && !save };
            default:
                throw new ArgumentException("action must be open, save, save_as or close");
        }
    }

    private static void SaveDoc(object doc, string title)
    {
        // Silent | SaveReferenced: an assembly's changed parts are saved with it instead of SolidWorks asking about them.
        var args = new object?[] { 1 | 4, 0, 0 };
        var mods = new ParameterModifier(3);
        mods[1] = true; mods[2] = true; // ref int Errors, ref int Warnings
        bool ok;
        try { ok = doc.GetType().InvokeMember("Save3", BindingFlags.InvokeMethod, null, doc, args, [mods], null, null) is true; }
        catch (TargetInvocationException ex) { throw new InvalidOperationException($"saving {title} failed: {ex.InnerException?.Message}", ex); }
        int err = args[1] is int e ? e : 0;
        if (!ok && err == 0) err = -1;
        if (err != 0) throw new InvalidOperationException($"saving {title} failed (error {err})");
    }

    private JsonObject DoExport(IReadOnlyList<string> paths, IReadOnlyList<string> formats, string folder)
    {
        Connect();
        Directory.CreateDirectory(folder);
        var files = new JsonArray();
        var problems = new JsonArray();
        var targets = paths.Count > 0 ? paths.Select(p => (Path: p, Doc: (object?)null)).ToList()
            : (C(_sw, "ActiveDoc") is { } a ? [(Path: (C(a, "GetPathName") as string) ?? "", Doc: (object?)a)] : throw new ArgumentException("no document is active; give paths"));
        foreach (var (path, given) in targets)
        {
            bool openedHere = false;
            object doc;
            try
            {
                if (given is not null) doc = given;
                else
                {
                    openedHere = OpenDoc(path) is null;
                    doc = OpenOrGet(path);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { problems.Add(ex.Message); continue; }
            string title = (string)C(doc, "GetTitle")!;
            string stem = Path.GetFileNameWithoutExtension(path.Length > 0 ? path : title);
            bool drawing = C(doc, "GetType") is SwDocDrawing;
            if (formats.Contains("png")) Activate(doc); // a picture is taken of the document's window
            foreach (var name in formats)
            {
                if (ExportFormats.FirstOrDefault(f => f.Name == name) is not { } format) { problems.Add($"unknown format {name} ({string.Join(", ", ExportFormats.Select(f => f.Name))})"); continue; }
                if (drawing ? !format.ForDrawings : !format.ForModels)
                {
                    problems.Add($"{title}: {name} is for {(drawing ? "parts and assemblies" : "drawings")}; drawings export to {string.Join("/", ExportFormats.Where(f => f.ForDrawings).Select(f => f.Name))}");
                    continue;
                }
                string target = Path.Combine(folder, stem + format.Extension);
                if (File.Exists(target)) { problems.Add($"{Path.GetFileName(target)} already exists in {folder}: not replaced"); continue; }
                int err = (int)C(doc, "SaveAs3", target, 0, SwSaveAsCopy)!;
                if (err == 0 && File.Exists(target)) files.Add(target);
                else problems.Add($"{title} → {name} failed (error {err})");
            }
            if (openedHere) C(_sw, "CloseDoc", title);
        }
        return new JsonObject { ["files"] = files, ["problems"] = problems };
    }
}
