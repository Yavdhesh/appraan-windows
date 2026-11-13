using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Appraan.Windows;

/// <summary>
/// Aggregates kernel operations per process by category, with latency where
/// Windows reports it — the data engine behind the <c>winops</c> CLI, i.e.
/// the parity-contract form of Linux <c>syscount</c> (DESIGN.md): what is
/// this process asking the kernel to do, how often, and how long does it
/// take. Feed it a session's kernel parser via <see cref="Attach"/>; read
/// results via <see cref="GetStats"/> / <see cref="Snapshot"/>.
///
/// <para><b>Counts</b> come from the typed kernel-parser events, one count
/// per user-visible operation: a FileIO create/read/write/close/... event is
/// one file operation, a DiskIO read/write/flush <i>completion</i> is one
/// disk operation (the matching init events are the same operations' start
/// halves and are deliberately not counted — unlike winscope, which counts
/// raw events), a TCP/UDP connect/send/recv/... is one network operation,
/// and process/thread starts/stops plus context switches make up
/// ProcessThread. A context switch is attributed to the process being
/// switched <i>in</i> (the payload's NewProcessID): "times this process was
/// scheduled onto a CPU".</para>
///
/// <para><b>File latency — IrpPtr pairing.</b> Verified by reflection
/// against the restored TraceEvent 3.1.8 package (not assumed): the FileIO
/// start events (opcode 64–75: create, read, write, set-info, delete,
/// rename, query-info, FS-control, dir-enum, cleanup, close, flush) carry
/// an <c>IrpPtr</c> but no elapsed time, while the completion event
/// <c>FileIOOperationEnd</c> (opcode 76, payload
/// <c>FileIOOpEndTraceData</c>) carries the same <c>IrpPtr</c> (plus the
/// operation's NtStatus). So a start event enters a pending table keyed by
/// IrpPtr, and its OperationEnd produces latency = end − start on ETW's
/// QPC-based relative clock. Latency is attributed to the process that
/// <i>started</i> the operation (stored in the pending entry), not to
/// whichever thread happened to complete the IRP. FileIO name/rundown
/// events (FileIOName, FileIOFileRundown) and the FileCreate/FileDelete
/// object notifications are not operations and are not counted; every other
/// IrpPtr-carrying start is paired, including Close/Cleanup.</para>
///
/// <para><b>Disk latency — embedded elapsed time.</b> Also reflection-
/// verified: <c>DiskIOTraceData</c> (the DiskIORead/DiskIOWrite completion
/// payload) and <c>DiskIOFlushBuffersTraceData</c> carry an
/// <c>ElapsedTimeMSec</c> property that TraceEvent computes from a
/// response-time field the kernel embeds in the completion event itself.
/// No pairing is needed (or possible — it is exact as reported).</para>
///
/// <para><b>Registry — counts only, opt-in.</b> Registry events live on the
/// same kernel parser (RegistryCreate/Open/QueryValue/... →
/// <see cref="RegistryTraceData"/>) and are enabled by the first-class NT
/// Kernel Logger keyword <c>Keywords.Registry</c> (0x00020000) in the same
/// session — see <see cref="KernelGroups.Registry"/>. This tracker always
/// subscribes; if the keyword was not enabled no events arrive and the
/// category stays zero. Key-control-block bookkeeping events (KCB create/
/// delete, rundown) are not process operations and are skipped.</para>
///
/// <para><b>Bounded memory.</b> The pending table is capped
/// (<see cref="MaxPendingOperations"/>) and entries older than the
/// staleness horizon (10 s of trace time) are evicted; both kinds of loss
/// increment <see cref="OpsDiagnostics.EvictedPending"/> rather than
/// silently distorting the latency stats. Completions with no observed
/// start increment <see cref="OpsDiagnostics.UnmatchedCompletions"/>, and a
/// start reusing a still-pending IRP address (its predecessor's completion
/// was evidently missed) replaces the entry and increments
/// <see cref="OpsDiagnostics.DuplicateStarts"/>.</para>
///
/// <para>Thread safety: all state is guarded by one lock; handlers run on
/// the ETW dispatch thread and snapshots may be taken from any thread. When
/// a process filter is set, other processes' events are dropped before the
/// lock is taken (except FileIO OperationEnd, whose pending-table lookup
/// must happen regardless of the completing thread's process).</para>
/// </summary>
public sealed class OpsTracker
{
    /// <summary>Upper bound on simultaneously pending (started, not yet completed) file operations.</summary>
    public const int MaxPendingOperations = 100_000;

    /// <summary>Pending file operations older than this (trace time) are evicted as stale.</summary>
    private const double StalePendingMs = 10_000;

