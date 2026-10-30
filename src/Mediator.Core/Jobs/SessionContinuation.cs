using System.Text.RegularExpressions;
using Mediator.Core.Attachments;
using Mediator.Core.Jobs;

namespace Mediator.Core.Jobs
{

/// <summary>
/// CLI session identifiers (V9, for `claude --resume`). A session id reaches a command line, and through the npm
/// shim (claude.cmd) also cmd.exe, so only a plain token is accepted: letters, digits, '-' and '_', 8–128 characters,
/// not starting with '-' (it could be read as an option). Claude Code's ids are UUIDs, which always pass.
/// </summary>
public static partial class SessionIds
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{7,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeToken();

    public static bool IsSafe(string? id) => id is not null && SafeToken().IsMatch(id);

    /// <summary>The first 8 characters, for the overlay chip ("↩ Continue session (940df0a2)").</summary>
    public static string Short(string id) => id.Length <= 8 ? id : id[..8];
}

}

namespace Mediator.Core.Runtime
{
/// <summary>
/// A finished headless Claude Code run that a follow-up prompt can continue with `claude -p --resume &lt;id&gt;` (V9).
/// Claude Code keeps sessions per working folder, so the follow-up runs in the same folder; it gets a NEW job, grant
/// and workspace (the old job's token was revoked when it ended and is never revived), with the same tools, roots
/// and attachments, so the resumed conversation can keep using what it already referred to.
/// Antigravity runs are not resumed with a CLI flag: they run in the per-job workspace, and resuming one is not
/// wired yet (agy --conversation exists; untested here).
/// </summary>
public sealed record ContinuableSession(string SessionId, AiProvider Provider, string WorkingDirectory, string JobId, JobSpec Spec, DateTimeOffset EndedAt)
{
    public string ShortId => SessionIds.Short(SessionId);
    /// <summary>V10: the last answer, carried into a cross-CLI follow-up as context.</summary>
    public string? LastResponseText { get; init; }
    /// <summary>V10: true when the CLI itself can resume it (`claude --resume`): Claude Code with a safe session id.</summary>
    public bool NativeResume { get; init; }

    /// <summary>
    /// The job as a continuable session, or null. V10: headless Claude Code AND Antigravity CLI runs that produced a result.
    /// Claude runs with a safe session id resume natively; the others continue by carrying the last turn as context.
    /// </summary>
    public static ContinuableSession? From(RunningJob job)
    {
        if (job.Provider is not { } provider || job.Spec.Launch != LaunchMode.Headless) return null;
        if (job.State is not (JobState.Succeeded or JobState.Failed) || job.Outcome is not (RunOutcome.Succeeded or RunOutcome.ResultError)) return null;
        bool native = provider == AiProvider.ClaudeCode && SessionIds.IsSafe(job.SessionId);
        return new ContinuableSession(native ? job.SessionId! : "turn-" + job.JobId, provider, job.Spec.WorkingDirectory, job.JobId, job.Spec, job.EndedAt ?? DateTimeOffset.UtcNow)
        {
            LastResponseText = job.LastResult?.ResultText,
            NativeResume = native,
        };
    }
}

public static partial class JobSpecFactory
{
    /// <summary>
    /// The follow-up job for <paramref name="session"/>: the same kind, tools, roots, folder, category and limits, and the
    /// previous attachments plus <paramref name="newAttachments"/>. A natively resumable Claude session continued on Claude
    /// gets `--resume` (V9). Otherwise (V10: Antigravity, or another target CLI) the job is routed normally and the prompt starts
    /// with the conversation so far (every earlier turn, the first request whole).
    /// </summary>
    /// <summary>One earlier turn of a conversation, for <see cref="ConversationContext"/>.</summary>
    public sealed record ConversationTurn(string User, string? Answer, bool Failed = false);

    /// <summary>Starts every conversation block, so a prompt is never given the history twice.</summary>
    public const string ConversationHeader = "Conversation so far (the user's requests still apply; the follow-up continues them):";

    /// <summary>Longest first request, later requests and answers kept, in characters.</summary>
    public const int FirstRequestChars = 12_000, RequestChars = 3_000, AnswerChars = 1_500, LastAnswerChars = 4_000;

