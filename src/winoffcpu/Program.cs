using Appraan.Windows;

// winoffcpu — off-CPU analysis for one process (Appraan.Windows step 4):
// the Windows analogue of BCC's offcputime, minus stack capture (a later
// step). Built on ETW context switches: every time a thread of the target
// process leaves a CPU an off-CPU episode opens (stamped with the kernel's
// wait reason); when the thread is scheduled again the episode closes.
// Symmetric on-CPU accounting gives off-CPU % per thread and per process.
// Usage: winoffcpu <PID> [--seconds N]
// Prints process totals, the wait-reason table and the top threads every
// 5 s and again at exit. Without --seconds it runs until Ctrl+C. Must be
// run elevated (KernelSession explains why if not).

int? targetPid = null;
int seconds = 0; // 0 = until Ctrl+C
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out int s) && s > 0)
    {
        seconds = s;
        i++;
    }
    else if (targetPid is null && int.TryParse(args[i], out int p) && p > 0)
    {
        targetPid = p;
    }
    else
    {
        Console.Error.WriteLine("usage: winoffcpu <PID> [--seconds N]");
        return 2;
    }
}
if (targetPid is not int pid)
{
    Console.Error.WriteLine("usage: winoffcpu <PID> [--seconds N]");
    return 2;
}

// Thread enables ThreadStart/Stop + ContextSwitch; Process adds
// ProcessStop so a dying process's threads are all closed out cleanly.
var groups = KernelGroups.Thread | KernelGroups.Process;
var tracker = new OffCpuTracker(pid);
KernelSession session;
try
{
    session = new KernelSession(groups);
    session.Start();
    tracker.Attach(session.KernelParser);
}
catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"winoffcpu: {ex.Message}");
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
Console.WriteLine($"winoffcpu: watching pid {pid} {limit} (session '{KernelSession.SessionName}').");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
try
{
    while (!cts.IsCancellationRequested &&
           (seconds == 0 || stopwatch.Elapsed.TotalSeconds < seconds))
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        double t = stopwatch.Elapsed.TotalSeconds;
        PrintReport($"[t+{t,4:0}s] pid {pid} (cumulative)",
                    tracker.GetStats(), tracker.GetDiagnostics());
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

Console.WriteLine();
Console.WriteLine($"=== winoffcpu summary: pid {pid} over ~{stopwatch.Elapsed.TotalSeconds:0}s ===");
PrintReport("", tracker.GetStats(), tracker.GetDiagnostics());
return 0;

static void PrintReport(string heading, ProcessOffCpuStats stats, OffCpuDiagnostics diag)
{
    if (heading.Length > 0)
        Console.WriteLine(heading);
    Console.WriteLine($"on-CPU {stats.OnCpuMs,10:0.0} ms   off-CPU {stats.OffCpuMs,10:0.0} ms   " +
                      $"off-CPU {stats.OffCpuPercent,5:0.0}%   episodes {stats.OffCpuEpisodes}   " +
                      $"max episode {stats.MaxOffCpuEpisodeMs:0.0} ms");

    Console.WriteLine("wait reasons:");
    if (stats.WaitReasons.Count == 0)
    {
        Console.WriteLine("  (none yet)");
    }
    else
    {
        Console.WriteLine($"  {"REASON",-16} {"EPISODES",8} {"TOTAL_MS",10} {"%",6}");
        foreach (var reason in stats.WaitReasons)
        {
            double pct = stats.OffCpuMs <= 0 ? 0 : 100.0 * reason.TotalMs / stats.OffCpuMs;
            Console.WriteLine($"  {reason.Reason,-16} {reason.Episodes,8} {reason.TotalMs,10:0.0} {pct,6:0.0}");
        }
    }

    Console.WriteLine("top threads by off-CPU:");
    if (stats.Threads.Count == 0)
    {
        Console.WriteLine("  (no threads observed yet — was the process running?)");
    }
    else
    {
        Console.WriteLine($"  {"TID",8} {"EPISODES",8} {"OFFCPU_MS",10} {"MAX_MS",8} {"ONCPU_MS",10} {"OFFCPU_%",8}");
        foreach (var thread in stats.Threads.Take(10))
        {
            string partial = thread.Partial ? "  (partial)" : "";
            Console.WriteLine($"  {thread.ThreadId,8} {thread.OffCpuEpisodes,8} {thread.OffCpuMs,10:0.0} " +
                              $"{thread.MaxOffCpuEpisodeMs,8:0.0} {thread.OnCpuMs,10:0.0} {thread.OffCpuPercent,8:0.0}{partial}");
        }
    }

    Console.WriteLine($"diagnostics: threads observed={diag.ThreadsObserved}  partial={diag.PartialThreads}  " +
                      $"episodes closed={diag.EpisodesClosed} (by thread exit={diag.EpisodesClosedByThreadExit})  " +
                      $"switch-ins w/o episode={diag.SwitchInsWithoutEpisode}  " +
                      $"switch-outs w/o on-CPU={diag.SwitchOutsWithoutOnCpu}  " +
                      $"open off-CPU={diag.OpenOffCpuThreads}  open on-CPU={diag.OpenOnCpuThreads}");
    Console.WriteLine();
}