    /// <summary>Mutable per-category accumulators for one process.</summary>
    private sealed class MutableCategory
    {
        public long Count;
        public long LatencyCount;
        public double TotalLatencyUs;
        public double MaxLatencyUs;
        public readonly long[] Histogram = new long[OpsLatencyHistogram.BucketCount];

        public CategoryOpsStats Snapshot() => new(
            Count, LatencyCount, TotalLatencyUs, MaxLatencyUs,
            (long[])Histogram.Clone());
    }

    /// <summary>Mutable accumulators for one process.</summary>
    private sealed class MutablePidStats
    {
        public readonly Dictionary<OpsCategory, MutableCategory> Categories = new();
        public readonly Dictionary<string, long> KindCounts = new(StringComparer.Ordinal);

        public MutableCategory For(OpsCategory category)
        {
            if (!Categories.TryGetValue(category, out var stats))
                Categories[category] = stats = new MutableCategory();
            return stats;
        }
    }

    /// <summary>One started-but-not-completed file operation, keyed by IRP address.</summary>
    private sealed class PendingOp
    {
        public required int ProcessId { get; init; }
        public required double StartRelativeMs { get; init; }
        public required long Sequence { get; init; }
    }

    private readonly int? _processIdFilter;
    private readonly object _gate = new();
    private readonly Dictionary<int, MutablePidStats> _byPid = new();
    private readonly Dictionary<ulong, PendingOp> _pending = new();
    private readonly Queue<(ulong Irp, long Sequence)> _pendingOrder = new();
    private long _nextSequence;
    private double _latestRelativeMs;
    private long _unmatchedCompletions;
    private long _duplicateStarts;
    private long _evictedPending;
    private int _attached;

    /// <param name="processIdFilter">
    /// When set, only this process's operations are aggregated; all other
    /// processes' events are ignored. Null aggregates every process.
    /// </param>
    public OpsTracker(int? processIdFilter = null) => _processIdFilter = processIdFilter;

    /// <summary>The process filter this tracker was constructed with, if any.</summary>
    public int? ProcessIdFilter => _processIdFilter;

