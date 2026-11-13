namespace Appraan.Windows;

/// <summary>
/// Coarse category a kernel event belongs to. Features in Appraan are defined
/// by meaning (DESIGN.md parity contract), so events from different Windows
/// providers that mean the same thing share a category.
/// </summary>
public enum EventCategory
{
    /// <summary>TCP/UDP network activity (Kernel-Network / TCPIP providers).</summary>
    Tcp,

    /// <summary>File-system operations (Kernel FileIO provider).</summary>
    File,

    /// <summary>Physical disk I/O (Kernel DiskIO provider).</summary>
    Disk,

    /// <summary>Everything else: process/thread lifecycle, context switches, ...</summary>
    Other,
}
