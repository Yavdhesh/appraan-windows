using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Appraan.Windows;

/// <summary>
/// Tracks TCP connection lifecycles from ETW kernel TCP events — the data
/// engine behind the <c>wintcplife</c> CLI, i.e. BCC's <c>tcplife</c> for
/// Windows. Feed it a session's kernel parser via <see cref="Attach"/>; it
/// maintains a live table of open connections and raises
/// <see cref="ConnectionClosed"/> with a final <see cref="TcpConnectionRecord"/>
/// when each connection disconnects.
///
/// <para><b>Correlation — connid is the primary key.</b> Every ETW TCP event
/// payload carries <c>connid</c>, the kernel's per-connection identifier,
/// and the open table is keyed by (ProcessId, connid). A connection's
/// endpoints and direction are fixed <i>once</i>, when its entry is created
/// (see below); send/recv events for a known connid then simply add bytes —
/// Send always means "bytes sent by the owning socket", Recv "bytes
/// received by it", so no per-event orientation guessing is involved.</para>
///
/// <para><b>Why not the endpoint tuple?</b> The first Step 2 build keyed the
/// table by (ProcessId, local, remote) and searched it in both orientations,
/// with the connid as a "guarded" secondary index. On same-process loopback
/// (e.g. winload, which owns both ends of its connections) the client entry
/// and the accepted-server entry are exact mirror tuples, so the swapped
/// search matched the <i>peer's</i> entry: an Accept "adopted" the client
/// entry and registered the accepted connection's connid against it, the
/// endpoint-fit guard could not tell mirror tuples apart, and from then on
/// the server's send/recv bytes landed on the client record (doubling its
/// TX and RX) while the server record starved and its disconnect fabricated
/// a zero-duration stub. The 2026-11-13 Windows 11 run showed exactly that
/// signature (client 0.06/0.06 KB instead of 0.03/0.03, server 0.00/0.00,
/// phantom duplicate lines). Tuple keys survive only as the fallback for
/// events whose connid is 0.</para>
///
/// <para><b>Entry creation and orientation.</b> ETW reports each event's
/// <c>saddr/daddr</c> from the packet's perspective. Each event kind is
/// normalised exactly once, at entry creation: Connect/Send/Retransmit/
/// Reconnect/Disconnect are reported by the owning socket itself, so
/// source = local; Accept/Recv are delivered to the owning socket, so
/// destination = local. An entry created by Connect is outbound, by Accept
/// inbound; an entry created by a data event has unknown direction and
/// <see cref="TcpConnectionRecord.EstimatedStart"/> set. Disconnects whose
/// connid was never seen create <i>no</i> record — they only increment
/// <see cref="UnmatchedDisconnects"/>.</para>
///
/// <para><b>connid reuse.</b> Closed connids are remembered. A data event
/// arriving for a closed connid is a late arrival of the already-reported
/// connection and is dropped; a Connect/Accept for a closed connid starts a
/// fresh entry (new generation). A Connect/Accept for a connid that is
/// still open but whose endpoints contradict the stored entry likewise
/// wins: the old entry is closed as estimated and replaced. (The remembered
/// closed set grows by one entry per closed connection for the tracker's
/// lifetime — acceptable for a tracing tool's session lengths.)</para>
///
/// <para><b>Byte accounting.</b> BytesSent/BytesReceived sum the <c>size</c>
/// field of ETW send/recv events attributed by connid, i.e. payload bytes
/// as the kernel reports them, per owning socket. Retransmit events only
/// bump <see cref="TcpConnectionRecord.Retransmits"/>; their bytes were
/// already counted from the original send.</para>
///
/// <para><b>Partial visibility.</b> A connection already open when tracing
/// starts has no connect event: its entry is created on the first observed
/// data event with <see cref="TcpConnectionRecord.EstimatedStart"/> set, so
/// its duration is a lower bound. Entries whose disconnect is never
/// observed (dropped ETW events, abrupt process death) stay Open for the
/// tracker's lifetime; callers can inspect them via
/// <see cref="GetOpenConnections"/>.</para>
///
/// <para>Thread safety: event handlers run on the ETW dispatch thread, but
/// all state is guarded by one lock so <see cref="OpenCount"/> and
/// <see cref="GetOpenConnections"/> may be called from any thread.
/// <see cref="ConnectionClosed"/> is raised outside the lock.</para>
/// </summary>
public sealed class TcpConnectionTracker
{
    /// <summary>
    /// Identity of one connection in the table. When
    /// <see cref="ConnId"/> is non-zero the key is just (ProcessId, ConnId)
    /// and the endpoint fields are null/0 — endpoints live on the entry.
    /// When ConnId is 0 (an event with no kernel connection id) the key
    /// falls back to the endpoint tuple in the fixed per-event-kind
    /// orientation (see <see cref="EndpointsFor"/>).
    /// </summary>
    private readonly record struct ConnKey(
        int ProcessId, ulong ConnId,
        IPAddress? LocalAddress, int LocalPort, IPAddress? RemoteAddress, int RemotePort);

