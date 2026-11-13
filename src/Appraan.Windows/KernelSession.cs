using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Appraan.Windows;

/// <summary>
/// Owns a real-time ETW kernel session (via TraceEvent, the library PerfView
/// is built on) and republishes the events as <see cref="KernelEvent"/>s.
///
/// Lifecycle: construct → <see cref="Start"/> → <see cref="ProcessEvents"/>
/// (blocks, usually on a background thread) → <see cref="Stop"/> (unblocks
/// <see cref="ProcessEvents"/>) → Dispose.
///
/// Kernel ETW sessions require an elevated (administrator) process. Starting
/// without elevation fails fast with an actionable message instead of an
/// opaque ETW error. A leftover session from a crashed run is stopped and
/// replaced, so <see cref="SessionName"/> is always owned by the live instance.
/// </summary>
public sealed class KernelSession : IDisposable
{
    /// <summary>Name of the ETW real-time session this class owns.</summary>
    public const string SessionName = "AppraanSession";

    private readonly KernelGroups _groups;
    private TraceEventSession? _session;
    private volatile bool _stopping;
    private bool _disposed;

    /// <summary>Create a session that will enable the given kernel groups.</summary>
    public KernelSession(KernelGroups groups = KernelGroups.All)
    {
        if (groups == KernelGroups.None)
            throw new ArgumentException("At least one kernel group must be enabled.", nameof(groups));
        _groups = groups;
    }

    /// <summary>
    /// Raised once per kernel event, on the ETW dispatch thread (i.e. the
    /// thread running <see cref="ProcessEvents"/>). Handlers must be cheap and
    /// thread-safe; hand real work to a queue or an aggregator such as
    /// <see cref="PidEventCounter"/>.
    /// </summary>
    public event Action<KernelEvent>? EventReceived;

    /// <summary>The groups this session was constructed with.</summary>
    public KernelGroups Groups => _groups;

    /// <summary>True between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning => _session is not null && !_stopping;

    /// <summary>
    /// The raw kernel parser behind this session, for consumers that need
    /// typed payloads (TCP endpoints, byte counts, ...) beyond the normalised
    /// <see cref="EventReceived"/> model — e.g. <see cref="TcpConnectionTracker"/>.
    /// Subscribe handlers after <see cref="Start"/> and before
    /// <see cref="ProcessEvents"/> starts dispatching; handlers subscribed
    /// later can miss events. This is purely additive: the normalised
    /// <see cref="EventReceived"/> wiring is unaffected, and both handler sets
    /// receive every event.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session has not been started.</exception>
    public KernelTraceEventParser KernelParser =>
        _session?.Source.Kernel
        ?? throw new InvalidOperationException("Call Start() before accessing KernelParser.");

    /// <summary>
    /// Create (or take over) the ETW session and enable the kernel providers.
    /// Does not consume events yet; call <see cref="ProcessEvents"/> for that.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    /// <exception cref="InvalidOperationException">
    /// The process is not elevated, or the session could not be created.
    /// </exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
            throw new InvalidOperationException("Session already started; Stop() it before starting again.");

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Appraan.Windows kernel tracing uses ETW and only runs on Windows.");

        if (TraceEventSession.IsElevated() != true)
            throw new InvalidOperationException(
                "Kernel ETW tracing requires an elevated process. " +
                "Re-run from an Administrator PowerShell / Command Prompt " +
                "(right-click → 'Run as administrator'), then try again.");

        StopStaleSession();

        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        try
        {
            if (!session.EnableKernelProvider(ToKeywords(_groups)))
                throw new InvalidOperationException(
                    $"ETW refused to enable kernel providers for session '{SessionName}'.");
        }
        catch
        {
            session.Dispose();
            throw;
        }

