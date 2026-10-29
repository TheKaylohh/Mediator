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

    /// <summary>The job as a continuable session, or null: only headless Claude runs that produced a result and a safe session id.</summary>
    public static ContinuableSession? From(RunningJob job)
    {
        if (job.Provider != AiProvider.ClaudeCode || job.Spec.Launch != LaunchMode.Headless) return null;
        if (job.State is not (JobState.Succeeded or JobState.Failed) || job.Outcome is not (RunOutcome.Succeeded or RunOutcome.ResultError)) return null;
        if (!SessionIds.IsSafe(job.SessionId)) return null;
        return new ContinuableSession(job.SessionId!, AiProvider.ClaudeCode, job.Spec.WorkingDirectory, job.JobId, job.Spec, job.EndedAt ?? DateTimeOffset.UtcNow);
    }
}

public static partial class JobSpecFactory
{
    /// <summary>
    /// The follow-up job for <paramref name="session"/>: the same kind, tools, roots, folder, category and limits; the
    /// user's follow-up text as the prompt (the original instructions are already in the session); the previous
    /// attachments plus <paramref name="newAttachments"/>; pinned to Claude Code (a session cannot move to Gemini).
    /// </summary>
    public static JobSpec Continue(ContinuableSession session, string prompt, LaunchMode launch = LaunchMode.Headless, IReadOnlyList<Attachment>? newAttachments = null)
    {
        var previous = session.Spec;
        string title = previous.Title.StartsWith(FollowUpPrefix, StringComparison.Ordinal) ? previous.Title : FollowUpPrefix + previous.Title;
        var attachments = previous.Attachments.Concat(newAttachments ?? []).GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        return previous with
        {
            Title = title,
            Prompt = prompt,
            Launch = launch,
            JobId = null,
            Attachments = attachments,
            Provider = AiProvider.ClaudeCode,
            NeverDowngrade = true,
            ContinueSessionId = session.SessionId,
        };
    }

    public const string FollowUpPrefix = "Follow-up: ";
}
}
