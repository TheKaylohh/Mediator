using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Cockpit.Core.Bus;

public enum ApprovalMode { Auto, Ask, Deny }

/// <summary>The one transport a job's bridge may use. A token presented over the other transport is rejected and audited.</summary>
public enum BusTransport { NamedPipe, LoopbackTcp }

/// <summary>
/// Everything a job is allowed to do. Created by the Core when a job starts, referenced by the
/// bridge only through (JobId, Token), and enforced server-side on every call.
/// </summary>
public sealed class JobGrant
{
    public required string JobId { get; init; }
    public required string Token { get; init; }
    public required IReadOnlySet<string> AllowedTools { get; init; }
    /// <summary>Real (link-resolved) folder roots this job may read or change, each ending with a separator. Display only: authorize with <see cref="Paths"/>.</summary>
    public required IReadOnlyList<string> FileRoots { get; init; }
    /// <summary>Per-tool override of the risk-based default. Destructive tools can never be set to Auto.</summary>
    public IReadOnlyDictionary<string, ApprovalMode> ToolModes { get; init; } = new Dictionary<string, ApprovalMode>();
    /// <summary>NamedPipe for host jobs; LoopbackTcp only for jobs whose CLI runs inside the restricted WSL distro.</summary>
    public BusTransport Transport { get; init; } = BusTransport.NamedPipe;
    /// <summary>Lets this job's CLI keep API-key environment variables (pay-per-token). Off unless the user opts in.</summary>
    public bool AllowMeteredBilling { get; init; }
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The only way a provider may decide whether a path is inside this job's roots. Lexical prefix checks are
    /// not allowed anywhere else (they miss links, junctions and mount points; see docs/v6/FILESYSTEM_SECURITY.md).
    /// </summary>
    public required Cockpit.Core.Security.PathAuthorizer Paths { get; init; }
}

/// <summary>Issues, authenticates and revokes job grants. Thread-safe.</summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, JobGrant> _jobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public JobRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised after a job is revoked so leases, connections and approvals tied to it can be released.</summary>
    public event Action<string>? JobRevoked;

    /// <summary>Raised synchronously inside <see cref="Issue"/> (the bus starts its TCP listener here for WSL jobs).</summary>
    public event Action<JobGrant>? JobIssued;

    public JobGrant Issue(string jobId, IEnumerable<string> allowedTools, IEnumerable<string> fileRoots, TimeSpan lifetime,
        BusTransport transport = BusTransport.NamedPipe, IReadOnlyDictionary<string, ApprovalMode>? toolModes = null,
        TimeSpan? approvalTimeout = null, bool allowMeteredBilling = false)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 || !jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Job IDs are 1-64 characters of letters, digits, '-' or '_'.", nameof(jobId));

        // Roots are resolved to their real paths now; a root that does not exist fails the grant (fail-closed).
        var paths = new Cockpit.Core.Security.PathAuthorizer(fileRoots);
        var roots = paths.Roots.Select(r => Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar).ToArray();
        var grant = new JobGrant
        {
            JobId = jobId,
            Token = Base64Url(RandomNumberGenerator.GetBytes(32)),
            AllowedTools = new HashSet<string>(allowedTools, StringComparer.Ordinal),
            FileRoots = roots,
            Paths = paths,
            ToolModes = toolModes ?? new Dictionary<string, ApprovalMode>(),
            Transport = transport,
            AllowMeteredBilling = allowMeteredBilling,
            ApprovalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(5),
            ExpiresAt = _time.GetUtcNow() + lifetime,
        };
        if (!_jobs.TryAdd(jobId, grant))
            throw new InvalidOperationException($"Job '{jobId}' already has a grant.");
        JobIssued?.Invoke(grant);
        return grant;
    }

    public bool TryAuthenticate(string jobId, string token, out JobGrant? grant)
    {
        grant = null;
        if (!_jobs.TryGetValue(jobId, out var candidate)) return false;
        if (_time.GetUtcNow() >= candidate.ExpiresAt)
        {
            Revoke(jobId);
            return false;
        }
        // Constant-time compare so the token cannot be recovered by timing.
        byte[] a = Encoding.ASCII.GetBytes(candidate.Token);
        byte[] b = Encoding.ASCII.GetBytes(token ?? "");
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) return false;
        grant = candidate;
        return true;
    }

    public bool TryGet(string jobId, out JobGrant? grant) => _jobs.TryGetValue(jobId, out grant);

    public IReadOnlyList<JobGrant> Active => _jobs.Values.ToList();

    public bool HasActive(BusTransport transport) => _jobs.Values.Any(g => g.Transport == transport && _time.GetUtcNow() < g.ExpiresAt);

    /// <summary>Revokes every expired grant (called periodically by the bus).</summary>
    public int RevokeExpired()
    {
        var now = _time.GetUtcNow();
        var expired = _jobs.Values.Where(g => now >= g.ExpiresAt).Select(g => g.JobId).ToList();
        foreach (var id in expired) Revoke(id);
        return expired.Count;
    }

    public void Revoke(string jobId)
    {
        if (_jobs.TryRemove(jobId, out _))
            JobRevoked?.Invoke(jobId);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