        WireHandlers(session.Source.Kernel);
        _session = session;
        _stopping = false;
    }

    /// <summary>
    /// Block, dispatching events to <see cref="EventReceived"/> until
    /// <see cref="Stop"/> is called (from any thread) or <paramref name="cancellation"/>
    /// is cancelled. Run this on a dedicated background thread.
    /// </summary>
    public void ProcessEvents(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Call Start() before ProcessEvents().");

        using var registration = cancellation.Register(Stop);
        try
        {
            session.Source.Process();
        }
        catch (Exception) when (_stopping)
        {
            // Stopping a real-time ETW session can surface as an exception from
            // the blocked Process() call on some Windows builds; that is the
            // normal shutdown path once Stop() was requested.
        }
    }

    /// <summary>Stop the ETW session. Safe to call more than once, from any thread.</summary>
    public void Stop()
    {
        _stopping = true;
        try
        {
            _session?.Stop();
        }
        catch (Exception)
        {
            // Session may already be gone (e.g. stopped externally); teardown
            // must never throw.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// If a previous Appraan run died without cleanup, its session is still
    /// registered with ETW. Stop it so this instance owns the name cleanly.
    /// </summary>
    private static void StopStaleSession()
    {
        foreach (var active in TraceEventSession.GetActiveSessionNames())
        {
            if (!string.Equals(active, SessionName, StringComparison.OrdinalIgnoreCase))
                continue;
            using var stale = new TraceEventSession(SessionName);
            stale.Stop();
            return;
        }
    }

    private static KernelTraceEventParser.Keywords ToKeywords(KernelGroups groups)
    {
        var keywords = KernelTraceEventParser.Keywords.None;
        if (groups.HasFlag(KernelGroups.Tcp))
            keywords |= KernelTraceEventParser.Keywords.NetworkTCPIP;
        if (groups.HasFlag(KernelGroups.File))
            keywords |= KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO;
        if (groups.HasFlag(KernelGroups.Disk))
            keywords |= KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.DiskIO;
        if (groups.HasFlag(KernelGroups.Process))
            keywords |= KernelTraceEventParser.Keywords.Process;
        if (groups.HasFlag(KernelGroups.Thread))
            keywords |= KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch;
        if (groups.HasFlag(KernelGroups.Registry))
            keywords |= KernelTraceEventParser.Keywords.Registry;
        return keywords;
    }

    private void Emit(TraceEvent data, string provider, EventCategory category)
    {
        // Check for subscribers BEFORE allocating the record: consumers that
        // only use the typed parser path (e.g. winops, which enables the
        // very-high-volume Thread/ContextSwitch group) would otherwise pay
        // one allocation per kernel event for events nobody observes.
        if (EventReceived is { } handler)
            handler(new KernelEvent(
                data.TimeStamp, provider, data.EventName, data.ProcessID, data.ThreadID, category));
    }

    /// <summary>
    /// Subscribe the typed kernel-parser handlers for the enabled groups.
    /// Typed handlers (rather than a catch-all) keep event naming and
    /// categorisation exact, and each event reaches us exactly once.
    /// The Registry group intentionally gets NO wiring here: the normalised
    /// <see cref="EventCategory"/> model has no registry category, so
    /// registry consumers (step 3's <see cref="OpsTracker"/>) subscribe to
    /// the typed registry events on <see cref="KernelParser"/> directly.
    /// Enabling the keyword (see ToKeywords) is all the session must do.
    /// </summary>
    private void WireHandlers(KernelTraceEventParser parser)
    {
        if (_groups.HasFlag(KernelGroups.Tcp))
        {
            const string p = "Kernel.TcpIp";
            parser.TcpIpConnect += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpAccept += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpDisconnect += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpReconnect += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpSend += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpRecv += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpFail += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpConnectIPV6 += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpAcceptIPV6 += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpDisconnectIPV6 += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpSendIPV6 += d => Emit(d, p, EventCategory.Tcp);
            parser.TcpIpRecvIPV6 += d => Emit(d, p, EventCategory.Tcp);
            parser.UdpIpSend += d => Emit(d, p, EventCategory.Tcp);
            parser.UdpIpRecv += d => Emit(d, p, EventCategory.Tcp);
            parser.UdpIpFail += d => Emit(d, p, EventCategory.Tcp);
            parser.UdpIpSendIPV6 += d => Emit(d, p, EventCategory.Tcp);
            parser.UdpIpRecvIPV6 += d => Emit(d, p, EventCategory.Tcp);
        }

        if (_groups.HasFlag(KernelGroups.File))
        {
            const string p = "Kernel.FileIO";
            parser.FileIOCreate += d => Emit(d, p, EventCategory.File);
            parser.FileIORead += d => Emit(d, p, EventCategory.File);
            parser.FileIOWrite += d => Emit(d, p, EventCategory.File);
            parser.FileIOCleanup += d => Emit(d, p, EventCategory.File);
            parser.FileIOClose += d => Emit(d, p, EventCategory.File);
            parser.FileIODelete += d => Emit(d, p, EventCategory.File);
            parser.FileIORename += d => Emit(d, p, EventCategory.File);
            parser.FileIOSetInfo += d => Emit(d, p, EventCategory.File);
            parser.FileIOQueryInfo += d => Emit(d, p, EventCategory.File);
            parser.FileIOFileCreate += d => Emit(d, p, EventCategory.File);
            parser.FileIODirEnum += d => Emit(d, p, EventCategory.File);
        }

        if (_groups.HasFlag(KernelGroups.Disk))
        {
            const string p = "Kernel.DiskIO";
            parser.DiskIORead += d => Emit(d, p, EventCategory.Disk);
            parser.DiskIOWrite += d => Emit(d, p, EventCategory.Disk);
            parser.DiskIOReadInit += d => Emit(d, p, EventCategory.Disk);
            parser.DiskIOWriteInit += d => Emit(d, p, EventCategory.Disk);
            parser.DiskIOFlushBuffers += d => Emit(d, p, EventCategory.Disk);
        }

        if (_groups.HasFlag(KernelGroups.Process))
        {
            const string p = "Kernel.Process";
            parser.ProcessStart += d => Emit(d, p, EventCategory.Other);
            parser.ProcessStop += d => Emit(d, p, EventCategory.Other);
        }

        if (_groups.HasFlag(KernelGroups.Thread))
        {
            const string p = "Kernel.Thread";
            parser.ThreadStart += d => Emit(d, p, EventCategory.Other);
            parser.ThreadStop += d => Emit(d, p, EventCategory.Other);
            parser.ThreadCSwitch += d => Emit(d, p, EventCategory.Other);
        }
    }
}
