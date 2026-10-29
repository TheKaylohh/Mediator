using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.CadFiles;
using Mediator.Core.Jobs;
using Mediator.Core.Office;
using Mediator.Core.Runtime;
using Mediator.Core.Security;

namespace Mediator.Tests;

/// <summary>V15: Word/Excel/PowerPoint files, classic Outlook (faked), CAD files (meshes, STEP, IGES) and their routing.</summary>
static class OfficeCadTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", "officecad-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static JobGrant Grant(string root) => new()
    {
        JobId = "j-" + Guid.NewGuid().ToString("N")[..6], Token = "t", AllowedTools = new HashSet<string>(), FileRoots = [root],
        Paths = new PathAuthorizer([root]), ExpiresAt = DateTimeOffset.MaxValue,
    };

    static Task<ToolResult> Call(IToolProvider p, JobGrant job, string tool, string json) =>
        p.ExecuteAsync(new ToolCallContext(job, p.Tools.Single(t => t.Name == tool), JsonDocument.Parse(json).RootElement, "c"), CancellationToken.None);

    static string J(string s) => JsonSerializer.Serialize(s);

    sealed class FakeOutlook : IOutlookClient
    {
        public readonly List<string> Drafts = new();
        public string? Unavailable() => null;
        public JsonArray SearchMail(string folder, string? query, string? from, int days, bool unreadOnly, int max) =>
            [new JsonObject { ["id"] = "m1", ["from"] = "Ana", ["subject"] = "Budget " + query, ["date"] = "2026-10-28 09:00", ["unread"] = true }];
        public JsonObject ReadMail(string id, int maxChars) => new()
        {
            ["from"] = "Ana", ["fromAddress"] = "ana@example.com", ["to"] = "me", ["cc"] = "", ["date"] = "2026-10-28", ["subject"] = "Budget",
            ["attachments"] = new JsonArray("q3.xlsx"), ["body"] = "Numbers attached.",
        };
        public string CreateDraft(string to, string? cc, string subject, string body, bool html, bool show) { Drafts.Add($"{to}|{subject}"); return "d1"; }
        public JsonArray Calendar(DateTime from, DateTime to, int max) => [];
    }

    public static async Task OfficeDocumentsRoundTrip()
    {
        string root = TempDir("office");
        var job = Grant(root);
        var opened = new List<string>();
        var p = new OfficeToolProvider(new FakeOutlook(), psi => opened.Add(psi.FileName));

        string docx = Path.Combine(root, "report.docx");
        var made = await Call(p, job, OfficeToolProvider.CreateTool, $$$"""
            {"path":{{{J(docx)}}},"content":{"title":"Trip report","blocks":[{"type":"heading","text":"Summary","level":1},{"type":"paragraph","text":"It went **well**."},
            {"type":"bullet","text":"Flights booked"},{"type":"table","rows":[["Item","Cost"],["Hotel","120"]]}]}}
            """);
        Check.True(!made.IsError && File.Exists(docx), "docx created: " + made.Text);
        string text = (await Call(p, job, OfficeToolProvider.ReadTool, $$$"""{"path":{{{J(docx)}}}}""")).Text;
        Check.True(text.Contains("Summary") && text.Contains("It went well.") && text.Contains("Flights booked") && text.Contains("| Hotel | 120 |"), "docx read back: " + text);

        string xlsx = Path.Combine(root, "budget.xlsx");
        Check.True(!(await Call(p, job, OfficeToolProvider.CreateTool, $$$"""
            {"path":{{{J(xlsx)}}},"content":{"sheets":[{"name":"Costs","rows":[["Item","Cost"],["Hotel",120],["Food",80],["Total","=SUM(B2:B3)"]]}]}}
            """)).IsError, "xlsx created");
        string sheet = (await Call(p, job, OfficeToolProvider.ReadTool, $$$"""{"path":{{{J(xlsx)}}},"sheet":"Costs"}""")).Text;
        Check.True(sheet.Contains("Costs") && sheet.Contains("Hotel") && sheet.Contains("120") && sheet.Contains("SUM(B2:B3)"), "xlsx read back: " + sheet);

        string pptx = Path.Combine(root, "deck.pptx");
        Check.True(!(await Call(p, job, OfficeToolProvider.CreateTool, $$$"""
            {"path":{{{J(pptx)}}},"content":{"slides":[{"title":"Plan","bullets":["Book","Pack"],"notes":"Say hello"}]}}
            """)).IsError, "pptx created");
        string deck = (await Call(p, job, OfficeToolProvider.ReadTool, $$$"""{"path":{{{J(pptx)}}}}""")).Text;
        Check.True(deck.Contains("Slide 1: Plan") && deck.Contains("- Pack") && deck.Contains("Notes: Say hello"), "pptx read back: " + deck);

        // Never overwrites; never writes outside the job; old binary formats are refused with the fix.
        long before = new FileInfo(docx).Length;
        var again = await Call(p, job, OfficeToolProvider.CreateTool, $$$"""{"path":{{{J(docx)}}},"content":{"blocks":[{"type":"paragraph","text":"x"}]}}""");
        Check.True(again.IsError && new FileInfo(docx).Length == before, "existing document not replaced: " + again.Text);
        string outside = Path.Combine(TempDir("outside"), "x.docx");
        Check.True((await Call(p, job, OfficeToolProvider.CreateTool, $$$"""{"path":{{{J(outside)}}},"content":{"blocks":[]}}""")).IsError && !File.Exists(outside), "outside the job refused");
        File.WriteAllBytes(Path.Combine(root, "old.doc"), [0xD0, 0xCF, 0x11, 0xE0]);
        var legacy = await Call(p, job, OfficeToolProvider.ReadTool, $$$"""{"path":{{{J(Path.Combine(root, "old.doc"))}}}}""");
        Check.True(legacy.IsError && legacy.Text.Contains("docx", StringComparison.Ordinal), "legacy .doc explained: " + legacy.Text);
        Check.True(OfficeDocuments.ColumnName(OfficeDocuments.ColumnIndex("AB")) == "AB", "column names round trip");

        Check.True(!(await Call(p, job, OfficeToolProvider.OpenTool, $$$"""{"path":{{{J(xlsx)}}}}""")).IsError && opened.Single() == xlsx, "open hands the file to its app");
        File.WriteAllText(Path.Combine(root, "run.bat"), "echo");
        Check.True((await Call(p, job, OfficeToolProvider.OpenTool, $$$"""{"path":{{{J(Path.Combine(root, "run.bat"))}}}}""")).IsError && opened.Count == 1, "scripts are not opened");
    }

    public static async Task OutlookDraftsNeverSend()
    {
        var outlook = new FakeOutlook();
        var p = new OfficeToolProvider(outlook, _ => { });
        var job = Grant(TempDir("mail"));
        Check.True(p.Tools.Single(t => t.Name == OfficeToolProvider.DraftTool).Risk == ToolRisk.Write, "drafting asks first");
        Check.True(p.Tools.Single(t => t.Name == OfficeToolProvider.SearchMailTool).Risk != ToolRisk.Destructive, "search is not destructive");
        Check.True(!p.Tools.Any(t => t.Name.Contains("send", StringComparison.OrdinalIgnoreCase)), "there is no send tool");
        var found = await Call(p, job, OfficeToolProvider.SearchMailTool, """{"query":"q3"}""");
        Check.True(!found.IsError && found.Text.Contains("Budget q3"), "search: " + found.Text);
        var read = await Call(p, job, OfficeToolProvider.ReadMailTool, """{"id":"m1"}""");
        Check.True(read.Text.Contains("ana@example.com") && read.Text.Contains("q3.xlsx") && read.Text.Contains("Numbers attached."), "read: " + read.Text);
        var draft = await Call(p, job, OfficeToolProvider.DraftTool, """{"to":"ana@example.com","subject":"Re: Budget","body":"Thanks"}""");
        Check.True(outlook.Drafts.Single() == "ana@example.com|Re: Budget" && draft.Text.Contains("NOT sent"), "draft saved, not sent: " + draft.Text);
    }

    static string Cube(double s)
    {
        var v = new[] { (0, 0, 0), (s, 0, 0), (s, s, 0), (0, s, 0), (0, 0, s), (s, 0, s), (s, s, s), (0, s, s) };
        int[][] f = [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [1, 2, 6], [1, 6, 5], [2, 3, 7], [2, 7, 6], [3, 0, 4], [3, 4, 7]];
        var sb = new StringBuilder();
        foreach (var p in v) sb.Append(FormattableString.Invariant($"v {p.Item1} {p.Item2} {p.Item3}\n"));
        foreach (var t in f) sb.Append($"f {t[0] + 1} {t[1] + 1} {t[2] + 1}\n");
        return sb.ToString();
    }

    public static async Task CadFiles()
    {
        string root = TempDir("cad");
        var job = Grant(root);
        var p = new CadFileToolProvider();
        string obj = Path.Combine(root, "cube.obj");
        File.WriteAllText(obj, Cube(1)); // a 1 inch cube, modelled in inches

        var r = Mesh.Load(obj).Analyze();
        Check.True(r.Watertight && r.Triangles == 12 && Math.Abs(r.Volume - 1) < 1e-6 && Math.Abs(r.Area - 6) < 1e-6 && !r.InsideOut && r.Shells == 1, "cube analysed");
        string described = (await Call(p, job, CadFileToolProvider.InspectTool, $$$"""{"path":{{{J(obj)}}}}""")).Text;
        Check.True(described.Contains("12 triangles") && described.Contains("Closed (watertight)"), "inspect: " + described);

        string stl = Path.Combine(root, "cube-mm.stl");
        var conv = await Call(p, job, CadFileToolProvider.ConvertTool, $$$"""{"path":{{{J(obj)}}},"outputPath":{{{J(stl)}}},"fromUnits":"in","toUnits":"mm","center":true}""");
        Check.True(!conv.IsError && File.Exists(stl), "converted: " + conv.Text);
        var mm = Mesh.Load(stl).Analyze();
        Check.True(Math.Abs(mm.Size.X - 25.4) < 1e-3 && Math.Abs(mm.Min.Z) < 1e-4 && Math.Abs(mm.Min.X + 12.7) < 1e-3 && mm.Watertight, "inch → mm, base centred at the origin");
        foreach (var ext in new[] { ".ply", ".3mf", ".obj" })
        {
            string o = Path.Combine(root, "again" + ext);
            Check.True(!(await Call(p, job, CadFileToolProvider.ConvertTool, $$$"""{"path":{{{J(stl)}}},"outputPath":{{{J(o)}}}}""")).IsError, ext + " written");
            Check.True(Mesh.Load(o).Analyze() is { Triangles: 12, Watertight: true } back && Math.Abs(back.Volume - mm.Volume) / mm.Volume < 1e-4, ext + " round trip keeps the solid");
        }
        Check.True((await Call(p, job, CadFileToolProvider.ConvertTool, $$$"""{"path":{{{J(obj)}}},"outputPath":{{{J(stl)}}}}""")).IsError, "an existing file is never replaced");

        string step = Path.Combine(root, "bracket.step");
        File.WriteAllText(step, """
            ISO-10303-21;
            HEADER;
            FILE_DESCRIPTION((''),'2;1');
            FILE_NAME('bracket.step','2026-10-01T10:00:00',(''),(''),'SwSTEP 2.0','SOLIDWORKS 2025','');
            FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));
            ENDSEC;
            DATA;
            #1=PRODUCT('Bracket','Bracket','',(#2));
            #3=MANIFOLD_SOLID_BREP('',#4);
            #5=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );
            #6=CARTESIAN_POINT('',(0.,0.,0.));
            #7=CARTESIAN_POINT('',(40.,20.,5.));
            ENDSEC;
            END-ISO-10303-21;
            """);
        string s = CadFileInspector.Describe(step);
        Check.True(s.Contains("AP214") && s.Contains("SOLIDWORKS 2025") && s.Contains("millimetre") && s.Contains("Bracket"), "STEP described: " + s);
        Check.True(CadFileInspector.IsCadFile("a.IGS") && CadFileInspector.IsCadFile("x.3mf") && !CadFileInspector.IsCadFile("x.docx"), "CAD extensions");
        File.WriteAllText(Path.Combine(root, "fake.step"), "hello");
        Check.True((await Call(p, job, CadFileToolProvider.InspectTool, $$$"""{"path":{{{J(Path.Combine(root, "fake.step"))}}}}""")).IsError, "a non-STEP .step is reported, not guessed");
    }

    public static Task Routing()
    {
        foreach (var office in new[] { "make a budget spreadsheet for my trip", "summarize my unread emails in outlook", "read report.docx and list the actions", "turn these notes into a powerpoint" })
            Check.True(JobSpecFactory.InferToolsAndCategory(office, null) is var (tools, cat) && cat == TaskCategory.Office && tools.Contains(OfficeToolProvider.ReadTool), $"'{office}' → Office");
        foreach (var cad in new[] { "how big is bracket.stl", "convert the obj file to 3mf", "what units is this step file in" })
            Check.True(JobSpecFactory.InferToolsAndCategory(cad, null) is var (tools, cat) && cat == TaskCategory.Cad && tools.Contains(CadFileToolProvider.InspectTool), $"'{cad}' → CAD files");
        foreach (var notOffice in new[] { "add email validation to the signup form", "explain C++ STL vectors", "what is the next step in the algorithm" })
        {
            var (tools, _) = JobSpecFactory.InferToolsAndCategory(notOffice, null);
            Check.True(!tools.Contains(OfficeToolProvider.ReadTool) && !tools.Contains(CadFileToolProvider.InspectTool), $"'{notOffice}' gets no Office/CAD file tools");
        }
        Check.True(!JobSpecFactory.AsksForSolidWorks("how many triangles in part.stl"), "inspecting an STL does not start SolidWorks");
        Check.True(ProviderRouter.ParsePrefix("/office make a memo") is { Category: TaskCategory.Office, Remainder: "make a memo" }, "/office prefix");
        Check.True(ProviderRouter.ParsePrefix("/outlook unread today") is { Category: TaskCategory.Office }, "/outlook prefix");
        return Task.CompletedTask;
    }

    /// <summary>Attachments: an attached .xlsx comes back as its cells, an .stl as its description, not "binary file".</summary>
    public static async Task AttachmentsReadOfficeAndCad()
    {
        string root = TempDir("attach");
        var job = Grant(root);
        string xlsx = Path.Combine(root, "data.xlsx");
        OfficeDocuments.Write(xlsx, (JsonObject)JsonNode.Parse("""{"sheets":[{"name":"S","rows":[["a",1]]}]}""")!);
        string obj = Path.Combine(root, "c.obj");
        File.WriteAllText(obj, Cube(10));
        var at = new Mediator.Core.Attachments.AttachmentToolProvider();
        at.Add(job.JobId, new Mediator.Core.Attachments.Attachment("x1", Mediator.Core.Attachments.AttachmentKind.File, "data.xlsx", xlsx, root, xlsx, new FileInfo(xlsx).Length, null));
        at.Add(job.JobId, new Mediator.Core.Attachments.Attachment("x2", Mediator.Core.Attachments.AttachmentKind.File, "c.obj", obj, root, obj, new FileInfo(obj).Length, null));
        var x = await Call(at, job, Mediator.Core.Attachments.AttachmentToolProvider.ReadTool, """{"id":"x1"}""");
        Check.True(!x.IsError && x.Text.Contains("S") && x.Text.Contains('1'), "attached workbook read: " + x.Text);
        var c = await Call(at, job, Mediator.Core.Attachments.AttachmentToolProvider.ReadTool, """{"id":"x2"}""");
        Check.True(!c.IsError && c.Text.Contains("12 triangles"), "attached mesh described: " + c.Text);
    }
}
