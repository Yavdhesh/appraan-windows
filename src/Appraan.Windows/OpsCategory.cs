namespace Appraan.Windows;

/// <summary>
/// A category of kernel operations as aggregated by <see cref="OpsTracker"/> —
/// the per-process "what is this process asking the kernel to do" view from
/// DESIGN.md's parity contract (the cross-platform form of Linux
/// <c>syscount</c>: operations grouped by MEANING, with latency where the OS
/// reports it). Deliberately separate from <see cref="EventCategory"/>, the
/// coarser step-1 event buckets, so the normalised winscope model is
/// untouched.
/// </summary>
public enum OpsCategory
{
    /// <summary>File-system operations (creates, reads, writes, closes, renames, ...).</summary>
    File,

    /// <summary>Physical disk operations (reads, writes, flushes that reached a device).</summary>
    Disk,

    /// <summary>TCP/UDP network operations (connects, accepts, sends, receives, disconnects).</summary>
    Network,

    /// <summary>Process/thread lifecycle and scheduling (starts, stops, context switches).</summary>
    ProcessThread,

    /// <summary>
    /// Registry operations (counts only). Only populated when the session
    /// enabled <see cref="KernelGroups.Registry"/> — see
    /// <see cref="OpsTracker"/>.
    /// </summary>
    Registry,
}

/// <summary>
/// Log-scale latency histogram used for the File and Disk categories.
/// Buckets are upper bounds in microseconds; the last bucket catches
/// everything at or above the largest bound. Log scale because kernel-op
/// latencies span cached-IO microseconds to device milliseconds — linear
/// buckets would either drown the fast path or clip the slow tail.
/// </summary>
public static class OpsLatencyHistogram
{
    /// <summary>Upper bound of each bucket except the last, in microseconds.</summary>
    public static IReadOnlyList<double> UpperBoundsUs { get; } =
        new double[] { 10, 50, 100, 500, 1_000, 5_000, 10_000, 50_000 };

    /// <summary>Display label of every bucket, in order.</summary>
    public static IReadOnlyList<string> Labels { get; } =
        new[] { "<10us", "<50us", "<100us", "<500us", "<1ms", "<5ms", "<10ms", "<50ms", ">=50ms" };

    /// <summary>Number of buckets (upper bounds + the catch-all tail).</summary>
    public static int BucketCount => Labels.Count;

    /// <summary>Index of the bucket a latency in microseconds falls into.</summary>
    public static int BucketIndex(double latencyUs)
    {
        var bounds = UpperBoundsUs;
        for (int i = 0; i < bounds.Count; i++)
        {
            if (latencyUs < bounds[i])
                return i;
        }
        return bounds.Count; // catch-all tail bucket
    }
}