    /// <summary>
    /// The conversation's earlier turns as a prompt block. The FIRST request is kept whole (it usually holds the
    /// specification: sizes, parts, folders), later requests nearly whole, answers trimmed (the last one less). A failed turn
    /// is marked so the model redoes it instead of assuming it happened. Empty when there are no turns.
    /// </summary>
    public static string ConversationContext(IReadOnlyList<ConversationTurn> turns)
    {
        if (turns.Count == 0) return "";
        static string Cap(string? t, int n) => string.IsNullOrWhiteSpace(t) ? "(none)" : t.Length <= n ? t.Trim() : t[..n].TrimEnd() + "...";
        var b = new System.Text.StringBuilder(ConversationHeader);
        for (int i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            b.Append($"\n\n[Turn {i + 1}] User: ").Append(Cap(t.User, i == 0 ? FirstRequestChars : RequestChars));
            b.Append($"\n[Turn {i + 1}] Assistant{(t.Failed ? " (this turn FAILED; its work may be missing or incomplete)" : "")}: ")
             .Append(Cap(t.Answer, i == turns.Count - 1 ? LastAnswerChars : AnswerChars));
        }
        return b.ToString();
    }

    /// <summary><paramref name="spec"/> with the conversation in front of its prompt (unless it is already there or there is none).</summary>
    public static JobSpec WithConversation(JobSpec spec, IReadOnlyList<ConversationTurn> turns) =>
        turns.Count == 0 || spec.ContinueSessionId is not null || spec.Prompt.StartsWith(ConversationHeader, StringComparison.Ordinal)
            ? spec
            : spec with { Prompt = ConversationContext(turns) + "\n\nFollow-up (now): " + WithUserText(spec) };

    /// <summary>
    /// The spec's prompt, led by what the user typed when the prompt does not already contain it. Action templates (e.g.
    /// Assemble) describe HOW to work; the user's request says WHAT to make, and must reach the model whole. Applied to
    /// every job when it starts.
    /// </summary>
    public static string WithUserText(JobSpec spec) =>
        string.IsNullOrWhiteSpace(spec.UserText) || spec.Prompt.Contains(spec.UserText.Trim(), StringComparison.Ordinal)
            ? spec.Prompt
            : "The user's request (follow it exactly: every part, size, name, count, folder and mate it gives):\n" + spec.UserText.Trim() + "\n\nHow to do it:\n" + spec.Prompt;

    /// <param name="history">The whole conversation (all earlier turns); without it only the session's last turn is carried.</param>
    public static JobSpec Continue(ContinuableSession session, string prompt, LaunchMode launch = LaunchMode.Headless, IReadOnlyList<Attachment>? newAttachments = null, AiProvider? targetProvider = null, IReadOnlyList<ConversationTurn>? history = null)
    {
        var previous = session.Spec;
        string title = previous.Title.StartsWith(FollowUpPrefix, StringComparison.Ordinal) ? previous.Title : FollowUpPrefix + previous.Title;
        var attachments = previous.Attachments.Concat(newAttachments ?? []).GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        // A follow-up may need tools the first turn did not ("now model an F40" after an assembly): add the ones it implies.
        var (moreTools, moreCategory) = InferToolsAndCategory(prompt, null);
        var tools = WithCurrentToolset(previous.Tools.Concat(moreTools));
        var category = previous.Category == TaskCategory.Chat && moreTools.Count > 0 ? moreCategory : previous.Category;
        // "Using the chrome extension, now do it" after a plain answer: the follow-up becomes a Browser job (browser tools
        // are added when it starts; a Browser job gets no folder and no other Mediator tool).
        bool toBrowser = moreCategory == TaskCategory.Browser && previous.Category != TaskCategory.Browser;
        if (toBrowser) { category = TaskCategory.Browser; tools = []; }
        string userText = prompt;
        if (category == TaskCategory.Browser && (toBrowser || !session.NativeResume))
            prompt = Browser.ClaudeInChrome.Guidance + "\n\nUser: " + prompt;
        var spec = previous with { Title = title, Launch = launch, JobId = null, Attachments = attachments, UserText = userText, Tools = tools, Category = category, Roots = category == TaskCategory.Browser ? [] : previous.Roots };
        if (session.NativeResume && targetProvider is null or AiProvider.ClaudeCode)
            return spec with { Prompt = prompt, Provider = AiProvider.ClaudeCode, NeverDowngrade = true, ContinueSessionId = session.SessionId };
        var turns = history is { Count: > 0 } ? history : [new ConversationTurn(previous.UserText ?? previous.Prompt, session.LastResponseText)];
        return WithConversation(spec with { Prompt = prompt, Provider = targetProvider, ContinueSessionId = null }, turns);
    }

