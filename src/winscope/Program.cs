using Appraan.Windows;

// winscope — step-1 smoke-test CLI for Appraan.Windows.
// Usage: winscope <PID> [--seconds N]
// Starts a kernel ETW session, counts the target process's events by
// category (Tcp / File / Disk / Other), prints a live line every 2 s and a
// final summary. Must be run elevated (KernelSession explains why if not).

if (args.Length < 1 || !int.TryParse(args[0], out int targetPid))
{
    Console.Error.WriteLine("usage: winscope <PID> [--seconds N]");
    return 2;
}

int seconds = 10;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--seconds" && int.TryParse(args[i + 1], out int parsed) && parsed > 0)
        seconds = parsed;
}

var counter = new PidEventCounter();
using var session = new KernelSession(KernelGroups.All);
session.EventReceived += counter.Record;

try
{
    session.Start();
}
catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"winscope: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var processing = Task.Run(() => session.ProcessEvents(cts.Token));

Console.WriteLine($"winscope: watching pid {targetPid} for {seconds}s " +
                  $"(session '{KernelSession.SessionName}'). Ctrl+C to stop early.");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
try
{
    while (!cts.IsCancellationRequested && stopwatch.Elapsed.TotalSeconds < seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        double t = Math.Min(stopwatch.Elapsed.TotalSeconds, seconds);
        var c = counter.GetCounts(targetPid);
        Console.WriteLine($"[t+{t,4:0}s] pid {targetPid}: Tcp={c.Tcp} File={c.File} " +
                          $"Disk={c.Disk} Other={c.Other} (total {c.Total})");
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

var final = counter.GetCounts(targetPid);
var all = counter.GetTotal();
Console.WriteLine();
Console.WriteLine($"=== winscope summary: pid {targetPid} over ~{stopwatch.Elapsed.TotalSeconds:0}s ===");
Console.WriteLine($"Tcp={final.Tcp}  File={final.File}  Disk={final.Disk}  Other={final.Other}  Total={final.Total}");
Console.WriteLine($"(all processes in the same window: {all.Total} events)");
if (final.Tcp == 0 && final.File == 0)
    Console.WriteLine("note: no Tcp/File events for this pid — was it active during the window?");
return 0;
