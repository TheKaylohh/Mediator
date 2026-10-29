namespace Cockpit.Core.Bus;

/// <summary>
/// Exclusive leases on single-instance apps (SolidWorks, Resolve, an interactive Blender session).
/// A job keeps its lease until the job ends, so two jobs never interleave edits in the same app.
/// </summary>
public sealed class LeaseManager
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Acquires (or re-confirms) the lease. Returns false with the current holder if another job has it.</summary>
    public bool TryAcquire(string resource, string jobId, out string? holder)
    {
        lock (_lock)
        {
            if (_owners.TryGetValue(resource, out var owner) && owner != jobId)
            {
                holder = owner;
                return false;
            }
            _owners[resource] = jobId;
            holder = jobId;
            return true;
        }
    }

    public void ReleaseAll(string jobId)
    {
        lock (_lock)
        {
            foreach (var key in _owners.Where(kv => kv.Value == jobId).Select(kv => kv.Key).ToList())
                _owners.Remove(key);
        }
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock) return new Dictionary<string, string>(_owners, StringComparer.OrdinalIgnoreCase);
    }
}
