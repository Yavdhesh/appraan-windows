# Appraan.Windows

**Kernel-level observability for Windows — the functionality Linux gets from eBPF/BCC, built on Windows' own kernel tracing (ETW + eBPF-for-Windows).**

Linux engineers reach for BCC tools — `tcplife`, `syscount`, `biolatency`,
`offcputime` — to see what a process is really doing inside the kernel.
Windows has no direct equivalent. Appraan.Windows is that equivalent: a
.NET library plus a set of small, focused CLIs that answer the same
questions per process — what is this app asking the kernel to do, how
often, and how long does it take?

Appraan.Windows is the Windows collector layer of
[Appraan](https://github.com/Yavdhesh) — *the life of your application,
live* — which pairs it with a JVM observer (JFR + Attach API) and a Linux
eBPF collector behind one metric model and one UI. Features are defined by
their **meaning**, never by one OS's implementation; each platform fulfils
them with its native source. See [DESIGN.md](DESIGN.md) for the parity
contract, including the one documented gap (per-syscall-*name* tracing has
no supported Windows source — the cross-platform feature is *kernel
operations by category, with latency*).

## Tools

| Tool | BCC analogue | What it does | Status |
|---|---|---|---|
| `winscope` | — (foundation) | Live per-category kernel event counts for one PID (TCP / File / Disk / other) | ✅ verified on Windows 11 |
| `wintcplife` | `tcplife` | One line per closed TCP connection: endpoints, bytes sent/received, lifetime, retransmits | ✅ verified — byte totals arithmetically exact vs a known workload |
| `winops` | `syscount` + latency | Kernel operations by category (File, Disk, Network, Process/Thread, optional Registry) with average/max latency and latency histograms for File and Disk | ✅ verified — strict workload checks exact (File.Write = N), latency pairing covers 100% of file ops; Disk verified vs a 256 MiB write-through workload (1291 ops, 100% latency coverage) |
| `winoffcpu` | `offcputime` | Off-CPU analysis per process from ETW context switches: on/off-CPU totals and %, per-thread off-CPU episodes, and a wait-reason breakdown (why threads were not running) — without stack capture | built, verification in progress |
| `winload` | — (test workload) | Deterministic TCP + file workload used to verify the tools against known numbers | ✅ |

Every "verified" claim is backed by a run in [`evidence/`](evidence/) —
the workload, the exact commands, the observed output, the expected
values, and the verdict with the arithmetic shown. Failed verifications
are kept there too; they are how the tools got correct.

## Quick start (Windows 11, admin PowerShell)

```powershell
winget install Microsoft.DotNet.SDK.8   # if dotnet is not installed; reopen PowerShell after
git clone https://github.com/Yavdhesh/appraan-windows.git
cd appraan-windows
dotnet build -c Release

# window A — a workload to observe (prints its PID, waits for Enter)
dotnet run -c Release --project .\src\winload -- --wait

# window B — observe it
dotnet run -c Release --project .\src\wintcplife -- --pid <winload-pid> --seconds 25
# then press Enter in window A
```

Full walkthroughs, expected outputs and troubleshooting: [RUNBOOK.md](RUNBOOK.md).
Design and parity contract: [DESIGN.md](DESIGN.md). Build/verification
state per step: [STATUS.md](STATUS.md).

## Reading the output

Every label the tools print, what it means, its unit, and how to read a
sample value.

**Units used in this project**

- **µs** — microseconds (the tools spell it `us` in table labels, e.g.
  `AVG_US`, `<10us`). 1 µs = 1/1,000,000 of a second; 1,000 µs = 1 ms.
- **ms** — milliseconds. 1,000 ms = 1 s.
- **KB** in `wintcplife` — kibibytes: payload bytes counted by the kernel
  divided by 1,024 (e.g. 0.03 KB = 32 bytes). The summary also prints the
  exact byte count next to it.
- **Counts** are exact counts of kernel events — never samples, rates, or
  estimates. If a number surprises you, the workload really did that.

### `winops` — operations by category, with latency

Sample (real output, from the verified run in
[`evidence/2026-11-13-step3-winops-verification.md`](evidence/2026-11-13-step3-winops-verification.md);
the workload was `winload` doing cached file IO and loopback TCP):

```
=== winops summary: pid 9280 over ~25s ===
CATEGORY          COUNT  LAT_OPS     AVG_US     MAX_US
File               2802     2802       15.5      436.4
  hist: <10us:2225  <50us:316  <100us:126  <500us:135
  kinds: QueryInfo:1561  Close:339  Cleanup:237  Create:220  FSControl:220  Read:116  Write:108  Delete:1
Disk                  0        -          -          -
Network             865        -          -          -
  kinds: TcpRecv:217  TcpSend:216  TcpDisconnect:216  TcpConnect:108  TcpAccept:108
ProcessThread      3346        -          -          -
  kinds: ContextSwitch:3329  ThreadStop:15  ThreadStart:1  ProcessStop:1
diagnostics: pending file ops=0  unmatched completions=16  duplicate starts=0  evicted pending=0
total operations: 7013
```

The table prints every 5 s as a cumulative snapshot and once more at the
end as the summary (only the summary includes the `kinds:` rows).

**Columns**

| Label | Meaning | Unit | How to read it |
|---|---|---|---|
| `CATEGORY` | The family of kernel operations (see rows below) | — | One row per family |
| `COUNT` | Operations observed for this PID so far | operations | Cumulative — it only grows during a run. e.g. `File 2802` = the process asked the file system to do 2,802 things |
| `LAT_OPS` | Of `COUNT`, how many got a *measured latency* | operations | File latencies come from pairing each operation's start and completion events; Disk events carry their response time inside the event. `-` = this category reports no latencies |
| `AVG_US` | Average latency across the `LAT_OPS` | µs | e.g. `15.5` = a typical file operation took about 15.5 µs — memory/cache speed (see the Disk contrast below) |
| `MAX_US` | The slowest single operation | µs | The tail a user would actually feel. e.g. `436.4` = one file operation took 436.4 µs while the average was 15.5 µs |

**Rows (categories)**

| Row | What it counts |
|---|---|
| `File` | File-system operations: opens/creates, reads, writes, closes, metadata queries, … |
| `Disk` | Operations that reached a *physical storage device*. Cached IO never appears here, so `Disk 0` is the expected, correct result for a workload the file cache absorbs |
| `Network` | TCP/UDP operations: connects, accepts, sends, receives, disconnects (counts only — no latency columns) |
| `ProcessThread` | Process/thread lifecycle and scheduling events: starts, stops, context switches (counts only) |
| `Registry` | Registry operations (counts only). Only traced with `--registry`; without it the row reads `-` with `(not enabled — pass --registry)` — that means the tool didn't subscribe to those events, not that an error occurred |

**`hist:` row — the latency distribution.** Each bucket counts how many
measured operations fell into that latency range; every operation lands
in exactly one bucket (the first whose bound exceeds its latency, so
`<50us` means "10–50 µs"). Buckets run `<10us`, `<50us`, `<100us`,
`<500us`, `<1ms`, `<5ms`, `<10ms`, `<50ms`, `>=50ms`; only non-empty
buckets are printed. Read the sample as: 2,225 file ops finished in
under 10 µs, 316 more in 10–50 µs, and so on — 90.7% of all ops were
under 50 µs. **Trust the histogram over the average:** an average of
15.5 µs alone can't tell you whether every op took ~15 µs or most took
5 µs and a few took milliseconds. The histogram shows the tail.

**`kinds:` row — the breakdown inside a category.** Counts per operation
type, e.g. in the sample: of the 2,802 File ops, 108 were `Write`,
116 `Read`, 1,561 `QueryInfo` (file metadata queries — the .NET/Windows
IO stack issues several per file operation, so this dominating is real
behaviour), and Disk kinds are just `Read`/`Write`. This row is what
makes a run checkable: `winload` did exactly 108 loop iterations in this
run, and `Write:108` and `TcpConnect:108` match it exactly.

**`diagnostics:` line — the health of the latency pairing itself.**

| Label | Meaning |
|---|---|
| `pending file ops` | File operations whose start was seen but whose completion hasn't arrived yet. Should fall back to ~0 when the process goes idle; a large stuck value means completions aren't being observed |
| `unmatched completions` | Completion events with no matching start — usually operations already in flight when the watch began (16 in the sample, 0.6% — normal), or a dropped start event |
| `duplicate starts` | A start event re-used an operation address that was still pending, so the earlier op's completion was evidently missed. Normally 0 |
| `evicted pending` | Pending entries thrown away unfinished (older than 10 s, or the pending table hit its size cap). Normally 0; a large number means the latency figures understate reality |

**`total operations:`** — the sum of every category's `COUNT`
(2,802 + 0 + 865 + 3,346 = 7,013 in the sample).

**The same tool against a physical disk.** From the verified write-through
run ([`evidence/step3-winops-disk-verified-2026-11-13.md`](evidence/step3-winops-disk-verified-2026-11-13.md);
`winload --disk` wrote 256 MiB bypassing the cache):

```
Disk               1291     1291      251.1     6088.5
  hist: <50us:356  <100us:40  <500us:682  <1ms:172  <5ms:38  <10ms:3
  kinds: Write:1116  Read:175
```

Read it as: 1,291 operations really reached the device and *all* of them
carried a latency (`LAT_OPS` = `COUNT`); a typical one took 251.1 µs,
~83.5% finished within 500 µs, and the slowest took 6,088.5 µs
(≈ 6.1 ms). **The contrast to remember:** ~15 µs average = data served
from memory/cache; ~251 µs average = data served by a physical device.
Whichever neighbourhood your own `AVG_US` sits in tells you which of the
two your process is actually waiting on.

### `wintcplife` — one line per closed TCP connection

Sample (real output, from the verified run in
[`evidence/2026-11-13-step2-wintcplife-reverification.md`](evidence/2026-11-13-step2-wintcplife-reverification.md);
`winload` exchanges a 32-byte message and its echo per connection):

```
PID    COMM           LADDR:LPORT              RADDR:RPORT                 TX_KB    RX_KB        MS
42492  winload        127.0.0.1:58784          127.0.0.1:58785              0.03     0.03       0.7
42492  winload        127.0.0.1:58785          127.0.0.1:58784              0.03     0.03       4.1
[t+  25s] open connections: 0, closed so far: 262

=== wintcplife summary: 262 connection(s) closed over ~25s ===
total bytes: TX 8.19 KB (8384 bytes), RX 8.19 KB (8384 bytes)
longest-lived: pid 42492 (winload) 127.0.0.1:58784 -> 127.0.0.1:58796 — 321.4 ms
still open (never seen to close): 0
unmatched disconnects (start never seen, no record printed): 0
```

A line is printed at the moment a connection **closes** — never for one
still open.

| Label | Meaning | Unit |
|---|---|---|
| `PID` | Process that owned the connection | — |
| `COMM` | Process name (truncated to 14 characters) | — |
| `LADDR:LPORT` | Local endpoint: this machine's IP address and port | — |
| `RADDR:RPORT` | Remote endpoint: the other end's IP address and port | — |
| `TX_KB` | Payload bytes this side **sent** over the connection's life | KB (bytes ÷ 1,024) |
| `RX_KB` | Payload bytes this side **received** | KB (bytes ÷ 1,024) |
| `MS` | Connection lifetime, from connect/accept to close | ms |

How to read the sample: each line shows `0.03 / 0.03` KB because the
workload sent exactly 32 bytes and received the 32-byte echo
(32 ÷ 1,024 ≈ 0.03). Loopback connections appear **in pairs** — one line
for the client side, one for the accepted server side, with the
endpoints swapped — so 262 lines = 2 × 131 workload iterations. Most
lifetimes are ~0.3–0.8 ms (some up to a few ms); the 321.4 ms
`longest-lived` line is the accepted side of one connection, whose
passive close Windows delays — the summary calls out the extreme so a
quick scan doesn't miss it.

Summary and progress terms:

| Label | Meaning |
|---|---|
| `open connections` (in the `[t+ Ns]` progress lines) | Connections seen opened and not yet closed |
| `closed so far` | Connection lines printed up to that point |
| `total bytes: TX … RX …` | Summed payload over all closed connections, in KB *and* exact bytes — the figure to check against a known workload (262 × 32 B = 8,384 B in the sample) |
| `still open (never seen to close)` | Connections still open when the watch ended; they printed no line |
| `unmatched disconnects` | Closes whose opening was never seen — e.g. the connection was already open when tracing started. No line is printed for these; the counter exists so the gap is visible instead of silent |

### `winoffcpu` — where a process's threads spend time *not* running

> *Illustrative example.* `winoffcpu`'s runtime verification is still in
> progress, so the numbers below are made up to show the shape of the
> output; the labels and units are exactly what the tool prints.

```
on-CPU      812.4 ms   off-CPU    24103.7 ms   off-CPU  96.7%   episodes 214   max episode 100.3 ms
wait reasons:
  REASON           EPISODES   TOTAL_MS      %
  Executive             198    23180.2   96.2
  UserRequest            12      812.5    3.4
  Preempted               4      111.0    0.5
top threads by off-CPU:
       TID EPISODES  OFFCPU_MS   MAX_MS   ONCPU_MS  OFFCPU_%
     12345      105    12040.6    100.2      402.1     96.8
diagnostics: threads observed=9  partial=7  episodes closed=213 (by thread exit=1)  switch-ins w/o episode=0  switch-outs w/o on-CPU=0  open off-CPU=6  open on-CPU=1
```

**Process totals line**

| Label | Meaning | Unit |
|---|---|---|
| `on-CPU` | Total time the process's threads were actually running on a CPU | ms |
| `off-CPU` | Total time the threads had run before, then spent *not* running — blocked or waiting | ms |
| `off-CPU %` | `off-CPU ÷ (on-CPU + off-CPU)` — the share of observed thread time spent not running | % |
| `episodes` | How many separate off-CPU periods (one per switch-out → switch-in pair) | count |
| `max episode` | The single longest off-CPU period — e.g. 100.3 ms looks like one ~100 ms sleep | ms |

Note the `%` denominator is *summed thread time*, not wall-clock time: a
process with many mostly-idle threads legitimately sits near 100%. A
high off-CPU % is not by itself a problem — it means the threads were
waiting for something, and the wait-reason table says for what.

**Wait-reason table** — why threads left the CPU, one row per reason,
sorted by total time. Columns: `REASON`, `EPISODES` (how many off-CPU
periods), `TOTAL_MS` (their summed length, ms), `%` (share of the
process's total off-CPU ms).

| Reason | Meaning |
|---|---|
| `Executive` | Blocked on a kernel synchronisation object or timer — the general "asleep, waiting to be woken" bucket; a thread doing a timed sleep lands here |
| `UserRequest` | Blocked servicing a user request — e.g. waiting for work handed to it by another thread or process |
| `EventPairHigh` / `EventPairLow` | Blocked on an event-pair object (a synchronisation primitive some subsystem/RPC paths use) |
| `Preempted` | **Not a block:** the thread was still runnable but was switched out and had to wait for a CPU to become free. This is scheduling delay — the fix is less CPU contention, not faster IO |
| *(others)* | The kernel's remaining reason names (`FreePage`, `PageIn`, `LpcReceive`, …) appear verbatim when they occur |

**Top-threads table** — the same accounting per thread, worst first:
`TID` (OS thread id), `EPISODES`, `OFFCPU_MS` (total off-CPU, ms),
`MAX_MS` (longest single episode, ms), `ONCPU_MS` (total on-CPU, ms),
`OFFCPU_%` (that thread's off-CPU share, %). A trailing `(partial)`
means the thread was already alive when tracing started, so its totals
cover only the observed window, not its whole life.

**`diagnostics:` line** — `threads observed` (distinct threads seen),
`partial` (how many of them were already alive at trace start),
`episodes closed` (completed off-CPU periods, with `by thread exit`
counting those ended by the thread dying rather than being scheduled
again), `switch-ins w/o episode` / `switch-outs w/o on-CPU` (context
switches whose matching half was missed — normally 0; small numbers at
the window edges are expected), `open off-CPU` / `open on-CPU` (threads
in each state at the moment of the snapshot).

### `winscope` — live event counts at a glance

Sample (real output, from the verified run in
[`evidence/2026-11-13-step1-winscope-smoke-test.md`](evidence/2026-11-13-step1-winscope-smoke-test.md)):

```
[t+   2s] pid 33108: Tcp=65 File=183 Disk=0 Other=222 (total 470)
[t+   4s] pid 33108: Tcp=89 File=296 Disk=0 Other=369 (total 754)

=== winscope summary: pid 33108 over ~11s ===
Tcp=89  File=296  Disk=0  Other=369  Total=754
(all processes in the same window: 256921 events)
```

Every 2 s, one line with the **cumulative** count of kernel events
attributed to the PID in each bucket: `Tcp` (TCP/IP events), `File`
(file-system events), `Disk` (physical-device events), `Other`
(everything else the kernel logger emits — process/thread lifecycle and
the like), and `total`. Units are plain event counts. The summary's
`(all processes …)` line puts the target in perspective: this process's
754 events were a small slice of the 256,921 events machine-wide.

`Disk=0` here is expected, not a failure: `winload`'s small file writes
are absorbed by the Windows file cache, so no operation ever reaches a
physical device. Disk only counts when IO does (see the `winops` Disk
example above).

## Requirements

- Windows 11 (developed and verified on build 10.0.26300)
- .NET 8 SDK
- Administrator rights — ETW kernel tracing requires elevation

## Roadmap

- IO latency detail (per-operation-type histograms) — in progress
- Off-CPU analysis with wait reasons (ETW context switches)
- Per-core CPU and thread core occupancy; per-process network throughput
- eBPF-for-Windows utilities for the socket-level hooks ETW cannot provide
- Appraan integration: this library becomes Appraan's Windows collector

## License

Apache License 2.0 — see [LICENSE](LICENSE).
