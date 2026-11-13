# appraanagent → Appraan live loop — RUNTIME VERIFICATION (2026-11-13)

The standalone integration loop, run end-to-end on a real Windows machine:
a target JVM, `appraanagent` (elevated) pushing kernel snapshots, and
Appraan's UI displaying JVM + kernel data for the same PID. This is the
runtime verification that Milestone 3b was waiting for (ETW cannot run on
the assistant's Linux VM; proof run4 in the appraan repo covered
everything provable there).

## Environment

- Machine: user's Windows 11 (OS build 10.0.26300), host name **RADHEY**
- Target: `dev.appraan.demo.LoadApp` from the appraan repo, **PID 43292**,
  OpenJDK 24.0.2+12-54, launched with `-Xmx256m -XX:+UseG1GC`
- appraanagent: `dotnet run -c Release --project .\src\appraanagent -- 43292 --push http://127.0.0.1:7676/api/kernel/ingest --interval 2`
  (Administrator PowerShell; default transport — plain JSON over HTTP,
  no compression, no encryption)
- Appraan: `java --add-modules jdk.attach --add-exports jdk.attach/sun.tools.attach=ALL-UNNAMED -jar dist\appraan.jar 43292 --seconds 0`
  (normal shell, same user as the target; JDK 24 build, commit 96b8dea line)
- Browser: dashboard `/`, kernel page `/kernel` (user-pasted page text,
  excerpted below where used)

## Observed (excerpts, two snapshots ~3 minutes apart)

Dashboard header: "Observing PID 43292 · OpenJDK 64-Bit Server VM version
24.0.2+12-54 · elapsed 594.5s". JVM side: GC collector G1; MaxHeapSize
268435456; G1HeapRegionSize 1048576; 256 regions; Young GCs 432, Old 0;
pause total 499 ms, max 2 ms; promotion est. 3.2 MiB; allocation rate
104.7 MiB/s, total allocated 61.8 GiB, heap used 8.1 MiB; CPU 42,460
execution samples, top leaf `LoadApp.isPrime` 39,887 (93.9%).

Kernel panel (push, 2 s old): host RADHEY; off-CPU 96.556% (on 388,905 ms,
off 10,903,253 ms, 397,988 episodes, max episode 102,043 ms); File 81,708
(avg 46.0 µs, max 43.3 ms); Disk 5,960 (avg 218.2 µs); Network 0;
ProcessThread 399,315; Registry 0; TCP 0 closed, TX 0 B, RX 0 B.

Kernel page (later snapshot, agent window 403.544 s): off-CPU 96.6%
(on 411,373 ms, off 11,560,958 ms, 422,589 episodes, max 102,043 ms).
Wait reasons: UserRequest 65.6%, WrAlertByThreadId 27.9%, ExecutionDelay
3.4%, WrQueue 3.0%, Executive ~0.0%, Preempted 95,554 episodes / 4,832 ms,
plus Suspended / Standby / Unknown and one bare "9" (39,468 episodes,
164 ms — defect D1 below). File 87,604 with latency on 87,604 (100%);
kinds QueryInfo 33,264, Write 30,609, Create 7,623, Cleanup 7,392, Close
7,392, DirEnum 539, FSControl 308, Read 246, SetInfo 154, Flush 77;
histogram <10us 74,948 (85.6%). Disk 6,401 (Write 6,229, Read 170,
Flush 2), avg 222.3 µs. ProcessThread 423,995 (ContextSwitch 423,831,
ThreadStart 83, ThreadStop 81). Diagnostics: pending file ops 0,
unmatched completions 558, duplicate starts 0, evicted pending 0.
Per-thread table: top threads off-CPU ≈402.4 s each within the 403.5 s
agent window (99.9–100.0%), all marked (partial) as expected for an
agent that attached after the target started.

## Cross-checks (the arithmetic)

1. **Heap identity:** MaxHeapSize 268,435,456 B = 256 MiB = the
   `-Xmx256m` launch flag; 256 regions × 1 MiB region size = 256 MiB. ✔
2. **Episodes vs context switches:** 422,589 off-CPU episodes vs 423,831
   ContextSwitch events for the PID — same magnitude, 0.3% apart
   (switch count also covers edges that do not open an episode). Two
   independent trackers agree. ✔
3. **File latency coverage:** 87,604 / 87,604 = 100%; unmatched
   completions 558 = 0.64% of File ops — the same order as Step 3's
   verified 0.6% (ops in flight at session start). ✔
4. **Allocation consistency:** 61.8 GiB over 594.5 s elapsed ⇒ mean
   ≈ 106 MiB/s vs the displayed windowed rate 104.7 MiB/s. ✔
5. **Zeros are correct, not missing data:** LoadApp performs no network
   IO, so Network 0 and TCP 0 closed / 0 B are the right answer for this
   workload (contrast the winload runs, where TCP totals were exact).
   Registry 0: the agent does not enable the Registry keyword (it is
   opt-in in winops only). ✔
6. **Wait-reason shape matches the workload:** LoadApp sleeps and
   contends; UserRequest + WrAlertByThreadId (the alertable-wait
   mechanism Windows timers/sleeps use) = 93.5% of off-CPU time. ✔
7. **Off-CPU totals are summed across threads:** 11,561 s off within a
   403.5 s window ⇒ ≈28.7 threads' worth — LoadApp's burner/churn/
   contend/sleeper pool; the top threads sit at ~100% off for the whole
   window. ✔

## Defects found by this run (both fixed)

- **D1 — bare "9" label.** One off-CPU row was labelled "9": the kernel
  reports thread state 9 (**WaitingForProcessInSwap**, WDK KTHREAD_STATE)
  for some switch-outs, and `System.Diagnostics.ThreadState` does not
  define it, so `ToString()` rendered the number. Fixed in commit
  `fc321e4` (`StateLabel` in OffCpuTracker; unnamed values now fall back
  to `ThreadState(<n>)`). Shared by winoffcpu and appraanagent.
- **D2 — dashboard header STALE flapping** (appraan repo). JVM snapshots
  are captured every `--interval` seconds (default 5), but the header
  declared STALE at age ≥ 3 s, so a healthy observer showed STALE for
  ~2 s of every 5 s cycle — the user's capture shows "STALE · updated
  4s ago" while kernel data flowed at 2 s old. Fixed in appraan commit
  `747698f`: the page learns the capture cadence and declares STALE only
  after ~2.5 missed captures (floor 6 s).

## Not separately confirmed in this run

- Interactive flame-graph click-to-zoom in the browser (its markup was
  verified in appraan proof run4; the user did not report clicking it).
- The kernel panel's stale flip ~15 s after stopping the agent (the
  STALE visible in the capture was defect D2, not the kernel staleness
  path). Both are one-minute checks for the next session.

## Verdict

**RUNTIME VERIFIED.** The standalone loop — target JVM →
appraanagent (elevated, plain JSON/HTTP defaults) → Appraan UI with JVM
and kernel data for one PID on one machine — works end-to-end on
Windows 11, with every headline number cross-checked above. Milestone
3b is complete.
