# Appraan.Windows — STATUS

## Integration: appraanagent (the Appraan sidecar) — BUILT, compile-verified 2026-11-13; runtime verification pending

(Renamed from winagent to **appraanagent** at the user's direction,
2026-11-13, in a dedicated rename commit. The JSON snapshot contract
is unchanged by the rename.)

The standalone tools become Appraan's Windows kernel collector. Design
constraints that shaped it (all confirmed against the existing code):

- Windows allows effectively ONE NT Kernel Logger session
  ("AppraanSession") and every tool stops any stale session on start —
  so winops + wintcplife + winoffcpu cannot run concurrently as
  separate processes. `src/appraanagent` is a single process hosting
  OpsTracker + TcpConnectionTracker + OffCpuTracker in one
  KernelSession (groups: KernelGroups.All, plus opt-in Registry with
  winops' fallback-if-it-fails behaviour).
- Elevation split: appraanagent runs elevated (ETW); Appraan (Attach API,
  same-user) never elevates. The two meet over the snapshot document.
- Sinks (user design amendment): `--push <url>` POSTs the snapshot to
  Appraan's UI ingest endpoint (primary; failures never stop the agent —
  one warning per failure streak, snapshots are cumulative so the next
  success self-heals); `--out <path>` atomically rewrites a local JSON
  file (fallback / recorder / evidence). At least one sink required;
  both may be used together.

Snapshot JSON contract (one document per interval; hand-built,
invariant culture, zero dependencies): top-level `pid`, `tEpochMs`,
`windowSeconds`; `ops.categories[]` (name, count, latOps, avgUs/maxUs
or null, kinds) + `ops.diagnostics`; `tcp` (closedTotal, txBytes,
rxBytes, stillOpen, unmatchedDisconnects, recent ≤ 20 with txKb/rxKb at
the 2-decimal wintcplife convention); `offCpu` (onCpuMs, offCpuMs,
offCpuPct, episodes, maxEpisodeMs, reasons ≤ 10 with pct, threads
≤ 10 with partial flag).

Verification on this (Linux) VM — ETW cannot run here, so:
- Full solution build: 0 warnings, 0 errors (7 projects).
- CLI paths: no-sink → usage + exit 2; missing PID → usage + exit 2;
  valid args → platform guard message + exit 1, no file created.
- The JSON builder was exercised OUT of process (extracted verbatim
  into a throwaway harness fed with the trackers' real stats record
  types, using the verified winops pid-18772 / winoffcpu pid-53152
  figures): output parses as JSON and every contract field was asserted
  (null latencies, exact byte totals, 0.03 KB convention, reason pct).
