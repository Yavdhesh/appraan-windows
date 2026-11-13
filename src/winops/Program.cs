using System.Text;
using Appraan.Windows;

// winops — kernel operations by category, with latency, for one process
// (Appraan.Windows step 3): the parity-contract form of Linux syscount.
// Usage: winops <PID> [--seconds N] [--registry]
// Prints the category table every 5 s and again at exit. File/Disk rows
// carry latency stats and a histogram; Registry counts need --registry.
// Without --seconds it runs until Ctrl+C. Must be run elevated
// (KernelSession explains why if not).

int? targetPid = null;
int seconds = 0; // 0 = until Ctrl+C
bool registryRequested = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0)
    {
        seconds = s;
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
        Console.Error.WriteLine("usage: winops <PID> [--seconds N] [--registry]");
        return 2;
    }
}
if (targetPid is not int pid)
{
    Console.Error.WriteLine("usage: winops <PID> [--seconds N] [--registry]");
    return 2;
}

var groups = KernelGroups.File | KernelGroups.Disk | KernelGroups.Tcp |
             KernelGroups.Process | KernelGroups.Thread;
if (registryRequested)
    groups |= KernelGroups.Registry;

var tracker = new OpsTracker(pid);
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
        // Registry must never break the core tool: if enabling it in the
        // kernel session fails for any reason other than elevation, warn
        // and continue without Registry counts.
        Console.Error.WriteLine($"winops: could not enable registry tracing ({ex.Message}); continuing without Registry counts.");
        session.Dispose();
        registryActive = false;
        session = new KernelSession(groups & ~KernelGroups.Registry);
        session.Start();
    }
    tracker.Attach(session.KernelParser);
}
catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"winops: {ex.Message}");
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
Console.WriteLine($"winops: watching pid {pid} {limit} " +
                  $"(session '{KernelSession.SessionName}', registry: {(registryActive ? "on" : "off")}).");
Console.WriteLine("latencies are in microseconds (us); 1000 us = 1 ms");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
try
{
    while (!cts.IsCancellationRequested &&
           (seconds == 0 || stopwatch.Elapsed.TotalSeconds < seconds))
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        double t = stopwatch.Elapsed.TotalSeconds;
        PrintTable($"[t+{t,4:0}s] pid {pid} (cumulative)",
                   tracker.GetStats(pid), tracker.GetDiagnostics(),
                   registryActive, includeKinds: false);
    }
}
catch (TaskCanceledException)
{
    // Ctrl+C — fall through to the summary.
}
finally
{
    session.Stop();
}

await processing;

var finalStats = tracker.GetStats(pid);
var finalDiag = tracker.GetDiagnostics();
Console.WriteLine();
Console.WriteLine($"=== winops summary: pid {pid} over ~{stopwatch.Elapsed.TotalSeconds:0}s ===");
PrintTable("", finalStats, finalDiag, registryActive, includeKinds: true);
long totalOps = Enum.GetValues<OpsCategory>().Sum(c => finalStats.For(c).Count);
Console.WriteLine($"total operations: {totalOps}");
if (finalStats.For(OpsCategory.File).Count == 0 && finalStats.For(OpsCategory.Network).Count == 0)
    Console.WriteLine("note: no file or network operations observed for this pid — was it active during the window?");
return 0;

static bool IsElevationError(InvalidOperationException ex) =>
    ex.Message.Contains("elevated", StringComparison.OrdinalIgnoreCase);

static void PrintTable(string heading, OpsStats stats, OpsDiagnostics diag,
                       bool registryActive, bool includeKinds)
{
    if (heading.Length > 0)
        Console.WriteLine(heading);
    Console.WriteLine($"{"CATEGORY",-14} {"COUNT",8} {"LAT_OPS",8} {"AVG_US",10} {"MAX_US",10}");
    foreach (var category in Enum.GetValues<OpsCategory>())
    {
        var s = stats.For(category);
        string name = category.ToString();
        if (category == OpsCategory.Registry && !registryActive)
        {
            Console.WriteLine($"{name,-14} {"-",8} {"-",8} {"-",10} {"-",10}  (not enabled — pass --registry)");
            continue;
        }

        string latOps = s.LatencyCount > 0 ? s.LatencyCount.ToString() : "-";
        string avg = s.LatencyCount > 0 ? s.AverageLatencyUs.ToString("0.0") : "-";
        string max = s.LatencyCount > 0 ? s.MaxLatencyUs.ToString("0.0") : "-";
        Console.WriteLine($"{name,-14} {s.Count,8} {latOps,8} {avg,10} {max,10}");

        if (s.LatencyCount > 0 && category is OpsCategory.File or OpsCategory.Disk)
            Console.WriteLine($"  hist: {FormatHistogram(s.LatencyHistogram)}");

        if (includeKinds)
        {
            string prefix = name + ".";
            var kinds = stats.KindCounts
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key[prefix.Length..]}:{kv.Value}")
                .ToList();
            if (kinds.Count > 0)
                Console.WriteLine($"  kinds: {string.Join("  ", kinds)}");
        }
    }
    Console.WriteLine($"diagnostics: pending file ops={diag.PendingOperations}  " +
                      $"unmatched completions={diag.UnmatchedCompletions}  " +
                      $"duplicate starts={diag.DuplicateStarts}  " +
                      $"evicted pending={diag.EvictedPending}");
    Console.WriteLine();
}

static string FormatHistogram(IReadOnlyList<long> histogram)
{
    var sb = new StringBuilder();
    for (int i = 0; i < histogram.Count && i < OpsLatencyHistogram.Labels.Count; i++)
    {
        if (histogram[i] == 0)
            continue;
        if (sb.Length > 0)
            sb.Append("  ");
        sb.Append(OpsLatencyHistogram.Labels[i]).Append(':').Append(histogram[i]);
    }
    return sb.ToString();
}
