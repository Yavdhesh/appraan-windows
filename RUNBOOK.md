# Appraan.Windows — Runbook (Windows 11, Step 1 smoke test)

Goal of this run: prove the ETW plumbing end-to-end. You will start the
workload generator (`winload` — it makes loopback TCP connections and does
temp-file I/O for ~15 s), then watch its PID with `winscope` and confirm
winscope reports **nonzero Tcp and File counts** for that PID.

Everything below runs in **PowerShell**. Lines starting with `#` are comments.

---

## Step 0 — environment checks

Paste these into PowerShell:

```powershell
# 1) Is the .NET 8 SDK installed?
dotnet --list-sdks

# 2) Your Windows build (for the record):
winver        # a small window pops up; note the version/build, e.g. "Windows 11, build 22631"

# 3) Administrator check — run this in the SAME PowerShell you will use later:
net session
```

- `dotnet --list-sdks` should print at least one line starting with `8.`
  (e.g. `8.0.4xx [C:\Program Files\dotnet\sdk]`). If it errors or prints
  nothing → do **Step 0a** below.
- `net session` prints a list of sessions (or an empty list) when you are
  elevated. If it says **"Access is denied"**, that PowerShell is NOT
  administrator: close it, right-click PowerShell → **Run as administrator**,
  and keep using that window for Steps 2–3. Kernel ETW tracing cannot work
  from a non-admin window — winscope will refuse with a clear message if it
  is not elevated.

### Step 0a — install the .NET 8 SDK (only if Step 0 shows none)

```powershell
winget install Microsoft.DotNet.SDK.8
```

Then close and reopen PowerShell (admin) and re-check `dotnet --list-sdks`.
Fallback if winget is unavailable: download the SDK installer from
https://dotnet.microsoft.com/en-us/download/dotnet/8.0 and run it.

> Visual Studio is NOT required — the SDK alone builds everything.

## Step 1 — get the code

```powershell
git clone https://github.com/Yavdhesh/appraan-windows.git
cd appraan-windows
```

(Public repo — no login needed to clone. If `git` is not installed:
`winget install Git.Git`, reopen PowerShell, retry.)

If you already cloned earlier, update instead of re-cloning:

```powershell
git pull
```

This picks up the `winload --wait` fix (commit `e0c522f`) that makes the
smoke test race-free. Note: an admin PowerShell opens in
`C:\Windows\system32`, so relative paths like `.\src\...` only work after
you `cd` into the repo folder — every command below assumes you are in
`appraan-windows` first.

## Step 2 — build

```powershell
dotnet build -c Release
```

Expected: `Build succeeded` with `0 Error(s)` for all three projects
(`Appraan.Windows`, `winscope`, `winload`). NuGet restores the TraceEvent
package on first build, so the first run needs internet and takes a bit longer.

## Step 3 — run the smoke test (ADMIN PowerShell)

Open a **second** admin PowerShell window so winload and winscope run side
by side.

**Window A — start the workload, paused:**

```powershell
dotnet run -c Release --project src\winload -- --wait
```

It prints something like:

```
winload pid=12348
waiting — start winscope against this pid, then press Enter to begin the workload...
```

Note the pid (yours will differ) and **leave this window waiting**. The
`--wait` flag exists because the workload only runs ~15 s: without it,
you have to win a race against winload finishing.

**Window B — watch that PID (use YOUR pid, as a bare number):**

```powershell
dotnet run -c Release --project src\winscope -- 12348 --seconds 25
```

> Pass just the number — `12348`, not `pid=12348`. winscope parses its
> first argument as an integer PID and prints
> `usage: winscope <PID> [--seconds N]` if it cannot.

Once winscope is running in Window B, go back to Window A and **press
Enter**. The workload starts while winscope is already listening.

