using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
// The CSwitch payload's enums, aliased: System.Threading also defines a
// ThreadState, which would make the unqualified names ambiguous.
using ThreadState = System.Diagnostics.ThreadState;
using ThreadWaitReason = System.Diagnostics.ThreadWaitReason;

namespace Appraan.Windows;

/// <summary>
/// Off-CPU analysis for one process — the data engine behind the
/// <c>winoffcpu</c> CLI, i.e. the Windows form of BCC's <c>offcputime</c>
/// from DESIGN.md, minus stack capture (stacks are a later step; the wait
/// reason reported by the kernel stands in for "why was it off-CPU").
/// Feed it a session's kernel parser via <see cref="Attach"/>; read
/// results via <see cref="GetStats"/> / <see cref="GetDiagnostics"/>.
///
/// <para><b>Source — ETW context switches, payload-verified.</b> The only
/// input is <c>KernelTraceEventParser.ThreadCSwitch</c> (plus ThreadStart /
/// ThreadStop / ProcessStop for lifecycle edges). The payload type
/// <c>CSwitchTraceData</c> was verified by reflection against the restored
/// TraceEvent 3.1.8 package (not from memory): it carries explicit
/// <c>OldThreadID</c>/<c>OldProcessID</c>/<c>OldProcessName</c> and
/// <c>NewThreadID</c>/<c>NewProcessID</c>/<c>NewProcessName</c>, the
/// outgoing thread's <c>OldThreadWaitReason</c> (a
/// <c>System.Diagnostics.ThreadWaitReason</c>: Executive, FreePage, PageIn,
/// SystemAllocation, ExecutionDelay, Suspended, UserRequest, EventPairHigh,
/// EventPairLow, LpcReceive, LpcReply, VirtualMemory, PageOut, Unknown) and
/// its <c>OldThreadState</c> (a <c>System.Diagnostics.ThreadState</c>:
/// Initialized, Ready, Running, Standby, Terminated, Wait, Transition,
/// Unknown). It also declares its <i>own</i> <c>ProcessID</c> property —
/// one more reason this tracker never attributes by the event header:
/// <b>attribution uses only the payload's Old/New process ids</b>. The
/// outgoing thread is the one named by Old*, the incoming by New*.
/// ThreadStart/ThreadStop are the exception: they carry no old/new pair,
/// so the header ProcessID/ThreadID (which for kernel thread lifecycle
/// events identify the thread being created/destroyed) are used, as in
/// <see cref="OpsTracker"/>.</para>
///
/// <para><b>State machine (per thread).</b> A switch-out (the thread is
/// the event's Old*) closes its on-CPU interval and opens an off-CPU
/// episode stamped with the switch-out time and a reason label; a
/// switch-in (the thread is New*) closes the episode — duration in −
/// out — and opens an on-CPU interval. Symmetric accounting gives every
/// thread an off-CPU share, not just a total. A switch where the same
/// thread is both Old and New is ignored (it never left the CPU).</para>
///
/// <para><b>Reason labels.</b> An episode's label is fixed at switch-out:
/// if the outgoing thread entered a wait (OldThreadState = Wait), the
/// label is the kernel's wait reason (Executive, UserRequest, ...). The
/// TraceEvent enum names only the classic subset, so values it leaves
/// unnamed are mapped through the full NT KWAIT_REASON table (WrQueue,
/// WrLpcReply, ...) and anything beyond the table falls back to
/// <c>WaitReason(&lt;n&gt;)</c> — see <c>WaitReasonLabel</c>. If it
/// was switched out while still runnable (state = Ready) it did not
/// block — it lost the CPU — and the label is <c>Preempted</c>: in
/// off-CPU terms that is scheduling delay, and folding it into a wait
/// reason would fabricate a block that never happened. Switch-outs in any
/// other state (rare) are labelled with the state name itself.</para>
///
/// <para><b>Window edges.</b> A thread first observed mid-life (its
/// ThreadStart was not seen — normally because it predates the trace) is
/// marked <c>Partial</c>: if first seen switching out, the unknown
/// preceding on-CPU time is simply not counted; if first seen switching
/// in, no off-CPU episode is invented for the unobserved past. A thread
/// born in-window (ThreadStart seen) starts clean: it is neither on- nor
/// off-CPU until its first switch-in. ThreadStop closes a thread's open
/// interval at the stop timestamp (an off-CPU episode closed this way is
/// real blocked time — the thread waited until it died) and ProcessStop
/// does the same for every thread of the process; exited threads keep
/// contributing their final totals to snapshots. Missed-event edges (a
/// switch-in with no open episode, a switch-out while not on-CPU) are
/// counted in <see cref="OffCpuDiagnostics"/> rather than papered over.</para>
///
/// <para><b>Snapshots.</b> Totals are accrued provisionally up to the
/// newest context-switch timestamp observed anywhere on the machine
/// (context switches fire constantly, so that instant tracks "now" in
/// trace time): a thread currently off-CPU contributes its in-progress
/// episode, a thread currently running its in-progress on-CPU interval.
/// Snapshots never mutate the tracked state. One timestamp caveat: all
/// durations use ETW's QPC-based relative clock
/// (<c>TimeStampRelativeMSec</c>), the same clock <see cref="OpsTracker"/>
/// latency pairing uses.</para>
///
/// <para><b>Cost and bounds.</b> Context switches are the highest-volume
/// kernel event, so the handler checks the payload process ids before
/// taking any lock and drops other processes' switches immediately. State
/// is one small record per live thread of the target process, plus one
/// immutable record per exited thread (bounded by the target's thread
/// churn over the trace).</para>
///
/// <para>Thread safety: handlers run on the ETW dispatch thread; all
/// mutable state is guarded by one lock, and snapshots may be taken from
/// any thread.</para>
/// </summary>
public sealed class OffCpuTracker
{
    /// <summary>Per-thread mutable accounting state.</summary>
    private sealed class MutableThread
    {
        public bool Partial;
        public bool BornInWindow;
        public bool EverScheduled;
        public bool OnCpu;
        public double OnCpuStartMs;
        public bool OffCpu;
        public double OffCpuStartMs;
        public string OffCpuReason = "";
        public double OnCpuMs;
        public double OffCpuMs;
        public long Episodes;
        public double MaxEpisodeMs;
    }

