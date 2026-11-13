namespace Appraan.Windows;

/// <summary>
/// Immutable per-category totals for one process: how many operations, and —
/// for the categories where Windows reports operation latency (File, Disk) —
/// how many of them have a measured latency, the summed/average/maximum
/// latency in microseconds, and the log-scale latency histogram
/// (<see cref="OpsLatencyHistogram"/>).
/// </summary>
/// <param name="Count">Operations observed in this category.</param>
/// <param name="LatencyCount">
/// Operations with a measured latency. Always ≤ <paramref name="Count"/>:
/// File operations whose completion event was never observed (trace stopped
/// first, completion dropped) count without a latency, and the Network /
/// ProcessThread / Registry categories report no latencies at all (0).
/// </param>
/// <param name="TotalLatencyUs">Sum of measured latencies, microseconds.</param>
/// <param name="MaxLatencyUs">Largest measured latency, microseconds (0 when none).</param>
/// <param name="LatencyHistogram">
/// Per-bucket counts of measured latencies, indexed per
/// <see cref="OpsLatencyHistogram.Labels"/>. Sums to
/// <paramref name="LatencyCount"/>.
/// </param>
public sealed record CategoryOpsStats(
    long Count,
    long LatencyCount,
    double TotalLatencyUs,
    double MaxLatencyUs,
    IReadOnlyList<long> LatencyHistogram)
{
    /// <summary>Mean measured latency in microseconds (0 when none measured).</summary>
    public double AverageLatencyUs => LatencyCount == 0 ? 0 : TotalLatencyUs / LatencyCount;

    /// <summary>Zeroed stats, for categories/processes never observed.</summary>
    public static CategoryOpsStats Empty { get; } =
        new(0, 0, 0, 0, new long[OpsLatencyHistogram.BucketCount]);
}

/// <summary>
/// Immutable snapshot of one process's operation statistics: one
/// <see cref="CategoryOpsStats"/> per <see cref="OpsCategory"/>, plus a
/// per-kind drill-down (how many reads vs writes vs creates within File,
/// connects vs sends within Network, ...) — the counts that make a run
/// checkable against a known workload.
/// </summary>
/// <param name="Categories">Per-category totals; every category is present (zeroed when unseen).</param>
/// <param name="KindCounts">
/// Operation counts by kind within category, keyed <c>"Category.Kind"</c>
/// (e.g. <c>File.Read</c>, <c>Network.TcpConnect</c>,
/// <c>ProcessThread.ContextSwitch</c>). Sums per category equal that
/// category's <see cref="CategoryOpsStats.Count"/>.
/// </param>
public sealed record OpsStats(
    IReadOnlyDictionary<OpsCategory, CategoryOpsStats> Categories,
    IReadOnlyDictionary<string, long> KindCounts)
{
    /// <summary>Totals for one category (zeroed when the process had none).</summary>
    public CategoryOpsStats For(OpsCategory category) =>
        Categories.TryGetValue(category, out var stats) ? stats : CategoryOpsStats.Empty;

    /// <summary>Stats for a process that was never observed.</summary>
    public static OpsStats Empty { get; } = new(
        new Dictionary<OpsCategory, CategoryOpsStats>(),
        new Dictionary<string, long>());
}

/// <summary>
/// Health counters for <see cref="OpsTracker"/>'s FileIO latency pairing.
/// Pairing kernel start/completion events is best-effort by nature (events
/// can be dropped under load, IRP addresses are reused); these counters make
/// the loss visible instead of silently skewing the latency numbers.
/// </summary>
/// <param name="PendingOperations">
/// File operations whose start was seen but whose completion has not (yet)
/// arrived. Should drain back towards 0 when the target goes idle.
/// </param>
/// <param name="UnmatchedCompletions">
/// Completion events (FileIO OperationEnd) with no matching start — the
/// operation began before the trace started, its start event was dropped,
/// or its pending entry was evicted.
/// </param>
/// <param name="DuplicateStarts">
/// Start events reusing an IRP address that was still pending (the earlier
/// operation's completion was evidently missed; the newer start wins).
/// </param>
/// <param name="EvictedPending">
/// Pending entries discarded without a completion, either because they aged
/// past the staleness horizon (10 s) or because the pending table hit its
/// size cap. A large number next to a small latency count means completions
/// are not being observed for those operations.
/// </param>
public sealed record OpsDiagnostics(
    int PendingOperations,
    long UnmatchedCompletions,
    long DuplicateStarts,
    long EvictedPending);