    /// <summary>Mutable per-connection state while the connection is open.</summary>
    private sealed class Entry
    {
        public required ConnKey Key { get; init; }
        public required string ProcessName { get; set; }
        public required IPAddress LocalAddress { get; init; }
        public required int LocalPort { get; init; }
        public required IPAddress RemoteAddress { get; init; }
        public required int RemotePort { get; init; }

        /// <summary>
        /// Direction fixed once at creation: true for Connect-created,
        /// false for Accept-created, null when the entry was created by a
        /// data event (direction unknowable — but also unnecessary, since
        /// send/recv byte attribution is per owning socket, not per
        /// direction).
        /// </summary>
        public required bool? IsOutbound { get; init; }

        public required DateTime StartTime { get; init; }
        public required bool EstimatedStart { get; init; }
        public long BytesSent;
        public long BytesReceived;
        public int Retransmits;
    }

    private enum EventKind { Connect, Accept, Send, Recv, Retransmit, Reconnect, Disconnect }

    /// <summary>One TCP event, normalised across the v4/v6 payload classes.</summary>
    private readonly record struct TcpEvent(
        EventKind Kind, ulong ConnId, int ProcessId, string ProcessName,
        IPAddress SAddr, int SPort, IPAddress DAddr, int DPort, int Size, DateTime TimeStamp);

    private readonly int? _processIdFilter;
    private readonly object _gate = new();
    private readonly Dictionary<ConnKey, Entry> _open = new();
    private readonly HashSet<(int ProcessId, ulong ConnId)> _closedConnIds = new();
    private readonly Dictionary<int, string> _processNames = new();
    private DateTime _latestTimestamp;
    private long _unmatchedDisconnects;
    private int _attached;

    /// <param name="processIdFilter">
    /// When set, only this process's connections are tracked; all other
    /// processes' events are ignored. Null tracks every process.
    /// </param>
    public TcpConnectionTracker(int? processIdFilter = null) => _processIdFilter = processIdFilter;

    /// <summary>
    /// Raised once per connection when its disconnect is observed (or when
    /// a still-open entry is displaced by a contradicting Connect/Accept
    /// for a reused connid — then the record's EstimatedStart is set),
    /// with the final record. Raised on the ETW dispatch thread, outside
    /// the tracker's lock; handlers should be cheap (format/print, enqueue).
    /// </summary>
    public event Action<TcpConnectionRecord>? ConnectionClosed;

    /// <summary>Number of connections currently believed open.</summary>
    public int OpenCount
    {
        get { lock (_gate) return _open.Count; }
    }

    /// <summary>
    /// Disconnect events observed for a connid that was never seen (or was
    /// already closed). No record is fabricated for these; the count makes
    /// the visibility loss diagnosable and is shown in wintcplife's exit
    /// summary.
    /// </summary>
    public long UnmatchedDisconnects
    {
        get { lock (_gate) return _unmatchedDisconnects; }
    }