Expected output shape (counts will differ — what matters is the shape and
that Tcp and File are nonzero for winload's pid):

```
winscope: watching pid 12348 for 12s (session 'AppraanSession'). Ctrl+C to stop early.
[t+   2s] pid 12348: Tcp=46 File=128 Disk=7 Other=3 (total 184)
[t+   4s] pid 12348: Tcp=91 File=255 Disk=15 Other=6 (total 367)
...
=== winscope summary: pid 12348 over ~12s ===
Tcp=268  File=762  Disk=44  Other=17  Total=1091
(all processes in the same window: 50213 events)
```

### What success looks like

- ✅ winscope starts (no "requires an elevated process" error).
- ✅ In the summary for winload's pid: **Tcp > 0 AND File > 0**.
  winload does roughly 10 TCP connections and 10 file write+read cycles per
  second, and each produces several kernel events, so expect Tcp in the tens
  to low hundreds and File in the hundreds over a 12 s window.
- ✅ Counts climb between the live lines (events are streaming in real time).

### If something goes wrong

| Symptom | Fix |
|---|---|
| "Kernel ETW tracing requires an elevated process" | You're not in an admin PowerShell. Right-click → Run as administrator, retry. |
| `usage: winscope <PID> [--seconds N]` | The PID was passed with a prefix (e.g. `pid=12348`). Pass the bare number: `-- 12348`. |
| `Tcp=0 and File=0` for the pid | winload had already finished before winscope started, or the pid is wrong. Use `winload -- --wait`, start winscope against the fresh pid, then press Enter in winload's window. |
| `The term '.\src\winload\...' is not recognized` | You're not in the repo folder (admin PowerShell opens in `C:\Windows\system32`). `cd` into `appraan-windows` first, or use `dotnet run --project <full path to the .csproj>`. |
| `dotnet` not recognised | SDK missing → Step 0a, then reopen PowerShell. |
| Build errors mentioning TraceEvent / NuGet | Check internet access (first restore downloads packages), then re-run `dotnet build -c Release`. |

Paste the winscope summary back to Muse Ram either way — success or failure,
the exact output is what the next step gets built on.

---

# Step 2 — wintcplife (tcplife for Windows)

Goal of this run: prove the TCP lifecycle tracking end-to-end. `winload`
makes short loopback TCP connections; `wintcplife` should print one line
per connection **when it closes**, with endpoints, TX/RX bytes and lifetime
— the same report BCC's `tcplife` gives on Linux.

Prerequisite: Step 1 passed (SDK installed, repo cloned, solution builds).
Start by updating and rebuilding:

```powershell
cd appraan-windows          # wherever you cloned it
git pull
dotnet build -c Release
```

Expected: `Build succeeded`, `0 Error(s)`, now for **four** projects
(the new one is `wintcplife`).

## Run the test (ADMIN PowerShell, two windows)

**Window A — start the workload, paused** (same as Step 1):

```powershell
dotnet run -c Release --project src\winload -- --wait
```

Note the pid it prints and leave the window waiting.

**Window B — trace that pid's connections:**

```powershell
dotnet run -c Release --project src\wintcplife -- --pid 12348 --seconds 25
```

(Use YOUR winload pid, as a bare number after `--pid`.) Once wintcplife
prints its header, go back to Window A and **press Enter**. wintcplife
prints a one-line `open connections` summary every 5 s while it runs.

## What success looks like

One line per closed connection, roughly **two lines per winload iteration**
(winload hosts BOTH ends of each loopback connection — the outbound client
side and the accepted server side — and both belong to winload's pid):

```
PID    COMM           LADDR:LPORT              RADDR:RPORT                 TX_KB    RX_KB        MS
33108  winload        127.0.0.1:61339          127.0.0.1:55453              0.03     0.03       4.2
33108  winload        127.0.0.1:55453          127.0.0.1:61339              0.03     0.03       4.1
```

- ✅ **TX_KB ≈ RX_KB ≈ 0.03 on BOTH lines of each pair** — winload sends
  exactly 32 bytes and reads a 32-byte echo per connection (32 bytes =
  0.03 KB; the ETW `size` fields are what get summed, so small deviations
  are fine and worth pasting back verbatim). The client-side line and the
  accepted server-side line each carry their OWN socket's 32 out / 32 in:
  if the client line shows 0.06/0.06 and its twin 0.00/0.00, the byte
  accounting is cross-attributing the two ends (that was the pre-fix bug —
  make sure you `git pull`ed and rebuilt).
- ✅ **No phantom lines** — each connection prints exactly once. A second
  line repeating an earlier line's endpoints with 0.00/0.00 bytes and
  0.0 ms is a fabricated stub from an unmatched disconnect; the fixed
  build never prints those (it counts them in the summary instead — see
  below).
- ✅ **MS** is small — roughly 0.3–0.8 ms for winload's connections (each
  lives only for one request/echo).
- ✅ Final summary reports the total connections closed (≈ 2× winload's
  iteration count), total bytes, and the longest-lived connection, with
  `still open: 0` or a small number, plus an
  `unmatched disconnects (start never seen, no record printed)` count —
  0 or small is normal (a disconnect can outrun the trace start).

Also try it without `--pid` sometime — `wintcplife --seconds 20` shows
every process's connections on the machine, like the real `tcplife`.

### If something goes wrong

| Symptom | Fix |
|---|---|
| "Kernel ETW tracing requires an elevated process" | Not an admin PowerShell — reopen elevated, retry. |
| `usage: wintcplife [--pid N] [--seconds N]` | Bad flag or non-numeric value. Note the `--` before `--pid` is required with `dotnet run` so the flags reach wintcplife, not dotnet. |
| Header prints but no connection lines | winload's Enter wasn't pressed while wintcplife was running, or the pid is wrong. Redo with a fresh `winload -- --wait` and its new pid. |
| Lines appear but TX/RX are 0.00 | Paste the output back — that would mean ETW send/recv `size` fields aren't flowing as expected and the byte accounting needs a look. |
| A connection prints twice; the copy shows 0.00/0.00 and 0.0 ms, and/or one side shows 0.06 KB while its twin shows 0.00 | Pre-fix build — the old tracker cross-attributed the two ends of loopback connections and fabricated stub records from lone disconnects. `git pull`, rebuild, re-run. |

Paste the wintcplife output (a few connection lines + the summary) back to
Muse Ram — success or failure, the exact numbers are what Step 3 gets
built on.

---

# Step 3 — winops (kernel operations by category, with latency)

Goal of this run: prove the "syscount for Windows" view end-to-end.
`winops` watches one process and reports what it asks the kernel to do —
file, disk, network, process/thread (and optionally registry) operations:
how many, and for file/disk how long they take (average, max, and a
latency histogram). Same workload as before: `winload`'s loop of TCP
echo iterations + temp-file write/read gives us known numbers to check.

Prerequisite: Steps 1–2 passed. Update and rebuild first:

```powershell
cd appraan-windows          # wherever you cloned it
git pull
dotnet build -c Release
```

Expected: `Build succeeded`, `0 Error(s)`, now for **five** projects
(the new one is `winops`).

## Run the test (ADMIN PowerShell, two windows)

**Window A — start the workload, paused** (same as Steps 1–2):

```powershell
dotnet run -c Release --project src\winload -- --wait
```

Note the pid it prints, and — at the end of the run — winload's final
line: `winload done: N iterations ...`. **N matters**: the strict checks
below are expressed in terms of it (it was 131 in the Step 2 run).

**Window B — watch that pid's kernel operations:**

```powershell
dotnet run -c Release --project src\winops -- 12348 --seconds 25
```

> Pass the pid as a BARE number right after `--` — `12348`, not
> `--pid 12348` (winops takes the pid positionally, like winscope).
> Wrong arguments print `usage: winops <PID> [--seconds N] [--registry]`.

Once winops prints its first line, go back to Window A and **press
Enter**. winops prints the table every 5 s (cumulative) and once more at
exit with a per-kind drill-down and a diagnostics line.

## What success looks like

Table shape (your numbers will differ — the relationships are the test):

```
=== winops summary: pid 12348 over ~25s ===
CATEGORY        COUNT  LAT_OPS    AVG_US    MAX_US
File             1187      842      38.4     910.2
  hist: <10us:512  <50us:231  <100us:61  <500us:33  <1ms:5
  kinds: Create:262  Read:131  Write:131  Close:262  Cleanup:262  SetInfo:131 ...
Disk                0        -         -         -
Network          1048        -         -         -
  kinds: TcpConnect:131  TcpAccept:131  TcpSend:262  TcpRecv:262  TcpDisconnect:262
ProcessThread    2210        -         -         -
  kinds: ContextSwitch:2140  ThreadStart:35  ThreadStop:35
Registry            -        -         -         -  (not enabled — pass --registry)
diagnostics: pending file ops=2  unmatched completions=0  duplicate starts=0  evicted pending=0
total operations: 4445
```

**Strict checks** (these must hold — if not, paste the output back):

- ✅ In the File `kinds:` line, **Read ≥ N and Write ≥ N**, where N is
  winload's iteration count (one 4 KB write + one 4 KB read per
  iteration, every iteration, no exceptions).