    /// <summary>
    /// Subscribe this tracker to a kernel parser — typically
    /// <see cref="KernelSession.KernelParser"/> after
    /// <see cref="KernelSession.Start"/>. The session must have enabled the
    /// groups the caller wants counted (<see cref="KernelGroups.File"/>,
    /// Disk, Tcp, Process, Thread, and optionally Registry); events for
    /// groups that were not enabled simply never arrive. May only be
    /// called once.
    /// </summary>
    public void Attach(KernelTraceEventParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (Interlocked.Exchange(ref _attached, 1) != 0)
            throw new InvalidOperationException("This tracker is already attached to a parser.");

        // ---- File: starts (paired by IrpPtr) + the completion event ----
        parser.FileIOCreate += d => OnFileStart(d.ProcessID, "File.Create", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIORead += d => OnFileStart(d.ProcessID, "File.Read", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOWrite += d => OnFileStart(d.ProcessID, "File.Write", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOCleanup += d => OnFileStart(d.ProcessID, "File.Cleanup", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOClose += d => OnFileStart(d.ProcessID, "File.Close", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOFlush += d => OnFileStart(d.ProcessID, "File.Flush", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOSetInfo += d => OnFileStart(d.ProcessID, "File.SetInfo", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIODelete += d => OnFileStart(d.ProcessID, "File.Delete", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIORename += d => OnFileStart(d.ProcessID, "File.Rename", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOQueryInfo += d => OnFileStart(d.ProcessID, "File.QueryInfo", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOFSControl += d => OnFileStart(d.ProcessID, "File.FSControl", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIODirEnum += d => OnFileStart(d.ProcessID, "File.DirEnum", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIODirNotify += d => OnFileStart(d.ProcessID, "File.DirNotify", d.IrpPtr, d.TimeStampRelativeMSec);
        parser.FileIOOperationEnd += d => OnFileOperationEnd(d.IrpPtr, d.ProcessID, d.TimeStampRelativeMSec);
        // Name/rundown and file-object notifications are not operations:
        // FileIOName, FileIOFileRundown, FileIOFileCreate, FileIOFileDelete
        // are deliberately not subscribed (see class remarks).
        parser.FileIOMapFile += d => OnCount(d.ProcessID, OpsCategory.File, "File.MapFile");
        parser.FileIOUnmapFile += d => OnCount(d.ProcessID, OpsCategory.File, "File.UnmapFile");

        // ---- Disk: completions carry their own elapsed time ----
        parser.DiskIORead += d => OnDiskCompletion(d.ProcessID, "Disk.Read", d.ElapsedTimeMSec);
        parser.DiskIOWrite += d => OnDiskCompletion(d.ProcessID, "Disk.Write", d.ElapsedTimeMSec);
        parser.DiskIOFlushBuffers += d => OnDiskCompletion(d.ProcessID, "Disk.Flush", d.ElapsedTimeMSec);
        // DiskIOReadInit / DiskIOWriteInit / DiskIOFlushInit are the start
        // halves of the operations above and are deliberately not counted.

        // ---- Network: TCP + UDP operation events ----
        parser.TcpIpConnect += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpConnect");
        parser.TcpIpAccept += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpAccept");
        parser.TcpIpSend += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpSend");
        parser.TcpIpRecv += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpRecv");
        parser.TcpIpDisconnect += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpDisconnect");
        parser.TcpIpReconnect += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpReconnect");
        parser.TcpIpRetransmit += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpRetransmit");
        parser.TcpIpFail += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpFail");
        parser.TcpIpConnectIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpConnect");
        parser.TcpIpAcceptIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpAccept");
        parser.TcpIpSendIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpSend");
        parser.TcpIpRecvIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpRecv");
        parser.TcpIpDisconnectIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.TcpDisconnect");
        parser.UdpIpSend += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.UdpSend");
        parser.UdpIpRecv += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.UdpRecv");
        parser.UdpIpFail += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.UdpFail");
        parser.UdpIpSendIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.UdpSend");
        parser.UdpIpRecvIPV6 += d => OnCount(d.ProcessID, OpsCategory.Network, "Network.UdpRecv");

        // ---- Process / thread lifecycle + scheduling ----
        parser.ProcessStart += d => OnCount(d.ProcessID, OpsCategory.ProcessThread, "ProcessThread.ProcessStart");
        parser.ProcessStop += d => OnCount(d.ProcessID, OpsCategory.ProcessThread, "ProcessThread.ProcessStop");
        parser.ThreadStart += d => OnCount(d.ProcessID, OpsCategory.ProcessThread, "ProcessThread.ThreadStart");
        parser.ThreadStop += d => OnCount(d.ProcessID, OpsCategory.ProcessThread, "ProcessThread.ThreadStop");
        // A context switch is attributed to the process being switched IN.
        parser.ThreadCSwitch += d => OnCount(d.NewProcessID, OpsCategory.ProcessThread, "ProcessThread.ContextSwitch");

        // ---- Registry: counts only; fires only when the session enabled
        // ---- KernelGroups.Registry. KCB/rundown events are bookkeeping,
        // ---- not process operations, and are deliberately not subscribed.
        parser.RegistryCreate += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Create");
        parser.RegistryOpen += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Open");
        parser.RegistryDelete += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Delete");
        parser.RegistryDeleteValue += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.DeleteValue");
        parser.RegistryEnumerateKey += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.EnumerateKey");
        parser.RegistryEnumerateValueKey += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.EnumerateValueKey");
        parser.RegistryFlush += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Flush");
        parser.RegistryQuery += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Query");
        parser.RegistryQueryMultipleValue += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.QueryMultipleValue");
        parser.RegistryQueryValue += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.QueryValue");
        parser.RegistrySetInformation += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.SetInformation");
        parser.RegistrySetValue += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.SetValue");
        parser.RegistryClose += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Close");
        parser.RegistryVirtualize += d => OnCount(d.ProcessID, OpsCategory.Registry, "Registry.Virtualize");
    }

    /// <summary>Statistics for one process (zeroed stats if it was never observed).</summary>
    public OpsStats GetStats(int processId)
    {
        lock (_gate)
        {
            SweepPending();
            return _byPid.TryGetValue(processId, out var stats)
                ? SnapshotOf(stats)
                : OpsStats.Empty;
        }
    }

    /// <summary>Point-in-time copy of the per-process statistics table.</summary>
    public IReadOnlyDictionary<int, OpsStats> Snapshot()
    {
        lock (_gate)
        {
            SweepPending();
            var copy = new Dictionary<int, OpsStats>(_byPid.Count);
            foreach (var pair in _byPid)
                copy[pair.Key] = SnapshotOf(pair.Value);
            return copy;
        }
    }

    /// <summary>Current health counters for the FileIO latency pairing.</summary>
    public OpsDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            SweepPending();
            return new OpsDiagnostics(
                _pending.Count, _unmatchedCompletions, _duplicateStarts, _evictedPending);
        }
    }

    // ---- event handlers (filter first, then one lock for all state) ----

    private bool PassesFilter(int processId) =>
        _processIdFilter is not int filter || processId == filter;

    private void OnCount(int processId, OpsCategory category, string kind)
    {
        if (!PassesFilter(processId))
            return;
        lock (_gate)
            CountLocked(processId, category, kind);
    }

    private void OnFileStart(int processId, string kind, ulong irpPtr, double relativeMs)
    {
        if (!PassesFilter(processId))
            return;
        lock (_gate)
        {
            TouchTimeLocked(relativeMs);
            CountLocked(processId, OpsCategory.File, kind);

            if (irpPtr == 0)
                return; // No identity to pair a completion against; counted only.

            if (_pending.ContainsKey(irpPtr))
                _duplicateStarts++; // IRP address reused before its completion was seen; the newer start wins.
            var entry = new PendingOp
            {
                ProcessId = processId,
                StartRelativeMs = relativeMs,
                Sequence = ++_nextSequence,
            };
            _pending[irpPtr] = entry;
            _pendingOrder.Enqueue((irpPtr, entry.Sequence));
            SweepPending();
        }
    }

    private void OnFileOperationEnd(ulong irpPtr, int completingProcessId, double relativeMs)
    {
        // Deliberately NOT filtered by the completing process: an IRP may be
        // completed on another process's thread. The owner recorded at
        // start time decides attribution (and whether the filter keeps it).
        lock (_gate)
        {
            TouchTimeLocked(relativeMs);
            if (irpPtr != 0 && _pending.Remove(irpPtr, out var start))
            {
                double latencyUs = Math.Max(0, (relativeMs - start.StartRelativeMs) * 1000.0);
                RecordLatencyLocked(start.ProcessId, OpsCategory.File, latencyUs);
            }
            else if (_processIdFilter is not int filter || completingProcessId == filter)
            {
                _unmatchedCompletions++;
            }
        }
    }

    private void OnDiskCompletion(int processId, string kind, double elapsedTimeMSec)
    {
        if (!PassesFilter(processId))
            return;
        lock (_gate)
        {
            CountLocked(processId, OpsCategory.Disk, kind);
            RecordLatencyLocked(processId, OpsCategory.Disk, Math.Max(0, elapsedTimeMSec * 1000.0));
        }
    }

    // ---- aggregation primitives (caller must hold _gate) ----

    private void CountLocked(int processId, OpsCategory category, string kind)
    {
        var stats = GetOrAddLocked(processId);
        stats.For(category).Count++;
        stats.KindCounts[kind] = stats.KindCounts.GetValueOrDefault(kind) + 1;
    }

    private void RecordLatencyLocked(int processId, OpsCategory category, double latencyUs)
    {
        var stats = GetOrAddLocked(processId).For(category);
        stats.LatencyCount++;
        stats.TotalLatencyUs += latencyUs;
        if (latencyUs > stats.MaxLatencyUs)
            stats.MaxLatencyUs = latencyUs;
        stats.Histogram[OpsLatencyHistogram.BucketIndex(latencyUs)]++;
    }

    private MutablePidStats GetOrAddLocked(int processId)
    {
        if (!_byPid.TryGetValue(processId, out var stats))
            _byPid[processId] = stats = new MutablePidStats();
        return stats;
    }

    private static OpsStats SnapshotOf(MutablePidStats stats)
    {
        var categories = new Dictionary<OpsCategory, CategoryOpsStats>(stats.Categories.Count);
        foreach (var pair in stats.Categories)
            categories[pair.Key] = pair.Value.Snapshot();
        return new OpsStats(categories, new Dictionary<string, long>(stats.KindCounts));
    }

    // ---- pending-table maintenance (caller must hold _gate) ----

    private void TouchTimeLocked(double relativeMs)
    {
        if (relativeMs > _latestRelativeMs)
            _latestRelativeMs = relativeMs;
    }

    /// <summary>
    /// Discard pending entries that can no longer pair: replaced by a newer
    /// start (stale queue nodes — dropped silently), older than the
    /// staleness horizon, or beyond the size cap (both counted as
    /// evictions). The queue is in insertion order, which for a real-time
    /// session tracks event time closely enough for a 10 s horizon.
    /// </summary>
    private void SweepPending()
    {
        while (_pendingOrder.TryPeek(out var front))
        {
            if (!_pending.TryGetValue(front.Irp, out var entry) || entry.Sequence != front.Sequence)
            {
                _pendingOrder.Dequeue(); // Already completed or replaced.
                continue;
            }
            bool stale = _latestRelativeMs - entry.StartRelativeMs > StalePendingMs;
            bool overCap = _pending.Count > MaxPendingOperations;
            if (!stale && !overCap)
                break;
            _pendingOrder.Dequeue();
            _pending.Remove(front.Irp);
            _evictedPending++;
        }
    }
}
