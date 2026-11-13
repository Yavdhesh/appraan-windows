using System.Globalization;
using System.Text;
using Appraan.Windows;

// winagent — the Appraan sidecar (Appraan.Windows integration step).
// Usage: winagent <PID> [--out <path>] [--push <url>]
//                 [--seconds N] [--interval S] [--registry]
// At least one of --out / --push is required.
//
// Why this exists: Windows allows effectively ONE NT Kernel Logger
// session ("AppraanSession"), and every Appraan.Windows tool stops any
// stale session when it starts — so winops + wintcplife + winoffcpu can
// NOT run side by side as separate processes. winagent is the single ETW
// consumer for a PID: it hosts all three library trackers (OpsTracker,
// TcpConnectionTracker, OffCpuTracker) in one KernelSession and, every
// interval, publishes one JSON snapshot document to its sinks:
//
//   --push <url>  PRIMARY: POST the document to Appraan's UI server
//                 (http://127.0.0.1:7676/api/kernel/ingest). A failed
//                 push never stops the agent — snapshots are cumulative,
//                 so the next successful push self-heals; a one-line
//                 warning marks the start of each failure streak.
//   --out <path>  FALLBACK / RECORDER: atomically rewrite a local file
//                 (temp file + rename, so readers never see a torn
//                 write) that Appraan can poll via its --kernel-file
//                 option, and that serves as the run's evidence.
//
// The split is also an elevation split — ETW needs an elevated process,
// Appraan's Attach API wants the target's own user — so winagent runs
// elevated in its own window and Appraan never elevates.
//
// Without --seconds it runs until Ctrl+C. Must be run elevated
// (KernelSession explains why if not).

const string Usage = "usage: winagent <PID> [--out <path>] [--push <url>] [--seconds N] [--interval S] [--registry]";

int? targetPid = null;
string? outPath = null;
string? pushUrl = null;
int seconds = 0;   // 0 = until Ctrl+C
int interval = 5;  // seconds between snapshot publishes
bool registryRequested = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out" && i + 1 < args.Length)
    {
        outPath = args[i + 1];
        i++;
    }
    else if (args[i] == "--push" && i + 1 < args.Length)
    {
        pushUrl = args[i + 1];
        i++;
    }
    else if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0)
    {
        seconds = s;
        i++;
    }
    else if (args[i] == "--interval" && i + 1 < args.Length && int.TryParse(args[i + 1], out int iv) && iv > 0)
    {
        interval = iv;
        i++;
    }
    else if (args[i] == "--registry")
    {
        registryRequested = true;
    }
    else if (targetPid is null && int.TryParse(args[i], out int p) && p > 0)
    {
        targetPid = p;
    }
    else
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
if (targetPid is not int pid || (outPath is null && pushUrl is null))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string? tempPath = null;
if (outPath is not null)
{
    string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
    Directory.CreateDirectory(outDir);
    tempPath = Path.Combine(outDir, Path.GetFileName(outPath) + ".tmp");
}

var groups = KernelGroups.All;
if (registryRequested)
    groups |= KernelGroups.Registry;

var opsTracker = new OpsTracker(pid);
var offCpuTracker = new OffCpuTracker(pid);
var tcpTracker = new TcpConnectionTracker(pid);

// TCP totals + the most recent closed connections are accumulated here:
// the tracker itself only exposes the open set and the close event.
var tcpGate = new object();
long tcpClosedTotal = 0;
long tcpTxBytes = 0;
long tcpRxBytes = 0;
var recentClosed = new List<TcpConnectionRecord>();
tcpTracker.ConnectionClosed += record =>
{
    lock (tcpGate)
    {
        tcpClosedTotal++;
        tcpTxBytes += record.BytesSent;
        tcpRxBytes += record.BytesReceived;
        recentClosed.Add(record);
        if (recentClosed.Count > 20)
            recentClosed.RemoveAt(0);
    }
};