- ✅ **File COUNT in the hundreds** (roughly 6–10 file operations per
  iteration: create/write/close + create/read/close, plus the runtime's
  own file activity in that process).
- ✅ **Network COUNT ≥ 262** (each winload iteration makes 2 connections
  — client + accepted server — and each contributes at least a
  connect/accept plus a disconnect; sends and receives push the expected
  total to roughly 500–1100).

**Expected shape** (indicative — interesting if different, not a failure):

- File **LAT_OPS** should be a large fraction of File COUNT, and the
  histogram should sit in the sub-millisecond buckets — winload's temp
  file is served from the Windows file cache, so its file operations are
  fast. (How completely completion events cover cached I/O is the main
  thing this run measures; the `diagnostics:` line quantifies any gap —
  pending operations should drain back towards 0 once winload finishes.)
- **Disk COUNT ≈ 0** — same cached-I/O finding as winscope's `Disk=0`:
  nothing reaches a physical device. A small nonzero number is fine.
- **ProcessThread COUNT in the hundreds or more**, dominated by
  `ContextSwitch` (winload's thread pool + 100 ms sleep loop schedules
  constantly), with some ThreadStart/ThreadStop.

## Optional second run — registry counts

Same two windows, but Window B gets one more flag:

```powershell
dotnet run -c Release --project src\winops -- 12348 --seconds 25 --registry
```

