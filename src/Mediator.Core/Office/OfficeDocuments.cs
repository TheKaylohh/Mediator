using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Mediator.Core.Office;

/// <summary>A document Mediator could not read or write (bad shape, wrong type, damaged package).</summary>
public sealed class OfficeDocumentException(string message) : Exception(message);

/// <summary>
/// V15: Word, Excel and PowerPoint files (.docx, .xlsx, .pptx) read and written directly as Office Open XML packages
/// (a zip of XML parts), so it works without Office installed and never runs a macro. Reading gives the model plain
/// text: headings, paragraphs, lists and tables for Word; cell values (and formulas) per sheet for Excel; each slide's
/// title, text and speaker notes for PowerPoint. Writing builds a new, valid file from a simple JSON description.
/// Legacy binary files (.doc, .xls, .ppt) are refused with a hint to save them in the new format.
/// </summary>
public static class OfficeDocuments
{
    public const int DefaultMaxChars = 60_000;
    public const int MaxSheetRows = 2_000;
    public const int MaxSheetColumns = 60;
    private const long MaxPackageBytes = 200L * 1024 * 1024;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static readonly string[] Extensions = [".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm"];
    private static readonly string[] Legacy = [".doc", ".xls", ".ppt"];

    public static bool IsOfficeFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The file as text for the model. <paramref name="sheet"/> limits an .xlsx to one sheet (name or 1-based number).</summary>
    public static string ReadText(string path, int maxChars = DefaultMaxChars, string? sheet = null)
    {
        using var stream = File.OpenRead(path);
        return ReadText(stream, Path.GetExtension(path), Path.GetFileName(path), maxChars, sheet);
    }

