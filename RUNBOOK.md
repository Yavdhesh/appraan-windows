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
