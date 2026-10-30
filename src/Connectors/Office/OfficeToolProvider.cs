using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Security;

namespace Mediator.Connectors.Office;

/// <summary>
/// V15: the Microsoft Office tools. Word, Excel and PowerPoint files are read and created inside the job's folders
/// (no Office needed, no macros run); a new file never replaces an existing one. Outlook (classic desktop) is searched,
/// read and drafted into; every mailbox call asks the user first, and Mediator never sends mail.
/// </summary>
public sealed class OfficeToolProvider(IOutlookClient? outlook = null, Action<ProcessStartInfo>? openFile = null) : IToolProvider
{
    public const string ReadTool = "office_read_document";
    public const string CreateTool = "office_create_document";
    public const string OpenTool = "office_open_document";
    public const string SearchMailTool = "outlook_search_mail";
    public const string ReadMailTool = "outlook_read_mail";
    public const string DraftTool = "outlook_create_draft";
    public const string CalendarTool = "outlook_calendar";
    public static readonly string[] DocumentTools = [ReadTool, CreateTool, OpenTool];
    public static readonly string[] MailTools = [SearchMailTool, ReadMailTool, DraftTool, CalendarTool];
    public static readonly string[] Names = [.. DocumentTools, .. MailTools];

    private readonly IOutlookClient _outlook = outlook ?? new OutlookClient();
    private readonly Action<ProcessStartInfo> _open = openFile ?? (psi => { using var p = Process.Start(psi); });

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ReadTool,
            "Read a Word (.docx), Excel (.xlsx) or PowerPoint (.pptx) file as text: headings, paragraphs, lists and tables; every sheet's cells " +
            "(\"B4: =SUM(B2:B3) → 14\"); each slide's title, bullets and speaker notes. sheet: one sheet by name or number. Old .doc/.xls/.ppt are refused.",
            Schema("""{ "type": "object", "required": ["path"], "properties": { "path": { "type": "string" }, "sheet": { "type": "string" }, "maxChars": { "type": "integer" } }, "additionalProperties": false }"""),
            ToolRisk.ReadOnly, null, TimeSpan.FromMinutes(1)),

        new(CreateTool,
            "Create a NEW Word, Excel or PowerPoint file (the extension of path picks it; an existing file is never replaced: choose a new name). " +
            "content for .docx: {title?, blocks:[{type:\"heading\",text,level:1-3} | {type:\"paragraph\",text} | {type:\"bullet\",text} | {type:\"numbered\",text} | " +
            "{type:\"table\",rows:[[\"Header\",...],[...]]} | {type:\"pagebreak\"}]}. .xlsx: {sheets:[{name, rows:[[cell,...]], columnWidths?:[number]}]}, a cell is " +
            "a number, true/false, text or a formula \"=SUM(B2:B9)\"; row 1 is bold and frozen. .pptx: {slides:[{title, bullets?:[text], notes?}]} (16:9; " +
            "a first slide without bullets is a title slide). Text may use **bold**. The user approves the file before it is written.",
            Schema("""{ "type": "object", "required": ["path", "content"], "properties": { "path": { "type": "string" }, "content": { "type": "object" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromMinutes(2)),

        new(OpenTool,
            "Open a document from the job's folders in its desktop app (Word, Excel, PowerPoint, ...) for the user to look at or edit. Asks first.",
            Schema("""{ "type": "object", "required": ["path"], "properties": { "path": { "type": "string" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromSeconds(30)),

        new(SearchMailTool,
            "Search the user's classic Outlook mail, newest first: folder inbox | sent | drafts (default inbox), query (in subject, sender or the start of the " +
            "body), from (name or address), days back (default 14), unreadOnly, max (default 20, up to 50). Returns date, sender, subject, a preview and an id " +
            "for outlook_read_mail. Asks the user first (private mail).",
            Schema("""{ "type": "object", "properties": { "folder": { "type": "string", "enum": ["inbox", "sent", "drafts"] }, "query": { "type": "string" }, "from": { "type": "string" }, "days": { "type": "integer" }, "unreadOnly": { "type": "boolean" }, "max": { "type": "integer" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromMinutes(2)),

        new(ReadMailTool,
            "Read one Outlook message in full (id from outlook_search_mail): sender, recipients, date, attachment names and body text. Asks the user first.",
            Schema("""{ "type": "object", "required": ["id"], "properties": { "id": { "type": "string" }, "maxChars": { "type": "integer" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromMinutes(1)),

        new(DraftTool,
            "Write an email as a DRAFT in Outlook's Drafts folder (Mediator never sends mail; the user reviews and sends it). to/cc: addresses separated by ';'. " +
            "html true when body is HTML. show true opens the draft for the user. The user approves the draft first.",
            Schema("""{ "type": "object", "required": ["to", "subject", "body"], "properties": { "to": { "type": "string" }, "cc": { "type": "string" }, "subject": { "type": "string" }, "body": { "type": "string" }, "html": { "type": "boolean" }, "show": { "type": "boolean" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromMinutes(1)),

        new(CalendarTool,
            "List the user's Outlook calendar events between from and to (ISO dates, e.g. 2026-10-29 or 2026-10-29T14:00; default: today and the next 7 days), " +
            "recurring meetings included, at most 100. Asks the user first.",
            Schema("""{ "type": "object", "properties": { "from": { "type": "string" }, "to": { "type": "string" }, "max": { "type": "integer" } }, "additionalProperties": false }"""),
            ToolRisk.Write, null, TimeSpan.FromMinutes(1)),
    ];

    private static string? Str(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement a, string name, int fallback) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
    private static bool Bool(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonObject Preview(string kind, string title, string summary, params (string Key, string Primary, string Detail)[] rows) => new()
    {
        ["kind"] = kind, ["title"] = title, ["summary"] = summary, ["selectable"] = false,
        ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["key"] = r.Key, ["primary"] = r.Primary, ["detail"] = r.Detail }).ToArray()),
    };

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        var a = call.Arguments;
        JsonNode? preview = call.Tool.Name switch
        {
            CreateTool => Preview("office.create", "Create " + Path.GetFileName(Str(a, "path") ?? "a document"), "A new file; nothing existing is changed.",
                ("path", Str(a, "path") ?? "", DescribeContent(a))),
            OpenTool => Preview("office.open", "Open " + Path.GetFileName(Str(a, "path") ?? ""), "Opens the file in its desktop app.", ("path", Str(a, "path") ?? "", "")),
            SearchMailTool => Preview("outlook.search", "Search your Outlook mail", "Reads subjects, senders and previews from your mailbox.",
                ("folder", Str(a, "folder") ?? "inbox", $"query: {Str(a, "query") ?? "(any)"} · from: {Str(a, "from") ?? "(anyone)"} · last {Int(a, "days", 14)} days")),
            ReadMailTool => Preview("outlook.read", "Read one Outlook message", "Reads the whole message.", ("id", Str(a, "id") ?? "", "")),
            DraftTool => Preview("outlook.draft", "Save an email draft (not sent)", "Saved in Drafts for you to review and send.",
                ("to", Str(a, "to") ?? "", "Subject: " + (Str(a, "subject") ?? "")), ("body", Clip(Str(a, "body"), 400), "")),
            CalendarTool => Preview("outlook.calendar", "Read your Outlook calendar", "Lists events in a date range.",
                ("range", $"{Str(a, "from") ?? "today"} → {Str(a, "to") ?? "+7 days"}", "")),
            _ => null,
        };
        return Task.FromResult(preview);
    }

    private static string Clip(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] + "…" : s;

    private static string DescribeContent(JsonElement a)
    {
        if (!a.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Object) return "";
        int Count(string key) => c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
        return Path.GetExtension(Str(a, "path") ?? "").ToLowerInvariant() switch
        {
            ".docx" => $"{Count("blocks")} block(s)",
            ".xlsx" => $"{Count("sheets")} sheet(s)",
            ".pptx" => $"{Count("slides")} slide(s)",
            _ => "",
        };
    }

    public Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            var a = call.Arguments;
            return Task.FromResult(call.Tool.Name switch
            {
                ReadTool => Read(call.Job, a),
                CreateTool => Create(call.Job, a),
                OpenTool => Open(call.Job, a),
                SearchMailTool => Mail(() =>
                {
                    var found = _outlook.SearchMail(Str(a, "folder") ?? "inbox", Str(a, "query"), Str(a, "from"), Int(a, "days", 14), Bool(a, "unreadOnly"), Math.Clamp(Int(a, "max", 20), 1, 50));
                    return ToolResult.Ok($"{found.Count} message(s):\n" + OutlookClient.Describe(found), new JsonObject { ["messages"] = found });
                }),
                ReadMailTool => Mail(() =>
                {
                    var m = _outlook.ReadMail(Str(a, "id") ?? throw new OutlookException("id is required."), Int(a, "maxChars", 20_000));
                    return ToolResult.Ok($"From: {m["from"]} <{m["fromAddress"]}>\nTo: {m["to"]}\nCc: {m["cc"]}\nDate: {m["date"]}\nSubject: {m["subject"]}\n" +
                        $"Attachments: {string.Join(", ", ((JsonArray)m["attachments"]!).Select(x => (string?)x))}\n\n{m["body"]}");
                }),
                DraftTool => Mail(() =>
                {
                    string to = Str(a, "to") ?? "", subject = Str(a, "subject") ?? "", body = Str(a, "body") ?? "";
                    if (to.Trim().Length == 0) return ToolResult.Fail("to is required.");
                    string id = _outlook.CreateDraft(to, Str(a, "cc"), subject, body, Bool(a, "html"), Bool(a, "show"));
                    return ToolResult.Ok($"Saved a draft to {to} (\"{subject}\") in Outlook's Drafts. It was NOT sent; the user sends it from Outlook. id: {id}");
                }),
                CalendarTool => Mail(() =>
                {
                    var from = ParseDate(Str(a, "from")) ?? DateTime.Today;
                    var to = ParseDate(Str(a, "to")) ?? from.AddDays(7);
                    if (to <= from) to = from.AddDays(1);
                    var events = _outlook.Calendar(from, to, Math.Clamp(Int(a, "max", 50), 1, 100));
                    string text = events.Count == 0 ? "No events." : string.Join("\n", events.Select(e =>
                        $"- {e!["start"]} → {e["end"]}{((bool?)e["allDay"] == true ? " (all day)" : "")} | {e["subject"]}{((string?)e["location"] is { Length: > 0 } loc ? " @ " + loc : "")}{((bool?)e["recurring"] == true ? " (recurring)" : "")}"));
                    return ToolResult.Ok($"{events.Count} event(s) from {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}:\n{text}", new JsonObject { ["events"] = events });
                }),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            });
        }
        catch (PathDeniedException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
        catch (Exception ex) when (ex is OfficeDocumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }

    private static DateTime? ParseDate(string? s) =>
        s is { Length: > 0 } && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;

    private static ToolResult Mail(Func<ToolResult> work)
    {
        try { return work(); }
        catch (OutlookException ex) { return ToolResult.Fail(ex.Message); }
    }

    private static ToolResult Read(JobGrant job, JsonElement a)
    {
        string raw = Str(a, "path") ?? "";
        using var fs = job.Paths.OpenRead(raw);
        string text = OfficeDocuments.ReadText(fs, Path.GetExtension(raw), Path.GetFileName(raw), Math.Clamp(Int(a, "maxChars", OfficeDocuments.DefaultMaxChars), 1000, 200_000), Str(a, "sheet"));
        return ToolResult.Ok(text);
    }

    private static ToolResult Create(JobGrant job, JsonElement a)
    {
        string raw = Str(a, "path") ?? "";
        string ext = Path.GetExtension(raw).ToLowerInvariant();
        if (ext is not (".docx" or ".xlsx" or ".pptx")) return ToolResult.Fail("path must end in .docx, .xlsx or .pptx.");
        if (!a.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Object) return ToolResult.Fail("content must be an object.");
        using var package = OfficeDocuments.Build(ext, JsonNode.Parse(c.GetRawText())!.AsObject(), out string summary); // a bad description writes nothing
        using (var fs = job.Paths.CreateNew(raw)) package.WriteTo(fs);
        var verdict = job.Paths.Check(raw, PathAccess.Read);
        return ToolResult.Ok($"Created {verdict.FullPath}: {summary}.", new JsonObject { ["path"] = verdict.FullPath });
    }

    private ToolResult Open(JobGrant job, JsonElement a)
    {
        var verdict = job.Paths.VerifyExisting(Str(a, "path") ?? "", PathAccess.Read);
        if (!verdict.Allowed) return ToolResult.Fail(verdict.Message);
        string ext = Path.GetExtension(verdict.FullPath).ToLowerInvariant();
        string[] safe = [".docx", ".xlsx", ".pptx", ".pdf", ".txt", ".csv", ".md", ".png", ".jpg", ".jpeg", ".step", ".stp", ".stl", ".sldprt", ".sldasm"];
        if (!safe.Contains(ext)) return ToolResult.Fail($"{ext} files are not opened from a job (only documents, images and CAD files).");
        try { _open(new ProcessStartInfo(verdict.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ToolResult.Fail("Could not open it: " + ex.Message); }
        return ToolResult.Ok($"Opened {verdict.FullPath} for the user.");
    }
}
