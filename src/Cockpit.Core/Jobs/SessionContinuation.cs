using System.Text.RegularExpressions;
using Cockpit.Core.Attachments;
using Cockpit.Core.Jobs;

namespace Cockpit.Core.Jobs
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

namespace Cockpit.Core.Runtime
{
/// <summary>
/// A finished headless Claude Code run that a follow-up prompt can continue with `claude -p --resume &lt;id&gt;` (V9).
/// Claude Code keeps sessions per working folder, so the follow-up runs in the same folder; it gets a NEW job, grant
/// and workspace (the old job's token was revoked when it ended and is never revived), with the same tools, roots
/// and attachments, so the resumed conversation can keep using what it already referred to.
/// Gemini runs are not continuable: they run in the per-job workspace, and resuming a Gemini session is not
/// supported by the flags verified for the Gemini CLI version the Cockpit was tested with.
/// </summary>
public sealed record ContinuableSession(string SessionId, AiProvider Provider, string WorkingDirectory, string JobId, JobSpec Spec, DateTimeOffset EndedAt)
{
    public string ShortId => SessionIds.Short(SessionId);
    /// <summary>V10: the last answer, carried into a cross-CLI follow-up as context.</summary>
    public string? LastResponseText { get; init; }
    /// <summary>V10: true when the CLI itself can resume it (`claude --resume`): Claude Code with a safe session id.</summary>
    public bool NativeResume { get; init; }

    /// <summary>
    /// The job as a continuable session, or null. V10: headless Claude Code AND Gemini CLI runs that produced a result.
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
    /// gets `--resume` (V9). Otherwise (V10: Gemini, or another target CLI) the job is routed normally and the prompt starts
    /// with a compact block holding the previous turn.
    /// </summary>
    public static JobSpec Continue(ContinuableSession session, string prompt, LaunchMode launch = LaunchMode.Headless, IReadOnlyList<Attachment>? newAttachments = null, AiProvider? targetProvider = null)
    {
        var previous = session.Spec;
        string title = previous.Title.StartsWith(FollowUpPrefix, StringComparison.Ordinal) ? previous.Title : FollowUpPrefix + previous.Title;
        var attachments = previous.Attachments.Concat(newAttachments ?? []).GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var spec = previous with { Title = title, Launch = launch, JobId = null, Attachments = attachments, UserText = prompt };
        if (session.NativeResume && targetProvider is null or AiProvider.ClaudeCode)
            return spec with { Prompt = prompt, Provider = AiProvider.ClaudeCode, NeverDowngrade = true, ContinueSessionId = session.SessionId };
        static string Cap(string? t, int n) => t is null ? "" : t.Length <= n ? t : t[..n] + "…";
        string context = "Previous turn user: " + Cap(previous.UserText ?? previous.Prompt, 2000) + "\nPrevious turn assistant: " + Cap(session.LastResponseText, 4000);
        return spec with { Prompt = context + "\n\nFollow-up: " + prompt, Provider = targetProvider, ContinueSessionId = null };
    }

    /// <summary>
    /// V10: the Cockpit tools a free-text request needs, from keywords and the app in front (SLDWORKS, Resolve, blender).
    /// The tool arrays of every match are united; the category is the first match's. Nothing matched: no tools, Chat.
    /// </summary>
    public static (IReadOnlyList<string> Tools, TaskCategory Category) InferToolsAndCategory(string prompt, string? activeApp)
    {
        string t = (prompt ?? "").ToLowerInvariant(), app = (activeApp ?? "").ToLowerInvariant();
        var groups = new (string[] Keys, string? App, string[] Tools, TaskCategory Category)[]
        {
            (["assembl", "solidworks", "sldprt", "mate"], "sldworks", SwxTools, TaskCategory.Cad),
            (["parametric", "build123d", "generate part", "make part", ".step", "step file"], null, CadTools, TaskCategory.Cad),
            (["organi", "sort my", "tidy", "declutter", "move files", "rename files"], null, FileTools, TaskCategory.Files),
            (["davinci", "resolve", "timeline", "rough cut", "footage"], "resolve", ResolveTools, TaskCategory.Resolve),
            (["blender", "render", "3d scene"], "blender", BlenderTools, TaskCategory.Blender),
            (["tutor", "chapter", "eigen", "determinant", "matrix"], null, TutorTools, TaskCategory.Tutor),
        };
        var hits = groups.Where(g => g.Keys.Any(k => t.Contains(k, StringComparison.Ordinal)) || (g.App is not null && app.Contains(g.App, StringComparison.Ordinal))).ToList();
        if (hits.Count == 0) return ([], TaskCategory.Chat);
        return (hits.SelectMany(h => h.Tools).Distinct(StringComparer.Ordinal).ToList(), hits[0].Category);
    }

    /// <summary>
    /// V10: a headless "just answer" job. The model replies directly and uses the granted Cockpit tools only when the
    /// request needs them. <paramref name="folder"/> null: no file roots (general chat), run in the chat folder.
    /// </summary>
    public static JobSpec Assistant(string? folder, string prompt, LaunchMode launch, IReadOnlyList<string> tools, TaskCategory category, string? chatDirectory = null)
    {
        string? full = folder is null ? null : Path.GetFullPath(folder);
        string work = full ?? chatDirectory ?? Path.Combine(Path.GetTempPath(), "AiCockpit-chat");
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
            MaxTurns = 20,
            UserText = prompt,
            Prompt = "Answer the user's request directly and clearly." + (tools.Count > 0
                ? " If it needs their files or desktop apps, use the Cockpit tools you were granted (only those); otherwise just answer."
                : " No tools are needed or granted; answer from your knowledge.")
                + (full is null ? "" : $"\nThe user's folder: {full}") + "\n\nUser: " + prompt,
        };
    }

    public const string FollowUpPrefix = "Follow-up: ";
}
}
