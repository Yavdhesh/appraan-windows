using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Appraan.Windows;

// appraanagent — the Appraan sidecar (Appraan.Windows integration step).
// Usage: appraanagent <PID> [--out <path>] [--push <url>]
//                 [--seconds N] [--interval S] [--registry]
//                 [--key-file <path>] [--tls-cert <path>] [--label k=v]…
//        appraanagent --gen-key <path>
// At least one of --out / --push is required.
//
// Feed transport & security (DEFAULT: literal plain JSON over HTTP —
// no compression, no encryption. Everything else is opt-in — see
// Appraan's README, "Kernel feed transport & security configuration"):
//   --gzip             Gzip-compress the push body (smallest size).
//   --key-file <path>  AES-256-GCM shared key (base64 of 32 bytes, one
//                      line): the push body becomes an encrypted
//                      envelope over the JSON (gzipped first only when
//                      --gzip is also given). The --out file stays plain.
//   --gen-key <path>   Write a fresh random key file and exit.
//   --tls-cert <path>  For https:// push URLs: trust exactly this server
//                      certificate (.cer DER or PEM), nothing else.
//   --label k=v        Pod/centre-collector foresight: repeatable labels
//                      attached to every snapshot (plus "host", filled
//                      automatically with this machine's name).
//
// Why this exists: Windows allows effectively ONE NT Kernel Logger
// session ("AppraanSession"), and every Appraan.Windows tool stops any
// stale session when it starts — so winops + wintcplife + winoffcpu can
// NOT run side by side as separate processes. appraanagent is the single ETW
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
// Appraan's Attach API wants the target's own user — so appraanagent runs
// elevated in its own window and Appraan never elevates.
//
// Without --seconds it runs until Ctrl+C. Must be run elevated
// (KernelSession explains why if not).

const string Usage = "usage: appraanagent <PID> [--out <path>] [--push <url>] [--seconds N] [--interval S] [--registry] [--gzip] [--key-file <path>] [--tls-cert <path>] [--label k=v]...\n" +
                     "       appraanagent --gen-key <path>";

// --gen-key stands alone: create a key file and exit (works anywhere,
// no PID, no elevation, no ETW).
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--gen-key" && i + 1 < args.Length)
    {
        string keyPath = args[i + 1];
        File.WriteAllText(keyPath,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        Console.WriteLine($"appraanagent: wrote a fresh 256-bit key to {keyPath}");
        Console.WriteLine("appraanagent: keep it secret — pass it to this agent with --key-file " +
                          "and to Appraan with --kernel-key-file.");
        return 0;
    }
}

