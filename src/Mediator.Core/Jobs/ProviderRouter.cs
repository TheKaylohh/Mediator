using System.Globalization;

namespace Mediator.Core.Jobs;

/// <summary>The subscriptions the Mediator can drive. Ids are the usage-ledger keys.</summary>
/// <summary>GeminiCli is the Google AI Pro provider, run through Antigravity CLI (agy) since Gemini CLI stopped serving AI Pro on 2026-06-18.</summary>
public enum AiProvider { ClaudeCode, GeminiCli }

/// <summary>
/// Task categories, from the overlay's actions, the prompt prefixes (/cad /code /files /tutor /resolve /blender /search /ask)
/// or JobSpecFactory.InferToolsAndCategory. Each has a fixed tool policy:
/// Chat = answer from knowledge, no tools, no local files; Search = web search only, no local files;
/// Files and the app categories = only the Mediator tools of that category, through the approval gate.
/// </summary>
/// <summary>Browser (V11): work in the user's Chrome through Claude in Chrome (Claude Code only).</summary>
public enum TaskCategory { Cad, Code, Files, Tutor, Resolve, Blender, Chat, Search, Browser }

/// <summary>V10: which subscription goes first when the user did not pick one. AutoBest = per-category defaults.</summary>
public enum RoutingPolicy { AutoBest, ConserveClaude, PreferClaude }

/// <param name="Original">The provider the job would have used (explicit choice or the category default).</param>
/// <param name="Reason">Why the original was not used (null when it was).</param>
public sealed record RouteDecision(AiProvider Provider, bool Downgraded, string? Notice, AiProvider Original = AiProvider.ClaudeCode, string? Reason = null);

/// <summary>
/// How a headless CLI run ended (V6). Each has a distinct handling; only <see cref="RateLimited"/> with no possible
/// side effect may switch subscriptions mid-job, and never when the job says NeverDowngrade.
/// </summary>
public enum RunOutcome
{
    /// <summary>A successful result and exit code 0.</summary>
    Succeeded,
    /// <summary>A result marked as an error (model or tool failure, max turns, invalid output).</summary>
    ResultError,
    /// <summary>A usage or rate limit was reported.</summary>
    RateLimited,
    /// <summary>The CLI could not authenticate (not signed in, expired login). Never failed over: sign in.</summary>
    AuthFailed,
    /// <summary>The process exited without a result event (crash, kill, or a stream it could not finish).</summary>
    NoResult,
    /// <summary>A successful result, then a non-zero exit code.</summary>
    ResultThenAbnormalExit,
}

/// <summary>Who decided what about a subscription switch; recorded in the audit as mediator/route.</summary>
public enum FailoverDecision { NotNeeded, SwitchedBeforeLaunch, SwitchedAfterRateLimit, RefusedNeverDowngrade, RefusedBothUnavailable, RefusedSideEffects, RefusedAlreadySwitched }

/// <summary>Thrown when no allowed provider can take the job (cooling down, not installed, or downgrade refused).</summary>
public sealed class ProviderUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Zero-token routing. An explicit provider wins; otherwise each category has a primary and a fallback:
/// cad, code, blender, search → Claude Code (fallback Antigravity CLI); chat, tutor, files, resolve → Antigravity CLI
/// (fallback Claude Code). Search goes to Claude Code first because its web search can be allowed per run (--allowedTools WebSearch).
/// A primary that is cooling down (usage ledger) or not installed is replaced by the fallback with a notice,
/// unless the job says NeverDowngrade, in which case the job is refused with a clear message.
/// </summary>
public static class ProviderRouter
{
    public static string Id(AiProvider p) => p == AiProvider.ClaudeCode ? "claude-code" : "gemini-cli";
    public static string DisplayName(AiProvider p) => p == AiProvider.ClaudeCode ? "Claude Pro (Claude Code)" : "Google AI Pro (Antigravity CLI)";

    public static (AiProvider Primary, AiProvider Fallback) Defaults(TaskCategory category) => category switch
    {
        TaskCategory.Cad or TaskCategory.Code or TaskCategory.Blender or TaskCategory.Search or TaskCategory.Browser => (AiProvider.ClaudeCode, AiProvider.GeminiCli),
        _ => (AiProvider.GeminiCli, AiProvider.ClaudeCode),
    };

    /// <summary>
    /// V10: the model alias for a run when the job did not set one. Claude Code: opus / sonnet / haiku (CLI aliases).
    /// Antigravity CLI (agy models): gemini-3.8-flash-high / gemini-3.8-flash-low. Short chat and file requests take low effort.
    /// </summary>
    public static string SelectModel(AiProvider provider, TaskCategory category, string prompt, int toolCount = 0)
    {
        prompt ??= "";
        // A job with Mediator tools (SolidWorks, Blender, ...) is never "light": haiku could not drive them.
        bool light = category is TaskCategory.Chat or TaskCategory.Files or TaskCategory.Search && toolCount == 0;
        if (provider == AiProvider.ClaudeCode)
        {
            if (new[] { "/opus", "architecture", "full assembly", "complex" }.Any(k => prompt.Contains(k, StringComparison.OrdinalIgnoreCase))) return "opus";
            return light && prompt.Length < 160 ? "haiku" : "sonnet";
        }
        return light && prompt.Length < 200 ? ModelCatalog.LightAgy : ModelCatalog.StrongAgy;
    }

    /// <summary>
    /// V10: the model and effort a run actually gets on <paramref name="provider"/>. A requested model that this CLI does not
    /// take is replaced by <see cref="SelectModel"/>'s. A job with Mediator tools never runs on a light model (haiku, Gemini
    /// low/medium): it is raised to the provider's capable tier, and a "low" effort to "medium". Note: what was changed and why.
    /// </summary>
    private const string LowestEffort = "low", ToolEffortFloor = "medium";