Success: the **Registry** row now shows real counts instead of
"(not enabled...)", and everything else behaves exactly as in the main
run. If enabling registry fails on your machine, winops prints a warning
(`could not enable registry tracing ... continuing without Registry
counts`) and the rest of the run still works — that fallback is by
design, but paste the warning back verbatim if you see it.

### If something goes wrong

| Symptom | Fix |
|---|---|
| "Kernel ETW tracing requires an elevated process" | Not an admin PowerShell — reopen elevated, retry. |
| `usage: winops <PID> [--seconds N] [--registry]` | Bad arguments — most likely `--pid 12348` (winops wants the bare pid) or a missing `--` after `dotnet run ... --`. |
| File/Network counts are 0 | winload's Enter wasn't pressed while winops was running, or the pid is wrong. Redo with a fresh `winload -- --wait` and its new pid. |
| `File.Read` or `File.Write` below winload's iteration count | Paste the full output back — that contradicts guaranteed workload operations and the start-event capture needs a look. |
| LAT_OPS is 0 or tiny while File COUNT is large | Paste the full output back, including the `diagnostics:` line — completion pairing is not flowing as designed and the counters will show where. |
| Registry warning printed, rest of run fine | Working as designed (fallback). Paste the warning line back so the cause is on record. |

Paste the final winops summary back to Muse Ram — success or failure,
the exact numbers are what the next step gets built on.

---

# Step 4 — winoffcpu (off-CPU analysis)

Goal of this run: prove the off-CPU view end-to-end. `winoffcpu` watches
one process's threads through ETW context switches and reports where
their time goes when they are **not** running — how much, in how many
episodes, and *why* (the kernel wait reason at each switch-out:
`Executive`, `UserRequest`, ...). This is the data BCC's `offcputime`
gives on Linux, minus stack traces (a later step). Same known workload:
`winload` spends most of its life blocked — a 100 ms `Task.Delay` per
iteration plus async waits on its loopback echo and temp file — so its
off-CPU signature is predictable.