- The push transport could NOT be live-tested from this VM: the
  sandbox intercepts direct TCP from language runtimes (.NET/Java) —
  the same block recorded for Java HttpClient in Appraan's M2 proof;
  curl is unaffected. KernelPush is a single POST +
  EnsureSuccessStatusCode, compile-verified; the receiving side
  (Appraan's ingest endpoint) IS live-verified in Appraan's proof run4
  via curl, and the full appraanagent→Appraan loop is the user's two-window
  Windows test (RUNBOOK "Integration — appraanagent").

## Step 4: winoffcpu (off-CPU analysis) — RUNTIME VERIFIED 2026-11-13

**VERIFIED 2026-11-13** (user's Win11, winload pid 4828, 25 s watch):
off-CPU **99.6%** (strict > 80% ✅), max episode 15083.2 ms with the
100 ms Task.Delay signature in the per-thread MAX_MS cluster
(~103–123 ms), 5804 episodes, zero missed switch edges; arithmetic
reconciles (~15 threads × ~15 s ≈ 188.0 s summed off-CPU). Defect
found by the run: wait reasons the TraceEvent enum does not name
printed as raw numbers (15, 37, 17, 27, 28) — fixed in `e58be74` by
mapping the full NT KWAIT_REASON table (15 = `WrQueue`, the dominant
reason at 59.8%, then `UserRequest` 39.5%). The indicative
"Executive will dominate" guess in the user-test section below was
WRONG (Executive: 0.2%) — the wrong guess and the full audit are
recorded in evidence/step4-winoffcpu-verified-2026-11-13.md. The same
fix commit adds units lines under the winops/winoffcpu banners
(microseconds / milliseconds spelled out), at the user's request.
`winoffcpu <PID> [--seconds N]` prints, every 5 s and at exit, the
process's on-CPU / off-CPU totals and off-CPU %, a wait-reason table
(reason, episodes, total ms, %), the top threads by off-CPU time
(TID, episodes, off-CPU ms, max episode ms), and diagnostics. This is
DESIGN.md item 5 and the Windows form of BCC `offcputime`, minus stack
capture (stacks stay a later step; the kernel's wait reason stands in
for "why was it off-CPU").

### TraceEvent 3.1.8 API — verified by reflection, NOT from memory
- `KernelTraceEventParser.ThreadCSwitch` → `CSwitchTraceData` carries
  explicit `OldThreadID`/`OldProcessID`/`OldProcessName` and
  `NewThreadID`/`NewProcessID`/`NewProcessName`, `OldThreadWaitReason`
  (`System.Diagnostics.ThreadWaitReason`: Executive, FreePage, PageIn,
  SystemAllocation, ExecutionDelay, Suspended, UserRequest,
  EventPairHigh, EventPairLow, LpcReceive, LpcReply, VirtualMemory,
  PageOut, Unknown) and `OldThreadState`
  (`System.Diagnostics.ThreadState`: Initialized, Ready, Running,
  Standby, Terminated, Wait, Transition, Unknown), plus priorities,
  quantum fields and a `NewThreadWaitTime`. It also declares its OWN
  `ProcessID` property — one more reason attribution in
  `OffCpuTracker` uses ONLY the payload's Old/New process ids, never
  the event header.
- ThreadStart/ThreadStop → `ThreadTraceData`; ProcessStop →
  `ProcessTraceData`. Lifecycle events carry no old/new pair, so their
  header ProcessID/ThreadID identify the thread being created or
  destroyed (same convention as `OpsTracker`).

### Design (OffCpuTracker)
- Per-thread state machine: a switch-out (thread is the event's Old*)
  closes its on-CPU interval and opens an off-CPU episode stamped with
  the switch-out time and a reason label; a switch-in (thread is New*)
  closes the episode (duration = in − out) and opens an on-CPU
  interval. Symmetric accounting yields off-CPU % per thread and per
  process. A switch whose Old and New are the same thread is ignored.
- Reason label, fixed once at switch-out: OldThreadState = Wait → the
  kernel wait-reason name (Executive, UserRequest, ...); = Ready →
  `Preempted` (the thread was runnable but had no CPU — scheduling
  delay, not a block; folding it into a wait reason would fabricate
  one); any other state → the state name itself.
- Window edges: a thread first seen mid-life (ThreadStart not
  observed — normally it predates the trace) is marked Partial: if
  first seen switching out, its unknown preceding on-CPU time is not
  counted; if first seen switching in, no episode is invented for the
  unobserved past. A thread born in-window (ThreadStart seen) starts
  clean, neither on- nor off-CPU until its first switch-in.
  ThreadStop closes a thread's open interval at the stop timestamp and
  ProcessStop does so for all of the process's threads (such episodes
  are counted separately in diagnostics as closed-by-exit); exited
  threads keep contributing their final totals to snapshots.
  Switch-ins/outs whose matching edge was evidently missed increment
  diagnostics counters instead of being papered over.
- Snapshots accrue in-progress intervals provisionally up to the
  newest context-switch timestamp observed anywhere on the machine
  (context switches fire constantly, so it tracks "now" in trace
  time); snapshots never mutate state. The CSwitch handler checks the
  payload process ids before taking any lock — context switches are
  the highest-volume kernel event there is.
- `KernelSession` is unchanged in this step: the Thread group already
  maps to Thread | ContextSwitch keywords. winoffcpu enables
  Thread | Process.

### Build (real output, this VM, .NET SDK 8.0.428)
```
  Appraan.Windows -> .../src/Appraan.Windows/bin/Release/net8.0/Appraan.Windows.dll
  winscope -> .../src/winscope/bin/Release/net8.0/winscope.dll
  winload -> .../src/winload/bin/Release/net8.0/winload.dll
  wintcplife -> .../src/wintcplife/bin/Release/net8.0/wintcplife.dll
  winops -> .../src/winops/bin/Release/net8.0/winops.dll
  winoffcpu -> .../src/winoffcpu/bin/Release/net8.0/winoffcpu.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```
(Two compile fixes on the way: `ThreadState` needs an alias —
`System.Threading` defines one too — and a `double` cannot be
`volatile` in C#, so the newest-timestamp watermark is a `long` of
the double's bits, updated via Interlocked.CompareExchange.)
Linux-side checks: winoffcpu with no args / non-numeric pid / unknown
flag → usage + exit 2; valid pid off-Windows → platform-guard message
+ exit 1. Sibling behaviours re-checked unchanged (winscope platform
guard exit 1; wintcplife and winops usage exit 2). ETW runtime
behaviour remains UNVERIFIED until the run below.

### User test (RUNBOOK.md Step 4) — expected signature vs winload
Window A: `winload --wait` (note pid). Window B (admin):
`winoffcpu <pid> --seconds 25`, then Enter in A. winload's shape:
~15 s of iterations, each = brief TCP-echo + temp-file work followed
by a 100 ms `Task.Delay`; a handful of threads (main loop, accept
loop, threadpool workers) that block far more than they run.
- **STRICT** (must hold or something is wrong):
  - The report prints all three sections (process totals, wait-reason
    table, per-thread table) for winload's pid.
  - Process off-CPU % **> 80%** — winload is idle-dominated.
  - Max episode **>= 90 ms** — the 100 ms Task.Delay must appear as an
    off-CPU episode of roughly the delay's length.
- **INDICATIVE** (expected shape, not pass/fail):
  - Off-CPU % in fact > 90% aggregated over winload's threads.
  - Wait reasons dominated by `Executive` (timer/delay waits are the
    classic Executive case), with `UserRequest` / `EventPair*` present
    from the async socket/file waits; `Preempted` a small share on an
    unloaded machine.
  - Episodes in the **hundreds, not thousands** — winload's threads
    block for long stretches rather than spinning.
  - Several threads in the table (main loop, accept loop, threadpool),
    most marked (partial) — they predate the trace.

## Step 3: winops (kernel ops by category + latency) — BUILT, compile-verified, runtime UNVERIFIED
`winops <PID> [--seconds N] [--registry]` prints, every 5 s and at exit, a
per-category table (COUNT, LAT_OPS, AVG_US, MAX_US) with latency
histograms for File/Disk, a per-kind drill-down at exit, and pairing
diagnostics. This is the user-facing parity promise: what the process is
asking the kernel to do, how often, how long it takes.

### TraceEvent 3.1.8 API — verified by reflection + parser source, NOT from memory
- **Disk latency is embedded in the completion payload.**
  `DiskIOTraceData` (DiskIORead/DiskIOWrite) and
  `DiskIOFlushBuffersTraceData` both declare `ElapsedTimeMSec` (double),
  computed from a response-time QPC field the kernel embeds in the event.
  No pairing needed. Disk ops are counted ONCE, at completion; the
  DiskIOReadInit/WriteInit/FlushInit start halves are deliberately not
  counted (winscope counts them as raw events — a documented difference).
- **File latency needs IrpPtr pairing.** `FileIOReadWriteTraceData`
  (Read/Write), `FileIOCreateTraceData`, `FileIOInfoTraceData`
  (SetInfo/Delete/Rename/QueryInfo/FSControl) and
  `FileIODirEnumTraceData` all carry `IrpPtr` (UInt64) but NO elapsed
  time. The completion event `FileIOOperationEnd` →
  `FileIOOpEndTraceData` carries the same `IrpPtr` (+ NtStatus). Design:
  every IrpPtr-carrying FileIO start (Create, Read, Write, Cleanup, Close,
  Flush, SetInfo, Delete, Rename, QueryInfo, FSControl, DirEnum,
  DirNotify) enters a pending table keyed by IrpPtr; OperationEnd pairs
  with it (latency = Δ of ETW relative timestamps), attributed to the
  START event's pid (an IRP can complete on another thread/process).
  FileIOName/FileRundown and FileCreate/FileDelete object notifications
  are not operations and are not counted; MapFile/UnmapFile count without
  latency (no IrpPtr). Pending table: 10 s staleness horizon, 100k cap,
  evictions / unmatched completions / duplicate starts all surface in
  `OpsDiagnostics` (printed by winops every table).
- **Registry is a first-class kernel keyword.** Reflection over
  `KernelTraceEventParser.Keywords` shows `Registry = 0x00020000`, and the
  kernel parser has typed Registry* events (RegistryCreate/Open/Query/
  QueryValue/SetValue/EnumerateKey/... → `RegistryTraceData`). So
  registry rides the SAME NT Kernel Logger session via
  `EnableKernelProvider` — no separate EnableProvider/GUID needed (the
  TDH name→GUID lookup is Windows-only and could not be exercised here;
  it is also unnecessary). Still opt-in (`KernelGroups.Registry`, NOT in
  `KernelGroups.All`; winops `--registry`): if session start with the
  keyword fails for any non-elevation reason, winops warns and retries
  without it — registry can never break the core tool. Counts only;
  KCB/rundown events skipped as bookkeeping.
- **ProcessThread**: ProcessStart/Stop, ThreadStart/Stop (header PID) and
  ThreadCSwitch → `CSwitchTraceData`, which carries explicit
  `NewProcessID`/`OldProcessID`; a switch is attributed to the process
  switched IN (NewProcessID) — "times this process got a CPU".
- Network counts reuse the TcpIp/UdpIp event set from steps 1–2 (incl.
  Retransmit/Fail; v6 mirrors fold into the same kinds).

### KernelSession changes (minimal, non-breaking)
- `KernelGroups.Registry` (1<<5) added; `All` unchanged. `ToKeywords`
  maps it to `Keywords.Registry`. No normalised EventReceived wiring for
  registry (EventCategory has no registry bucket — winscope untouched);
  `OpsTracker` subscribes to the typed events on `KernelParser`.
- `Emit` now checks for subscribers before allocating the `KernelEvent`
  record — pure perf fix: winops enables the >10K events/s
  ContextSwitch group but consumes only the typed path, and previously
  every kernel event allocated a record nobody observed. winscope (which
  subscribes) is behaviourally unaffected; its Linux platform guard was
  re-checked (exit 1), as was wintcplife's usage path (exit 2).

### Build (real output, this VM, .NET SDK 8.0.428)
```
  Appraan.Windows -> .../src/Appraan.Windows/bin/Release/net8.0/Appraan.Windows.dll
  winscope -> .../src/winscope/bin/Release/net8.0/winscope.dll
  winload -> .../src/winload/bin/Release/net8.0/winload.dll
  wintcplife -> .../src/wintcplife/bin/Release/net8.0/wintcplife.dll
  winops -> .../src/winops/bin/Release/net8.0/winops.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```
Linux-side checks: winops with no args / non-numeric pid / unknown flag
→ usage + exit 2; valid pid off-Windows → platform-guard message + exit 1
(also with `--registry` — the fallback does not misfire on the platform
guard). ETW runtime behaviour remains UNVERIFIED until the run below.

### User test (RUNBOOK.md Step 3) — expected signature vs winload
Window A: `winload --wait` (note pid + its final iteration count N,
≈131 in the Step 2 verified run). Window B (admin):
`winops <pid> --seconds 25`, then Enter in A. Optionally repeat with
`--registry` afterwards.
- **STRICT** (must hold or something is wrong):
  - `File.Read` kind ≥ N and `File.Write` kind ≥ N (one read + one write
    of the 4 KB temp file per iteration are guaranteed kernel operations;
    both init events are enabled via FileIOInit).
  - File category COUNT in the hundreds (≈6–10 file ops per iteration:
    create+write+close and create+read+close, plus runtime file activity).
  - Network category COUNT ≥ 262 (each of the 2N connections contributes
    at least a connect/accept and a disconnect; sends/recvs push the
    expected total to ~500–1100).
- **INDICATIVE** (expected shape, not pass/fail):
  - File latency: LAT_OPS large (most starts pair with an OperationEnd)
    and histogram mass in the sub-ms buckets — winload's file is cached,
    so latencies should be mostly < 1 ms. How completely OperationEnd
    covers fast-IO (cached) completions is the main open question of this
    run; the diagnostics line (unmatched/evicted) quantifies it.
  - Disk COUNT ≈ 0 (cached IO, same finding as winscope's Disk=0) — a
    small nonzero number under memory pressure would not be a failure.
  - ProcessThread COUNT in the hundreds+, dominated by
    `ProcessThread.ContextSwitch` (winload's threadpool + 100 ms delay
    loop schedules constantly).
  - With `--registry`: Registry row shows real counts (the .NET runtime
    reads registry during winload's run) and the session survives; the
    exact number is not predictable.

### Follow-up (same day): winload `--disk` mode; registry workload SKIPPED by user decision
The verified Step 3 run left the Disk category at 0 (correctly —
winload's file I/O is cache-served, nothing reaches a device). To make
Disk verifiable, winload gained a deterministic disk phase:
- `--disk`: after the normal loop, winload writes **256 MiB in 256 ×
  1 MiB blocks** through a `FileStream` with
  `FileOptions.WriteThrough`, calls `Flush(true)`, prints
  `disk phase: wrote 256 MiB in 256 x 1 MiB write-through blocks`,
  and deletes the file at exit. Composes with `--wait` and the normal
  TCP+file loop. Test: RUNBOOK.md "Testing the Disk category"
  (winload `--wait --disk`; winops `<pid> --seconds 40`).
  Strict expectations are on Disk **writes** (COUNT > 0, LAT_OPS > 0,
  latency histogram present): the write total need not equal 256,
  because the storage stack may split or merge I/Os; and no strict
  read expectation is made, since reads of a just-written file may
  still be cache-served. **Disk category RUNTIME VERIFIED 2026-11-13**
  — see the verification log at the bottom of this file and
  evidence/step3-winops-disk-verified-2026-11-13.md.
- **Registry workload: SKIPPED, by the user's explicit decision.** A
  matching winload `--registry` mode (test code writing keys under
  HKCU) was proposed and dropped: winops' Registry category is
  observe-only and was already shown functional on the user's
  machine — an earlier run captured 16 registry events generated by
  the .NET runtime's own activity (Open 3, QueryValue 3, Close 10) —
  and the user prefers not to have test code writing to his registry.
  No `--registry` mode will be built; winops' own `--registry` flag
  (pure observation) is unaffected.

## Step 2: wintcplife (tcplife equivalent) — FUNCTIONAL on Win11, byte-accounting fix applied, RE-VERIFICATION pending
Functional on Win11 (2026-11-13 run: lines/endpoints/durations correct,
222+ closed tracked), byte-accounting fix applied, RE-VERIFICATION pending.

### What the first Win11 run proved (winload pid 54732, 132 iterations)
- ✅ Connection lines print on close with correct endpoints and plausible
      durations (~0.3–0.8 ms); 222+ connections closed and tracked.
- ✅ ETW `size` on send/recv is payload size — settled by the numbers
      below (each socket moves exactly 32 B each way per iteration).
- ❌ Byte attribution was WRONG, in a very specific pattern: client-side
      records showed TX_KB=RX_KB=0.06 (exactly double the expected
      0.03/0.03), accepted server-side records showed 0.00/0.00, and
      phantom duplicate lines appeared (same tuple as a real record,
      0.00/0.00 bytes, 0.0 ms).

### Root cause (confirmed in the old code)
The old table was keyed by (ProcessId, local, remote) and `FindEntry`
searched it in BOTH orientations, with connid as a guarded secondary
index. On same-process loopback the client entry (53623→53567) and the
accepted entry (53567→53623) are exact mirror tuples, so:
1. `OpenConnection` for the Accept event found the CLIENT entry via the
   swapped-orientation search and "adopted" it — the accepted entry was
   never created.
2. Worse, that lookup called `RegisterConnId`, binding the ACCEPTED
   connection's connid to the client entry. The `Fits` endpoint guard
   could not catch the poisoning: mirror tuples fit in either orientation.
3. From then on the server's Send (echo) and Recv (request) events hit the
   poisoned connid index first and landed on the client record → client
   TX and RX each grew by the server's 32 B → the symmetric 0.06/0.06.
4. The accepted side's Disconnect then matched nothing (or an
   already-closed entry), and `CloseConnection` fabricated the
   zero-duration orphan stub → the phantom duplicate lines.

### The fix (this commit)
- **connid is now the PRIMARY key**: the open table is keyed by
  (ProcessId, connid); endpoints are needed only as a fallback key for
  events whose connid is 0 (kept in the fixed per-event-kind orientation).
- Endpoints and direction (`IsOutbound`: Connect → true, Accept → false,
  data-created → unknown) are fixed ONCE at entry creation. Send/Recv for
  a known connid just add bytes — Send is always the owning socket's TX,
  Recv its RX — so no orientation is ever re-derived per data event.
- A Disconnect for an unknown connid creates NO record; it increments the
  new `TcpConnectionTracker.UnmatchedDisconnects` diagnostics counter,
  printed in wintcplife's exit summary.
- connid reuse: closed connids are remembered — trailing data events for
  them are dropped as late arrivals; a Connect/Accept for a closed connid
  starts a fresh generation; a Connect/Accept contradicting a still-open
  entry's endpoints displaces it (old entry closed as estimated).
- UDP: the tracker subscribes only to TcpIp* parser events (verified in
  `TcpConnectionTracker.Attach`); UDP events never enter it. The UdpIp
  subscriptions in KernelSession serve winscope's category counts only.
- winscope untouched; its build and Linux-verified behaviours (platform
  guard exit 1, usage exit 2 — re-checked for wintcplife too) unchanged.

### Build after the fix (real output, this VM, .NET SDK 8.0.428)
```
  Appraan.Windows -> .../src/Appraan.Windows/bin/Release/net8.0/Appraan.Windows.dll
  winscope -> .../src/winscope/bin/Release/net8.0/winscope.dll
  winload -> .../src/winload/bin/Release/net8.0/winload.dll
  wintcplife -> .../src/wintcplife/bin/Release/net8.0/wintcplife.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Re-verification (user test — RUNBOOK.md Step 2)
`git pull`, rebuild, then `winload --wait` in window A and
`wintcplife --pid <winload pid> --seconds 25` in admin window B; press
Enter in A. Expected after the fix, per winload iteration exactly 2
records (client + accepted server): client TX≈RX≈0.03 KB, server
TX≈RX≈0.03 KB, NO zero-byte phantom lines, durations ~0.3–0.8 ms, and an
`unmatched disconnects` count (0 or small) in the exit summary.

### Original Step 2 build notes (kept for reference)
- [x] TraceEvent 3.1.8 TCP API verified by reflection against the restored
      NuGet package (NOT from memory): parser events TcpIpConnect/TcpIpAccept
      (TcpIpConnectTraceData), TcpIpSend (TcpIpSendTraceData), TcpIpRecv /
      TcpIpDisconnect / TcpIpRetransmit / TcpIpReconnect (TcpIpTraceData),
      plus IPV6 mirrors (TcpIpV6*TraceData). Every payload carries
      connid (UInt64), saddr/sport, daddr/dport and `size` (int) — `size` on
      Send/Recv is the byte-accounting field. (The payload class names are
      *TraceData, not *Event — the reflection dump is what the code uses.)
- [x] KernelSession: added a `KernelParser` property exposing the raw
      KernelTraceEventParser (valid between Start and Stop) for typed-payload
      consumers. Purely additive — winscope's normalised EventReceived path
      is untouched and winscope still builds/behaves identically.
- [x] CLI: src/wintcplife — `wintcplife [--pid N] [--seconds N]` (no
      --seconds = until Ctrl+C, like BCC). Prints a tcplife-style line per
      closed connection (PID COMM LADDR:LPORT RADDR:RPORT TX_KB RX_KB MS),
      a 5 s live summary (open count), and an exit summary (closed count,
      total TX/RX bytes, longest-lived connection, still-open count, and —
      since the fix — the unmatched-disconnects count).
- [x] RUNBOOK.md: "Step 2 — wintcplife" section with the two-window test;
      expected-output notes updated for the fix (0.03 KB on BOTH sides,
      no phantom lines).

## Next (per DESIGN.md build order)
3. Kernel ops by category + latency — BUILT, see the Step 3 section at the
   top of this file (runtime verification on the user's machine pending).
   Note DESIGN.md item 4 (IO latency histograms per process) is already
   delivered inside winops for the File and Disk categories.
5. Off-CPU (CSwitch + wait reasons) — RUNTIME VERIFIED 2026-11-13,
   see the Step 4 section at the top of this file. Stack capture
   remains a later step.

## Step 1: solution skeleton + ETW session plumbing — CODE COMPLETE, COMPILE-VERIFIED
Finished 2026-11-13 (assistant's Linux VM). Runtime ETW behaviour is still
UNVERIFIED — per the DESIGN.md verification rule, step 1 is only "done" after
the RUNBOOK.md smoke test passes on the user's Windows 11 machine
(winload PID must show Tcp > 0 AND File > 0 in winscope).

- [x] .NET SDK installed on this VM: **8.0.428** at ~/workspace/tools/dotnet
      (official dotnet-install script's own extraction failed twice in this
      sandbox — /tmp is a 512 MB tmpfs, then tar chown to uid 2001 is blocked;
      worked around by downloading dotnet-sdk-8.0.428-linux-x64.tar.gz from
      builds.dotnet.microsoft.com and extracting with `tar --no-same-owner`)
- [x] Solution `Appraan.Windows.sln` with 3 projects (net8.0):
      src/Appraan.Windows (library), src/winscope (CLI), src/winload (workload)
- [x] KernelSession (TraceEventSession "AppraanSession", typed kernel-parser
      handlers for Tcp/Udp v4+v6, FileIO, DiskIO, Process, Thread/CSwitch;
      elevation check with actionable error; stale-session takeover),
      KernelEvent record, KernelGroups flags → kernel keywords,
      PidEventCounter (thread-safe per-PID × category counts)
- [x] `dotnet build -c Release` → **Build succeeded, 0 warnings, 0 errors**
      (NuGet restore of Microsoft.Diagnostics.Tracing.TraceEvent 3.1.8 worked;
      one fix needed during compile-check: TraceEvent 3.1.8 names the context
      -switch event `ThreadCSwitch`, not `CSwitch`)
- [x] Linux-side smoke checks: winload's TCP+file loop runs (50+ iterations);
      winscope off-Windows exits 1 with the platform-guard message, as designed
- [x] RUNBOOK.md — env checks, winget SDK install fallback, admin build/run
      steps, expected output, troubleshooting; updated 2026-11-13 with the
      race-free `--wait` smoke-test flow, bare-PID syntax note, `git pull`
      update step, and the system32 relative-path troubleshooting row
- [x] git: **pushed to PUBLIC repo https://github.com/Yavdhesh/appraan-windows**
      (user chose public from day one, 2026-11-13; Step 1 commit `848484b`,
      RUNBOOK pointer `2bb4ccd`, `winload --wait` fix `e0c522f`) — verified
      via `git log`/`git remote -v` 2026-11-13 16:07 UTC. NOTE: this file
      previously (incorrectly) said "NOT pushed anywhere" — corrected here;
      the earlier push is confirmed by the user's own clone + `dotnet run`
      from `C:\Windows\system32\appraan-windows` in chat 2026-11-13.

## Runtime-unverified (needs the user's Windows 11 machine)
- Everything ETW: session creation, kernel keyword enablement, event flow,
  per-PID attribution for FileIO/DiskIO completion events, counts vs winload.
- Whether FileIO/DiskIO counts land on winload's PID as expected is the key
  open question for the smoke test; results feed Step 2 (tcplife equivalent).

## Next (per DESIGN.md build order)
2. tcplife equivalent — BUILT, see the Step 2 section at the top of this
   file (runtime verification on the user's machine still pending).

## Runtime verification — 2026-11-13 (user's Windows 11, build 26300)
- Smoke test PASSED: winload pid 33108 observed by winscope — Tcp=89, File=296,
  Disk=0 (expected: cached file IO produces no physical DiskIO), Other=369,
  total 754 events for the target PID out of 256,921 session-wide in ~11 s.
  Per-PID filtering confirmed working against the full kernel firehose.
- Step 1 is now DONE per the project verification rule (real run, known workload).

## Step 2 RE-VERIFIED — 2026-11-13 (user's Windows 11, winload pid 42492)
- 262 connections closed = exactly 2 per iteration (client + accepted server),
  winload ran 131 iterations in the window.
- Bytes EXACT: TX 8384 B = RX 8384 B = 262 x 32 B (winload's known payload).
  Every record 0.03/0.03 KB. No phantom lines. Unmatched disconnects: 0.
  Still open at end: 0.
- Durations: most 0.1-1.2 ms; a minority of server-side (passive-close) records
  show ~300-321 ms — real Windows loopback teardown delay on the passive side,
  not a tracker artefact (byte totals and counts are exact; 300 ms is not an
  iteration-period multiple).
- Step 2 (wintcplife) is DONE per the verification rule.

## Step 3 VERIFIED — 2026-11-13 (user's Win11, winload pid 9280, N=108)
File.Write exactly N (108); File.Read N+8; Network kinds exact pairs
(Connect/Accept 108/108, Disconnect/Send 216=2N, Recv 217); File latency
pairing covered 2802/2802 ops (avg 15.5us, 90.7% <50us); Disk 0 (cached);
unmatched completions 16 (0.6%, in-flight at start). Evidence:
evidence/2026-11-13-step3-winops-verification.md. Optional --registry run
not yet exercised.

## Step 3 — Disk category VERIFIED — 2026-11-13 (user's Win11, winload pid 18772, N=114)
winload `--wait --disk` (256 MiB write-through phase) vs winops
--seconds 60: Disk 1291 ops, LAT_OPS 1291/1291 (every disk event carried
its embedded latency), avg 251.1µs, max 6.1 ms, kinds Write 1116 / Read
175 (86% Write). File.Write 1172 = 114 loop + ~1058 phase writes
(~4.1 per 1 MiB block — storage-stack splitting, disclosed); Flush kind
exactly 1. Cross-layer check: File avg latency 15.6→139.0µs (the wait)
while Disk avg 251.1µs (device service time). An earlier same-day
attempt (pid 11100) never ran the disk phase — File.Write stayed
exactly N — and was diagnosed from the counts before the re-run.
Evidence: evidence/step3-winops-disk-verified-2026-11-13.md. winops
File/Disk/Network/ProcessThread now fully verified; Registry remains
observe-only functional (16 runtime events observed 2026-11-13), its
deterministic test skipped at the user's direction.

## Step 4 VERIFIED — 2026-11-13 (user's Win11, winload pid 4828)
winoffcpu --seconds 25 vs `winload --wait`: off-CPU 99.6% (strict
> 80%), max episode 15083.2 ms (main thread's run-length completion
wait; the 100 ms Task.Delay signature is the per-thread MAX_MS cluster
at ~103–123 ms), on-CPU total only 758.4 ms, 5804 episodes, 15 threads
observed (4 partial), diagnostics clean (0 missed switch edges either
way). Wait reasons: raw `15` = WrQueue 59.8%, UserRequest 39.5%,
ExecutionDelay 0.5%, Executive only 0.2% (the pre-test prediction of
Executive dominance was wrong — recorded in the evidence). Defect:
unnamed wait reasons printed as bare numbers; fixed in `e58be74`
(full NT KWAIT_REASON mapping + `WaitReason(<n>)` fallback, plus
units lines in winops/winoffcpu banners). Evidence:
evidence/step4-winoffcpu-verified-2026-11-13.md.

## Concepts guide — 2026-11-13
docs/concepts.md ("Understanding the numbers — a plain-language
guide") added at the user's request: episodes, running/blocked/ready,
wait reasons in plain words, why Disk can honestly be 0, and a units
cheat sheet — worked numbers only from the verified runs. Linked from
README's "Reading the output" section.
