# Appraan.Windows — STATUS

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
2. tcplife equivalent — TCP connection lifecycle per process, using the
   TcpIp typed events already wired in KernelSession.
