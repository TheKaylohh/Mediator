using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Security;

namespace Mediator.Core.Diagnostics;

/// <summary>
/// A zip for troubleshooting that is safe to share. It contains:
/// <list type="bullet">
/// <item>versions and environment facts;</item>
/// <item>which credential variables are present (names only, never values);</item>
/// <item>the audit chain's verification and head;</item>
/// <item>the newest audit rows and the unresolved operations, with secrets redacted and every path anonymized;</item>
/// <item>batch states, usage totals and the doctor report.</item>
/// </list>
/// It never contains:
/// <list type="bullet">
/// <item>database files, MCP configs (they hold job tokens), job briefs or prompts;</item>
/// <item>workspace files, user files or file names.</item>
/// </list>
/// Paths become <c>&lt;path#hash.ext&gt;</c>: the same path always maps to the same placeholder, so a sequence of
/// events stays readable.
/// </summary>
public static partial class SupportBundle
{
    public const int AuditRows = 200;

    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\[^\\/\s""]+[\\/]|/)(?:[^\s""'<>|*?,;:]+[\\/])*[^\s""'<>|*?,;:\\/]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathLike();

    /// <summary>Redacts secrets, then replaces anything path-like (Windows, UNC, POSIX) with a stable placeholder.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string s = Redactor.Redact(text);
        return PathLike().Replace(s, m =>
        {
            string p = m.Value;
            if (p.Length < 3 || !(p.Contains('/') || p.Contains('\\'))) return p;
            if (p.StartsWith("//", StringComparison.Ordinal) && p.Length > 2 && char.IsLetter(p[2]) && !p.Contains('.', StringComparison.Ordinal)) return p;
            string ext = Path.GetExtension(p.TrimEnd('/', '\\'));
            if (ext.Length > 8 || !ext.All(c => c == '.' || char.IsAsciiLetterOrDigit(c))) ext = "";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.ToLowerInvariant())))[..8].ToLowerInvariant();
            return $"<path#{hash}{ext}>";
        });
    }

    public static async Task<string> CreateAsync(MediatorRuntime runtime, string zipPath, JsonNode? doctorReport = null, CancellationToken ct = default)
    {
        var files = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        files["manifest.json"] = new JsonObject
        {
            ["createdUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["mediator"] = typeof(SupportBundle).Assembly.GetName().Version?.ToString(),
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["policy"] = "Secrets redacted; every path replaced by <path#hash.ext>; no databases, MCP configs, prompts, job briefs, workspace or user files.",
        };
        var vars = GeminiLaunchBuilder.VariablesToScrub(false).Concat(["MEDIATOR_DATA_DIR", "MEDIATOR_PYTHON", "MEDIATOR_BLENDER", "MEDIATOR_AGY"]).Distinct(StringComparer.Ordinal);
        files["environment.json"] = new JsonObject
        {
            ["variablesPresent"] = new JsonObject(vars.Select(v => KeyValuePair.Create(v, (JsonNode?)JsonValue.Create(Environment.GetEnvironmentVariable(v) is { Length: > 0 })))),
            ["claudeOnPath"] = CliLaunchBuilder.ResolveOnPath("claude") is not null,
            ["agyInstalled"] = GeminiLaunchBuilder.Locate() is not null,
        };

        var verification = await runtime.Audit.VerifyAsync().ConfigureAwait(false);
        var (rows, total) = await runtime.Audit.RecentAsync(AuditRows).ConfigureAwait(false);
        files["audit.json"] = new JsonObject
        {
            ["chainOk"] = verification.Ok, ["rowsChecked"] = verification.RowsChecked, ["firstBadId"] = verification.FirstBadId,
            ["problem"] = verification.Problem, ["head"] = verification.Head, ["totalRows"] = total,
            ["recent"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["ts"] = r.TimestampUtc, ["job"] = r.JobId, ["tool"] = r.Tool, ["risk"] = r.Risk, ["decision"] = r.Decision,
                ["outcome"] = r.Outcome, ["ms"] = r.DurationMs,
                ["arguments"] = Truncate(Sanitize(r.ArgumentsJson), 600), ["detail"] = r.Detail is null ? null : Truncate(Sanitize(r.Detail), 600),
            }).ToArray()),
        };

        var ops = await runtime.Operations.ListAsync().ConfigureAwait(false);
        files["operations.json"] = new JsonObject
        {
            ["counts"] = new JsonObject(ops.GroupBy(o => OperationStore.Snake(o.State)).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)JsonValue.Create(g.Count())))),
            ["unresolved"] = new JsonArray(ops.Where(o => OperationStore.IsUnresolved(o.State)).Select(o => (JsonNode)new JsonObject
            {
                ["id"] = o.OperationId, ["job"] = o.JobId, ["tool"] = o.Tool, ["state"] = OperationStore.Snake(o.State), ["updated"] = o.UpdatedUtc,
                ["note"] = Sanitize(o.Note),
            }).ToArray()),
        };

        var batches = await runtime.Journal.ListAllBatchesAsync(100, ct).ConfigureAwait(false);
        files["journal.json"] = new JsonObject
        {
            ["batches"] = new JsonArray(batches.Select(b => (JsonNode)new JsonObject
            {
                ["job"] = b.JobId, ["batch"] = b.Batch.BatchId, ["status"] = b.Batch.Status, ["created"] = b.Batch.CreatedUtc, ["ops"] = b.Batch.OpCount, ["bytes"] = b.Batch.TotalBytes,
            }).ToArray()),
        };

        var usage = new JsonObject();
        foreach (var p in Enum.GetValues<AiProvider>())
        {
            var t = await runtime.Usage.TotalsAsync(ProviderRouter.Id(p), DateTimeOffset.UtcNow.AddDays(-7)).ConfigureAwait(false);
            usage[ProviderRouter.Id(p)] = new JsonObject
            {
                ["runs7d"] = t.Runs, ["unknownUsageRuns"] = t.UnknownUsageRuns, ["rateLimitedRuns"] = t.RateLimitedRuns,
                ["inputTokens"] = t.Tokens.InputTokens, ["outputTokens"] = t.Tokens.OutputTokens,
                ["cooldownUntil"] = (await runtime.GetCooldownAsync(ProviderRouter.Id(p)).ConfigureAwait(false))?.ToString("O", CultureInfo.InvariantCulture),
            };
        }
        files["usage.json"] = usage;
        if (doctorReport is not null) files["doctor.json"] = JsonNode.Parse(Sanitize(doctorReport.ToJsonString()))!;

        string full = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var (name, node) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
        return full;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