bool registryActive = registryRequested;
KernelSession session;
try
{
    session = new KernelSession(groups);
    try
    {
        session.Start();
    }
    catch (InvalidOperationException ex) when (registryRequested && !IsElevationError(ex))
    {
        // Same rule as winops: registry must never break the core tool.
        Console.Error.WriteLine($"winagent: could not enable registry tracing ({ex.Message}); continuing without Registry counts.");
        session.Dispose();
        registryActive = false;
        session = new KernelSession(groups & ~KernelGroups.Registry);
        session.Start();
    }
    opsTracker.Attach(session.KernelParser);
    tcpTracker.Attach(session.KernelParser);
    offCpuTracker.Attach(session.KernelParser);
}
catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"winagent: {ex.Message}");
    return 1;
}

using var sessionScope = session;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var processing = Task.Run(() => session.ProcessEvents(cts.Token));

string limit = seconds > 0 ? $"for {seconds}s" : "until Ctrl+C";
Console.WriteLine($"winagent: watching pid {pid} {limit} " +
                  $"(session '{KernelSession.SessionName}', registry: {(registryActive ? "on" : "off")}).");
Console.WriteLine($"winagent: publishing snapshots every {interval}s:");
if (pushUrl is not null)
    Console.WriteLine($"winagent:   push -> {pushUrl}");
if (outPath is not null)
    Console.WriteLine($"winagent:   file -> {outPath}");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();

// Short timeout: a wedged consumer must never stall the publish loop —
// the next interval's snapshot supersedes this one anyway.
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
int pushFailStreak = 0;

async Task PublishSnapshot()
{
    string json = BuildSnapshot();
    if (tempPath is not null && outPath is not null)
    {
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, outPath, overwrite: true);
    }
    if (pushUrl is not null)
    {
        try
        {
            // No caller token: the HttpClient timeout bounds the attempt,
            // and the final publish at exit must still go out after Ctrl+C
            // has cancelled the loop's token.
            await KernelPush.PostAsync(http, pushUrl, json);
            if (pushFailStreak > 0)
            {
                Console.WriteLine($"winagent: push to {pushUrl} recovered " +
                                  $"after {pushFailStreak} failed attempt(s).");
                pushFailStreak = 0;
            }
        }
        catch (Exception ex)
        {
            // A failed push never stops the agent: snapshots are
            // cumulative, so the next successful push self-heals. One
            // warning line per failure streak, not per attempt.
            pushFailStreak++;
            if (pushFailStreak == 1)
                Console.Error.WriteLine(
                    $"winagent: push to {pushUrl} failed ({ex.GetType().Name}: {ex.Message}); " +
                    "retrying each interval — snapshots are cumulative, the next success self-heals.");
        }
    }
}

string BuildSnapshot()
{
    var ops = opsTracker.GetStats(pid);
    var diag = opsTracker.GetDiagnostics();
    var offCpu = offCpuTracker.GetStats();
    lock (tcpGate)
    {
        return KernelJson.Build(
            pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            stopwatch.Elapsed.TotalSeconds,
            ops, diag, offCpu,
            tcpClosedTotal, tcpTxBytes, tcpRxBytes,
            tcpTracker.OpenCount, tcpTracker.UnmatchedDisconnects,
            recentClosed);
    }
}

try
{
    await PublishSnapshot(); // an initial (zeroed) snapshot from t=0
    while (!cts.IsCancellationRequested &&
           (seconds == 0 || stopwatch.Elapsed.TotalSeconds < seconds))
    {
        await Task.Delay(TimeSpan.FromSeconds(interval), cts.Token);
        await PublishSnapshot();
        double t = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine($"[t+{t,4:0}s] snapshot published " +
                          $"(off-CPU {offCpuTracker.GetStats().OffCpuPercent:0.0}%)");
    }
}
catch (TaskCanceledException)
{
    // Ctrl+C — fall through to the final snapshot.
}
catch (Exception ex)
{
    Console.Error.WriteLine($"winagent: snapshot publish failed: {ex.Message}");
    session.Stop();
    await processing;
    return 1;
}
finally
{
    session.Stop();
}

