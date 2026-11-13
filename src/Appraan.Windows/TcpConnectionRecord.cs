using System.Net;

namespace Appraan.Windows;

/// <summary>Lifecycle state of a tracked TCP connection.</summary>
public enum TcpConnectionState
{
    /// <summary>Seen but not yet seen to disconnect.</summary>
    Open,

    /// <summary>A disconnect was observed; the record is final.</summary>
    Closed,
}

/// <summary>
/// One TCP connection as tracked by <see cref="TcpConnectionTracker"/> — the
/// Windows equivalent of one line of BCC's <c>tcplife</c> output: who owned
/// the connection, between which endpoints, for how long, and how many bytes
/// moved in each direction.
/// Local/remote are always from the tracked process's perspective
/// (Local = the endpoint owned by <paramref name="ProcessId"/>), regardless
/// of the source/destination orientation ETW reports for individual events.
/// </summary>
/// <param name="ProcessId">Process that owns the connection.</param>
/// <param name="ProcessName">Process name when resolvable, otherwise empty.</param>
/// <param name="LocalAddress">Local endpoint address.</param>
/// <param name="LocalPort">Local endpoint port.</param>
/// <param name="RemoteAddress">Remote endpoint address.</param>
/// <param name="RemotePort">Remote endpoint port.</param>
/// <param name="StartTime">
/// When the connection was first seen (connect/accept time, or first data
/// event for a connection that predates the trace — see
/// <paramref name="EstimatedStart"/>).
/// </param>
/// <param name="EndTime">When the disconnect was observed; null while open.</param>
/// <param name="DurationMs">
/// Lifetime in milliseconds: end − start for closed connections, or
/// snapshot-time − start for open ones.
/// </param>
/// <param name="BytesSent">Payload bytes sent by the process (sum of ETW send sizes).</param>
/// <param name="BytesReceived">Payload bytes received by the process (sum of ETW recv sizes).</param>
/// <param name="Retransmits">Retransmitted segments observed (their bytes are NOT double-counted in <paramref name="BytesSent"/>).</param>
/// <param name="State">Open or Closed.</param>
/// <param name="EstimatedStart">
/// True when the timing is not fully observed: either the connect/accept
/// event was never seen (the connection was already open when tracing
/// started, or its first observed event was data — then
/// <paramref name="StartTime"/> is the first-seen time and
/// <paramref name="DurationMs"/> is a lower bound on the true lifetime),
/// or the entry was closed early because its connid was reused by a new
/// connection before its disconnect was seen.
/// </param>
public sealed record TcpConnectionRecord(
    int ProcessId,
    string ProcessName,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    DateTime StartTime,
    DateTime? EndTime,
    double DurationMs,
    long BytesSent,
    long BytesReceived,
    int Retransmits,
    TcpConnectionState State,
    bool EstimatedStart);
