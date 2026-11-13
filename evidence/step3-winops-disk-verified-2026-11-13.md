# Step 3 (follow-up) — winops Disk category verification (write-through workload)

- **Date:** 2026-11-13
- **Machine:** user's Windows 11, build 10.0.26300, admin PowerShell
- **Code:** commit `c94d764` (winload `--disk` mode)
- **Workload:** `winload --wait --disk`, PID 18772. Loop iterations this
  run: **N = 114** (from `Network.TcpConnect = 114` — one outbound
  connection per iteration), followed by the disk phase: **256 MiB in
  256 × 1 MiB blocks** written through a `FileStream` with
  `FileOptions.WriteThrough`, then one `Flush(true)`; the temp file is
  deleted at exit.

## Commands

```powershell
# Window A
dotnet run -c Release --project .\src\winload -- --wait --disk
# Window B (admin), then Enter in A
dotnet run -c Release --project .\src\winops -- 18772 --seconds 60
```

## Observed summary (final table)

```
=== winops summary: pid 18772 over ~60s ===
CATEGORY          COUNT  LAT_OPS     AVG_US     MAX_US
File               4040     4040      139.0    17270.1
  hist: <10us:2706  <50us:543  <100us:141  <500us:312  <1ms:187  <5ms:145  <10ms:5  <50ms:1
  kinds: QueryInfo:1663  Write:1172  Close:357  Cleanup:251  FSControl:235  Create:234  Read:125  Delete:2  Flush:1
Disk               1291     1291      251.1     6088.5
  hist: <50us:356  <100us:40  <500us:682  <1ms:172  <5ms:38  <10ms:3
  kinds: Write:1116  Read:175
Network             919        -          -          -
  kinds: TcpRecv:235  TcpSend:228  TcpDisconnect:228  TcpConnect:114  TcpAccept:114
ProcessThread      4813        -          -          -
  kinds: ContextSwitch:4794  ThreadStop:16  ThreadStart:2  ProcessStop:1
diagnostics: pending file ops=0  unmatched completions=22  duplicate starts=0  evicted pending=0
total operations: 11063
```

Timeline: Disk was **0** at the t+10s snapshot and **1291** at t+15s —
the entire write-through phase landed inside that 5 s window. In the
same window the File average latency rose from 15.6 µs to 139.0 µs
(cached-loop baseline ≈ 15.5 µs in the earlier verified Step 3 run).

## Diagnostic trail — the first attempt (same day, PID 11100)

An earlier run the same day also showed Disk = 0 — but for a workload
reason, not a tool reason. Its fingerprint: **File.Write stayed at
exactly N = 114** (the loop's own writes only) and the process exited
at ~15 s — the disk phase had never executed (the `--disk` build/flag
had not taken effect in that run). The phase was proven absent from
the counts alone, winload was re-run correctly as above, and Disk
populated immediately. Kept here because it is the same discipline as
the Step 2 defect record: the numbers say what actually ran.

## Expected vs observed

| Check | Expected | Observed |
|---|---|---|
| Disk COUNT | > 0 (strict) | **1291** ✅ |
| Disk LAT_OPS | > 0 (strict) | **1291 = COUNT** — every disk event carried its embedded kernel latency ✅ |
| Disk latency histogram | present (strict) | present; 83.5% of ops < 500 µs ✅ |
| Disk kinds dominated by Write | yes (strict) | Write 1116 / 1291 = **86%** ✅ |
| File.Write | loop writes (N) + disk-phase writes; phase count NOT exact by design | 1172 = 114 + **~1058 phase writes** (≈ 4.1 file-level writes per 1 MiB block — FileStream/storage-stack splitting, disclosed) ✅ |
| File Flush kind | exactly 1 (the single `Flush(true)`) | **1** ✅ |
| Disk Write vs file-level phase writes | need not match — the storage stack splits/merges I/Os and adds metadata writes | 1116 physical vs ~1058 logical ✅ (expected divergence, disclosed in RUNBOOK) |
| Disk Read | indicative only (reads may be cache-served) | 175 physical reads in the same window — filesystem metadata + cache pressure from 256 MiB of write-through traffic |
| Network (control) | loop shape unchanged | Connect/Accept 114/114, Send/Disconnect 228 = 2N, Recv 235 ✅ |
| Diagnostics | small/explainable | unmatched completions 22; duplicates 0; evicted 0; pending at end 0 ✅ |

## Cross-layer consistency

The two latency views reconcile, and each shows what the other cannot:

- **File layer** (what the process experienced): average latency rose
  15.6 → **139.0 µs**, max **17.3 ms** — this is the *wait*, including
  queueing behind the write-through traffic.
- **Disk layer** (what the device did): average **251.1 µs**, max
  **6.1 ms** per physical I/O — the *service time* the kernel embeds
  in each completion event.

A write-through workload is exactly the case where these diverge, and
both moved in the expected direction in the same 5 s window.

## Verdict

**PASS — Step 3 Disk category is RUNTIME VERIFIED.** All strict
criteria met: Disk COUNT 1291 > 0, LAT_OPS = COUNT (1291/1291),
histogram present, kinds 86% Write.

winops category status after this run:

- **File, Disk, Network, ProcessThread — fully verified** against a
  known workload.
- **Registry — observe-only functional**: 16 events from the .NET
  runtime's own activity were captured on this machine on 2026-11-13
  (Open 3, QueryValue 3, Close 10). Its deterministic workload test
  was **skipped at the user's direction** (no test code writes to the
  user's registry); winops' `--registry` flag is pure observation and
  is unaffected.