int? targetPid = null;
string? outPath = null;
string? pushUrl = null;
string? keyFile = null;
string? tlsCertPath = null;
bool gzipPush = false;   // opt-in; default is literal plain JSON
var labels = new SortedDictionary<string, string>(StringComparer.Ordinal);
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
    else if (args[i] == "--key-file" && i + 1 < args.Length)
    {
        keyFile = args[i + 1];
        i++;
    }
    else if (args[i] == "--tls-cert" && i + 1 < args.Length)
    {
        tlsCertPath = args[i + 1];
        i++;
    }
    else if (args[i] == "--label" && i + 1 < args.Length)
    {
        string kv = args[i + 1];
        int eq = kv.IndexOf('=');
        if (eq <= 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        labels[kv[..eq]] = kv[(eq + 1)..];
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
    else if (args[i] == "--gzip")
    {
        gzipPush = true;
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

// Shared-key mode: load + validate the key up front (fail fast, before
// any ETW session starts).
byte[]? pushKey = null;
if (keyFile is not null)
{
    try
    {
        pushKey = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
        if (pushKey.Length != 32)
            throw new FormatException($"expected 32 key bytes, got {pushKey.Length}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"appraanagent: cannot load key file '{keyFile}': {ex.Message}");
        return 2;
    }
}

// TLS pinning: load the one certificate the push endpoint must present.
X509Certificate2? pinnedCert = null;
if (tlsCertPath is not null)
{
    try
    {
        string text = File.ReadAllText(tlsCertPath);
        pinnedCert = text.StartsWith("-----BEGIN", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : new X509Certificate2(File.ReadAllBytes(tlsCertPath));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"appraanagent: cannot load TLS certificate '{tlsCertPath}': {ex.Message}");
        return 2;
    }
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
        Console.Error.WriteLine($"appraanagent: could not enable registry tracing ({ex.Message}); continuing without Registry counts.");
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
    Console.Error.WriteLine($"appraanagent: {ex.Message}");
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
Console.WriteLine($"appraanagent: watching pid {pid} {limit} " +
                  $"(session '{KernelSession.SessionName}', registry: {(registryActive ? "on" : "off")}).");
Console.WriteLine($"appraanagent: publishing snapshots every {interval}s:");
if (pushUrl is not null)
{
    Console.WriteLine($"appraanagent:   push -> {pushUrl}" +
                      (pinnedCert is not null ? " (pinned server cert)" : ""));
    // State the effective feed mode plainly (never any key material).
    Console.WriteLine(pushKey is not null
        ? $"appraanagent: kernel feed: AES-256-GCM encrypted envelope ({(gzipPush ? "gzip" : "plain JSON")} inside)."
        : gzipPush
            ? "appraanagent: kernel feed: gzip-compressed JSON (no encryption)."
            : "appraanagent: kernel feed: plain JSON over HTTP (no encryption).");
}
if (outPath is not null)
    Console.WriteLine($"appraanagent:   file -> {outPath}");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();

// Short timeout: a wedged consumer must never stall the publish loop —
// the next interval's snapshot supersedes this one anyway. With
// --tls-cert, the handler trusts exactly that one certificate (raw-byte
// comparison); without it, https push URLs use normal system trust.
HttpClient http;
if (pinnedCert is not null)
{
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null && cert.GetRawCertData().SequenceEqual(pinnedCert.GetRawCertData()),
    };
    http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
}
else
{
    http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
}
using var httpScope = http;
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
            await KernelPush.PostAsync(http, pushUrl, json, pushKey, gzipPush);
            if (pushFailStreak > 0)
            {
                Console.WriteLine($"appraanagent: push to {pushUrl} recovered " +
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
                    $"appraanagent: push to {pushUrl} failed ({ex.GetType().Name}: {ex.Message}); " +
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
            Environment.MachineName, labels,
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
    Console.Error.WriteLine($"appraanagent: snapshot publish failed: {ex.Message}");
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
Console.WriteLine($"appraanagent: done — final snapshot published " +
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
        string host, IReadOnlyDictionary<string, string> labels,
        OpsStats ops, OpsDiagnostics diag, ProcessOffCpuStats offCpu,
        long tcpClosedTotal, long tcpTxBytes, long tcpRxBytes,
        int tcpStillOpen, long tcpUnmatchedDisconnects,
        IReadOnlyList<TcpConnectionRecord> recent)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"pid\":").Append(pid);
        sb.Append(",\"tEpochMs\":").Append(tEpochMs);
        sb.Append(",\"windowSeconds\":").Append(Num(windowSeconds));
        // Optional pod/collector foresight fields: the producing host and
        // free-form labels. Consumers must treat both as optional.
        sb.Append(",\"host\":").Append(Str(host));
        if (labels.Count > 0)
        {
            sb.Append(",\"labels\":{");
            bool firstLabel = true;
            foreach (var kv in labels)
            {
                if (!firstLabel) sb.Append(',');
                firstLabel = false;
                sb.Append(Str(kv.Key)).Append(':').Append(Str(kv.Value));
            }
            sb.Append('}');
        }

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
            sb.Append('}');
            // Latency histogram with winops' exact bucket labels — only
            // for categories that measured latencies (File, Disk today).
            if (s.LatencyCount > 0)
            {
                sb.Append(",\"hist\":{");
                for (int b = 0; b < OpsLatencyHistogram.Labels.Count; b++)
                {
                    if (b > 0) sb.Append(',');
                    sb.Append(Str(OpsLatencyHistogram.Labels[b])).Append(':')
                      .Append(b < s.LatencyHistogram.Count ? s.LatencyHistogram[b] : 0);
                }
                sb.Append('}');
            }
            sb.Append('}');
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
/// POSTs one snapshot document to Appraan's ingest endpoint. The DEFAULT
/// body is the literal plain JSON (Content-Type: application/json, no
/// encoding, no encryption) — the out-of-the-box feed is plain JSON over
/// HTTP. Opt-in layers, in pipeline order:
///   --gzip      gzip-compress the JSON at the smallest size first
///               (Content-Encoding: gzip on the plain path).
///   --key-file  seal the (possibly gzipped) bytes in an AES-256-GCM
///               envelope — [0x01 version][flags][12-byte random nonce]
///               [ciphertext+tag] — sent as application/octet-stream.
///               flags bit0 records whether the sealed payload was
///               gzipped; the two header bytes are GCM AAD, so header
///               tampering fails decryption.
/// The file sink stays plain text in every mode: it is the
/// human-readable recorder. Kept as a separate unit (like
/// <see cref="KernelJson"/>) so the failure semantics live in exactly
/// one place: any failure — connection refused, timeout, non-2xx —
/// surfaces as an exception for the caller's streak handling; nothing
/// here retries or swallows.
/// </summary>
internal static class KernelPush
{
    /// <summary>
    /// Builds the exact wire body for one snapshot per the mode flags.
    /// Extracted from the POST so the byte format is testable without a
    /// network.
    /// </summary>
    public static (byte[] Body, string ContentType, string? ContentEncoding) BuildBody(
        string json, byte[]? key, bool gzip)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gz.Write(payload, 0, payload.Length);
            }
            payload = ms.ToArray();
        }
        if (key is null)
        {
            return (payload, "application/json", gzip ? "gzip" : null);
        }
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte flags = gzip ? (byte)0x01 : (byte)0x00;
        byte[] aad = { 0x01, flags };
        byte[] ciphertext = new byte[payload.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, payload, ciphertext, tag, aad);
        }
        byte[] envelope = new byte[2 + nonce.Length + ciphertext.Length + tag.Length];
        envelope[0] = 0x01;
        envelope[1] = flags;
        nonce.CopyTo(envelope, 2);
        ciphertext.CopyTo(envelope, 2 + nonce.Length);
        tag.CopyTo(envelope, 2 + nonce.Length + ciphertext.Length);
        return (envelope, "application/octet-stream", null);
    }

    public static async Task PostAsync(HttpClient client, string url, string json,
                                       byte[]? key, bool gzip)
    {
        var (body, contentType, contentEncoding) = BuildBody(json, key, gzip);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }
        using var response = await client.PostAsync(url, content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
