using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mediator.Core.Diagnostics;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.Core.Browser;

/// <summary>
/// V14.1: the chat in Mediator for Chrome's side panel. Each message the panel sends ({type:"panel_chat", chatId, text,
/// tab:{id,url,title}}) runs as a headless Browser job on the user's subscription, like a chat in the app: the browser_*
/// tools work in the tab the panel sits next to, and later messages in the same panel chat continue the same session.
/// Progress goes back as {type:"panel_event", chatId, kind, text}: kind "start" (the route), "step" (a tool), "text"
/// (the model's words), "done" (the final answer) or "error". {type:"panel_stop", chatId} stops the running message;
/// {type:"panel_reset", chatId} forgets the chat.
/// </summary>
public sealed class BrowserPanelChats : IDisposable
{
    /// <summary>The job kind of panel chats (so the overlay does not offer them as its own follow-up).</summary>
    public const string Kind = "chrome-panel";
    public const int MaxTextChars = 8000;

    private readonly IMediatorRuntime _runtime;
    private readonly Func<JsonObject, Task> _send;
    private readonly IMediatorLog? _log;
    private readonly ConcurrentDictionary<string, Chat> _chats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _jobChat = new(StringComparer.Ordinal); // jobId → chatId
    private Action? _detach;

    private sealed class Chat
    {
        public readonly object Gate = new();
        public ContinuableSession? Session;
        public readonly List<JobSpecFactory.ConversationTurn> Turns = new();
        public string? RunningJobId;
        public bool Starting;
    }

    public BrowserPanelChats(IMediatorRuntime runtime, Func<JsonObject, Task> send, IMediatorLog? log = null)
    {
        _runtime = runtime;
        _send = send;
        _log = log;
        _runtime.StreamEvent += OnStreamEvent;
    }

    /// <summary>Listens to the extension's panel messages and answers through the same bridge.</summary>
    public static BrowserPanelChats Attach(IMediatorRuntime runtime, ChromeBridgeServer bridge, IMediatorLog? log = null)
    {
        var chats = new BrowserPanelChats(runtime, m => bridge.NotifyAsync(m), log);
        bridge.ExtensionMessage += chats.Handle;
        chats._detach = () => bridge.ExtensionMessage -= chats.Handle;
        return chats;
    }

    /// <summary>One message from the extension. Unknown types are ignored.</summary>
    public void Handle(JsonObject msg)
    {
        string? chatId = Str(msg["chatId"]);
        if (chatId is not { Length: > 0 and <= 100 }) return;
        switch (Str(msg["type"]))
        {
            case "panel_chat":
                string text = (Str(msg["text"]) ?? "").Trim();
                if (text.Length == 0) return;
                if (text.Length > MaxTextChars) text = text[..MaxTextChars];
                _ = RunAsync(chatId, text, msg["tab"] as JsonObject);
                break;
            case "panel_stop":
                if (_chats.TryGetValue(chatId, out var running) && running.RunningJobId is { } jobId) _runtime.KillJob(jobId, "stopped from the Chrome side panel");
                break;
            case "panel_reset":
                if (_chats.TryRemove(chatId, out var gone) && gone.RunningJobId is { } goneJob) _runtime.KillJob(goneJob, "the Chrome side panel chat was cleared");
                break;
        }
    }

    /// <summary>What the model is told about where the user is talking from (the tab next to the panel).</summary>
    public static string PanelContext(JsonObject? tab)
    {
        string where = tab is null ? "" : $" next to tab {TabId(tab["id"])} (\"{Clip(Str(tab["title"]), 120)}\", {Clip(Str(tab["url"]), 300)})";
        return "The user is talking to you from the Mediator side panel in Chrome" + where + ". \"This page\", \"this quiz\", \"this question\" mean that tab: "
            + "work in it (pass its tabId to the browser tools) instead of opening a new one, unless they ask for another site. "
            + "If they ask you to teach, explain or help them learn, read the page and explain the ideas step by step, check their reasoning, "
            + "and do not pick or submit answers for them. If they ask you to answer or do it, act in the page. Keep answers short: the panel is narrow.";
    }

    /// <summary>The panel's values are untrusted shapes: anything that is not a string or a whole number is ignored.</summary>
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static long? TabId(JsonNode? n) =>
        n is not JsonValue v ? null : v.TryGetValue(out long l) ? l : v.TryGetValue(out int i) ? i : null;