    /// <summary>
    /// Subscribe this tracker to all TCP events (v4 and v6) of a kernel
    /// parser — typically <see cref="KernelSession.KernelParser"/> after
    /// <see cref="KernelSession.Start"/>. May only be called once.
    /// UDP parser events are deliberately not subscribed: UDP has no
    /// connection lifecycle and must never enter this tracker. (KernelSession
    /// subscribes UDP separately, for winscope's coarse category counts
    /// only.)
    /// </summary>
    public void Attach(KernelTraceEventParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (Interlocked.Exchange(ref _attached, 1) != 0)
            throw new InvalidOperationException("This tracker is already attached to a parser.");

        parser.TcpIpConnect += d => Handle(Map(EventKind.Connect, d));
        parser.TcpIpAccept += d => Handle(Map(EventKind.Accept, d));
        parser.TcpIpSend += d => Handle(Map(EventKind.Send, d));
        parser.TcpIpRecv += d => Handle(Map(EventKind.Recv, d));
        parser.TcpIpRetransmit += d => Handle(Map(EventKind.Retransmit, d));
        parser.TcpIpReconnect += d => Handle(Map(EventKind.Reconnect, d));
        parser.TcpIpDisconnect += d => Handle(Map(EventKind.Disconnect, d));

        parser.TcpIpConnectIPV6 += d => Handle(Map(EventKind.Connect, d));
        parser.TcpIpAcceptIPV6 += d => Handle(Map(EventKind.Accept, d));
        parser.TcpIpSendIPV6 += d => Handle(Map(EventKind.Send, d));
        parser.TcpIpRecvIPV6 += d => Handle(Map(EventKind.Recv, d));
        parser.TcpIpRetransmitIPV6 += d => Handle(Map(EventKind.Retransmit, d));
        parser.TcpIpReconnectIPV6 += d => Handle(Map(EventKind.Reconnect, d));
        parser.TcpIpDisconnectIPV6 += d => Handle(Map(EventKind.Disconnect, d));
    }

    /// <summary>Snapshot of the connections currently believed open.</summary>
    public IReadOnlyList<TcpConnectionRecord> GetOpenConnections()
    {
        lock (_gate)
            return _open.Values.Select(e => ToRecord(e, endTime: null)).ToList();
    }

    // ---- payload normalisation (property names verified against
    // ---- TraceEvent 3.1.8: every TCP payload class carries connid,
    // ---- saddr/sport, daddr/dport and size with identical names/types)

    private static TcpEvent Map(EventKind kind, TcpIpConnectTraceData d) =>
        new(kind, d.connid, d.ProcessID, d.ProcessName, d.saddr, d.sport, d.daddr, d.dport, d.size, d.TimeStamp);

    private static TcpEvent Map(EventKind kind, TcpIpV6ConnectTraceData d) =>
        new(kind, d.connid, d.ProcessID, d.ProcessName, d.saddr, d.sport, d.daddr, d.dport, d.size, d.TimeStamp);

    private static TcpEvent Map(EventKind kind, TcpIpSendTraceData d) =>
        new(kind, d.connid, d.ProcessID, d.ProcessName, d.saddr, d.sport, d.daddr, d.dport, d.size, d.TimeStamp);

    private static TcpEvent Map(EventKind kind, TcpIpV6SendTraceData d) =>
        new(kind, d.connid, d.ProcessID, d.ProcessName, d.saddr, d.sport, d.daddr, d.dport, d.size, d.TimeStamp);

    private static TcpEvent Map(EventKind kind, TcpIpTraceData d) =>
        new(kind, d.connid, d.ProcessID, d.ProcessName, d.saddr, d.sport, d.daddr, d.dport, d.size, d.TimeStamp);

    private static TcpEvent Map(EventKind kind, TcpIpV6TraceData d) =>
        new(kind, d.connid, d.ProcessID, d.ProcessName, d.saddr, d.sport, d.daddr, d.dport, d.size, d.TimeStamp);

    // ---- orientation: decided once per event kind, never re-derived ----

    /// <summary>
    /// True when the event is reported by the owning socket itself, so the
    /// packet's source is the socket's local endpoint. Accept/Recv are
    /// delivered TO the owning socket, so there the destination is local.
    /// Because every kind is normalised by this one rule, all events of one
    /// socket yield the same (local, remote) — and for connid-keyed
    /// entries the result is used only at creation time anyway.
    /// </summary>
    private static bool SourceIsLocal(EventKind kind) => kind is
        EventKind.Connect or EventKind.Send or EventKind.Retransmit or
        EventKind.Reconnect or EventKind.Disconnect;

