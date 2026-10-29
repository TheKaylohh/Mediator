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
        var tools = previous.Tools.Concat(moreTools).Distinct(StringComparer.Ordinal).ToList();
        var category = previous.Category == TaskCategory.Chat && moreTools.Count > 0 ? moreCategory : previous.Category;
        var spec = previous with { Title = title, Launch = launch, JobId = null, Attachments = attachments, UserText = prompt, Tools = tools, Category = category };
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
    private static readonly string[] BrowserIntent = ["chrome", "browser", "this tab", "new tab", "web page", "webpage"];

    /// <summary>True when the prompt asks for work in the user's browser (V11).</summary>
    public static bool IsBrowserRequest(string? prompt)
    {
        string t = (prompt ?? "").ToLowerInvariant();
        return BrowserIntent.Any(k => HasWord(t, k));
    }

    /// <summary>A Browser job runs Claude Code with --chrome (Claude in Chrome); no other CLI can drive the browser.</summary>
    public static bool UsesChrome(JobSpec spec) => spec.Category == TaskCategory.Browser;

    /// <summary>V13: a Browser job on Mediator's own extension (it holds the browser_* tools); any CLI can run it.</summary>
    public static bool UsesExtension(JobSpec spec) => spec.Tools.Contains(Browser.BrowserToolProvider.ReadTool, StringComparer.Ordinal);

    /// <summary>A Browser job without the extension: Claude Code with --chrome (Claude in Chrome), Claude only.</summary>
    public static bool UsesClaudeInChrome(JobSpec spec) => UsesChrome(spec) && !UsesExtension(spec);

    private static readonly string[] CreateIntent = ["create", "make", "model", "design", "build", "3d print", "stl", "print"];

    /// <summary>"cad" as a whole word only ("cadence", "decade" are not CAD).</summary>
    [GeneratedRegex(@"\bcad\b")]
    private static partial Regex CadWord();

    private static (IReadOnlyList<string> Tools, TaskCategory Category) InferAppTools(string prompt, string? activeApp)
    {
        string t = (prompt ?? "").ToLowerInvariant(), app = (activeApp ?? "").ToLowerInvariant();
        var groups = new (string[] Keys, string? App, string[] Tools, TaskCategory Category)[]
        {
            (["assembl", "solidworks", "sldprt", "mate"], "sldworks", CadTools, TaskCategory.Cad),
            (["parametric", "build123d", "3d print", "stl", "generate part", "make part", ".step", "step file"], null, CadTools, TaskCategory.Cad),
            (["organi", "sort my", "tidy", "declutter", "move files", "rename files"], null, FileTools, TaskCategory.Files),
            (["davinci", "rough cut", "footage", "timeline"], "resolve", ResolveTools, TaskCategory.Resolve),
            (["blender", "render", "3d scene"], "blender", BlenderTools, TaskCategory.Blender),
            (["tutor", "chapter", "eigen", "determinant", "matrix"], null, TutorTools, TaskCategory.Tutor),
        };
        // Every SolidWorks job gets the whole CAD toolset (parts AND assemblies), so "make the parts and mate them" works in
        // one job. "cad" as a word counts only with a make/create word: "explain what a fillet is in CAD" is a question.
        bool create = CreateIntent.Any(k => HasWord(t, k));
        if (create && CadWord().IsMatch(t)) groups[0] = (["assembl", "solidworks", "sldprt", "mate", "cad"], "sldworks", CadTools, TaskCategory.Cad);
        var hits = groups.Where(g => g.Keys.Any(k => HasWord(t, k)) || (g.App is not null && app.Contains(g.App, StringComparison.Ordinal))).ToList();
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
                : category == TaskCategory.Search
                    ? " Use web search to find current, specific information, then answer concisely and name your sources. Do not read, list or search local files: none are shared with you."
                    : " No tools are needed or granted; answer from your knowledge. Do not read, list or search local files or run commands.")
                + (full is null ? "" : $"\nThe user's folder: {full}") + "\n\nUser: " + prompt,
        };
    }

    public const string FollowUpPrefix = "Follow-up: ";
}
}
