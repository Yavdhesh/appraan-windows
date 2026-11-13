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
/// <para><b>Correlation.</b> The table is keyed by
/// (ProcessId, local endpoint, remote endpoint). ETW reports each event's
/// <c>saddr/daddr</c> from the <i>packet's</i> perspective, not the
/// connection's: for Send the source is the local endpoint, for Recv the
/// source is the remote sender, for Accept the source is the remote
/// initiator. The tracker converts per event kind so Local/Remote in the
/// table are always from the owning process's perspective. As a second,
/// orientation-proof index it also remembers each event's <c>connid</c>
/// (the kernel's per-connection identifier, present in every TCP event):
/// a connid hit is only trusted when the event's endpoints actually fit the
/// stored connection, which keeps a recycled connid (or a recycled PID —
/// same caveat, plus the full endpoint tuple must also collide) from
/// merging two different connections.</para>
///
/// <para><b>Byte accounting.</b> BytesSent/BytesReceived sum the <c>size</c>
/// field of ETW send/recv events, i.e. payload bytes as the kernel reports
/// them. Retransmit events only bump <see cref="TcpConnectionRecord.Retransmits"/>;
/// their bytes are already counted from the original send.</para>
///
/// <para><b>Partial visibility.</b> A connection already open when tracing
/// starts has no connect event: its entry is created on the first observed
/// data event with <see cref="TcpConnectionRecord.EstimatedStart"/> set, so
/// its duration is a lower bound. A disconnect with no prior events at all
/// still produces a (zero-duration, estimated) closed record rather than
/// being dropped. Entries whose disconnect is never observed (dropped ETW
/// events, abrupt process death) stay Open for the tracker's lifetime;
/// callers can inspect them via <see cref="GetOpenConnections"/>.</para>
///
/// <para>Thread safety: event handlers run on the ETW dispatch thread, but
/// all state is guarded by one lock so <see cref="OpenCount"/> and
/// <see cref="GetOpenConnections"/> may be called from any thread.
/// <see cref="ConnectionClosed"/> is raised outside the lock.</para>
/// </summary>
public sealed class TcpConnectionTracker
{
    /// <summary>Identity of one connection in the table.</summary>
    private readonly record struct ConnKey(
        int ProcessId, IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort);

    /// <summary>Mutable per-connection state while the connection is open.</summary>
    private sealed class Entry
    {
        public required ConnKey Key { get; init; }
        public required string ProcessName { get; set; }
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
    private readonly Dictionary<(int ProcessId, ulong ConnId), ConnKey> _byConnId = new();
    private readonly Dictionary<int, string> _processNames = new();
    private DateTime _latestTimestamp;
    private int _attached;

    /// <param name="processIdFilter">
    /// When set, only this process's connections are tracked; all other
    /// processes' events are ignored. Null tracks every process.
    /// </param>
    public TcpConnectionTracker(int? processIdFilter = null) => _processIdFilter = processIdFilter;

    /// <summary>
    /// Raised once per connection when its disconnect is observed, with the
    /// final record. Raised on the ETW dispatch thread, outside the tracker's
    /// lock; handlers should be cheap (format/print, enqueue).
    /// </summary>
    public event Action<TcpConnectionRecord>? ConnectionClosed;

    /// <summary>Number of connections currently believed open.</summary>
    public int OpenCount
    {
        get { lock (_gate) return _open.Count; }
    }

