using System.Collections.Concurrent;

namespace Appraan.Windows;

/// <summary>Immutable snapshot of per-category event counts.</summary>
/// <param name="Tcp">TCP/UDP events.</param>
/// <param name="File">File I/O events.</param>
/// <param name="Disk">Disk I/O events.</param>
/// <param name="Other">All other events.</param>
public readonly record struct EventCounts(long Tcp, long File, long Disk, long Other)
{
    /// <summary>Total events across all categories.</summary>
    public long Total => Tcp + File + Disk + Other;
}

/// <summary>
/// Thread-safe aggregator that counts <see cref="KernelEvent"/>s per process id
/// and <see cref="EventCategory"/>. Safe to feed from the ETW dispatch thread
/// while other threads take snapshots.
/// </summary>
public sealed class PidEventCounter
{
    private sealed class MutableCounts
    {
        public long Tcp;
        public long File;
        public long Disk;
        public long Other;

        public void Add(EventCategory category)
        {
            switch (category)
            {
                case EventCategory.Tcp: Interlocked.Increment(ref Tcp); break;
                case EventCategory.File: Interlocked.Increment(ref File); break;
                case EventCategory.Disk: Interlocked.Increment(ref Disk); break;
                default: Interlocked.Increment(ref Other); break;
            }
        }

        public EventCounts Snapshot() => new(
            Interlocked.Read(ref Tcp),
            Interlocked.Read(ref File),
            Interlocked.Read(ref Disk),
            Interlocked.Read(ref Other));
    }

    private readonly ConcurrentDictionary<int, MutableCounts> _byPid = new();
    private readonly MutableCounts _total = new();

    /// <summary>Record one event. Called from the ETW dispatch thread.</summary>
    public void Record(KernelEvent kernelEvent)
    {
        ArgumentNullException.ThrowIfNull(kernelEvent);
        _total.Add(kernelEvent.Category);
        _byPid.GetOrAdd(kernelEvent.ProcessId, static _ => new MutableCounts())
              .Add(kernelEvent.Category);
    }

    /// <summary>Counts for one process (zero counts if the pid was never seen).</summary>
    public EventCounts GetCounts(int processId) =>
        _byPid.TryGetValue(processId, out var counts) ? counts.Snapshot() : default;

    /// <summary>Counts across all processes.</summary>
    public EventCounts GetTotal() => _total.Snapshot();

    /// <summary>Point-in-time copy of the per-process table.</summary>
    public IReadOnlyDictionary<int, EventCounts> Snapshot()
    {
        var copy = new Dictionary<int, EventCounts>(_byPid.Count);
        foreach (var pair in _byPid)
            copy[pair.Key] = pair.Value.Snapshot();
        return copy;
    }
}
