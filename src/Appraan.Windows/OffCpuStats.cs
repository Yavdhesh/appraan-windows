namespace Appraan.Windows;

/// <summary>
/// Immutable off-CPU / on-CPU totals for one thread of the tracked process,
/// as accumulated by <see cref="OffCpuTracker"/>.
/// </summary>
/// <param name="ThreadId">The thread's OS thread id (TID).</param>
/// <param name="OnCpuMs">
/// Milliseconds the thread was observed running on a CPU (switch-in →
/// switch-out intervals, plus the in-progress interval up to the snapshot
/// instant for a thread currently running).
/// </param>
/// <param name="OffCpuMs">
/// Milliseconds the thread was observed off-CPU <i>after having run</i>
/// (switch-out → switch-in intervals). The in-progress episode of a thread
/// currently off-CPU is included, accrued up to the snapshot instant.
/// Time before the thread's first observed switch-in does not count: a
/// thread's life before it first runs is not off-CPU time in the
/// offcputime sense.
/// </param>
/// <param name="OffCpuEpisodes">
/// Off-CPU episodes contributing to <paramref name="OffCpuMs"/>. A thread
/// currently off-CPU counts its in-progress episode as one.
/// </param>
/// <param name="MaxOffCpuEpisodeMs">Longest single off-CPU episode, milliseconds.</param>
/// <param name="Partial">
/// True when the thread was already alive when the trace started (or its
/// ThreadStart was otherwise missed): its totals cover only the observed
/// window, not its whole life.
/// </param>
public sealed record ThreadOffCpuStats(
    int ThreadId,
    double OnCpuMs,
    double OffCpuMs,
    long OffCpuEpisodes,
    double MaxOffCpuEpisodeMs,
    bool Partial)
{
    /// <summary>On-CPU + off-CPU milliseconds observed for this thread.</summary>
    public double TotalMs => OnCpuMs + OffCpuMs;

    /// <summary>Off-CPU share of observed time, 0–100 (0 when nothing observed).</summary>
    public double OffCpuPercent => TotalMs <= 0 ? 0 : 100.0 * OffCpuMs / TotalMs;
}

/// <summary>
/// Off-CPU time grouped by why the thread left the CPU. The reason names
/// are the <c>System.Diagnostics.ThreadWaitReason</c> enum names reported
/// by the kernel in the context-switch event; values the enum does not
/// name are mapped through the NT KWAIT_REASON table by
/// <see cref="OffCpuTracker"/> (WrQueue, WrPushLock, ...), and values
/// beyond that table appear as <c>WaitReason(&lt;n&gt;)</c>. Two further
/// labels are produced by the tracker itself: <c>Preempted</c> (the
/// thread was switched out while still runnable — scheduling delay, not
/// a block) and the thread-state name itself for the rare switch-outs in
/// states other than Wait/Ready.
/// </summary>
/// <param name="Reason">Wait-reason (or switch-out classification) name.</param>
/// <param name="Episodes">Off-CPU episodes attributed to this reason.</param>
/// <param name="TotalMs">Summed off-CPU milliseconds for this reason.</param>
public sealed record WaitReasonStats(
    string Reason,
    long Episodes,
    double TotalMs);

/// <summary>
/// Immutable snapshot of one process's off-CPU profile: process totals,
/// the wait-reason breakdown, and per-thread detail (sorted by off-CPU
/// time, descending). Produced by <see cref="OffCpuTracker.GetStats"/>.
/// </summary>
/// <param name="ProcessId">The tracked process id.</param>
/// <param name="OnCpuMs">Summed on-CPU milliseconds over the process's threads.</param>
/// <param name="OffCpuMs">Summed off-CPU milliseconds over the process's threads.</param>
/// <param name="OffCpuEpisodes">Total off-CPU episodes over all threads.</param>
/// <param name="MaxOffCpuEpisodeMs">Longest single off-CPU episode in the process, milliseconds.</param>
/// <param name="WaitReasons">Per-reason totals, sorted by total time descending.</param>
/// <param name="Threads">Per-thread totals, sorted by off-CPU time descending.</param>
public sealed record ProcessOffCpuStats(
    int ProcessId,
    double OnCpuMs,
    double OffCpuMs,
    long OffCpuEpisodes,
    double MaxOffCpuEpisodeMs,
    IReadOnlyList<WaitReasonStats> WaitReasons,
    IReadOnlyList<ThreadOffCpuStats> Threads)
{
    /// <summary>On-CPU + off-CPU milliseconds observed for the process.</summary>
    public double TotalMs => OnCpuMs + OffCpuMs;

    /// <summary>
    /// Off-CPU share of observed thread-time, 0–100. Note the denominator
    /// is summed thread-time, not wall-clock: a process with many idle
    /// threads legitimately sits near 100%.
    /// </summary>
    public double OffCpuPercent => TotalMs <= 0 ? 0 : 100.0 * OffCpuMs / TotalMs;

    /// <summary>Zeroed stats for a process with no observed threads.</summary>
    public static ProcessOffCpuStats Empty(int processId) => new(
        processId, 0, 0, 0, 0,
        Array.Empty<WaitReasonStats>(), Array.Empty<ThreadOffCpuStats>());
}

/// <summary>
/// Health counters for <see cref="OffCpuTracker"/>. Context-switch based
/// accounting is best-effort at the edges of the observation window
/// (threads already alive at trace start, exits whose stop event races
/// the trace end); these counters make those edges visible instead of
/// silently skewing the percentages.
/// </summary>
/// <param name="ThreadsObserved">
/// Distinct thread ids of the target process seen in any subscribed event
/// (context switches, ThreadStart, ThreadStop).
/// </param>
/// <param name="PartialThreads">
/// Threads whose ThreadStart was not observed — they were already alive
/// when the trace started, so their totals cover only part of their life.
/// </param>
/// <param name="EpisodesClosed">
/// Off-CPU episodes closed by a switch-in or by a thread/process exit.
/// (Episodes still in progress are not in this count; snapshots accrue
/// them provisionally into the totals.)
/// </param>
/// <param name="EpisodesClosedByThreadExit">
/// Subset of <paramref name="EpisodesClosed"/> closed by ThreadStop /
/// ProcessStop rather than by the thread being scheduled again.
/// </param>
/// <param name="SwitchInsWithoutEpisode">
/// Switch-ins for a thread with no open off-CPU episode and no other
/// benign explanation (first sighting, born-in-window): the matching
/// switch-out was evidently missed (dropped event).
/// </param>
/// <param name="SwitchOutsWithoutOnCpu">
/// Switch-outs for a thread not tracked as on-CPU and not a first
/// sighting: the matching switch-in was evidently missed.
/// </param>
/// <param name="OpenOffCpuThreads">
/// Threads currently inside an off-CPU episode (accrued provisionally
/// into snapshots, not yet closed).
/// </param>
/// <param name="OpenOnCpuThreads">Threads currently running on a CPU.</param>
public sealed record OffCpuDiagnostics(
    int ThreadsObserved,
    int PartialThreads,
    long EpisodesClosed,
    long EpisodesClosedByThreadExit,
    long SwitchInsWithoutEpisode,
    long SwitchOutsWithoutOnCpu,
    int OpenOffCpuThreads,
    int OpenOnCpuThreads);
