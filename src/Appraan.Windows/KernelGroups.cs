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

    /// <summary>All groups defined for step 1.</summary>
    All = Tcp | File | Disk | Process | Thread,
}
