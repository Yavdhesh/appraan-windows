using System.Net;
using System.Net.Sockets;

// winload — known-workload generator used to verify the Appraan.Windows
// tools on Windows. For ~15 s it:
//   * repeatedly opens loopback TCP connections to a listener it hosts
//     itself (ephemeral port), exchanges a few bytes both ways;
//   * repeatedly writes and reads back a temp file;
//   * sleeps briefly between iterations (low CPU, mostly kernel activity).
// Expected winscope result for this pid: Tcp counts > 0 AND File counts > 0.
//
// Optional deterministic mode for verifying a winops category the base
// loop never triggers:
//   --disk      after the loop, a write-through phase writes 256 MiB in
//               256 x 1 MiB blocks (FileOptions.WriteThrough). The base
//               loop's file I/O is cache-served, so winops sees Disk = 0
//               for it; write-through forces physical disk I/O so the
//               Disk category must become non-zero. Verify Disk WRITES
//               (count > 0, latency ops > 0) — the storage stack may
//               split/merge I/Os, so totals need not equal 256, and
//               reads of the file may still be cache-served.
// (There is deliberately no --registry test mode: winops' Registry
// category is observe-only and was already shown working against the
// runtime's own registry activity; the user does not want test code
// writing to his registry. See STATUS.md, Step 3 follow-up.)

int pid = Environment.ProcessId;
Console.WriteLine($"winload pid={pid}");

bool diskMode = args.Contains("--disk");
if (diskMode)
    Console.WriteLine("disk mode: after the loop, a write-through phase writes 256 MiB in 256 x 1 MiB blocks");

// Optional --wait: print the PID and pause until Enter is pressed, so an
// observer (winscope) can be started against this PID before any workload
// happens. This makes the smoke test race-free.
if (args.Contains("--wait"))
{
    Console.WriteLine("waiting — start winscope against this pid, then press Enter to begin the workload...");
    Console.ReadLine();
}

Console.WriteLine("generating TCP + file workload for ~15 s... watch this pid with winscope.");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
Console.WriteLine($"listening on 127.0.0.1:{port}");

using var acceptCts = new CancellationTokenSource();
var acceptLoop = Task.Run(async () =>
{
    while (!acceptCts.IsCancellationRequested)
    {
        try
        {
            using var serverSide = await listener.AcceptTcpClientAsync(acceptCts.Token);
            var stream = serverSide.GetStream();
            var buffer = new byte[64];
            int read = await stream.ReadAsync(buffer, acceptCts.Token);
            if (read > 0)
                await stream.WriteAsync(buffer.AsMemory(0, read), acceptCts.Token); // echo back
        }
        catch (OperationCanceledException) { break; }
        catch (SocketException) { break; }
        catch (ObjectDisposedException) { break; }
    }
});

string tempFile = Path.Combine(Path.GetTempPath(), $"winload-{pid}.tmp");
var payload = new byte[4096];
Random.Shared.NextBytes(payload);

var deadline = DateTime.UtcNow.AddSeconds(15);
int iterations = 0;
while (DateTime.UtcNow < deadline)
{
    iterations++;

    // --- TCP: one short-lived loopback connection, small echo exchange ---
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(payload.AsMemory(0, 32));
        var reply = new byte[32];
        await stream.ReadExactlyAsync(reply);
    }

    // --- File: write the payload, read it back, keep the file for next round ---
    await File.WriteAllBytesAsync(tempFile, payload);
    _ = await File.ReadAllBytesAsync(tempFile);

    if (iterations % 10 == 0)
        Console.WriteLine($"  ...{iterations} iterations");

    await Task.Delay(100);
}

acceptCts.Cancel();
listener.Stop();
try { await acceptLoop; } catch { /* shutdown */ }
try { File.Delete(tempFile); } catch { /* best effort */ }

// --- Disk phase (--disk only): 256 MiB written through the cache, so the
// operations reach the physical device and winops' Disk category must
// become non-zero (the normal loop's file I/O is cache-served and never
// does). Note the file is deleted again at exit, below.
if (diskMode)
{
    string diskFile = Path.Combine(Path.GetTempPath(), $"winload-disk-{pid}.bin");
    var block = new byte[1024 * 1024];
    Random.Shared.NextBytes(block);
    using (var fs = new FileStream(diskFile, FileMode.Create, FileAccess.Write, FileShare.None,
                                   block.Length, FileOptions.WriteThrough))
    {
        for (int i = 0; i < 256; i++)
            fs.Write(block, 0, block.Length);
        fs.Flush(flushToDisk: true);
    }
    Console.WriteLine("disk phase: wrote 256 MiB in 256 x 1 MiB write-through blocks");
    try { File.Delete(diskFile); } catch { /* best effort */ }
}

Console.WriteLine($"winload done: {iterations} iterations (each = 1 TCP connection + 1 file write + 1 file read).");
return 0;
