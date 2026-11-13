namespace Appraan.Windows;

/// <summary>
/// Groups of kernel event sources a <see cref="KernelSession"/> can enable.
/// Each group maps to a set of NT Kernel Logger keywords; groups exist so
/// callers ask for meanings ("TCP", "file I/O") instead of raw ETW keywords.
/// </summary>
[Flags]
public enum KernelGroups
{
    /// <summary>No sources enabled.</summary>
    None = 0,

    /// <summary>TCP/UDP sends, receives, connects, accepts, disconnects.</summary>
    Tcp = 1 << 0,

    /// <summary>File-system creates, reads, writes, closes, deletes, renames.</summary>
    File = 1 << 1,

    /// <summary>Physical disk reads, writes and flushes (init + completion).</summary>
    Disk = 1 << 2,

    /// <summary>Process start/stop.</summary>
    Process = 1 << 3,

    /// <summary>Thread start/stop and context switches.</summary>
    Thread = 1 << 4,

    /// <summary>
    /// Registry operations (create/open/query/set/delete keys and values),
    /// added in step 3. Deliberately NOT part of <see cref="All"/>: registry
    /// tracing is opt-in (winops exposes it as <c>--registry</c>) because it
    /// adds a high-volume event stream most investigations do not need.
    /// Unlike the other groups, registry events have no normalised
    /// <see cref="KernelEvent"/> wiring in <see cref="KernelSession"/> —
    /// consumers subscribe to the typed registry events on
    /// <see cref="KernelSession.KernelParser"/> directly (see
    /// <see cref="OpsTracker"/>).
    /// </summary>
    Registry = 1 << 5,

    /// <summary>All groups defined for step 1 (registry is opt-in, see <see cref="Registry"/>).</summary>
    All = Tcp | File | Disk | Process | Thread,
}
