# Step 4 — winoffcpu verification (off-CPU analysis vs winload)

- **Date:** 2026-11-13
- **Machine:** user's Windows 11, build 10.0.26300, admin PowerShell
- **Code:** commit `4f9060b` (Step 4: winoffcpu). A labelling defect
  found by this run was fixed in the companion commit `e58be74` —
  see "Defect found" below.
- **Workload:** `winload --wait`, PID 4828: ~15 s of iterations, each
  a brief TCP-echo + temp-file operation followed by a 100 ms
  `Task.Delay`, across ~15 threads (main loop, accept loop,
  thread-pool workers). A wait-dominated process by construction.

## Commands

```powershell
# Window A
dotnet run -c Release --project .\src\winload -- --wait
# Window B (admin), then Enter in A
dotnet run -c Release --project .\src\winoffcpu -- 4828 --seconds 25
```

## Observed — first snapshot (t+5s)

```
[t+   5s] pid 4828 (cumulative)
on-CPU       37.6 ms   off-CPU     1322.3 ms   off-CPU  97.2%   episodes 273   max episode 123.0 ms
wait reasons:
  REASON           EPISODES   TOTAL_MS      %
  15                     45      702.4   53.1
  UserRequest             9      469.6   35.5
  ExecutionDelay          2      138.8   10.5
  Executive              18       10.5    0.8
  Preempted             184        0.4    0.0
  37                      2        0.3    0.0
  Standby                 9        0.3    0.0
  Suspended               1        0.0    0.0
  Running                 3        0.0    0.0
top threads by off-CPU:
       TID EPISODES  OFFCPU_MS   MAX_MS   ONCPU_MS OFFCPU_%
     51468        4      140.0    116.7        0.1     99.9
     19144        7      123.1    123.0        0.4     99.7
     42220        1      120.7    120.7        0.0    100.0  (partial)
     45140        2      119.4    119.4        0.1     99.9
     49408        4      119.3    112.5        0.1     99.9
     15940       29      119.3    103.9        2.8     97.7
      6588       11      119.2    105.9        0.1     99.9
      5040       10      118.6    114.5       21.7     84.6  (partial)
     48092       42      115.5    105.4        8.1     93.4
     41520       71      113.2    103.4        3.8     96.7
diagnostics: threads observed=12  partial=2  episodes closed=263 (by thread exit=0)  switch-ins w/o episode=0  switch-outs w/o on-CPU=0  open off-CPU=12  open on-CPU=0
```

## Observed — final summary (~25s)

```
=== winoffcpu summary: pid 4828 over ~25s ===
on-CPU      758.4 ms   off-CPU   188004.3 ms   off-CPU  99.6%   episodes 5804   max episode 15083.2 ms
wait reasons:
  REASON           EPISODES   TOTAL_MS      %
  15                   2551    112420.5   59.8
  UserRequest           379     74310.0   39.5
  ExecutionDelay          10       880.7    0.5
  Executive              319       367.7    0.2
  Preempted              2161        16.2    0.0
  Standby                 295         7.7    0.0
  Unknown                  30         0.7    0.0
  37                      4         0.4    0.0
  17                      1         0.1    0.0
  Running                  47         0.1    0.0
  27                      4         0.1    0.0
  Suspended                2         0.0    0.0
  28                      1         0.0    0.0
top threads by off-CPU:
       TID EPISODES  OFFCPU_MS   MAX_MS   ONCPU_MS OFFCPU_%
     42220       12    15083.2   2016.3        1.2    100.0  (partial)
     45140        2    15083.2  15083.2        0.1    100.0
     19144      237    15082.4    523.8        4.9    100.0
      5040       14    15082.1  15077.7       22.4     99.9  (partial)
      6588      317    15078.7    448.3        4.5    100.0
      49408      410    15076.6    557.1        6.5    100.0
      29312      280    15065.4    122.2        8.1     99.9
     48092      705    15024.9   5364.1       62.5     99.6
     15940      998    14965.2    331.2      120.5     99.2
     41520     1138    14926.6    225.3      154.2     99.0
diagnostics: threads observed=15  partial=4  episodes closed=5804 (by thread exit=0)  switch-ins w/o episode=0  switch-outs w/o on-CPU=0  open off-CPU=0  open on-CPU=0
```

## Expected vs observed

| Check | Expected | Observed |
|---|---|---|
| All three report sections print | totals + wait-reason table + per-thread table (strict) | all three, every snapshot and in the summary ✅ |
| Process off-CPU % | **> 80%** (strict) — winload is idle-dominated | **99.6%** ✅ |
| Max episode | **≥ 90 ms** (strict) — the 100 ms `Task.Delay` must appear as an episode of roughly its length | **15083.2 ms** headline (see audit note i); the delay signature itself is in the per-thread MAX_MS cluster, note (i) ✅ |
| Missed switch edges | 0 or small/explainable | switch-ins w/o episode **0**, switch-outs w/o on-CPU **0** ✅ |
| Threads in the table | several (main loop, accept loop, thread pool) | 15 observed, 10 shown ✅ |

