using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Security;

/// <summary>
/// Removes secrets from text before it is stored (audit, operation records) or exported (support bundle).
/// Two layers: known secret values registered at runtime (job tokens, billing variables found in the environment),
/// replaced wherever they appear; and patterns for well-known credential formats and JSON fields whose names say
/// they hold a secret. Redaction is best effort for unknown formats; the design goal is that the Cockpit never
/// writes a secret it knows about.
/// </summary>
public static partial class Redactor
{
    public const string Mark = "[redacted]";
    private static readonly ConcurrentDictionary<string, byte> Known = new(StringComparer.Ordinal);

    /// <summary>Registers a secret value (8+ characters) to be removed wherever it appears.</summary>
    public static void Register(string? secret)
    {
        if (secret is { Length: >= 8 }) Known[secret] = 0;
    }

    public static void Unregister(string? secret)
    {
        if (secret is not null) Known.TryRemove(secret, out _);
    }

    /// <summary>Registers the values of credential-bearing environment variables present in this process.</summary>
    public static void RegisterEnvironmentSecrets(IEnumerable<string> variableNames)
    {
        foreach (var name in variableNames) Register(Environment.GetEnvironmentVariable(name));
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}|sk-[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_\-]{30,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9\-]{10,}|ya29\.[A-Za-z0-9_\-\.]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFormats();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"(?i)(""[^""\\]*(?:token|secret|password|passwd|api[_-]?key|authorization|cookie|credential)[^""\\]*""\s*:\s*)""(?:[^""\\]|\\.)*""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretJsonField();

    [GeneratedRegex(@"(?i)\b([A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY|APIKEY)[A-Z0-9_]*)=([^\s""']{6,})", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string s = text;
        foreach (var secret in Known.Keys)
            if (s.Contains(secret, StringComparison.Ordinal)) s = s.Replace(secret, Mark, StringComparison.Ordinal);
        s = KeyFormats().Replace(s, Mark);
        s = AuthHeader().Replace(s, m => m.Groups[1].Value + " " + Mark);
        s = SecretJsonField().Replace(s, m => m.Groups[1].Value + "\"" + Mark + "\"");
        s = SecretAssignment().Replace(s, m => m.Groups[1].Value + "=" + Mark);
        return s;
    }
}
