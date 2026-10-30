using System.Text.RegularExpressions;
using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.Connectors.Office;

/// <summary>V18 connector: Word, Excel and PowerPoint files (read and written by Mediator itself) and classic Outlook.</summary>
public sealed partial class OfficeConnector : IMediatorConnector
{
    public string Id => "office";
    public string DisplayName => "Microsoft Office";
    public string Category => "Productivity";
    public string Summary => "Read and create Word, Excel and PowerPoint files; search mail, write drafts, read the calendar in classic Outlook.";

    /// <summary>Classic desktop Outlook. Null: the real one (it reports itself unavailable off Windows). Tests pass a fake.</summary>
    public IOutlookClient? Outlook { get; init; }

    public static readonly string[] Tools = OfficeToolProvider.Names;
    public IReadOnlyList<string> ChatTools => Tools;

    public ConnectorInstance Start(ConnectorContext context) => new([new OfficeToolProvider(Outlook)]);

    /// <summary>V15: Word, Excel and PowerPoint files by extension.</summary>
    [GeneratedRegex(@"\.(?:docx|xlsx|pptx|docm|xlsm|pptm|doc|xls|ppt|csv)\b|\b(?:docx|xlsx|pptx)\b")]
    private static partial Regex OfficeFileWord();

    /// <summary>V15: asking for Office apps, documents, mail or the calendar (phrases, so "email validation" in code is not mail).</summary>
    private static readonly string[] OfficeIntent =
        ["word document", "word doc", "word file", "inbox", "my email", "my emails", "my mail", "unread email", "email to", "email from", "draft an email",
         "write an email", "reply to the email", "my calendar", "on my calendar", "meeting invite", "my meetings"];

    /// <summary>V15: app and document names that mean Office work only with a doing word ("make a spreadsheet", "turn this into a
    /// PowerPoint"); a question about PowerPoint or Excel ("the links in my powerpoint go blank") is a search, not a file job.</summary>
    private static readonly string[] OfficeNames = ["excel", "spreadsheet", "workbook", "powerpoint", "slide deck", "slides", "presentation", "outlook", "in word", "an email"];
    private static readonly string[] OfficeAction = ["create", "make", "build", "write", "draft", "turn", "convert", "summari", "generate", "export", "fill in", "put", "list"];

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
    [
        new(50, TaskCategory.Office, Tools, r => r.AnyWord(OfficeIntent) || OfficeFileWord().IsMatch(r.Text)
            || (r.AnyWord(OfficeNames) && r.AnyWord(OfficeAction))),
    ];

    public IReadOnlyList<ConnectorProgram> Programs { get; } =
    [
        new("word", "Microsoft Word", "\uE8A5", "Open the documents Mediator writes", "Productivity", "read and create Word, Excel and PowerPoint files", PromptPrefix: "/office "),
        new("excel", "Microsoft Excel", "\uE80A", "Open the workbooks Mediator writes", "Productivity", "read and create Word, Excel and PowerPoint files", PromptPrefix: "/office "),
        new("powerpoint", "Microsoft PowerPoint", "\uE8A1", "Open the decks Mediator writes", "Productivity", "read and create Word, Excel and PowerPoint files", PromptPrefix: "/office "),
        new("outlook", "Microsoft Outlook", "\uE715", "Search and read mail, write drafts, calendar", "Productivity", "search and read mail and calendar, save drafts (never sends)", PromptPrefix: "/outlook "),
    ];

    public AppStatus ProbeProgram(string programId, ProgramProbe probe) => Probe(programId, probe);

    public bool ReadsAttachment(string path) => OfficeDocuments.IsOfficeFile(path);

    public string DescribeAttachment(string fullPath, int maxChars) => OfficeDocuments.ReadText(fullPath, maxChars);

    /// <summary>V15: Word, Excel and PowerPoint are optional (the Office tools write the files themselves; the app only
    /// opens them). Outlook is usable only as classic desktop Outlook with a mail profile.</summary>
    private static AppStatus Probe(string id, ProgramProbe probe)
    {
        string name = OfficeApps.DisplayName(id);
        if (!probe.WindowsApps) return new AppStatus(id, name, false, "Windows only");
        string? exe = probe.Forced(id, out var forced) ? forced : OfficeApps.Locate(id);
        if (id != "outlook")
            return exe is null
                ? new AppStatus(id, name, false, "not installed. The Mediator still reads and writes these files; install Microsoft 365 to open them.")
                : new AppStatus(id, name, true, $"installed — {exe}. The Mediator reads and writes the files itself and opens them here.", exe);
        if (exe is null)
            return OfficeApps.LocateNewOutlook() is { } olk
                ? new AppStatus(id, name, false, "only the new Outlook is installed, and it cannot be automated. Install classic Outlook (Microsoft 365 apps) for mail and calendar.", olk)
                : new AppStatus(id, name, false, "classic Outlook is not installed.");
        if (probe.IsTest) return new AppStatus(id, name, true, exe, exe);
        string? why = new OutlookClient().Unavailable();
        return why is null
            ? new AppStatus(id, name, true, $"classic Outlook with a mail profile — {exe}. Search, read, draft (never sends) and calendar.", exe)
            : new AppStatus(id, name, false, why, exe);
    }
}