## Audit notes (recorded honestly, including where the pre-test guesses were wrong)

**(i) The headline max episode is not the Task.Delay.** The 15083.2 ms
max episode is the main thread blocked for essentially the whole
workload run, awaiting completion — its single episode spans the run.
The 100 ms `Task.Delay` signature is where it should be: in the
per-thread `MAX_MS` column, clustering at **~103–123 ms** across
threads (t+5s snapshot: max episode 123.0 ms; thread MAX_MS values
116.7, 123.0, 120.7, 119.4, 112.5, 103.9, 105.9, 114.5, 105.4, 103.4).
The strict ≥ 90 ms criterion is satisfied by both readings, but only
the cluster is the delay.

**(ii) Episode count beat the indicative guess.** The pre-test
estimate was "hundreds, not thousands"; the observed total is
**5804**. That is thread-pool churn across ~15 threads, not spinning:
the busy loop threads individually sit in the hundreds
(TID 41520: 1138 episodes, 15940: 998, 48092: 705, 49408: 410), and
2161 of the 5804 are `Preempted` switch-outs totalling just 16.2 ms —
scheduling noise, not blocks. Long episodes dominate the time:
the top two reasons average ~44 ms and ~196 ms per episode.

**(iii) Arithmetic reconciliation.** ~13–15 threads × the ~15 s
workload lifetime ≈ 188 s of summed thread time; observed off-CPU
total is **188004.3 ms ≈ 188.0 s** ✅. On-CPU total is only
**758.4 ms** across all threads for the entire run — a
wait-dominated workload, exactly as constructed. Percentages check
out against the totals (112420.5 / 188004.3 = 59.8%; 74310.0 /
188004.3 = 39.5%).

**(iv) Diagnostics are clean.** Zero missed switch edges in both
directions, 5804 episodes closed and none by thread exit (winload's
threads were closed out by process end / trace end accounting), 4 of
15 threads partial (alive before the trace started — expected for a
`--wait` workload attached after launch).

**(v) The pre-test wait-reason guess was WRONG.** STATUS.md predicted
`Executive` would dominate ("timer/delay waits are the classic
Executive case"). Observed: **Executive is 0.2%** (319 episodes,
367.7 ms). In .NET's async machinery the waits surface differently:
the dominant reason is the raw value 15 = **WrQueue** (59.8%) —
threads parked waiting for work on a queue (the thread pool /
completion-port queue), and **UserRequest** (39.5%) — waits on
user-mode synchronisation, i.e. the main thread's completion wait
plus handle waits. Recorded here so the wrong guess stays visible;
the tool reported the kernel's truth, not the prediction.

## Defect found by this run — and the fix

The wait-reason table printed **raw numbers** for five reasons
(`15`, `37`, `17`, `27`, `28`): `System.Diagnostics.ThreadWaitReason`
(TraceEvent) names only the classic subset (Executive…PageOut,
Unknown), while the kernel also reports the NT **Wr\*** family.
Bare numbers in a user-facing table are a labelling defect.

Fixed in companion commit `e58be74`: `OffCpuTracker` maps the
payload's wait-reason integer through the full NT KWAIT_REASON table
whenever the enum leaves it unnamed (enum spellings still win where
they exist — 3 stays `SystemAllocation`, 4 stays `ExecutionDelay`).
With the fix, this run's rows would read:

| Printed in this run | After the fix | Meaning |
|---|---|---|
| `15` (2551 eps, 59.8%) | **WrQueue** | thread parked waiting for work on a queue — thread pool / completion port |
| `37` (4 eps) | **WrAlertByThreadId** | wait alertable by thread id |
| `17` (1 ep) | **WrLpcReply** | waiting for an LPC reply |
| `27` (4 eps) | **WrResource** | waiting on a resource (ERESOURCE) |
| `28` (1 ep) | **WrPushLock** | waiting on a push lock |

Values beyond the table fall back to `WaitReason(<n>)` rather than a
guessed name. The same commit adds a units line under each tool's
banner (winops: "latencies are in microseconds (us); 1000 us = 1 ms";
winoffcpu: "all times are in milliseconds (ms)"), at the user's
request after reading this output. Re-running this exact test is not
required to trust the rename — it is a pure label mapping over the
same integers — but the next winoffcpu run will show the names.

## Verdict

**PASS — Step 4 (winoffcpu) is RUNTIME VERIFIED.** Both strict
criteria met (off-CPU 99.6% > 80%; max episode ≥ 90 ms — by the
run-length main-thread block and by the ~103–123 ms Task.Delay
cluster), diagnostics clean, arithmetic reconciles to the workload's
known shape. One labelling defect found and fixed in `e58be74`.