    private readonly int _processId;
    private readonly object _gate = new();
    private readonly Dictionary<int, MutableThread> _threads = new();
    private readonly List<ThreadOffCpuStats> _exitedThreads = new();
    private readonly HashSet<int> _seenThreadIds = new();
    private readonly HashSet<int> _partialThreadIds = new();
    private readonly Dictionary<string, (long Episodes, double TotalMs)> _reasonTotals = new(StringComparer.Ordinal);
    private long _episodesClosed;
    private long _episodesClosedByExit;
    private long _switchInsWithoutEpisode;
    private long _switchOutsWithoutOnCpu;
    private long _latestRelativeMsBits; // double, via BitConverter — see TouchTime
    private int _attached;

    /// <param name="processId">The single process to profile.</param>
    public OffCpuTracker(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        _processId = processId;
    }

    /// <summary>The process this tracker profiles.</summary>
    public int ProcessId => _processId;

    /// <summary>
    /// Subscribe this tracker to a kernel parser — typically
    /// <see cref="KernelSession.KernelParser"/> after
    /// <see cref="KernelSession.Start"/>. The session must have enabled
    /// <see cref="KernelGroups.Thread"/> (thread lifecycle + the
    /// ContextSwitch keyword); <see cref="KernelGroups.Process"/> is
    /// needed for ProcessStop to close out a dying process's threads
    /// (without it, ThreadStop still closes threads individually).
    /// May only be called once.
    /// </summary>
    public void Attach(KernelTraceEventParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (Interlocked.Exchange(ref _attached, 1) != 0)
            throw new InvalidOperationException("This tracker is already attached to a parser.");

        parser.ThreadCSwitch += OnCSwitch;
        parser.ThreadStart += d => OnThreadStart(d.ProcessID, d.ThreadID, d.TimeStampRelativeMSec);
        parser.ThreadStop += d => OnThreadStop(d.ProcessID, d.ThreadID, d.TimeStampRelativeMSec);
        parser.ProcessStop += d => OnProcessStop(d.ProcessID, d.TimeStampRelativeMSec);
    }