    /// <summary>The event's endpoints from the owning socket's perspective.</summary>
    private static (IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort) EndpointsFor(TcpEvent e) =>
        SourceIsLocal(e.Kind)
            ? (e.SAddr, e.SPort, e.DAddr, e.DPort)
            : (e.DAddr, e.DPort, e.SAddr, e.SPort);

    /// <summary>
    /// The table key for an event: (ProcessId, connid) when the event
    /// carries a connid; the normalised endpoint tuple when it does not.
    /// </summary>
    private static ConnKey KeyFor(TcpEvent e)
    {
        if (e.ConnId != 0)
            return new ConnKey(e.ProcessId, e.ConnId, null, 0, null, 0);
        var (local, localPort, remote, remotePort) = EndpointsFor(e);
        return new ConnKey(e.ProcessId, 0, local, localPort, remote, remotePort);
    }

    // ---- event handling (all state changes under _gate) ----

    private void Handle(TcpEvent e)
    {
        if (_processIdFilter is int filter && e.ProcessId != filter)
            return;

        TcpConnectionRecord? closed = null;
        lock (_gate)
        {
            if (e.TimeStamp > _latestTimestamp)
                _latestTimestamp = e.TimeStamp;

            switch (e.Kind)
            {
                case EventKind.Connect:
                    closed = OnConnectOrAccept(e, isConnect: true);
                    break;
                case EventKind.Accept:
                    closed = OnConnectOrAccept(e, isConnect: false);
                    break;
                case EventKind.Send:
                    if (GetOrCreateDataEntry(e) is { } sent)
                        sent.BytesSent += e.Size;
                    break;
                case EventKind.Recv:
                    if (GetOrCreateDataEntry(e) is { } received)
                        received.BytesReceived += e.Size;
                    break;
                case EventKind.Reconnect:
                    GetOrCreateDataEntry(e);
                    break;
                case EventKind.Retransmit:
                    // Never creates an entry: a retransmit implies a send
                    // that was (or should have been) observed, and its bytes
                    // were already counted then.
                    if (FindOpen(e) is { } retransmitted)
                        retransmitted.Retransmits++;
                    break;
                case EventKind.Disconnect:
                    closed = OnDisconnect(e);
                    break;
            }
        }

        if (closed is not null)
            ConnectionClosed?.Invoke(closed);
    }

    /// <summary>
    /// Handle a Connect/Accept: create the entry (fixing its endpoints and
    /// direction), adopt a live duplicate, or — when the connid was reused
    /// and the stored endpoints contradict the new connection — displace
    /// the old entry, which is returned closed-as-estimated.
    /// Caller must hold <see cref="_gate"/>.
    /// </summary>
    private TcpConnectionRecord? OnConnectOrAccept(TcpEvent e, bool isConnect)
    {
        var key = KeyFor(e);
        var (local, localPort, remote, remotePort) = EndpointsFor(e);

        if (_open.TryGetValue(key, out var existing))
        {
            bool contradicts = e.ConnId != 0 &&
                !(existing.LocalAddress.Equals(local) && existing.LocalPort == localPort &&
                  existing.RemoteAddress.Equals(remote) && existing.RemotePort == remotePort);
            if (!contradicts)
                return null; // Duplicate/out-of-order connect for the live entry: adopt it.

            // connid reuse with a still-open entry: the new connection
            // wins. The old one's disconnect was never observed, so it is
            // closed as estimated at this event's timestamp.
            _open.Remove(key);
            if (e.ConnId != 0)
                _closedConnIds.Add((e.ProcessId, e.ConnId));
            var displaced = ToRecord(existing, e.TimeStamp, forceEstimated: true);
            CreateEntry(e, key, isOutbound: isConnect, estimatedStart: false);
            return displaced;
        }

        CreateEntry(e, key, isOutbound: isConnect, estimatedStart: false);
        return null;
    }