    /// <summary>
    /// Subscribe this tracker to all TCP events (v4 and v6) of a kernel
    /// parser — typically <see cref="KernelSession.KernelParser"/> after
    /// <see cref="KernelSession.Start"/>. May only be called once.
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
                    OpenConnection(e, localIsSource: true);
                    break;
                case EventKind.Accept:
                    // Accept events are reported from the packet's perspective:
                    // source = the remote initiator, destination = our listener.
                    OpenConnection(e, localIsSource: false);
                    break;
                case EventKind.Reconnect:
                    GetOrCreate(e, localIsSource: true, estimatedStart: true);
                    break;
                case EventKind.Send:
                    GetOrCreate(e, localIsSource: true, estimatedStart: true).BytesSent += e.Size;
                    break;
                case EventKind.Recv:
                    // Recv events: source = the remote sender, destination = us.
                    GetOrCreate(e, localIsSource: false, estimatedStart: true).BytesReceived += e.Size;
                    break;
                case EventKind.Retransmit:
                    // Bytes were already counted on the original send.
                    if (FindEntry(e, localIsSource: null) is { } entry)
                        entry.Retransmits++;
                    break;
                case EventKind.Disconnect:
                    closed = CloseConnection(e);
                    break;
            }
        }

        if (closed is not null)
            ConnectionClosed?.Invoke(closed);
    }

    /// <summary>Register a freshly observed connection (connect/accept).</summary>
    private void OpenConnection(TcpEvent e, bool localIsSource)
    {
        // An entry can already exist (created by an out-of-order data event,
        // or a duplicate connect): adopt it rather than resetting its start
        // time and byte counters.
        if (FindEntry(e, localIsSource) is not null)
            return;
        CreateEntry(e, localIsSource, estimatedStart: false);
    }

    private Entry GetOrCreate(TcpEvent e, bool localIsSource, bool estimatedStart) =>
        FindEntry(e, localIsSource) ?? CreateEntry(e, localIsSource, estimatedStart);

    private Entry CreateEntry(TcpEvent e, bool localIsSource, bool estimatedStart)
    {
        var key = KeyFor(e, localIsSource);
        var entry = new Entry
        {
            Key = key,
            ProcessName = ResolveProcessName(e.ProcessId, e.ProcessName),
            StartTime = e.TimeStamp,
            EstimatedStart = estimatedStart,
        };
        _open[key] = entry;
        RegisterConnId(e, key);
        return entry;
    }

    private TcpConnectionRecord CloseConnection(TcpEvent e)
    {
        var entry = FindEntry(e, localIsSource: null);
        if (entry is null)
        {
            // Disconnect for a connection we never saw any other event for:
            // report it as a zero-duration estimated record instead of
            // dropping it (tcplife would miss it entirely; a visible stub
            // makes dropped-event situations diagnosable).
            var orphanKey = KeyFor(e, localIsSource: true);
            var orphan = new Entry
            {
                Key = orphanKey,
                ProcessName = ResolveProcessName(e.ProcessId, e.ProcessName),
                StartTime = e.TimeStamp,
                EstimatedStart = true,
            };
            return ToRecord(orphan, endTime: e.TimeStamp);
        }

        _open.Remove(entry.Key);
        if (e.ConnId != 0 &&
            _byConnId.TryGetValue((e.ProcessId, e.ConnId), out var mapped) &&
            mapped.Equals(entry.Key))
        {
            _byConnId.Remove((e.ProcessId, e.ConnId));
        }
        return ToRecord(entry, endTime: e.TimeStamp);
    }

    /// <summary>
    /// Find the open entry an event belongs to: first via the connid index
    /// (trusted only if the event's endpoints fit the stored connection —
    /// guards against connid reuse), then via the endpoint tuple in the
    /// event kind's preferred orientation, then in the swapped orientation.
    /// A successful tuple lookup (re)registers the connid mapping.
    /// </summary>
    private Entry? FindEntry(TcpEvent e, bool? localIsSource)
    {
        if (e.ConnId != 0 &&
            _byConnId.TryGetValue((e.ProcessId, e.ConnId), out var byId) &&
            _open.TryGetValue(byId, out var indexed) &&
            Fits(indexed.Key, e))
        {
            return indexed;
        }

        bool first = localIsSource ?? true;
        foreach (bool orientation in new[] { first, !first })
        {
            var key = KeyFor(e, orientation);
            if (_open.TryGetValue(key, out var entry))
            {
                RegisterConnId(e, key);
                return entry;
            }
        }
        return null;
    }

    /// <summary>True when the event's endpoints match the key in either orientation.</summary>
    private static bool Fits(ConnKey key, TcpEvent e) =>
        (key.LocalAddress.Equals(e.SAddr) && key.LocalPort == e.SPort &&
         key.RemoteAddress.Equals(e.DAddr) && key.RemotePort == e.DPort) ||
        (key.LocalAddress.Equals(e.DAddr) && key.LocalPort == e.DPort &&
         key.RemoteAddress.Equals(e.SAddr) && key.RemotePort == e.SPort);

    private static ConnKey KeyFor(TcpEvent e, bool localIsSource) =>
        localIsSource
            ? new ConnKey(e.ProcessId, e.SAddr, e.SPort, e.DAddr, e.DPort)
            : new ConnKey(e.ProcessId, e.DAddr, e.DPort, e.SAddr, e.SPort);

    private void RegisterConnId(TcpEvent e, ConnKey key)
    {
        if (e.ConnId != 0)
            _byConnId[(e.ProcessId, e.ConnId)] = key;
    }

    private TcpConnectionRecord ToRecord(Entry entry, DateTime? endTime)
    {
        var end = endTime ?? _latestTimestamp;
        return new TcpConnectionRecord(
            entry.Key.ProcessId,
            entry.ProcessName,
            entry.Key.LocalAddress,
            entry.Key.LocalPort,
            entry.Key.RemoteAddress,
            entry.Key.RemotePort,
            entry.StartTime,
            endTime,
            Math.Max(0, (end - entry.StartTime).TotalMilliseconds),
            entry.BytesSent,
            entry.BytesReceived,
            entry.Retransmits,
            endTime is null ? TcpConnectionState.Open : TcpConnectionState.Closed,
            entry.EstimatedStart);
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