Prerequisite: Steps 1–3 passed. Update and rebuild first:

```powershell
cd appraan-windows          # wherever you cloned it
git pull
dotnet build -c Release
```

Expected: `Build succeeded`, `0 Error(s)`, now for **six** projects
(the new one is `winoffcpu`).

## Run the test (ADMIN PowerShell, two windows)

**Window A — start the workload, paused** (same as Steps 1–3):

```powershell
dotnet run -c Release --project src\winload -- --wait
```

Note the pid it prints and leave the window waiting.

**Window B — profile that pid's off-CPU time:**

```powershell
dotnet run -c Release --project src\winoffcpu -- 12348 --seconds 25
```

> Pass the pid as a BARE number right after `--` — `12348`, not
> `--pid 12348` (winoffcpu takes the pid positionally, like winscope
> and winops). Wrong arguments print `usage: winoffcpu <PID> [--seconds N]`.

Once winoffcpu prints its first line, go back to Window A and **press
Enter**. winoffcpu prints a report every 5 s (cumulative) and once more
at exit.

## What success looks like

Report shape (your numbers will differ — the relationships are the test):

```
=== winoffcpu summary: pid 12348 over ~25s ===
on-CPU     412.6 ms   off-CPU    18204.3 ms   off-CPU  97.8%   episodes 486   max episode 108.4 ms
wait reasons:
  REASON            EPISODES   TOTAL_MS      %
  Executive              402    14980.2    82.3
  UserRequest             61     2890.5    15.9
  Preempted               18      210.9     1.2
  ...
top threads by off-CPU:
       TID EPISODES  OFFCPU_MS   MAX_MS   ONCPU_MS OFFCPU_%
     22104      131    9210.4    104.9      180.2     98.1
     ...
diagnostics: threads observed=9  partial=8  episodes closed=486 (by thread exit=131)  switch-ins w/o episode=0  switch-outs w/o on-CPU=0  open off-CPU=5  open on-CPU=0
```

**Strict checks** (these must hold — if not, paste the output back):

- ✅ All three sections print for winload's pid (totals line, wait-reason
  table, per-thread table).
- ✅ **Process off-CPU % > 80%** — winload is idle-dominated: brief work
  per iteration, then it blocks. (Expect it to actually land above 90%.)
- ✅ **Max episode >= 90 ms** — every iteration ends in a 100 ms
  `Task.Delay`, and that delay must show up as an off-CPU episode of
  roughly the delay's length on the main-loop thread.

**Expected shape** (indicative — interesting if different, not a failure):

- Wait reasons dominated by **Executive** (timer and delay waits are the
  classic Executive case), with **UserRequest** / **EventPair\*** present
  from the async socket and file waits, and only a small **Preempted**
  share (Preempted = switched out while still runnable — scheduling
  delay, which should be rare on an unloaded machine).
- **Episodes in the hundreds, not thousands** — winload's threads block
  for long stretches (100 ms at a time), they do not spin.
- The per-thread table shows several threads (main loop, accept loop,
  threadpool workers), most marked `(partial)` — they were already alive
  when the trace started, so their totals cover only the observed
  window. The thread with the most episodes and ~100 ms max episodes is
  winload's main loop.
