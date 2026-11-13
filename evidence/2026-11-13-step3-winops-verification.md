# Step 3 — winops verification (kernel ops by category + latency)

- **Date:** 2026-11-13
- **Machine:** user's Windows 11, build 10.0.26300, admin PowerShell
- **Code:** commit `54ed4be`
- **Workload:** `winload`, PID 9280. Iterations this run: **N = 108**
  (inferred from `Network.TcpConnect = 108` — one outbound connection per
  iteration; consistent with the ~113 ms/iteration rhythm over ~13 s)

## Command

```powershell
dotnet run -c Release --project .\src\winops -- 9280 --seconds 25
```

## Observed summary (verbatim)

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

(Tables were identical from t+15s to t+25s because winload's ~15 s workload
had finished and the process had exited — ProcessStop:1 — not because the
tool stalled.)

## Expected vs observed

| Check | Expected | Observed |
|---|---|---|
| Iterations N | — | 108 (from TcpConnect) |
| File.Write | ≥ N, ideally exactly N | **108 = N** ✅ |
| File.Read | ≥ N | 116 = N + 8 (target's own runtime reads) ✅ |
| Network total | ≥ 2×N×… strict ≥ 262 | 865 ✅ |
| TcpConnect / TcpAccept | equal pair | 108 / 108 ✅ |
| TcpDisconnect | 2 per iteration (both ends) | 216 = 2N ✅ |
| TcpSend | 2 per iteration (request + echo) | 216 = 2N ✅ |
| TcpRecv | 2 per iteration | 217 = 2N + 1 (one extra zero-byte/close-related recv completion; 0.1% noise, disclosed) |
| File latency coverage (LAT_OPS vs COUNT) | high — open question was whether OperationEnd covers cached fast-IO | **2802 / 2802 = 100%** ✅ |
| File latency shape | mostly sub-ms for cached IO | avg 15.5 µs; 90.7% < 50 µs; max 436.4 µs ✅ |
| Disk count | ~0 (cached IO, as in Steps 1–2) | 0 ✅ |
| Diagnostics | small/explainable | unmatched completions 16 (0.6% — ops already in flight at session start); duplicate starts 0; evicted 0; pending at end 0 ✅ |

Notes: QueryInfo (1561) dominating File kinds is real .NET/Windows IO-stack
behaviour (each file open/write/read issues several information queries),
not an artefact — the workload-guaranteed kinds (Read, Write) match their
exact expectations independently of it.

## Verdict

**PASS.** All strict checks met, two of them exact (Write = N,
Disconnect/Send = 2N); latency pairing covers 100% of file operations.
**Step 3 (winops) is verified.**
