using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Swx;

/// <summary>
/// Document handling in SolidWorks: what is open (<see cref="Session"/>), open/save/close (<see cref="Document"/>) and
/// neutral exports (<see cref="Export"/>). Paths are checked by the tool provider before they reach these.
/// </summary>
public sealed partial class SwxParametric
{
    private const int SwDocAssembly = 2, SwDocDrawing = 3;
    private const int SwOpenSilent = 1; // swOpenDocOptions_Silent

    /// <summary>Export formats: name → file extension. PDF is for drawings.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExportFormats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["step"] = ".step", ["stl"] = ".stl", ["iges"] = ".igs", ["parasolid"] = ".x_t", ["pdf"] = ".pdf",
    };

    public static int DocType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sldprt" => SwDocPart, ".sldasm" => SwDocAssembly, ".slddrw" => SwDocDrawing,
        _ => throw new ArgumentException($"{Path.GetFileName(path)}: not a SolidWorks part, assembly or drawing"),
    };

    private static string TypeName(object? t) => t switch { SwDocPart => "part", SwDocAssembly => "assembly", SwDocDrawing => "drawing", _ => "other" };

    public static JsonObject Session(TimeSpan timeout) => Run(p => p.DoSession(), timeout);
    public static JsonObject Document(string action, string? path, bool save, bool discard, TimeSpan timeout) =>
        Run(p => p.DoDocument(action, path, save, discard), timeout);
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

    private JsonObject DoDocument(string action, string? path, bool save, bool discard)
    {
        Connect();
        object? doc = path is null ? C(_sw, "ActiveDoc") : action == "open" ? OpenOrGet(path) : OpenDoc(path);
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
                var act = new object?[] { title, false, 0, 0 };
                var actMods = new ParameterModifier(4);
                actMods[3] = true; // ref int Errors
                try { _sw.GetType().InvokeMember("ActivateDoc3", BindingFlags.InvokeMethod, null, _sw, act, [actMods], null, null); }
                catch (Exception ex) when (ex is TargetInvocationException or COMException) { _log.Add("could not bring it to the front: " + ex.Message); }
                return new JsonObject { ["opened"] = docPath, ["title"] = title, ["type"] = TypeName(C(doc, "GetType")), ["unsaved"] = dirty };
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
                throw new ArgumentException("action must be open, save or close");
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
            foreach (var format in formats)
            {
                if (!ExportFormats.TryGetValue(format, out var ext)) { problems.Add($"unknown format {format} ({string.Join(", ", ExportFormats.Keys)})"); continue; }
                if (drawing != (ext == ".pdf")) { problems.Add($"{title}: {(drawing ? "drawings export to pdf" : "pdf is for drawings")}"); continue; }
                string target = Path.Combine(folder, stem + ext);
                int err = (int)C(doc, "SaveAs3", target, 0, SwSaveAsCopy)!;
                if (err == 0 && File.Exists(target)) files.Add(target);
                else problems.Add($"{title} → {format} failed (error {err})");
            }
            if (openedHere) C(_sw, "CloseDoc", title);
        }
        return new JsonObject { ["files"] = files, ["problems"] = problems };
    }
}