    /// <summary>
    /// Point-in-time profile of the tracked process, with in-progress
    /// intervals accrued up to the newest observed trace timestamp.
    /// </summary>
    public ProcessOffCpuStats GetStats()
    {
        lock (_gate)
        {
            double now = LatestRelativeMs;
            var threads = new List<ThreadOffCpuStats>(_threads.Count + _exitedThreads.Count);
            threads.AddRange(_exitedThreads);

            // Provisional accrual for episodes still in progress starts
            // from the closed-episode reason totals.
            var reasons = new Dictionary<string, (long Episodes, double TotalMs)>(_reasonTotals);

            foreach (var (threadId, thread) in _threads)
            {
                double onCpuMs = thread.OnCpuMs;
                double offCpuMs = thread.OffCpuMs;
                long episodes = thread.Episodes;
                double maxEpisodeMs = thread.MaxEpisodeMs;

                if (thread.OnCpu)
                    onCpuMs += Math.Max(0, now - thread.OnCpuStartMs);
                if (thread.OffCpu)
                {
                    double provisionalMs = Math.Max(0, now - thread.OffCpuStartMs);
                    offCpuMs += provisionalMs;
                    episodes++;
                    if (provisionalMs > maxEpisodeMs)
                        maxEpisodeMs = provisionalMs;
                    var totals = reasons.GetValueOrDefault(thread.OffCpuReason);
                    reasons[thread.OffCpuReason] = (totals.Episodes + 1, totals.TotalMs + provisionalMs);
                }

                threads.Add(new ThreadOffCpuStats(
                    threadId, onCpuMs, offCpuMs, episodes, maxEpisodeMs, thread.Partial));
            }

            threads.Sort((a, b) => b.OffCpuMs.CompareTo(a.OffCpuMs));

            double totalOnCpuMs = 0, totalOffCpuMs = 0, maxOffCpuEpisodeMs = 0;
            long totalEpisodes = 0;
            foreach (var thread in threads)
            {
                totalOnCpuMs += thread.OnCpuMs;
                totalOffCpuMs += thread.OffCpuMs;
                totalEpisodes += thread.OffCpuEpisodes;
                if (thread.MaxOffCpuEpisodeMs > maxOffCpuEpisodeMs)
                    maxOffCpuEpisodeMs = thread.MaxOffCpuEpisodeMs;
            }

            var reasonStats = reasons
                .Select(pair => new WaitReasonStats(pair.Key, pair.Value.Episodes, pair.Value.TotalMs))
                .OrderByDescending(r => r.TotalMs)
                .ToList();

            return new ProcessOffCpuStats(
                _processId, totalOnCpuMs, totalOffCpuMs, totalEpisodes,
                maxOffCpuEpisodeMs, reasonStats, threads);
        }
    }