await processing;

await PublishSnapshot(); // final publish at exit, with the complete totals
Console.WriteLine($"winagent: done — final snapshot published " +
                  $"after ~{stopwatch.Elapsed.TotalSeconds:0}s.");
return 0;

static bool IsElevationError(InvalidOperationException ex) =>
    ex.Message.Contains("elevated", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// Renders one snapshot as the JSON document Appraan consumes. The shape
/// is the integration contract (see Appraan's DESIGN.md): hand-built with
/// invariant-culture numbers and proper string escaping — no JSON library,
/// matching both projects' zero-dependency rule.
/// </summary>
internal static class KernelJson
{
    public static string Build(
        int pid, long tEpochMs, double windowSeconds,
        OpsStats ops, OpsDiagnostics diag, ProcessOffCpuStats offCpu,
        long tcpClosedTotal, long tcpTxBytes, long tcpRxBytes,
        int tcpStillOpen, long tcpUnmatchedDisconnects,
        IReadOnlyList<TcpConnectionRecord> recent)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"pid\":").Append(pid);
        sb.Append(",\"tEpochMs\":").Append(tEpochMs);
        sb.Append(",\"windowSeconds\":").Append(Num(windowSeconds));

        // ---- ops: operations by category, with latency where reported ----
        sb.Append(",\"ops\":{\"categories\":[");
        bool firstCategory = true;
        foreach (var category in Enum.GetValues<OpsCategory>())
        {
            var s = ops.For(category);
            if (!firstCategory) sb.Append(',');
            firstCategory = false;
            string name = category.ToString();
            sb.Append("{\"name\":").Append(Str(name));
            sb.Append(",\"count\":").Append(s.Count);
            sb.Append(",\"latOps\":").Append(s.LatencyCount);
            if (s.LatencyCount > 0)
            {
                sb.Append(",\"avgUs\":").Append(Num(s.AverageLatencyUs));
                sb.Append(",\"maxUs\":").Append(Num(s.MaxLatencyUs));
            }
            else
            {
                sb.Append(",\"avgUs\":null,\"maxUs\":null");
            }
            sb.Append(",\"kinds\":{");
            string prefix = name + ".";
            bool firstKind = true;
            foreach (var kv in ops.KindCounts
                         .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                         .OrderByDescending(kv => kv.Value)
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!firstKind) sb.Append(',');
                firstKind = false;
                sb.Append(Str(kv.Key[prefix.Length..])).Append(':').Append(kv.Value);
            }
            sb.Append("}}");
        }
        sb.Append("],\"diagnostics\":{");
        sb.Append("\"pendingFileOps\":").Append(diag.PendingOperations);
        sb.Append(",\"unmatchedCompletions\":").Append(diag.UnmatchedCompletions);
        sb.Append(",\"duplicateStarts\":").Append(diag.DuplicateStarts);
        sb.Append(",\"evictedPending\":").Append(diag.EvictedPending);
        sb.Append("}}");

        // ---- tcp: closed-connection totals + the most recent closes ----
        sb.Append(",\"tcp\":{");
        sb.Append("\"closedTotal\":").Append(tcpClosedTotal);
        sb.Append(",\"txBytes\":").Append(tcpTxBytes);
        sb.Append(",\"rxBytes\":").Append(tcpRxBytes);
        sb.Append(",\"stillOpen\":").Append(tcpStillOpen);
        sb.Append(",\"unmatchedDisconnects\":").Append(tcpUnmatchedDisconnects);
        sb.Append(",\"recent\":[");
        for (int i = 0; i < recent.Count; i++)
        {
            var r = recent[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"pid\":").Append(r.ProcessId);
            sb.Append(",\"comm\":").Append(Str(string.IsNullOrEmpty(r.ProcessName) ? "?" : r.ProcessName));
            sb.Append(",\"laddr\":").Append(Str(r.LocalAddress.ToString()));
            sb.Append(",\"lport\":").Append(r.LocalPort);
            sb.Append(",\"raddr\":").Append(Str(r.RemoteAddress.ToString()));
            sb.Append(",\"rport\":").Append(r.RemotePort);
            sb.Append(",\"txKb\":").Append(NumKb(r.BytesSent / 1024.0));
            sb.Append(",\"rxKb\":").Append(NumKb(r.BytesReceived / 1024.0));
            sb.Append(",\"ms\":").Append(Num(r.DurationMs));
            sb.Append('}');
        }
        sb.Append("]}");

        // ---- offCpu: on/off-CPU totals, wait reasons, hottest threads ----
        sb.Append(",\"offCpu\":{");
        sb.Append("\"onCpuMs\":").Append(Num(offCpu.OnCpuMs));
        sb.Append(",\"offCpuMs\":").Append(Num(offCpu.OffCpuMs));
        sb.Append(",\"offCpuPct\":").Append(Num(offCpu.OffCpuPercent));
        sb.Append(",\"episodes\":").Append(offCpu.OffCpuEpisodes);
        sb.Append(",\"maxEpisodeMs\":").Append(Num(offCpu.MaxOffCpuEpisodeMs));
        sb.Append(",\"reasons\":[");
        int reasonCount = 0;
        foreach (var reason in offCpu.WaitReasons)
        {
            if (reasonCount >= 10) break;
            if (reasonCount > 0) sb.Append(',');
            reasonCount++;
            double pct = offCpu.OffCpuMs > 0 ? 100.0 * reason.TotalMs / offCpu.OffCpuMs : 0;
            sb.Append("{\"name\":").Append(Str(reason.Reason));
            sb.Append(",\"episodes\":").Append(reason.Episodes);
            sb.Append(",\"totalMs\":").Append(Num(reason.TotalMs));
            sb.Append(",\"pct\":").Append(Num(pct));
            sb.Append('}');
        }
        sb.Append("],\"threads\":[");
        int threadCount = 0;
        foreach (var thread in offCpu.Threads)
        {
            if (threadCount >= 10) break;
            if (threadCount > 0) sb.Append(',');
            threadCount++;
            sb.Append("{\"tid\":").Append(thread.ThreadId);
            sb.Append(",\"episodes\":").Append(thread.OffCpuEpisodes);
            sb.Append(",\"offCpuMs\":").Append(Num(thread.OffCpuMs));
            sb.Append(",\"maxMs\":").Append(Num(thread.MaxOffCpuEpisodeMs));
            sb.Append(",\"onCpuMs\":").Append(Num(thread.OnCpuMs));
            sb.Append(",\"offCpuPct\":").Append(Num(thread.OffCpuPercent));
            sb.Append(",\"partial\":").Append(thread.Partial ? "true" : "false");
            sb.Append('}');
        }
        sb.Append("]}");

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>JSON number with up to 3 decimals, invariant culture, no grouping.</summary>
    private static string Num(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Kilobyte figure at exactly 2 decimals — the display convention
    /// wintcplife established (32 bytes = 0.03 KB), kept so the snapshot's
    /// per-connection numbers read identically to the tool's own output.
    /// </summary>
    private static string NumKb(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>JSON string literal with RFC 8259 escaping.</summary>
    private static string Str(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// POSTs one snapshot document to Appraan's ingest endpoint. Kept as a
/// separate unit (like <see cref="KernelJson"/>) so the failure semantics
/// live in exactly one place: any failure — connection refused, timeout,
/// non-2xx — surfaces as an exception for the caller's streak handling;
/// nothing here retries or swallows.
/// </summary>
internal static class KernelPush
{
    public static async Task PostAsync(HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