- Diagnostics: `switch-ins w/o episode` and `switch-outs w/o on-CPU`
  small or zero (a few can occur at the trace edges). winload exits
  before winoffcpu's 25 s are up, so expect `episodes closed ... (by
  thread exit=...)` to be nonzero in the final summary — that is the
  ThreadStop/ProcessStop path closing out the dying process's threads.

### If something goes wrong

| Symptom | Fix |
|---|---|
| "Kernel ETW tracing requires an elevated process" | Not an admin PowerShell — reopen elevated, retry. |
| `usage: winoffcpu <PID> [--seconds N]` | Bad arguments — most likely `--pid 12348` (winoffcpu wants the bare pid) or a missing `--` after `dotnet run ... --`. |
| "(no threads observed yet)" / zero totals | winload's Enter wasn't pressed while winoffcpu was running, or the pid is wrong. Redo with a fresh `winload -- --wait` and its new pid. |
| Off-CPU % far below 80%, or max episode far below 90 ms | Paste the full output back, including the `diagnostics:` line — that contradicts winload's known block-mostly behaviour and the switch accounting needs a look. |
| One thread shows a single very long off-CPU episode | Plausible and usually real: a threadpool worker that blocks once and is never scheduled again accrues one episode until the trace ends or it exits. Episodes only ever open at an observed switch-out — the tool never invents off-CPU time for before the trace started — so a long single episode means the thread genuinely stayed off-CPU. |

Paste the final winoffcpu summary back to Muse Ram — success or failure,
the exact numbers are what the next step gets built on.

---

# Testing the Disk category (winops) — winload `--disk`

Why this exists: in every winops run so far, **Disk stayed 0** — correctly.
winload's temp file is small and repeatedly rewritten, so Windows serves
all of it from the file cache and nothing reaches a physical device. To
verify the Disk category for real, winload gained a deterministic disk
phase: after the normal loop it writes **256 MiB in 256 × 1 MiB blocks**
with `FileOptions.WriteThrough` (plus a final `Flush(true)`), which
bypasses the cache and forces physical disk I/O, then deletes the file.

> There is no matching registry test mode, deliberately: winops'
> Registry category is observe-only and already showed itself working
> (16 events generated by the .NET runtime's own activity in an earlier
> run: Open 3, QueryValue 3, Close 10), and no test code should write
> keys into your registry just to make a counter move. See STATUS.md.

Prerequisite: Steps 1–3 done. Update and rebuild:

```powershell
cd appraan-windows
git pull
dotnet build -c Release
```

## Run the test (ADMIN PowerShell, two windows)

**Window A — workload with the disk phase, paused:**

```powershell
dotnet run -c Release --project src\winload -- --wait --disk
```

It prints `disk mode: after the loop, ...` at startup, then the usual
waiting line. Note the pid and leave the window waiting.

**Window B — watch that pid (40 s, to cover the ~15 s loop + disk phase):**

```powershell
dotnet run -c Release --project src\winops -- 12348 --seconds 40
```

Once winops prints its first line, go back to Window A and **press
Enter**. winload runs its normal loop, then prints
`disk phase: wrote 256 MiB in 256 x 1 MiB write-through blocks`.

## What success looks like

**Strict checks** (these must hold — if not, paste the output back):

- ✅ **Disk COUNT > 0** — the write-through phase reached the device.
- ✅ **Disk LAT_OPS > 0** and a `hist:` line under the Disk row —
  completions carried their kernel-embedded elapsed times.
- ✅ In the final `kinds:` drill-down, Disk is dominated by **Write**
  (the phase is writes only; a few extra ops for file creation and
  metadata are normal).

**Expected shape** (indicative — interesting if different, not a failure):

- Disk Write count in the *neighbourhood* of 256, **not exactly 256**:
  the storage stack may split a 1 MiB write into several device I/Os or
  merge adjacent ones, and creating a 256 MiB file adds metadata writes.
  Any clearly-three-figure write count with latency ops is a pass.
- Disk latencies should be visibly larger than the file category's
  cache-served microseconds — real device time (write-through on an
  SSD is still fast, so sub-millisecond to a few ms is normal).
- File COUNT also grows during the disk phase (the write-through writes
  are file operations too, with their own latencies) — that is expected
  overlap between the categories, not double-counting.
- Nothing here tests disk *reads*: winload's disk phase only writes,
  and reads of a just-written file may be served from cache anyway, so
  no strict read expectation is made.

### If something goes wrong

| Symptom | Fix |
|---|---|
| Disk COUNT still 0 | The disk phase didn't run — check Window A shows the `disk mode:` startup line and the `disk phase: wrote 256 MiB ...` line; without `--disk` on winload, Disk = 0 is the correct result. |
| Disk COUNT > 0 but LAT_OPS = 0 | Paste the full output back, including the `diagnostics:` line — disk completions are arriving without their embedded elapsed time, which contradicts the payload design and needs a look. |
| winload throws during the disk phase | Note the message and paste it back — likely suspects are temp-folder space (the phase needs 256 MiB free in %TEMP%) or permissions. |
| `usage: winops <PID> ...` | winops wants the bare pid after `--` (see Step 3). |

Paste the final winops summary back to Muse Ram — the Disk row and its
`kinds:` line are the verdict.

# Integration — appraanagent (the Appraan sidecar)

`appraanagent` is how the four tools above plug into **Appraan** (the Java
tool). It runs all three trackers — ops (`winops`), TCP life
(`wintcplife`), off-CPU (`winoffcpu`) — inside **one** kernel session
for one PID. That is not a style choice: Windows allows effectively one
NT Kernel Logger session, and each standalone tool stops any stale
session when it starts, so the three tools cannot run side by side as
separate processes. One process, one session, three trackers.

Every interval it publishes **one JSON snapshot document** (the same
document on both sinks):

- **`--push <url>` — primary.** POSTs the snapshot to Appraan's UI
  server ingest endpoint. A failed push never stops the agent: you get
  one warning line when a failure streak starts, one line when it
  recovers, and because snapshots are cumulative the next successful
  push self-heals.
- **`--out <path>` — fallback / recorder.** Atomically rewrites a local
  file (temp file + rename, never a torn write) that Appraan polls with
  its `--kernel-file` option, and that doubles as the run's evidence.
  Use both sinks together when you want the live UI *and* the recording.

At least one of `--out` / `--push` is required. appraanagent is the
elevated half of Appraan on Windows — run it from an **admin
PowerShell**; Appraan itself never needs elevation (its Attach API
wants the target's own user).

## Run it (ADMIN PowerShell)

Primary flow — push to a running Appraan UI (Appraan is observing the
same PID in its own window; see the Appraan repo for the full
three-window test):

```powershell
dotnet run -c Release --project .\src\appraanagent -- <pid> --push http://127.0.0.1:7676/api/kernel/ingest --seconds 60
```

Fallback flow — record to a file instead (Appraan side then runs with
`--kernel-file %TEMP%\appraan-kernel.json`):

```powershell
dotnet run -c Release --project .\src\appraanagent -- <pid> --out %TEMP%\appraan-kernel.json --seconds 60
```

For a realtime feel in Appraan's UI mode (`--seconds 0`), publish more
often — **`--interval 2`** pairs well with it:

```powershell
dotnet run -c Release --project .\src\appraanagent -- <pid> --push http://127.0.0.1:7676/api/kernel/ingest --interval 2
```

## What success looks like

- The banner lists the sink(s): `push -> http://127.0.0.1:7676/api/kernel/ingest`
  and/or `file -> ...\appraan-kernel.json`.
- A `[t+ Ns] snapshot published (off-CPU X%)` line every interval.
- With `--out`: the file exists from t=0 and its `tEpochMs` /
  `windowSeconds` advance on every interval.
- With `--push`: no `push ... failed` warning lines (or a `recovered`
  line after Appraan's UI starts). In Appraan's dashboard, the Kernel
  panel fills in within one interval.

### If something goes wrong

| Symptom | Fix |
|---|---|
| `usage: appraanagent <PID> ...` (exit 2) | A PID and at least one of `--out` / `--push` are required. |
| `appraanagent: ... requires an elevated process` | Re-run from an admin PowerShell — ETW kernel sessions need elevation. |
| `push ... failed (HttpRequestException ...)` warnings | Appraan's UI isn't up yet, or the port differs (`--port` on the Appraan side). The agent keeps running; pushes self-heal when the UI appears. |
| Kernel panel in Appraan says "stale" | Snapshots stopped arriving — check the agent window is still running and the interval/seconds haven't expired. |