    public static string ReadText(Stream stream, string extension, string name, int maxChars = DefaultMaxChars, string? sheet = null)
    {
        string ext = extension.ToLowerInvariant();
        if (Legacy.Contains(ext))
            throw new OfficeDocumentException($"{name} is an old binary Office file ({ext}). Open it in Office and save it as {ext}x, then try again.");
        if (stream.CanSeek && stream.Length > MaxPackageBytes) throw new OfficeDocumentException($"{name} is over {MaxPackageBytes / (1024 * 1024)} MB.");
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new OfficeDocumentException($"{name} is not a valid Office file (it is not a zip package). It may be encrypted with a password."); }
        using (zip)
        {
            var sb = new StringBuilder();
            switch (ext)
            {
                case ".docx" or ".docm": ReadDocx(zip, sb); break;
                case ".xlsx" or ".xlsm": ReadXlsx(zip, sb, sheet); break;
                case ".pptx" or ".pptm": ReadPptx(zip, sb); break;
                default: throw new OfficeDocumentException($"{name}: only .docx, .xlsx and .pptx files are read.");
            }
            string text = sb.ToString().TrimEnd();
            return text.Length > maxChars ? text[..maxChars] + $"\n… [truncated: {name} has more than {maxChars:N0} characters; ask for a sheet or part]" : text;
        }
    }

    private static XDocument Part(ZipArchive zip, string partName)
    {
        var entry = zip.GetEntry(partName.TrimStart('/')) ?? throw new OfficeDocumentException($"The file has no {partName} part (damaged or not this kind of document).");
        using var s = entry.Open();
        try { return XDocument.Load(s); }
        catch (System.Xml.XmlException ex) { throw new OfficeDocumentException($"{partName} is not valid XML: {ex.Message}"); }
    }

    private static XDocument? OptionalPart(ZipArchive zip, string partName) =>
        zip.GetEntry(partName.TrimStart('/')) is null ? null : Part(zip, partName);

    /// <summary>Relationship id → target part path, for the relationships of <paramref name="partName"/>.</summary>
    private static Dictionary<string, string> Relationships(ZipArchive zip, string partName)
    {
        string dir = Path.GetDirectoryName(partName.TrimStart('/'))?.Replace('\\', '/') ?? "";
        string rels = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + Path.GetFileName(partName) + ".rels";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OptionalPart(zip, rels) is not { } doc) return map;
        foreach (var r in doc.Root!.Elements(PR + "Relationship"))
        {
            string? id = (string?)r.Attribute("Id"), target = (string?)r.Attribute("Target");
            if (id is null || target is null || (string?)r.Attribute("TargetMode") == "External") continue;
            map[id] = target.StartsWith('/') ? target.TrimStart('/') : Normalize((dir.Length > 0 ? dir + "/" : "") + target);
        }
        return map;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p.Length > 0 && p != ".") parts.Add(p);
        }
        return string.Join('/', parts);
    }

    private static void ReadDocx(ZipArchive zip, StringBuilder sb)
    {
        var body = Part(zip, "word/document.xml").Root!.Element(W + "body") ?? throw new OfficeDocumentException("The document has no body.");
        foreach (var block in body.Elements())
        {
            if (block.Name == W + "p") AppendParagraph(block, sb);
            else if (block.Name == W + "tbl") AppendTable(block, sb);
            else if (block.Name == W + "sdt") foreach (var p in block.Descendants(W + "p")) AppendParagraph(p, sb);
        }
    }

    private static string RunsText(XElement paragraph)
    {
        var t = new StringBuilder();
        foreach (var e in paragraph.Descendants())
        {
            if (e.Name == W + "t") t.Append(e.Value);
            else if (e.Name == W + "tab") t.Append('\t');
            else if (e.Name == W + "br" || e.Name == W + "cr") t.Append('\n');
        }
        return t.ToString();
    }

    private static void AppendParagraph(XElement p, StringBuilder sb)
    {
        string text = RunsText(p);
        var props = p.Element(W + "pPr");
        string style = (string?)props?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
        bool list = props?.Element(W + "numPr") is not null || style.StartsWith("List", StringComparison.OrdinalIgnoreCase);
        if (text.Trim().Length == 0) { if (sb.Length > 0 && !(sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n')) sb.Append('\n'); return; }
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) sb.Append("# ");
        else if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out int level)) sb.Append(new string('#', Math.Clamp(level + 1, 2, 6))).Append(' ');
        else if (list)
        {
            int indent = (int?)props?.Element(W + "numPr")?.Element(W + "ilvl")?.Attribute(W + "val") ?? 0;
            sb.Append(new string(' ', indent * 2)).Append("- ");
        }
        sb.Append(text).Append('\n');
    }

    private static void AppendTable(XElement tbl, StringBuilder sb)
    {
        sb.Append('\n');
        bool first = true;
        foreach (var row in tbl.Elements(W + "tr"))
        {
            var cells = row.Elements(W + "tc").Select(c => string.Join(" ", c.Elements(W + "p").Select(RunsText)).Replace('|', '/').Trim()).ToList();
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (first) { sb.Append('|').Append(string.Concat(cells.Select(_ => " --- |"))).Append('\n'); first = false; }
        }
        sb.Append('\n');
    }

    private static void ReadXlsx(ZipArchive zip, StringBuilder sb, string? onlySheet)
    {
        var workbook = Part(zip, "xl/workbook.xml");
        var rels = Relationships(zip, "xl/workbook.xml");
        var shared = new List<string>();
        if (OptionalPart(zip, "xl/sharedStrings.xml") is { } sst)
            foreach (var si in sst.Root!.Elements(S + "si")) shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        var sheets = workbook.Root!.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? [];
        if (sheets.Count == 0) throw new OfficeDocumentException("The workbook has no sheets.");
        sb.Append("Sheets: ").Append(string.Join(", ", sheets.Select((s, i) => $"{i + 1}. {(string?)s.Attribute("name")}"))).Append('\n');
        int number = 0;
        bool matched = false;
        foreach (var sheet in sheets)
        {
            number++;
            string name = (string?)sheet.Attribute("name") ?? $"Sheet{number}";
            if (onlySheet is { Length: > 0 } && !name.Equals(onlySheet, StringComparison.OrdinalIgnoreCase) && onlySheet != number.ToString(CultureInfo.InvariantCulture)) continue;
            matched = true;
            string? rid = (string?)sheet.Attribute(R + "id");
            if (rid is null || !rels.TryGetValue(rid, out var target)) continue;
            sb.Append("\n## Sheet ").Append(name).Append('\n');
            var data = Part(zip, target).Root!.Element(S + "sheetData");
            int rows = 0;
            foreach (var row in data?.Elements(S + "row") ?? [])
            {
                if (++rows > MaxSheetRows) { sb.Append($"… [more rows: only the first {MaxSheetRows} are shown]\n"); break; }
                var cells = new List<string>();
                foreach (var c in row.Elements(S + "c"))
                {
                    string reference = (string?)c.Attribute("r") ?? "";
                    if (ColumnIndex(reference) > MaxSheetColumns) continue;
                    string value = CellValue(c, shared);
                    string? formula = c.Element(S + "f")?.Value;
                    if (value.Length == 0 && formula is null) continue;
                    cells.Add(formula is { Length: > 0 } ? $"{reference}: ={formula} → {value}" : $"{reference}: {value}");
                }
                if (cells.Count > 0) sb.Append(string.Join("  ", cells)).Append('\n');
            }
            if (rows == 0) sb.Append("(empty)\n");
        }
        if (!matched) throw new OfficeDocumentException($"No sheet named or numbered '{onlySheet}'.");
    }

    private static string CellValue(XElement c, List<string> shared)
    {
        string type = (string?)c.Attribute("t") ?? "n";
        string raw = c.Element(S + "v")?.Value ?? "";
        return type switch
        {
            "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < shared.Count ? shared[i] : raw,
            "inlineStr" => string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []),
            "b" => raw == "1" ? "TRUE" : "FALSE",
            _ => raw,
        };
    }

    /// <summary>"C12" → 3. 0 when there is no column.</summary>
    public static int ColumnIndex(string reference)
    {
        int n = 0;
        foreach (char ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
        }
        return n;
    }

    /// <summary>3 → "C", 27 → "AA".</summary>
    public static string ColumnName(int index)
    {
        var s = new StringBuilder();
        for (int n = index; n > 0; n = (n - 1) / 26) s.Insert(0, (char)('A' + (n - 1) % 26));
        return s.ToString();
    }

    private static void ReadPptx(ZipArchive zip, StringBuilder sb)
    {
        var pres = Part(zip, "ppt/presentation.xml");
        var rels = Relationships(zip, "ppt/presentation.xml");
        var ids = pres.Root!.Element(P + "sldIdLst")?.Elements(P + "sldId").Select(s => (string?)s.Attribute(R + "id")).ToList() ?? [];
        sb.Append($"{ids.Count} slide(s)\n");
        int number = 0;
        foreach (var rid in ids)
        {
            number++;
            if (rid is null || !rels.TryGetValue(rid, out var slidePath)) continue;
            var slide = Part(zip, slidePath);
            string? title = null;
            var lines = new List<string>();
            foreach (var sp in slide.Descendants(P + "sp"))
            {
                string? ph = (string?)sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type");
                var paragraphs = sp.Element(P + "txBody")?.Elements(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim()).Where(t => t.Length > 0).ToList() ?? [];
                if (paragraphs.Count == 0) continue;
                if (title is null && ph is "title" or "ctrTitle") title = string.Join(" ", paragraphs);
                else lines.AddRange(paragraphs.Select(p => "- " + p));
            }
            foreach (var tbl in slide.Descendants(A + "tbl"))
                foreach (var tr in tbl.Elements(A + "tr"))
                    lines.Add("| " + string.Join(" | ", tr.Elements(A + "tc").Select(tc => string.Concat(tc.Descendants(A + "t").Select(t => t.Value)).Trim())) + " |");
            sb.Append($"\n## Slide {number}: {title ?? "(no title)"}\n");
            foreach (var l in lines) sb.Append(l).Append('\n');
            var slideRels = Relationships(zip, slidePath);
            string? notesPath = slideRels.Values.FirstOrDefault(v => v.Contains("notesSlide", StringComparison.OrdinalIgnoreCase));
            if (notesPath is not null && OptionalPart(zip, notesPath) is { } notes)
            {
                string text = string.Join(" ", notes.Descendants(P + "sp")
                    .Where(sp => (string?)sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type") == "body")
                    .SelectMany(sp => sp.Descendants(A + "t")).Select(t => t.Value)).Trim();
                if (text.Length > 0) sb.Append("Notes: ").Append(text).Append('\n');
            }
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a new document at <paramref name="path"/> (the extension picks the kind). <paramref name="content"/>:
    /// .docx {title?, blocks:[{type: heading|paragraph|bullet|numbered|table|pagebreak, text?, level?, rows?}]};
    /// .xlsx {sheets:[{name, rows:[[cell, ...]], columnWidths?}]} (a cell is a number, a bool, text, or "=FORMULA");
    /// .pptx {title?, slides:[{title, bullets?:[...], notes?}]}. Text may use **bold**. Returns a one-line summary.
    /// </summary>
    public static string Write(string path, JsonObject content)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var ms = Build(ext, content, out string summary);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) ms.WriteTo(fs);
        return summary;
    }

    /// <summary>The finished package in memory (built completely before any file is created, so a bad description leaves no file).</summary>
    public static MemoryStream Build(string extension, JsonObject content, out string summary)
    {
        string ext = extension.ToLowerInvariant();
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            summary = ext switch
            {
                ".docx" => WriteDocx(zip, content),
                ".xlsx" => WriteXlsx(zip, content),
                ".pptx" => WritePptx(zip, content),
                _ => throw new OfficeDocumentException("Only .docx, .xlsx and .pptx files can be created."),
            };
        }
        return ms;
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    private static string X(string? s) => SecurityElement.Escape(StripControl(s ?? "")) ?? "";

    /// <summary>XML 1.0 forbids most control characters; a model's text can contain them.</summary>
    private static string StripControl(string s) => s.Any(c => c < 0x20 && c is not ('\t' or '\n' or '\r')) ? new string(s.Where(c => c >= 0x20 || c is '\t' or '\n' or '\r').ToArray()) : s;

    private static string Str(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue(out string? s) => s ?? "",
        _ => n.ToJsonString().Trim('"'),
    };

    private static JsonArray Arr(JsonObject o, string key, string what) =>
        o[key] as JsonArray ?? throw new OfficeDocumentException($"'{key}' must be an array of {what}.");

    /// <summary>"plain **bold** plain" → (text, bold) pieces.</summary>
    private static IEnumerable<(string Text, bool Bold)> Pieces(string text)
    {
        var parts = text.Split("**");
        bool balanced = parts.Length % 2 == 1; // an unmatched ** leaves the rest plain
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) yield return (parts[i], i % 2 == 1 && (balanced || i < parts.Length - 1));
    }

    private const string ContentTypesHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>";
    private const string RelsHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">";
    private const string OfficeDocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    private static string WriteDocx(ZipArchive zip, JsonObject content)
    {
        var blocks = Arr(content, "blocks", "blocks");
        var body = new StringBuilder();
        string Runs(string text) => string.Concat(Pieces(text).Select(p => $"<w:r>{(p.Bold ? "<w:rPr><w:b/></w:rPr>" : "")}<w:t xml:space=\"preserve\">{X(p.Text)}</w:t></w:r>"));
        string Para(string? style, string text, string extraProps = "") =>
            $"<w:p>{(style is null && extraProps.Length == 0 ? "" : $"<w:pPr>{(style is null ? "" : $"<w:pStyle w:val=\"{style}\"/>")}{extraProps}</w:pPr>")}{Runs(text)}</w:p>";
        if (content["title"] is { } title && Str(title).Length > 0) body.Append(Para("Title", Str(title)));
        int count = 0;
        foreach (var node in blocks)
        {
            if (node is not JsonObject b) throw new OfficeDocumentException("Each block must be an object with a type.");
            count++;
            string type = Str(b["type"]).ToLowerInvariant(), text = Str(b["text"]);
            switch (type)
            {
                case "heading":
                    int level = b["level"] is JsonValue lv && lv.TryGetValue(out int l) ? Math.Clamp(l, 1, 3) : 1;
                    body.Append(Para("Heading" + level, text));
                    break;
                case "paragraph" or "text" or "":
                    foreach (var line in text.Split('\n')) body.Append(Para(null, line));
                    break;
                case "bullet":
                    body.Append(Para("ListBullet", text, "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
                    break;
                case "numbered":
                    body.Append(Para("ListNumber", text, "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"2\"/></w:numPr>"));
                    break;
                case "pagebreak":
                    body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    break;
                case "table":
                    var rows = Arr(b, "rows", "rows (arrays of cells)");
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>")
                        .Append(string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(s => $"<w:{s} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"A0A0A0\"/>")))
                        .Append("</w:tblBorders></w:tblPr>");
                    bool header = true;
                    foreach (var r in rows)
                    {
                        if (r is not JsonArray cells) throw new OfficeDocumentException("Each table row must be an array of cells.");
                        body.Append("<w:tr>");
                        foreach (var c in cells) body.Append("<w:tc><w:tcPr><w:tcW w:w=\"0\" w:type=\"auto\"/></w:tcPr>").Append(Para(null, header ? "**" + Str(c) + "**" : Str(c))).Append("</w:tc>");
                        body.Append("</w:tr>");
                        header = false;
                    }
                    body.Append("</w:tbl><w:p/>");
                    break;
                default:
                    throw new OfficeDocumentException($"Unknown block type '{type}' (heading, paragraph, bullet, numbered, table, pagebreak).");
            }
        }
        Add(zip, "[Content_Types].xml", ContentTypesHead
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
            + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
            + "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/></Types>");
        Add(zip, "_rels/.rels", RelsHead + $"<Relationship Id=\"rId1\" Type=\"{OfficeDocRel}\" Target=\"word/document.xml\"/></Relationships>");
        Add(zip, "word/_rels/document.xml.rels", RelsHead
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>");
        Add(zip, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"
            + body + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
        Add(zip, "word/styles.xml", DocxStyles);
        Add(zip, "word/numbering.xml", DocxNumbering);
        return $"Word document with {count} block(s)";
    }

    private const string DocxStyles = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:after="240"/></w:pPr><w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:sz w:val="52"/><w:szCs w:val="52"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:color w:val="1F3864"/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="60"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:color w:val="2F5496"/><w:sz w:val="26"/><w:szCs w:val="26"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="160" w:after="40"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="ListBullet"><w:name w:val="List Bullet"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="60"/><w:ind w:left="720" w:hanging="360"/></w:pPr></w:style>
<w:style w:type="paragraph" w:styleId="ListNumber"><w:name w:val="List Number"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="60"/><w:ind w:left="720" w:hanging="360"/></w:pPr></w:style>
<w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
<w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:basedOn w:val="TableNormal"/><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:style>
</w:styles>
""";

    private const string DocxNumbering = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum>
<w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum>
<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
<w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
</w:numbering>
""";

    private static string WriteXlsx(ZipArchive zip, JsonObject content)
    {
        var sheets = Arr(content, "sheets", "sheets");
        if (sheets.Count == 0) throw new OfficeDocumentException("A workbook needs at least one sheet.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sheetEntries = new StringBuilder();
        var rels = new StringBuilder(RelsHead);
        var overrides = new StringBuilder();
        int index = 0, cellsWritten = 0;
        foreach (var node in sheets)
        {
            if (node is not JsonObject sheet) throw new OfficeDocumentException("Each sheet must be an object {name, rows}.");
            index++;
            string name = SheetName(Str(sheet["name"]), index);
            if (!names.Add(name)) name = SheetName(name + " " + index, index);
            names.Add(name);
            var rows = Arr(sheet, "rows", "rows (arrays of cells)");
            var data = new StringBuilder();
            int r = 0;
            foreach (var rowNode in rows)
            {
                r++;
                if (rowNode is not JsonArray cells) throw new OfficeDocumentException("Each row must be an array of cells.");
                data.Append($"<row r=\"{r}\">");
                int col = 0;
                foreach (var cell in cells)
                {
                    col++;
                    string reference = ColumnName(col) + r.ToString(CultureInfo.InvariantCulture);
                    string style = r == 1 ? " s=\"1\"" : "";
                    switch (cell)
                    {
                        case null: continue;
                        case JsonValue v when v.TryGetValue(out double d):
                            data.Append($"<c r=\"{reference}\"{style}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>"); break;
                        case JsonValue v when v.TryGetValue(out bool flag):
                            data.Append($"<c r=\"{reference}\"{style} t=\"b\"><v>{(flag ? 1 : 0)}</v></c>"); break;
                        default:
                            string text = Str(cell);
                            if (text.StartsWith('=') && text.Length > 1) data.Append($"<c r=\"{reference}\"{style}><f>{X(text[1..])}</f></c>");
                            else data.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(text)}</t></is></c>");
                            break;
                    }
                    cellsWritten++;
                }
                data.Append("</row>");
            }
            string cols = "";
            if (sheet["columnWidths"] is JsonArray widths && widths.Count > 0)
                cols = "<cols>" + string.Concat(widths.Select((w, i) => w is JsonValue wv && wv.TryGetValue(out double width)
                    ? $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Math.Clamp(width, 1, 255).ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>" : "")) + "</cols>";
            Add(zip, $"xl/worksheets/sheet{index}.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + (r > 1 ? "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" : "")
                + cols + "<sheetData>" + data + "</sheetData></worksheet>");
            sheetEntries.Append($"<sheet name=\"{X(name)}\" sheetId=\"{index}\" r:id=\"rId{index}\"/>");
            rels.Append($"<Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>");
            overrides.Append($"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        rels.Append($"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Add(zip, "[Content_Types].xml", ContentTypesHead
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" + overrides + "</Types>");
        Add(zip, "_rels/.rels", RelsHead + $"<Relationship Id=\"rId1\" Type=\"{OfficeDocRel}\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
        Add(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
            + sheetEntries + "</sheets><calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>");
        Add(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        return $"Excel workbook with {index} sheet(s), {cellsWritten} cell(s)";
    }

    /// <summary>Excel sheet names: 1-31 characters, none of : \ / ? * [ ].</summary>
    private static string SheetName(string name, int index)
    {
        string clean = new(name.Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']') && !char.IsControl(c)).ToArray());
        clean = clean.Trim().Trim('\'');
        if (clean.Length == 0) clean = "Sheet" + index.ToString(CultureInfo.InvariantCulture);
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private static string WritePptx(ZipArchive zip, JsonObject content)
    {
        var slides = Arr(content, "slides", "slides");
        if (slides.Count == 0) throw new OfficeDocumentException("A presentation needs at least one slide.");
        var presRels = new StringBuilder(RelsHead)
            .Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>")
            .Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>");
        var ids = new StringBuilder();
        var overrides = new StringBuilder();
        int n = 0;
        foreach (var node in slides)
        {
            if (node is not JsonObject slide) throw new OfficeDocumentException("Each slide must be an object {title, bullets?, notes?}.");
            n++;
            string title = Str(slide["title"]);
            var bullets = (slide["bullets"] as JsonArray)?.Select(Str).Where(b => b.Length > 0).ToList() ?? [];
            bool first = n == 1 && bullets.Count == 0;
            string body = bullets.Count == 0 ? "" : string.Concat(bullets.Select(b => $"<a:p>{string.Concat(Pieces(b).Select(p => $"<a:r><a:rPr lang=\"en-US\" dirty=\"0\"{(p.Bold ? " b=\"1\"" : "")}/><a:t>{X(p.Text)}</a:t></a:r>"))}</a:p>"));
            Add(zip, $"ppt/slides/slide{n}.xml", SlideXml(title, body, first));
            string notes = Str(slide["notes"]);
            var slideRels = new StringBuilder(RelsHead).Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>");
            if (notes.Length > 0)
            {
                slideRels.Append($"<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide\" Target=\"../notesSlides/notesSlide{n}.xml\"/>");
                Add(zip, $"ppt/notesSlides/notesSlide{n}.xml", NotesXml(notes));
                Add(zip, $"ppt/notesSlides/_rels/notesSlide{n}.xml.rels", RelsHead
                    + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesMaster\" Target=\"../notesMasters/notesMaster1.xml\"/>"
                    + $"<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"../slides/slide{n}.xml\"/></Relationships>");
                overrides.Append($"<Override PartName=\"/ppt/notesSlides/notesSlide{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml\"/>");
            }
            Add(zip, $"ppt/slides/_rels/slide{n}.xml.rels", slideRels.Append("</Relationships>").ToString());
            presRels.Append($"<Relationship Id=\"rId{n + 10}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide{n}.xml\"/>");
            ids.Append($"<p:sldId id=\"{255 + n}\" r:id=\"rId{n + 10}\"/>");
            overrides.Append($"<Override PartName=\"/ppt/slides/slide{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        }
        bool anyNotes = overrides.ToString().Contains("notesSlide", StringComparison.Ordinal);
        if (anyNotes)
        {
            presRels.Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesMaster\" Target=\"notesMasters/notesMaster1.xml\"/>");
            Add(zip, "ppt/notesMasters/notesMaster1.xml", NotesMasterXml);
            Add(zip, "ppt/notesMasters/_rels/notesMaster1.xml.rels", RelsHead + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme2.xml\"/></Relationships>");
            Add(zip, "ppt/theme/theme2.xml", ThemeXml);
            overrides.Append("<Override PartName=\"/ppt/notesMasters/notesMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesMaster+xml\"/>")
                .Append("<Override PartName=\"/ppt/theme/theme2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        }
        Add(zip, "[Content_Types].xml", ContentTypesHead
            + "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>"
            + "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>"
            + "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>"
            + "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" + overrides + "</Types>");
        Add(zip, "_rels/.rels", RelsHead + $"<Relationship Id=\"rId1\" Type=\"{OfficeDocRel}\" Target=\"ppt/presentation.xml\"/></Relationships>");
        Add(zip, "ppt/_rels/presentation.xml.rels", presRels.Append("</Relationships>").ToString());
        Add(zip, "ppt/presentation.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:presentation xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" saveSubsetFonts=\"1\">"
            + "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>"
            + (anyNotes ? "<p:notesMasterIdLst><p:notesMasterId r:id=\"rId3\"/></p:notesMasterIdLst>" : "")
            + "<p:sldIdLst>" + ids + "</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>"
            + "<p:defaultTextStyle><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:defaultTextStyle></p:presentation>");
        Add(zip, "ppt/slideMasters/slideMaster1.xml", SlideMasterXml);
        Add(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", RelsHead
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
        Add(zip, "ppt/slideLayouts/slideLayout1.xml", SlideLayoutXml);
        Add(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", RelsHead
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        Add(zip, "ppt/theme/theme1.xml", ThemeXml);
        return $"PowerPoint presentation with {n} slide(s)";
    }

    private const string PNs = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string EmptyGroup = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string Shape(int id, string name, string ph, string xfrm, string paragraphs, string bodyPr = "<a:bodyPr/>") =>
        $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr><p:spPr>{xfrm}</p:spPr><p:txBody>{bodyPr}<a:lstStyle/>{paragraphs}</p:txBody></p:sp>";

    private const string TitleXfrm = "<a:xfrm><a:off x=\"838200\" y=\"365125\"/><a:ext cx=\"10515600\" cy=\"1325563\"/></a:xfrm>";
    private const string BodyXfrm = "<a:xfrm><a:off x=\"838200\" y=\"1825625\"/><a:ext cx=\"10515600\" cy=\"4351338\"/></a:xfrm>";
    private const string CoverXfrm = "<a:xfrm><a:off x=\"1524000\" y=\"2130425\"/><a:ext cx=\"9144000\" cy=\"1470025\"/></a:xfrm>";

    private static string SlideXml(string title, string bodyParagraphs, bool cover)
    {
        string titleP = $"<a:p><a:r><a:rPr lang=\"en-US\" dirty=\"0\"/><a:t>{X(title)}</a:t></a:r></a:p>";
        string shapes = Shape(2, "Title 1", "<p:ph type=\"title\"/>", cover ? CoverXfrm : "", cover ? titleP.Replace("<a:p>", "<a:p><a:pPr algn=\"ctr\"/>", StringComparison.Ordinal) : titleP)
            + (bodyParagraphs.Length > 0 ? Shape(3, "Content 2", "<p:ph idx=\"1\"/>", "", bodyParagraphs) : "");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sld {PNs}><p:cSld><p:spTree>{EmptyGroup}{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string NotesXml(string notes) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:notes {PNs}><p:cSld><p:spTree>{EmptyGroup}"
        + Shape(2, "Notes 1", "<p:ph type=\"body\" idx=\"1\"/>", "", string.Concat(notes.Split('\n').Select(l => $"<a:p><a:r><a:rPr lang=\"en-US\" dirty=\"0\"/><a:t>{X(l)}</a:t></a:r></a:p>")))
        + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>";

    private static readonly string SlideMasterXml =
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldMaster {PNs}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{EmptyGroup}"
        + Shape(2, "Title Placeholder 1", "<p:ph type=\"title\"/>", TitleXfrm, "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>", "<a:bodyPr anchor=\"ctr\"><a:normAutofit/></a:bodyPr>")
        + Shape(3, "Text Placeholder 2", "<p:ph type=\"body\" idx=\"1\"/>", BodyXfrm, "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>", "<a:bodyPr><a:normAutofit/></a:bodyPr>")
        + "</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
        + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>"
        + "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"4000\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle>"
        + "<p:bodyStyle><a:lvl1pPr marL=\"228600\" indent=\"-228600\"><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef><a:buFont typeface=\"Arial\"/><a:buChar char=\"&#8226;\"/><a:defRPr sz=\"2400\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/></a:defRPr></a:lvl1pPr></p:bodyStyle>"
        + "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>";

    private static readonly string SlideLayoutXml =
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldLayout {PNs} type=\"obj\" preserve=\"1\"><p:cSld name=\"Title and Content\"><p:spTree>{EmptyGroup}"
        + Shape(2, "Title 1", "<p:ph type=\"title\"/>", "", "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>")
        + Shape(3, "Content Placeholder 2", "<p:ph idx=\"1\"/>", "", "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>")
        + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static readonly string NotesMasterXml =
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:notesMaster {PNs}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{EmptyGroup}"
        + Shape(2, "Notes Placeholder 1", "<p:ph type=\"body\" idx=\"1\"/>", "<a:xfrm><a:off x=\"685800\" y=\"4400550\"/><a:ext cx=\"5486400\" cy=\"3600450\"/></a:xfrm>", "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>")
        + "</p:spTree></p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/></p:notesMaster>";

    private static string ThemeColor(string name, string hex) => $"<a:{name}><a:srgbClr val=\"{hex}\"/></a:{name}>";

    private static readonly string ThemeXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Mediator\"><a:themeElements>"
        + "<a:clrScheme name=\"Mediator\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
        + ThemeColor("dk2", "1F2937") + ThemeColor("lt2", "E7E6E6") + ThemeColor("accent1", "2F6FDE") + ThemeColor("accent2", "ED7D31") + ThemeColor("accent3", "A5A5A5")
        + ThemeColor("accent4", "FFC000") + ThemeColor("accent5", "5B9BD5") + ThemeColor("accent6", "70AD47") + ThemeColor("hlink", "0563C1") + ThemeColor("folHlink", "954F72") + "</a:clrScheme>"
        + "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
        + "<a:fmtScheme name=\"Office\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>"
        + "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>"
        + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>"
        + "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>"
        + "</a:themeElements></a:theme>";
}