    public static (string Model, string? Effort, string? Note) ResolveRun(AiProvider provider, string? requestedModel, string? requestedEffort,
        TaskCategory category, string prompt, int toolCount)
    {
        var notes = new List<string>();
        string model;
        if (requestedModel is null) model = SelectModel(provider, category, prompt, toolCount);
        else if (ModelCatalog.Fits(provider, requestedModel)) model = requestedModel;
        else
        {
            model = SelectModel(provider, category, prompt, toolCount);
            notes.Add($"{requestedModel} does not run on {DisplayName(provider)}; using {model}");
        }
        if (toolCount > 0 && !ModelCatalog.IsCapable(model))
        {
            string capable = provider == AiProvider.ClaudeCode ? ModelCatalog.ClaudeToolDefault : ModelCatalog.StrongAgy;
            notes.Add($"{model} is too light to drive tools; using {capable}");
            model = capable;
        }
        string? effort = ModelCatalog.IsEffort(requestedEffort) ? requestedEffort : null;
        if (toolCount > 0 && effort == LowestEffort)
        {
            effort = ToolEffortFloor;
            notes.Add("low effort raised to medium for a tool job");
        }
        return (model, effort, notes.Count == 0 ? null : string.Join("; ", notes) + ".");
    }

    /// <summary>"/agy summarise this" (also /antigravity, /gemini) → (GeminiCli = Antigravity CLI, "summarise this"); "/claude …" likewise. Null without a provider prefix.</summary>
    public static (AiProvider Provider, string Remainder)? ParseProviderPrefix(string prompt)
    {
        string t = (prompt ?? "").TrimStart();
        int space = t.IndexOf(' ', StringComparison.Ordinal);
        string head = space < 0 ? t : t[..space];
        AiProvider? p = head.ToLowerInvariant() switch { "/agy" or "/antigravity" or "/gemini" => AiProvider.GeminiCli, "/claude" => AiProvider.ClaudeCode, _ => null };
        return p is { } provider ? (provider, space < 0 ? "" : t[(space + 1)..].Trim()) : null;
    }

    private static readonly Dictionary<string, TaskCategory> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/cad"] = TaskCategory.Cad, ["/code"] = TaskCategory.Code, ["/files"] = TaskCategory.Files,
        ["/tutor"] = TaskCategory.Tutor, ["/resolve"] = TaskCategory.Resolve, ["/blender"] = TaskCategory.Blender,
        ["/search"] = TaskCategory.Search, ["/ask"] = TaskCategory.Chat, ["/chrome"] = TaskCategory.Browser, ["/browser"] = TaskCategory.Browser,
    };

    /// <summary>What the chat badge calls a category ("Search", "Chat", "Files", ...).</summary>
    public static string CategoryName(TaskCategory c) => c switch
    {
        TaskCategory.Cad => "CAD", TaskCategory.Search => "Web search", TaskCategory.Chat => "Answer", TaskCategory.Browser => "Chrome",
        _ => c.ToString(),
    };

    /// <summary>"/tutor explain chapter 3" → (Tutor, "explain chapter 3"). Null when the prompt has no known prefix.</summary>
    public static (TaskCategory Category, string Remainder)? ParsePrefix(string prompt)
    {
        string t = prompt.TrimStart();
        int space = t.IndexOf(' ', StringComparison.Ordinal);
        string head = space < 0 ? t : t[..space];
        return Prefixes.TryGetValue(head, out var c) ? (c, space < 0 ? "" : t[(space + 1)..].Trim()) : null;
    }

    /// <param name="cooldownUntil">Per provider, the reset time of an active cooldown (null = available).</param>
    /// <param name="installed">Providers whose CLI was found.</param>
    public static RouteDecision Route(TaskCategory category, AiProvider? explicitProvider, bool neverDowngrade,
        Func<AiProvider, DateTimeOffset?> cooldownUntil, Func<AiProvider, bool> installed, RoutingPolicy policy = RoutingPolicy.AutoBest)
    {
        var (primary, fallback) = policy switch
        {
            RoutingPolicy.ConserveClaude => (AiProvider.GeminiCli, AiProvider.ClaudeCode),
            RoutingPolicy.PreferClaude => (AiProvider.ClaudeCode, AiProvider.GeminiCli),
            _ => Defaults(category),
        };
        if (explicitProvider is { } chosen) { primary = chosen; fallback = chosen == AiProvider.ClaudeCode ? AiProvider.GeminiCli : AiProvider.ClaudeCode; }

        string? why = Unavailable(primary, cooldownUntil, installed);
        if (why is null) return new RouteDecision(primary, false, null, primary);

        if (neverDowngrade)
            throw new ProviderUnavailableException($"{DisplayName(primary)} {why}, and this job is set to never switch subscriptions. Try again later or allow failover.");

        string? fallbackWhy = Unavailable(fallback, cooldownUntil, installed);
        if (fallbackWhy is not null)
            throw new ProviderUnavailableException($"{DisplayName(primary)} {why}, and {DisplayName(fallback)} {fallbackWhy}.");

        return new RouteDecision(fallback, true, $"{DisplayName(primary)} {why}; using {DisplayName(fallback)} instead.", primary, why);
    }

    private static string? Unavailable(AiProvider p, Func<AiProvider, DateTimeOffset?> cooldownUntil, Func<AiProvider, bool> installed)
    {
        if (!installed(p)) return "is not installed (CLI not on PATH)";
        if (cooldownUntil(p) is { } until)
            return "is at its usage limit until " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        return null;
    }
}