    /// <summary>A Windows path (C:\..., \\server\share) or a file name with a common extension typed in the prompt.</summary>
    [GeneratedRegex(@"(?:\b[a-z]:\\|\\\\[\w.$-]+\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalPath();

    /// <summary>Words that ask the model to work on the user's own files (not merely mention a file type).</summary>
    private static readonly string[] FileIntent =
        ["my files", "my folder", "this folder", "that folder", "the folder", "my documents", "my downloads", "my desktop",
         "these files", "those files", "in this file", "open the file", "read the file", "read my", "look at my", "list the files", "find files"];

    /// <summary>Questions that are best answered with a web search (current facts, troubleshooting, how-to).</summary>
    private static readonly string[] SearchIntent =
        ["search", "look up", "google", "latest", "current", "today", "news", "price", "release", "version", "update",
         "diagnose", "troubleshoot", "error", "not working", "doesn't work", "does not work", "won't", "fails", "failing",
         "broken", "blank", "how do i", "how to", "how can", "why does", "why do", "why is", "fix"];

    /// <summary>True when <paramref name="key"/> starts a word in <paramref name="text"/> ("mate" matches "mates", not "estimate").</summary>
    private static bool HasWord(string text, string key)
    {
        for (int i = text.IndexOf(key, StringComparison.Ordinal); i >= 0; i = text.IndexOf(key, i + 1, StringComparison.Ordinal))
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || !char.IsLetterOrDigit(key[0])) return true;
        return false;
    }

    /// <summary>
    /// V10: the routing step for free text. Checked in this order, first match wins the category:
    /// 1. an app or task keyword, or the app in front (SolidWorks, Resolve, Blender, CAD, files, tutor) → that category and its tools;
    /// 2. an explicit request about the user's files (a typed path, "my folder", "these files") → Files and the file tools;
    /// 3. a troubleshooting, how-to or current-facts question → Search: web search only, no local files;
    /// 4. anything else → Chat: answer from knowledge, no tools, no local files.
    /// The tool arrays of every step-1 match are united. Keywords match at the start of a word.
    /// </summary>
    public static (IReadOnlyList<string> Tools, TaskCategory Category) InferToolsAndCategory(string prompt, string? activeApp)
    {
        // V11: asking for the browser by name wins over everything ("open Khan Academy in chrome and answer the matrix MCQ").
        if (IsBrowserRequest(prompt)) return ([], TaskCategory.Browser);
        var (tools, category) = InferAppTools(prompt, activeApp);
        if (category != TaskCategory.Chat) return (tools, category);
        string t = (prompt ?? "").ToLowerInvariant();
        if (LocalPath().IsMatch(t) || FileIntent.Any(k => HasWord(t, k))) return (FileTools, TaskCategory.Files);
        if (SearchIntent.Any(k => HasWord(t, k))) return ([], TaskCategory.Search);
        return ([], TaskCategory.Chat);
    }

    /// <summary>True for the categories that never get the user's folder or any Mediator tool (a Browser job works only in Chrome).</summary>
    public static bool IsNoLocalAccess(TaskCategory category) => category is TaskCategory.Chat or TaskCategory.Search or TaskCategory.Browser;

    /// <summary>Words that ask for the browser itself: "claude in chrome", "in chrome", "open ... in the browser", "this tab".</summary>
    private static readonly string[] BrowserIntent = ["chrome", "browser", "this tab", "new tab", "web page", "webpage", "website", "web site"];

    /// <summary>"go to / visit / navigate to" ... (V14.1)</summary>
    private static readonly string[] NavigateIntent = ["go to", "goto", "visit", "navigate to", "browse to", "head to", "head over to", "open up"];

    /// <summary>... together with something only a web page has: "go to w3 schools and take an mcq".</summary>
    private static readonly string[] WebTaskIntent = ["quiz", "mcq", "online", "youtube", "w3", "khan academy", "wikipedia"];

    /// <summary>A web address: http(s)://, www., or a name with a common site ending ("w3schools.com"; not "asp.net" or "file.js").</summary>
    [GeneratedRegex(@"https?://|\bwww\.|\b[a-z0-9-]{2,}\.(?:com|org|edu|gov|io|co|uk|fr|de|ca|au)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WebAddress();

    /// <summary>True when the prompt asks for work in the user's browser (V11; V14.1: also a web address or "go to ... quiz").</summary>
    public static bool IsBrowserRequest(string? prompt)
    {
        string t = (prompt ?? "").ToLowerInvariant();
        return BrowserIntent.Any(k => HasWord(t, k)) || WebAddress().IsMatch(t)
            || (NavigateIntent.Any(k => HasWord(t, k)) && WebTaskIntent.Any(k => HasWord(t, k)));
    }

    /// <summary>A Browser job runs Claude Code with --chrome (Claude in Chrome); no other CLI can drive the browser.</summary>
    public static bool UsesChrome(JobSpec spec) => spec.Category == TaskCategory.Browser;

    /// <summary>V13: a Browser job on Mediator's own extension (it holds the browser_* tools); any CLI can run it.</summary>
    public static bool UsesExtension(JobSpec spec) => spec.Tools.Contains(Browser.BrowserToolProvider.ReadTool, StringComparer.Ordinal);

    /// <summary>A Browser job without the extension: Claude Code with --chrome (Claude in Chrome), Claude only.</summary>
    public static bool UsesClaudeInChrome(JobSpec spec) => UsesChrome(spec) && !UsesExtension(spec);

    private static readonly string[] CreateIntent = ["create", "make", "model", "design", "build", "3d print", "stl", "print"];

    /// <summary>"mate" as a CAD word only: mate, mates, mated, mating (not "material", "matey").</summary>
    [GeneratedRegex(@"\bmat(?:e|es|ed|ing)\b")]
    private static partial Regex MateWord();

    /// <summary>
    /// What makes "assembly" mean a SolidWorks assembly and not a .NET assembly or assembly language: a part, SolidWorks,
    /// a mate or CAD in the same request ("assemble the parts in this folder").
    /// </summary>
    [GeneratedRegex(@"\b(?:parts?|solidworks|sldprt|sldasm|cad|bolts?|brackets?|shafts?|housing)\b")]
    private static partial Regex AssemblyContext();

    /// <summary>"stl" as a print file (".stl", "stl file"), not C++'s STL.</summary>
    [GeneratedRegex(@"\.stl\b|\bstls?\s+files?\b|\bstls\b")]
    private static partial Regex StlFile();

    /// <summary>
    /// V16.1: a chat saved by an earlier version keeps the tool list it started with, so a SolidWorks chat from V15 never saw
    /// the V16 tools. A chat that had any SolidWorks tool gets today's whole SolidWorks toolset; one with Office tools, all of them.
    /// </summary>
    public static List<string> WithCurrentToolset(IEnumerable<string> tools)
    {
        var list = tools.Distinct(StringComparer.Ordinal).ToList();
        if (list.Any(t => t.StartsWith("swx_", StringComparison.Ordinal))) list = list.Concat(CadTools).Distinct(StringComparer.Ordinal).ToList();
        foreach (var (prefix, appTools) in CadToolsByPrefix)
            if (list.Any(t => t.StartsWith(prefix + "_", StringComparison.Ordinal))) list = list.Concat(appTools).Distinct(StringComparer.Ordinal).ToList();
        if (list.Any(OfficeTools.Contains)) list = list.Concat(OfficeTools).Distinct(StringComparer.Ordinal).ToList();
        return list;
    }

    /// <summary>
    /// V16.1: whether SolidWorks is running (checked at most every 5 s). While it is, a request about parts, features,
    /// dimensions, materials... is about the model on screen, even without the word "SolidWorks". Tests replace it.
    /// </summary>
    public static Func<bool> SolidWorksOpen { get; set; } = ProbeSolidWorks;

    private static long _swCheckedAt = long.MinValue / 2;
    private static volatile bool _swOpen;

    private static bool ProbeSolidWorks()
    {
        if (!OperatingSystem.IsWindows()) return false;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _swCheckedAt) > 5000)
        {
            _swOpen = Swx.SolidWorksLauncher.IsRunning();
            Interlocked.Exchange(ref _swCheckedAt, now);
        }
        return _swOpen;
    }

    /// <summary>Words that, while SolidWorks is open, mean the model on screen (start of a word: "part" also matches "parts").</summary>
    private static readonly string[] OpenModelWords =
        ["part", "assembl", "drawing", "feature", "dimension", "sketch", "extrude", "fillet", "chamfer", "hole", "material", "mass", "weigh",
         "bom", "bill of material", "interference", "configuration", "mate", "face", "edge", "suppress", "custom propert", "propert",
         "export", "save as", "save it", "save this", "save the", "screenshot", "picture", "the model", "this model", "open model", "selected", "selection", "thickness",
         "diameter", "radius", "rebuild", "volume", "center of mass", "centre of mass", "equation", "global variable", "iso view", "isometric"];

    /// <summary>True when the request itself asks for SolidWorks work (used to start SolidWorks early only for those).</summary>
    public static bool AsksForSolidWorks(string? prompt) => InferAppTools(prompt ?? "", null).Tools.Contains(CadTools[0], StringComparer.Ordinal);

    /// <summary>V15: a CAD file named by type or extension (".stl", "step file", "the 3mf"), not words like "step" or C++'s STL.</summary>
    [GeneratedRegex(@"\.(?:stl|obj|ply|3mf|step|stp|iges|igs|dxf)\b|\b(?:stl|step|stp|iges|igs|3mf|obj|ply|dxf)\s+(?:files?|models?|meshes?)\b|\bstls\b|\b3mf\b")]
    private static partial Regex CadFileWord();

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

    /// <summary>"cad" as a whole word only ("cadence", "decade" are not CAD).</summary>
    [GeneratedRegex(@"\bcad\b")]
    private static partial Regex CadWord();

    private static (IReadOnlyList<string> Tools, TaskCategory Category) InferAppTools(string prompt, string? activeApp)
    {
        string t = (prompt ?? "").ToLowerInvariant(), app = (activeApp ?? "").ToLowerInvariant();
        var groups = new (string[] Keys, string? App, string[] Tools, TaskCategory Category)[]
        {
            (["solidworks", "sldprt", "sldasm", "slddrw"], "sldworks", CadTools, TaskCategory.Cad),
            // V17: the other CAD programs, by name, file type, or the program in front (process name).
            (["freecad", "fcstd"], "freecad", CadToolsByPrefix["fc"], TaskCategory.Cad),
            (["autocad", "dwg", "acad"], "acad", CadToolsByPrefix["acad"], TaskCategory.Cad),
            (["inventor", "ipt", "iam", "idw"], "inventor", CadToolsByPrefix["inv"], TaskCategory.Cad),
            (["siemens nx", "nx open", "nx part", "ugraf"], "ugraf", CadToolsByPrefix["nx"], TaskCategory.Cad),
            (["creo", "pro/engineer", "proe"], "xtop", CadToolsByPrefix["creo"], TaskCategory.Cad),
            (["catia", "catpart", "catproduct", "catdrawing"], "cnext", CadToolsByPrefix["catia"], TaskCategory.Cad),
            (["parametric", "build123d", "3d print", "generate part", "make part"], null, CadTools, TaskCategory.Cad),
            ([], null, CadFileTools, TaskCategory.Cad),       // V15: CAD files (matched by CadFileWord below)
            (OfficeIntent, null, OfficeTools, TaskCategory.Office), // V15: Word, Excel, PowerPoint, Outlook (and OfficeFileWord)
            (["organi", "sort my", "tidy", "declutter", "move files", "rename files"], null, FileTools, TaskCategory.Files),
            (["davinci", "rough cut", "footage", "timeline"], "resolve", ResolveTools, TaskCategory.Resolve),
            (["blender", "render", "3d scene"], "blender", BlenderTools, TaskCategory.Blender),
            (["tutor", "chapter", "eigen", "determinant", "matrix"], null, TutorTools, TaskCategory.Tutor),
        };
        // Every SolidWorks job gets the whole CAD toolset (parts AND assemblies), so "make the parts and mate them" works in
        // one job. "cad" as a word counts only with a make/create word: "explain what a fillet is in CAD" is a question.
        // V14.1: "assembly", "mate" and "stl" only in their CAD sense, so a coding request ("load the assembly", "the
        // material", "C++ STL") is not a SolidWorks job (which started SolidWorks).
        bool create = CreateIntent.Any(k => HasWord(t, k));
        bool cad = (create && CadWord().IsMatch(t)) || MateWord().IsMatch(t)
            || (HasWord(t, "assembl") && AssemblyContext().IsMatch(t))
            || (OpenModelWords.Any(k => HasWord(t, k)) && SolidWorksOpen()); // V16.1: about the model open in SolidWorks
        bool cadFile = CadFileWord().IsMatch(t) || StlFile().IsMatch(t);
        bool officeFile = OfficeFileWord().IsMatch(t) || (OfficeNames.Any(k => HasWord(t, k)) && OfficeAction.Any(k => HasWord(t, k)));
        bool Special(int i) => (i == 0 && cad) || (i == 8 && cadFile) || (i == 9 && officeFile);
        var hits = groups.Where((g, i) => Special(i) || g.Keys.Any(k => HasWord(t, k)) || (g.App is not null && app.Contains(g.App, StringComparison.Ordinal))).ToList();
        if (hits.Count == 0) return ([], TaskCategory.Chat);
        return (hits.SelectMany(h => h.Tools).Distinct(StringComparer.Ordinal).ToList(), hits[0].Category);
    }

    /// <summary>
    /// V10: a headless "just answer" job. The model replies directly and uses the granted Mediator tools only when the
    /// request needs them. <paramref name="folder"/> null: no file roots (general chat), run in the chat folder.
    /// </summary>
    public static JobSpec Assistant(string? folder, string prompt, LaunchMode launch, IReadOnlyList<string> tools, TaskCategory category, string? chatDirectory = null)
    {
        // Chat and Search never get a folder, even one selected in Explorer or set with Save to…: a general question
        // must not give the model the user's files.
        // V15: an Office job with no folder named saves new documents in Documents\Mediator.
        if (folder is null && category == TaskCategory.Office) folder = DefaultOfficeFolder();
        string? full = folder is null || IsNoLocalAccess(category) ? null : Path.GetFullPath(folder);
        string work = full ?? chatDirectory ?? Path.Combine(Path.GetTempPath(), "Mediator-chat");
        Directory.CreateDirectory(work);
        string oneLine = prompt.ReplaceLineEndings(" ").Trim();
        return new JobSpec
        {
            Kind = "assistant",
            Category = category,
            Title = "Ask: " + (oneLine.Length > 60 ? oneLine[..60] + "…" : oneLine),
            Tools = tools,
            Roots = full is null ? [] : [full],
            WorkingDirectory = work,
            Launch = launch,
            // CAD runs chain many tool calls (parts, fixes, cards, assembly); a plain answer needs few.
            MaxTurns = category is TaskCategory.Cad or TaskCategory.Browser ? CadMaxTurns : 20,
            UserText = prompt,
            Prompt = category == TaskCategory.Browser ? Browser.ClaudeInChrome.Guidance + "\n\nUser: " + prompt
                : "Answer the user's request directly and clearly." + (tools.Count > 0
                ? " If it needs their files or desktop apps, use the Mediator tools you were granted (only those); otherwise just answer."
                  + (category == TaskCategory.Cad ? " For \"this part\", \"the open model\" or what is on screen in SolidWorks, start with swx_session and swx_inspect_model (sections selection for what they selected); look at it with swx_capture_views before judging a shape." : "")
                : category == TaskCategory.Search
                    ? " Use web search to find current, specific information, then answer concisely and name your sources. Do not read, list or search local files: none are shared with you."
                    : " No tools are needed or granted; answer from your knowledge. Do not read, list or search local files or run commands.")
                + (full is null ? "" : $"\nThe user's folder: {full}") + "\n\nUser: " + prompt,
        };
    }

    public const string FollowUpPrefix = "Follow-up: ";

    /// <summary>V15: Documents\Mediator (created), where Office jobs without a folder save what they make.</summary>
    public static string DefaultOfficeFolder()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (docs.Length == 0) docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        string dir = Path.Combine(docs, "Mediator");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
}
