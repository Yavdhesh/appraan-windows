# Step 1 — winscope smoke test (runtime verification)

- **Date:** 2026-11-13
- **Machine:** user's Windows 11, OS build 10.0.26300, admin PowerShell,
  .NET SDK 8 (installed via `winget install Microsoft.DotNet.SDK.8`)
- **Code:** commit `848484b` (+ `2bb4ccd` runbook)
- **Workload:** `winload` — loopback TCP connections + temp-file write/read
  churn for ~15 s (this run: PID 33108, 132 iterations; winscope attached
  partway through the run)

## Commands

```powershell
dotnet run -c Release --project .\src\winload
dotnet run -c Release --project .\src\winscope -- 33108
```

## Observed output (verbatim)

```
winscope: watching pid 33108 for 10s (session 'AppraanSession'). Ctrl+C to stop early.
[t+   2s] pid 33108: Tcp=65 File=183 Disk=0 Other=222 (total 470)
[t+   4s] pid 33108: Tcp=89 File=296 Disk=0 Other=369 (total 754)
[t+   6s] pid 33108: Tcp=89 File=296 Disk=0 Other=369 (total 754)
[t+   8s] pid 33108: Tcp=89 File=296 Disk=0 Other=369 (total 754)
[t+  10s] pid 33108: Tcp=89 File=296 Disk=0 Other=369 (total 754)

=== winscope summary: pid 33108 over ~11s ===
Tcp=89  File=296  Disk=0  Other=369  Total=754
(all processes in the same window: 256921 events)
```

(Counts stop growing after t+4s because winload had finished by then.)

## Expected vs observed

| Check | Expected | Observed |
|---|---|---|
| Tcp events for target PID | > 0 | 89 ✅ |
| File events for target PID | > 0 | 296 ✅ |
| Disk events | ~0 (file IO served from Windows cache → no physical disk IO) | 0 ✅ |
| Per-PID filtering | target's events isolated from the full kernel stream | 754 of 256,921 session-wide events ✅ |

## Verdict

**PASS.** The ETW real-time kernel session, typed event subscription and
per-PID filtering all work on real Windows 11 hardware. Step 1 done per the
project verification rule.
