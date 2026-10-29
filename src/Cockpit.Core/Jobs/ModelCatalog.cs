using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Core.Jobs;

/// <summary>The models one installed CLI offers.</summary>
public sealed record CliModels(AiProvider Provider, string Cli, IReadOnlyList<string> Models);

/// <summary>
/// What the installed CLIs offer. Claude Code takes the aliases opus / sonnet / haiku (or full claude-* names);
/// Antigravity lists its models with `agy models` (id, tab, name). Both take --effort low|medium|high|xhigh|max.
/// </summary>
public static class ModelCatalog
{
    // Model-name conventions the tiering relies on. Kept here so no other file spells them out.
    private const string ClaudeFullNamePrefix = "claude-";
    private const string GeminiPrefix = "gemini";
    private const string HighSuffix = "-high";
    private const string LowSuffix = "-low";
    private const string LightClaudeFamily = "haiku";
    private static readonly ImmutableArray<string> CapableClaudePrefixes = ["claude-opus", "claude-sonnet"];

    /// <summary>How long `agy models` may take before the known ids are used.</summary>
    public static readonly TimeSpan AgyListTimeout = TimeSpan.FromSeconds(10);

    public static ImmutableArray<string> Efforts { get; } = ["low", "medium", "high", "xhigh", "max"];
    public static ImmutableArray<string> ClaudeModels { get; } = ["opus", "sonnet", "haiku"];
    /// <summary>The capable Claude alias a light pick on a tool job is raised to.</summary>
    public const string ClaudeToolDefault = "sonnet";
    /// <summary>Used until `agy models` has answered (or when it cannot); strongest first.</summary>
    public static ImmutableArray<string> AgyFallback { get; } = ["gemini-3.8-flash-high", "gemini-3.8-flash-medium", "gemini-3.8-flash-low"];

    private static IReadOnlyList<string>? s_agyModels; // written once, by the first successful `agy models`

    public static bool IsEffort(string? e) => e is not null && Efforts.Contains(e, StringComparer.Ordinal);

    /// <summary>The Antigravity models detected on this machine (after <see cref="AgyModelsAsync"/>), else the known ids.</summary>
    public static IReadOnlyList<string> KnownAgyModels => Volatile.Read(ref s_agyModels) ?? AgyFallback;

    /// <summary>True when <paramref name="model"/> is something this CLI accepts.</summary>
    public static bool Fits(AiProvider provider, string? model) => model is { Length: > 0 } && (provider == AiProvider.ClaudeCode
        ? ClaudeModels.Contains(model, StringComparer.Ordinal) || model.StartsWith(ClaudeFullNamePrefix, StringComparison.Ordinal)
        : KnownAgyModels.Contains(model, StringComparer.Ordinal));

    /// <summary>
    /// Strong enough to drive Cockpit tools: Claude opus / sonnet (alias or full name, on either CLI) or a Gemini "-high"
    /// model. Haiku, "-low" / "-medium" variants and other small models are not: they invent tool calls.
    /// </summary>
    public static bool IsCapable(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string m = model.ToLowerInvariant();
        if (m.Contains(LightClaudeFamily, StringComparison.Ordinal)) return false;
        if (m is "opus" or "sonnet" || CapableClaudePrefixes.Any(p => m.StartsWith(p, StringComparison.Ordinal))) return true;
        return IsGemini(m, HighSuffix);
    }

    /// <summary>The strongest / lightest detected Gemini model ("-high" / "-low"), by name rather than a fixed id.</summary>
    public static string StrongAgy => KnownAgyModels.FirstOrDefault(m => IsGemini(m, HighSuffix)) ?? AgyFallback[0];
    public static string LightAgy => KnownAgyModels.FirstOrDefault(m => IsGemini(m, LowSuffix)) ?? AgyFallback[^1];

    private static bool IsGemini(string model, string suffix) =>
        model.StartsWith(GeminiPrefix, StringComparison.Ordinal) && model.EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>`agy models` output → model ids. Lines without an id-like first column ("Fetching...") are skipped.</summary>
    public static IReadOnlyList<string> ParseAgyModels(string? stdout) =>
        (stdout ?? "").Split('\n')
            .Select(l => l.Trim().Split('\t', ' ')[0].Trim())
            .Where(id => id.Length > 2 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') && id.Any(char.IsDigit))
            .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Runs `agy models` (capped at <see cref="AgyListTimeout"/>; the process tree is killed if it overruns) and caches a
    /// successful answer for the app's lifetime. Any failure returns the known ids and is retried on the next call.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AgyModelsAsync(string agyExecutable, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agyExecutable);
        if (Volatile.Read(ref s_agyModels) is { } cached) return cached;

        var psi = CliLaunchBuilder.WrapForShim(agyExecutable, ["models"]);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        foreach (var v in CliLaunchBuilder.VariablesToScrub(allowMeteredBilling: false)) psi.Environment.Remove(v);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null) return AgyFallback;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AgyListTimeout);
            // Both pipes are drained so a chatty stderr can never block the child.
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cts.Token)).ConfigureAwait(false);
            var models = ParseAgyModels(await stdout.ConfigureAwait(false));
            if (models.Count == 0) return AgyFallback;
            Interlocked.CompareExchange(ref s_agyModels, models, null);
            return Volatile.Read(ref s_agyModels)!;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception or IOException)
        {
            return AgyFallback;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already gone */ }
                process.Dispose();
            }
        }
    }
}
