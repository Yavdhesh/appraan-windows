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

**Window A — start the workload:**

```powershell
dotnet run -c Release --project src\winload
```

It prints something like:

```
winload pid=12348
generating TCP + file workload for ~15 s... watch this pid with winscope.
listening on 127.0.0.1:51234
```

Note the pid (yours will differ). It only runs ~15 s, so be ready with
Window B — or simply re-run it in Window A when it finishes; you can repeat
the test as often as you like.

**Window B — watch that PID (use YOUR pid):**

```powershell
dotnet run -c Release --project src\winscope -- 12348 --seconds 12
```

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
| Tcp=0 and File=0 for the pid | winload had already finished before winscope started, or the pid is wrong. Re-run winload and immediately run winscope with the fresh pid. |
| `dotnet` not recognised | SDK missing → Step 0a, then reopen PowerShell. |
| Build errors mentioning TraceEvent / NuGet | Check internet access (first restore downloads packages), then re-run `dotnet build -c Release`. |

Paste the winscope summary back to Muse Ram either way — success or failure,
the exact output is what the next step gets built on.