    private static string Clip(string? s, int max) => s is null ? "" : (s.Length > max ? s[..max] + "…" : s).Replace('"', '\'');

    /// <summary>The job for one panel message: a new Browser job, or a follow-up in the chat's session.</summary>
    public static JobSpec SpecFor(string text, JsonObject? tab, ContinuableSession? session, IReadOnlyList<JobSpecFactory.ConversationTurn> turns, string chatDirectory)
    {
        string prompt = PanelContext(tab) + "\n\n" + text;
        var spec = session is null
            ? JobSpecFactory.Assistant(null, prompt, LaunchMode.Headless, [], TaskCategory.Browser, chatDirectory)
            : JobSpecFactory.Continue(session, prompt, LaunchMode.Headless, history: turns.Count > 0 ? turns : null);
        string oneLine = text.ReplaceLineEndings(" ");
        return spec with
        {
            Kind = Kind,
            Category = TaskCategory.Browser,
            Tools = [],
            Roots = [],
            UserText = text,
            Title = "Chrome panel: " + (oneLine.Length > 50 ? oneLine[..50] + "…" : oneLine),
        };
    }

    private async Task RunAsync(string chatId, string text, JsonObject? tab)
    {
        var chat = _chats.GetOrAdd(chatId, _ => new Chat());
        ContinuableSession? session;
        List<JobSpecFactory.ConversationTurn> turns;
        lock (chat.Gate)
        {
            if (chat.Starting || chat.RunningJobId is not null)
            {
                _ = SendAsync(chatId, "error", "Still working on your last message. Wait for it, or press Stop.");
                return;
            }
            chat.Starting = true;
            session = chat.Session;
            turns = chat.Turns.ToList();
        }
        RunningJob job;
        try
        {
            job = await _runtime.StartJobAsync(SpecFor(text, tab, session, turns, _runtime.ChatDirectory)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProviderUnavailableException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            lock (chat.Gate) chat.Starting = false;
            await SendAsync(chatId, "error", ex.Message).ConfigureAwait(false);
            return;
        }
        _jobChat[job.JobId] = chatId;
        lock (chat.Gate) { chat.Starting = false; chat.RunningJobId = job.JobId; }
        string route = (job.Provider == AiProvider.GeminiCli ? "Antigravity CLI" : "Claude Code") + (job.Model is { } m ? " · " + m : "");
        await SendAsync(chatId, "start", route).ConfigureAwait(false);

        var state = await job.Completion.ConfigureAwait(false);
        string? answer = job.LastResult?.ResultText;
        bool ok = state == JobState.Succeeded && !string.IsNullOrWhiteSpace(answer);
        lock (chat.Gate)
        {
            chat.RunningJobId = null;
            if (ContinuableSession.From(job) is { } next) chat.Session = next;
            chat.Turns.Add(new JobSpecFactory.ConversationTurn(text, answer, Failed: !ok));
        }
        _jobChat.TryRemove(job.JobId, out _);
        if (ok) await SendAsync(chatId, "done", answer!).ConfigureAwait(false);
        else await SendAsync(chatId, "error", string.IsNullOrWhiteSpace(answer) ? job.Detail ?? $"The run ended: {state}." : answer).ConfigureAwait(false);
    }

    private void OnStreamEvent(string jobId, MediatorStreamEvent e)
    {
        if (!_jobChat.TryGetValue(jobId, out var chatId)) return;
        switch (e)
        {
            case ToolUseEvent tu:
                _ = SendAsync(chatId, "step", StepName(tu.Name));
                break;
            case AssistantTextEvent t when !string.IsNullOrWhiteSpace(t.Text):
                _ = SendAsync(chatId, "text", t.Text);
                break;
        }
    }

    /// <summary>"mcp__mediator__browser_click" → "click"; other tools by their last name part.</summary>
    public static string StepName(string tool)
    {
        string name = tool.Contains("__", StringComparison.Ordinal) ? tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : tool;
        return name.StartsWith("browser_", StringComparison.Ordinal) ? name["browser_".Length..] : name;
    }

    private async Task SendAsync(string chatId, string kind, string text)
    {
        try { await _send(new JsonObject { ["type"] = "panel_event", ["chatId"] = chatId, ["kind"] = kind, ["text"] = text }).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { _log?.Info("Chrome side panel: " + ex.Message); }
    }

    public void Dispose()
    {
        _runtime.StreamEvent -= OnStreamEvent;
        _detach?.Invoke();
    }
}
