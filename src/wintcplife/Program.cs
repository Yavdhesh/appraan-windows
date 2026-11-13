using System.Net;
using System.Net.Sockets;
using Appraan.Windows;

// wintcplife — BCC tcplife for Windows (Appraan.Windows step 2).
// Usage: wintcplife [--pid N] [--seconds N]
// Traces TCP connections via a kernel ETW session and prints one line per
// connection when it closes: endpoints, bytes sent/received, lifetime.
// Without --pid it watches all processes; without --seconds it runs until
// Ctrl+C. Must be run elevated (KernelSession explains why if not).

int? pidFilter = null;
int seconds = 0; // 0 = until Ctrl+C
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--pid" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p > 0)
    {
        pidFilter = p;
        i++;
    }
    else if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0)
    {
        seconds = s;
        i++;
    }
    else
    {
        Console.Error.WriteLine("usage: wintcplife [--pid N] [--seconds N]");
        return 2;
    }
}

var consoleLock = new object();
long closedCount = 0;
long totalTx = 0;
long totalRx = 0;
TcpConnectionRecord? longest = null;

var tracker = new TcpConnectionTracker(pidFilter);
tracker.ConnectionClosed += record =>
{
    lock (consoleLock)
    {
        closedCount++;
        totalTx += record.BytesSent;
        totalRx += record.BytesReceived;
        if (longest is null || record.DurationMs > longest.DurationMs)
            longest = record;
        Console.WriteLine(FormatRow(record));
    }
};

using var session = new KernelSession(KernelGroups.Tcp);
try
{
    session.Start();
    tracker.Attach(session.KernelParser);
}
catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"wintcplife: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var processing = Task.Run(() => session.ProcessEvents(cts.Token));

string scope = pidFilter is int only ? $"pid {only}" : "all processes";
string limit = seconds > 0 ? $"for {seconds}s" : "until Ctrl+C";
Console.WriteLine($"wintcplife: watching {scope} {limit} (session '{KernelSession.SessionName}').");
Console.WriteLine(FormatHeader());

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
try
{
    while (!cts.IsCancellationRequested &&
           (seconds == 0 || stopwatch.Elapsed.TotalSeconds < seconds))
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        double t = stopwatch.Elapsed.TotalSeconds;
        lock (consoleLock)
            Console.WriteLine($"[t+{t,4:0}s] open connections: {tracker.OpenCount}, closed so far: {closedCount}");
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

double elapsed = stopwatch.Elapsed.TotalSeconds;
Console.WriteLine();
Console.WriteLine($"=== wintcplife summary: {closedCount} connection(s) closed over ~{elapsed:0}s ===");
Console.WriteLine($"total bytes: TX {totalTx / 1024.0:0.00} KB ({totalTx} bytes), RX {totalRx / 1024.0:0.00} KB ({totalRx} bytes)");
if (longest is not null)
    Console.WriteLine($"longest-lived: pid {longest.ProcessId} ({DisplayName(longest)}) " +
                      $"{Endpoint(longest.LocalAddress, longest.LocalPort)} -> " +
                      $"{Endpoint(longest.RemoteAddress, longest.RemotePort)} — {longest.DurationMs:0.0} ms");
Console.WriteLine($"still open (never seen to close): {tracker.OpenCount}");
Console.WriteLine($"unmatched disconnects (start never seen, no record printed): {tracker.UnmatchedDisconnects}");
if (closedCount == 0)
    Console.WriteLine("note: no connections closed in the window — was the target making short-lived TCP connections?");
return 0;

static string FormatHeader() =>
    $"{"PID",-6} {"COMM",-14} {"LADDR:LPORT",-24} {"RADDR:RPORT",-24} {"TX_KB",8} {"RX_KB",8} {"MS",9}";

static string FormatRow(TcpConnectionRecord r) =>
    $"{r.ProcessId,-6} {Truncate(DisplayName(r), 14),-14} " +
    $"{Endpoint(r.LocalAddress, r.LocalPort),-24} {Endpoint(r.RemoteAddress, r.RemotePort),-24} " +
    $"{r.BytesSent / 1024.0,8:0.00} {r.BytesReceived / 1024.0,8:0.00} {r.DurationMs,9:0.0}";

static string DisplayName(TcpConnectionRecord r) =>
    string.IsNullOrEmpty(r.ProcessName) ? "?" : r.ProcessName;

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

static string Endpoint(IPAddress address, int port) =>
    address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