    /// <summary>Current health counters (see <see cref="OffCpuDiagnostics"/>).</summary>
    public OffCpuDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            int openOffCpu = 0, openOnCpu = 0;
            foreach (var thread in _threads.Values)
            {
                if (thread.OffCpu)
                    openOffCpu++;
                if (thread.OnCpu)
                    openOnCpu++;
            }
            return new OffCpuDiagnostics(
                _seenThreadIds.Count,
                _partialThreadIds.Count,
                _episodesClosed,
                _episodesClosedByExit,
                _switchInsWithoutEpisode,
                _switchOutsWithoutOnCpu,
                openOffCpu,
                openOnCpu);
        }
    }

    // ---- event handlers ----

    private void OnCSwitch(CSwitchTraceData data)
    {
        // Context switches fire system-wide at a very high rate: update the
        // "now" watermark and bail out BEFORE locking unless the target
        // process is one of the two parties.
        TouchTime(data.TimeStampRelativeMSec);
        bool oldIsTarget = data.OldProcessID == _processId;
        bool newIsTarget = data.NewProcessID == _processId;
        if (!oldIsTarget && !newIsTarget)
            return;
        if (oldIsTarget && newIsTarget && data.OldThreadID == data.NewThreadID)
            return; // Self-switch: the thread never left the CPU.

        lock (_gate)
        {
            if (oldIsTarget)
                OnSwitchOutLocked(data.OldThreadID, data.TimeStampRelativeMSec,
                                  data.OldThreadState, data.OldThreadWaitReason);
            if (newIsTarget)
                OnSwitchInLocked(data.NewThreadID, data.TimeStampRelativeMSec);
        }
    }

    private void OnThreadStart(int processId, int threadId, double relativeMs)
    {
        if (processId != _processId)
            return;
        TouchTime(relativeMs);
        lock (_gate)
        {
            var thread = GetOrAddLocked(threadId, out bool created);
            if (created)
                thread.BornInWindow = true; // Known from birth: not partial.
        }
    }

    private void OnThreadStop(int processId, int threadId, double relativeMs)
    {
        if (processId != _processId)
            return;
        TouchTime(relativeMs);
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var thread))
                return;
            CloseThreadLocked(threadId, thread, relativeMs);
        }
    }

    private void OnProcessStop(int processId, double relativeMs)
    {
        if (processId != _processId)
            return;
        TouchTime(relativeMs);
        lock (_gate)
        {
            // Copy the keys: CloseThreadLocked removes from _threads.
            foreach (int threadId in _threads.Keys.ToList())
                CloseThreadLocked(threadId, _threads[threadId], relativeMs);
        }
    }

    // ---- state machine (caller must hold _gate) ----

    private void OnSwitchOutLocked(int threadId, double relativeMs, ThreadState state, ThreadWaitReason reason)
    {
        var thread = GetOrAddLocked(threadId, out bool created);
        if (created)
            MarkPartialLocked(threadId, thread);

        if (thread.OnCpu)
        {
            thread.OnCpuMs += Math.Max(0, relativeMs - thread.OnCpuStartMs);
            thread.OnCpu = false;
        }
        else if (!created && thread.EverScheduled)
        {
            // Tracked, has run before, yet was not on-CPU: the switch-in
            // that put it there was evidently missed.
            _switchOutsWithoutOnCpu++;
        }
        // First sighting switching out: it was running when the trace
        // started; the unknown on-CPU interval is simply not counted.

        thread.EverScheduled = true;
        thread.OffCpu = true;
        thread.OffCpuStartMs = relativeMs;
        thread.OffCpuReason = ReasonLabel(state, reason);
    }

    private void OnSwitchInLocked(int threadId, double relativeMs)
    {
        var thread = GetOrAddLocked(threadId, out bool created);
        if (created)
            MarkPartialLocked(threadId, thread);

        if (thread.OffCpu)
        {
            CloseEpisodeLocked(thread, relativeMs, byExit: false);
        }
        else if (!created && thread.EverScheduled)
        {
            // Tracked and scheduled before, but no open episode: the
            // switch-out that ended its run was evidently missed. (A
            // born-in-window thread's first switch-in lands here with
            // EverScheduled still false — the benign case.)
            _switchInsWithoutEpisode++;
        }

        thread.EverScheduled = true;
        thread.OnCpu = true;
        thread.OnCpuStartMs = relativeMs;
    }

    /// <summary>Close a thread's open interval at its exit and retire its state into the exited list.</summary>
    private void CloseThreadLocked(int threadId, MutableThread thread, double relativeMs)
    {
        if (thread.OffCpu)
        {
            CloseEpisodeLocked(thread, relativeMs, byExit: true);
        }
        else if (thread.OnCpu)
        {
            thread.OnCpuMs += Math.Max(0, relativeMs - thread.OnCpuStartMs);
            thread.OnCpu = false;
        }
        _exitedThreads.Add(new ThreadOffCpuStats(
            threadId, thread.OnCpuMs, thread.OffCpuMs, thread.Episodes,
            thread.MaxEpisodeMs, thread.Partial));
        _threads.Remove(threadId);
    }

    private void CloseEpisodeLocked(MutableThread thread, double relativeMs, bool byExit)
    {
        double durationMs = Math.Max(0, relativeMs - thread.OffCpuStartMs);
        thread.OffCpuMs += durationMs;
        thread.Episodes++;
        if (durationMs > thread.MaxEpisodeMs)
            thread.MaxEpisodeMs = durationMs;
        var totals = _reasonTotals.GetValueOrDefault(thread.OffCpuReason);
        _reasonTotals[thread.OffCpuReason] = (totals.Episodes + 1, totals.TotalMs + durationMs);
        thread.OffCpu = false;
        _episodesClosed++;
        if (byExit)
            _episodesClosedByExit++;
    }

    // ---- helpers ----

    /// <summary>
    /// The episode label for a switch-out: the kernel wait reason when the
    /// thread actually waited, <c>Preempted</c> when it was switched out
    /// while runnable, the state name for anything else.
    /// </summary>
    private static string ReasonLabel(ThreadState state, ThreadWaitReason reason) => state switch
    {
        ThreadState.Wait => WaitReasonLabel(reason),
        ThreadState.Ready => "Preempted",
        _ => state.ToString(),
    };

    /// <summary>
    /// NT KWAIT_REASON names, by integer value. The TraceEvent
    /// <see cref="ThreadWaitReason"/> enum names only the classic subset
    /// (0–14, with its own spellings — e.g. 3 is "SystemAllocation" there,
    /// 4 is "ExecutionDelay"); the kernel also reports the Wr* family
    /// (15–39), which the enum leaves unnamed and <c>ToString()</c> would
    /// render as a bare number. This table is consulted only for values
    /// the enum does not name, so enum spellings always win where they
    /// exist. The Wr* names are version-dependent NT kernel naming
    /// (KWAIT_REASON in the WDK); values beyond this table fall back to
    /// the numeric <c>WaitReason(&lt;n&gt;)</c> form rather than a
    /// guessed name — honest, no fabrication.
    /// </summary>
    private static readonly string[] NtWaitReasonNames =
    {
        "Executive",        // 0
        "FreePage",         // 1
        "PageIn",           // 2
        "PoolAllocation",   // 3 (enum: SystemAllocation)
        "DelayExecution",   // 4 (enum: ExecutionDelay)
        "Suspended",        // 5
        "UserRequest",      // 6
        "WrExecutive",      // 7
        "WrFreePage",       // 8
        "WrPageIn",         // 9
        "WrPoolAllocation", // 10
        "WrDelayExecution", // 11
        "WrSuspended",      // 12
        "WrUserRequest",    // 13
        "WrEventPair",      // 14
        "WrQueue",          // 15
        "WrLpcReceive",     // 16
        "WrLpcReply",       // 17
        "WrVirtualMemory",  // 18
        "WrPageOut",        // 19
        "WrRendezvous",     // 20
        "WrKeyedEvent",     // 21
        "WrTerminated",     // 22
        "WrProcessInSwap",  // 23
        "WrCpuRateControl", // 24
        "WrCalloutStack",   // 25
        "WrKernel",         // 26
        "WrResource",       // 27
        "WrPushLock",       // 28
        "WrMutex",          // 29
        "WrQuantumEnd",     // 30
        "WrDispatchInt",    // 31
        "WrPreempted",      // 32
        "WrYieldExecution", // 33
        "WrFastMutex",      // 34
        "WrGuardedMutex",   // 35
        "WrRundown",        // 36
        "WrAlertByThreadId",// 37
        "WrDeferredPreempt",// 38
        "WrPhysicalFault",  // 39
    };

    /// <summary>
    /// The label for one wait reason: the enum's own name when it has one,
    /// else the KWAIT_REASON table name, else <c>WaitReason(&lt;n&gt;)</c>.
    /// </summary>
    private static string WaitReasonLabel(ThreadWaitReason reason)
    {
        if (Enum.GetName(reason) is { } name)
            return name;
        int value = (int)reason;
        if ((uint)value < (uint)NtWaitReasonNames.Length)
            return NtWaitReasonNames[value];
        return $"WaitReason({value})";
    }

    private MutableThread GetOrAddLocked(int threadId, out bool created)
    {
        if (_threads.TryGetValue(threadId, out var thread))
        {
            created = false;
            return thread;
        }
        created = true;
        thread = new MutableThread();
        _threads[threadId] = thread;
        _seenThreadIds.Add(threadId);
        return thread;
    }

    private void MarkPartialLocked(int threadId, MutableThread thread)
    {
        thread.Partial = true;
        _partialThreadIds.Add(threadId);
    }

    /// <summary>The newest trace timestamp observed anywhere (the snapshot "now").</summary>
    private double LatestRelativeMs =>
        BitConverter.Int64BitsToDouble(Volatile.Read(ref _latestRelativeMsBits));

    private void TouchTime(double relativeMs)
    {
        // Called for every context switch on the machine, so it must stay
        // lock-free (a double cannot be volatile in C#, hence the bits
        // round-trip). Worst case a snapshot is a few milliseconds stale.
        long bits = BitConverter.DoubleToInt64Bits(relativeMs);
        long current;
        while (bits > (current = Volatile.Read(ref _latestRelativeMsBits)))
        {
            if (Interlocked.CompareExchange(ref _latestRelativeMsBits, bits, current) == current)
                break;
        }
    }
}