    /// <summary>
    /// Find the open entry for a data event, creating it (estimated) when
    /// the connection predates the trace or its connect was dropped.
    /// Returns null for a late event of an already-closed connid — see the
    /// class remarks on connid reuse. Caller must hold <see cref="_gate"/>.
    /// </summary>
    private Entry? GetOrCreateDataEntry(TcpEvent e)
    {
        var key = KeyFor(e);
        if (_open.TryGetValue(key, out var entry))
            return entry;

        if (e.ConnId != 0 && _closedConnIds.Contains((e.ProcessId, e.ConnId)))
            return null; // Trailing event of a connection already reported closed.

        return CreateEntry(e, key, isOutbound: null, estimatedStart: true);
    }

    /// <summary>The open entry an event belongs to, if any. Caller must hold <see cref="_gate"/>.</summary>
    private Entry? FindOpen(TcpEvent e) =>
        _open.TryGetValue(KeyFor(e), out var entry) ? entry : null;

    private Entry CreateEntry(TcpEvent e, ConnKey key, bool? isOutbound, bool estimatedStart)
    {
        var (local, localPort, remote, remotePort) = EndpointsFor(e);
        var entry = new Entry
        {
            Key = key,
            ProcessName = ResolveProcessName(e.ProcessId, e.ProcessName),
            LocalAddress = local,
            LocalPort = localPort,
            RemoteAddress = remote,
            RemotePort = remotePort,
            IsOutbound = isOutbound,
            StartTime = e.TimeStamp,
            EstimatedStart = estimatedStart,
        };
        _open[key] = entry;
        if (e.ConnId != 0)
            _closedConnIds.Remove((e.ProcessId, e.ConnId)); // a fresh generation begins
        return entry;
    }

    /// <summary>
    /// Handle a Disconnect: close and return the entry when one is open.
    /// An unmatched disconnect fabricates NO record (the zero-duration
    /// stubs that produced in the first Step 2 build were indistinguishable
    /// from real connections) — it only bumps
    /// <see cref="UnmatchedDisconnects"/>. Caller must hold
    /// <see cref="_gate"/>.
    /// </summary>
    private TcpConnectionRecord? OnDisconnect(TcpEvent e)
    {
        var key = KeyFor(e);
        if (_open.Remove(key, out var entry))
        {
            if (e.ConnId != 0)
                _closedConnIds.Add((e.ProcessId, e.ConnId));
            return ToRecord(entry, e.TimeStamp);
        }

        _unmatchedDisconnects++;
        return null;
    }

    private TcpConnectionRecord ToRecord(Entry entry, DateTime? endTime, bool forceEstimated = false)
    {
        var end = endTime ?? _latestTimestamp;
        return new TcpConnectionRecord(
            entry.Key.ProcessId,
            entry.ProcessName,
            entry.LocalAddress,
            entry.LocalPort,
            entry.RemoteAddress,
            entry.RemotePort,
            entry.StartTime,
            endTime,
            Math.Max(0, (end - entry.StartTime).TotalMilliseconds),
            entry.BytesSent,
            entry.BytesReceived,
            entry.Retransmits,
            endTime is null ? TcpConnectionState.Open : TcpConnectionState.Closed,
            entry.EstimatedStart || forceEstimated);
    }

    /// <summary>
    /// Best-effort process name: the name ETW/TraceEvent attached to the
    /// event, else a live lookup by PID (cached). Empty when unresolvable
    /// (process already exited, access denied, non-Windows test host).
    /// Caller must hold <see cref="_gate"/>.
    /// </summary>
    private string ResolveProcessName(int processId, string fromEvent)
    {
        if (!string.IsNullOrEmpty(fromEvent))
        {
            _processNames[processId] = fromEvent;
            return fromEvent;
        }
        if (_processNames.TryGetValue(processId, out var cached))
            return cached;

        string name = "";
        if (processId > 0)
        {
            try
            {
                name = System.Diagnostics.Process.GetProcessById(processId).ProcessName;
            }
            catch (Exception)
            {
                // Process exited between event and lookup, or access denied.
            }
        }
        _processNames[processId] = name;
        return name;
    }
}
