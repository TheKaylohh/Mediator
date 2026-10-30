using Mediator.Core.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mediator.Core.Jobs;

/// <summary>One saved exchange: the user's input and the CLI's output.</summary>
public sealed record SavedTurn(string JobId, string Prompt, string Badge, string Response, List<string> Steps, bool IsError);

/// <summary>
/// What a saved chat needs to continue: a Claude session id resumes natively (`claude --resume`, full context);
/// otherwise the earlier turns are carried into the next prompt as context.
/// </summary>
public sealed record SavedResume(string SessionId, AiProvider Provider, string WorkingDirectory, string JobId, bool NativeResume, string LastPrompt, string? LastResponse)
{
    /// <summary>The job's Mediator tools, folders and category, so a chat reopened after a restart keeps its tools.
    /// Null in chats saved before they were stored: rebuilt from the prompt and folder instead.</summary>
    public List<string>? Tools { get; init; }
    public List<string>? Roots { get; init; }
    public TaskCategory? Category { get; init; }

    public static SavedResume From(ContinuableSession s) =>
        new(s.SessionId, s.Provider, s.WorkingDirectory, s.JobId, s.NativeResume, s.Spec.UserText ?? s.Spec.Prompt, s.LastResponseText)
        {
            Tools = s.Spec.Tools.ToList(), Roots = s.Spec.Roots.ToList(), Category = s.Spec.Category,
        };

    /// <summary>A continuable session rebuilt from disk. <paramref name="history"/> replaces the last answer for non-native resumes.</summary>
    public ContinuableSession ToSession(DateTimeOffset endedAt, string? history = null)
    {
        // Reopened chats used to come back with no tools and the Chat category, so a follow-up could not reach SolidWorks.
        var inferred = JobSpecFactory.InferToolsAndCategory(LastPrompt, null);
        var tools = JobSpecFactory.WithCurrentToolset(Tools ?? inferred.Tools); // V16.1: chats saved by older versions get today's tools
        var category = Category ?? inferred.Category;
        var roots = Roots ?? (tools.Count > 0 && System.IO.Directory.Exists(WorkingDirectory) ? [WorkingDirectory] : []);
        var spec = JobSpecFactory.Assistant(null, LastPrompt, LaunchMode.Headless, tools, category, WorkingDirectory) with { Roots = roots };
        return new ContinuableSession(SessionId, Provider, spec.WorkingDirectory, JobId, spec, endedAt)
        {
            LastResponseText = NativeResume ? LastResponse : history ?? LastResponse,
            NativeResume = NativeResume,
        };
    }
}

/// <param name="Pinned">Starred: listed first in the sidebar.</param>
/// <param name="CustomTitle">A title the user typed (otherwise the first prompt is the title).</param>
/// <param name="Folders">Folders the user allowed for this chat: every job it starts may read and write them.</param>
public sealed record SavedChat(string Id, string Title, DateTimeOffset Updated, List<SavedTurn> Turns, SavedResume? Resume,
    bool Pinned = false, string? CustomTitle = null, List<string>? Folders = null)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle!;

    /// <summary>Search over the title and every prompt and answer (case-insensitive).</summary>
    public bool Matches(string query) =>
        query.Length == 0 || DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Turns.Any(t => t.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase) || (t.Response ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Saved chats as one JSON file each under &lt;data&gt;\chats. Failures never break the overlay: they return empty.</summary>
public sealed class ChatStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string Directory { get; } = directory;

    public static string NewId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    private string PathFor(string id) =>
        SessionIds.IsSafe(id) ? Path.Combine(Directory, id + ".json") : throw new ArgumentException("Bad chat id: " + id);

    public void Save(SavedChat chat)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathFor(chat.Id), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(chat, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public SavedChat? Load(string id)
    {
        try { return JsonSerializer.Deserialize<SavedChat>(File.ReadAllText(PathFor(id)), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every saved chat, newest first.</summary>
    public List<SavedChat> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(f => Load(Path.GetFileNameWithoutExtension(f)))
            .OfType<SavedChat>()
            .OrderByDescending(c => c.Pinned)
            .ThenByDescending(c => c.Updated)
            .ToList();
    }

    public void Delete(string id)
    {
        try { File.Delete(PathFor(id)); } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }
}
