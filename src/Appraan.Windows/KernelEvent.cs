namespace Appraan.Windows;

/// <summary>
/// One observed kernel event, normalised into the record model shared by every
/// Appraan.Windows utility (and, later, by Appraan's Windows collector).
/// Later steps extend this with per-provider payloads (latency pairs, TCP
/// endpoints, file names); step 1 keeps the common envelope only.
/// </summary>
/// <param name="Timestamp">When the event occurred (ETW timestamp, local time).</param>
/// <param name="Provider">
/// Logical source of the event, e.g. <c>Kernel.TcpIp</c>, <c>Kernel.FileIO</c>,
/// <c>Kernel.DiskIO</c>, <c>Kernel.Process</c>, <c>Kernel.Thread</c>.
/// </param>
/// <param name="EventName">Provider event name, e.g. <c>TcpIpConnect</c>, <c>FileIOWrite</c>.</param>
/// <param name="ProcessId">Process the event is attributed to (-1 when ETW does not attribute one).</param>
/// <param name="ThreadId">Thread the event is attributed to (-1 when not applicable).</param>
/// <param name="Category">Coarse category used for aggregation; see <see cref="EventCategory"/>.</param>
public sealed record KernelEvent(
    DateTime Timestamp,
    string Provider,
    string EventName,
    int ProcessId,
    int ThreadId,
    EventCategory Category);
