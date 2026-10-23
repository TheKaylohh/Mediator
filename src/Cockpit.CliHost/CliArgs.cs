using System.Globalization;

namespace Cockpit.CliHost;

public sealed class UsageException(string message) : Exception(message);

/// <summary>`command positional... --flag --key value`. Unknown options are an error, so typos never run a job.</summary>
public sealed class CliArgs
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "headless", "interactive", "simulate", "yes", "json", "recursive", "allow-metered", "check-swx", "dry-run", "keep-subscription", "gemini-skip-trust",
    };
    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "output", "rules", "criteria", "model", "max-turns", "data-dir", "bridge", "worker", "claude", "gemini", "batch", "job-id", "request", "provider", "b",
    };

    public string Command { get; private init; } = "";
    public IReadOnlyList<string> Positional { get; private init; } = [];
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static CliArgs Parse(string[] args)
    {
        var positional = new List<string>();
        var parsed = new CliArgs { Command = args.Length > 0 ? args[0] : "" };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { positional.Add(a); continue; }
            string name = a[2..];
            if (Flags.Contains(name)) parsed._flags.Add(name);
            else if (Valued.Contains(name))
            {
                if (i + 1 >= args.Length) throw new UsageException($"--{name} needs a value.");
                parsed._values[name] = args[++i];
            }
            else throw new UsageException($"Unknown option --{name}.");
        }
        return new CliArgs { Command = parsed.Command, Positional = positional }.With(parsed);
    }

    private CliArgs With(CliArgs other)
    {
        foreach (var f in other._flags) _flags.Add(f);
        foreach (var kv in other._values) _values[kv.Key] = kv.Value;
        return this;
    }

    public bool Has(string flag) => _flags.Contains(flag);
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback) =>
        Get(key) is { } v ? int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : throw new UsageException($"--{key} must be a positive number.") : fallback;

    public string RequirePositional(int index, string what) =>
        Positional.Count > index ? Positional[index] : throw new UsageException($"Missing {what}.");
}
