using System.Globalization;

namespace Cockpit.Core.Jobs;

/// <summary>The subscriptions the Cockpit can drive. Ids are the usage-ledger keys.</summary>
public enum AiProvider { ClaudeCode, GeminiCli }

/// <summary>Task categories, from the overlay's actions and the prompt prefixes /cad /code /files /tutor /resolve /blender.</summary>
public enum TaskCategory { Cad, Code, Files, Tutor, Resolve, Blender }

public sealed record RouteDecision(AiProvider Provider, bool Downgraded, string? Notice);

/// <summary>Thrown when no allowed provider can take the job (cooling down, not installed, or downgrade refused).</summary>
public sealed class ProviderUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Zero-token routing. An explicit provider wins; otherwise each category has a primary and a fallback:
/// cad, code, blender → Claude Code (fallback Gemini CLI); tutor, files, resolve → Gemini CLI (fallback Claude Code).
/// A primary that is cooling down (usage ledger) or not installed is replaced by the fallback with a notice,
/// unless the job says NeverDowngrade, in which case the job is refused with a clear message.
/// </summary>
public static class ProviderRouter
{
    public static string Id(AiProvider p) => p == AiProvider.ClaudeCode ? "claude-code" : "gemini-cli";
    public static string DisplayName(AiProvider p) => p == AiProvider.ClaudeCode ? "Claude Pro (Claude Code)" : "Google AI Pro (Gemini CLI)";

    public static (AiProvider Primary, AiProvider Fallback) Defaults(TaskCategory category) => category switch
    {
        TaskCategory.Cad or TaskCategory.Code or TaskCategory.Blender => (AiProvider.ClaudeCode, AiProvider.GeminiCli),
        _ => (AiProvider.GeminiCli, AiProvider.ClaudeCode),
    };

    private static readonly Dictionary<string, TaskCategory> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/cad"] = TaskCategory.Cad, ["/code"] = TaskCategory.Code, ["/files"] = TaskCategory.Files,
        ["/tutor"] = TaskCategory.Tutor, ["/resolve"] = TaskCategory.Resolve, ["/blender"] = TaskCategory.Blender,
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
        Func<AiProvider, DateTimeOffset?> cooldownUntil, Func<AiProvider, bool> installed)
    {
        var (primary, fallback) = Defaults(category);
        if (explicitProvider is { } chosen) { primary = chosen; fallback = chosen == AiProvider.ClaudeCode ? AiProvider.GeminiCli : AiProvider.ClaudeCode; }

        string? why = Unavailable(primary, cooldownUntil, installed);
        if (why is null) return new RouteDecision(primary, false, null);

        if (neverDowngrade)
            throw new ProviderUnavailableException($"{DisplayName(primary)} {why}, and this job is set to never switch subscriptions. Try again later or allow failover.");

        string? fallbackWhy = Unavailable(fallback, cooldownUntil, installed);
        if (fallbackWhy is not null)
            throw new ProviderUnavailableException($"{DisplayName(primary)} {why}, and {DisplayName(fallback)} {fallbackWhy}.");

        return new RouteDecision(fallback, true, $"{DisplayName(primary)} {why}; using {DisplayName(fallback)} instead.");
    }

    private static string? Unavailable(AiProvider p, Func<AiProvider, DateTimeOffset?> cooldownUntil, Func<AiProvider, bool> installed)
    {
        if (!installed(p)) return "is not installed (CLI not on PATH)";
        if (cooldownUntil(p) is { } until)
            return "is at its usage limit until " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        return null;
    }
}
