# Appraan.Windows — STATUS

## Step 2: wintcplife (tcplife equivalent) — CODE COMPLETE, COMPILE-VERIFIED (2026-11-13)
Runtime is UNVERIFIED — per the verification rule it is "done" only after
the RUNBOOK.md Step 2 test passes on the user's Windows 11 machine.

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
- [x] Library: `TcpConnectionTracker` + `TcpConnectionRecord` (+
      TcpConnectionState). Table keyed by (ProcessId, local endpoint,
      remote endpoint); ETW's packet-perspective saddr/daddr is converted
      per event kind (Accept/Recv: source is the remote side). A secondary
      connid index — validated against the stored endpoints, so recycled
      connids/PIDs can't merge connections — makes send/recv/disconnect
      correlation orientation-proof. Bytes = sum of ETW `size` on send/recv;
      retransmits counted separately, never double-counted. Connections
      predating the trace are created on first data event with
      EstimatedStart=true; a lone disconnect yields a zero-duration
      estimated record instead of being dropped; never-closed entries stay
      visible via GetOpenConnections().
- [x] CLI: src/wintcplife — `wintcplife [--pid N] [--seconds N]` (no
      --seconds = until Ctrl+C, like BCC). Prints a tcplife-style line per
      closed connection (PID COMM LADDR:LPORT RADDR:RPORT TX_KB RX_KB MS),
      a 5 s live summary (open count), and an exit summary (closed count,
      total TX/RX bytes, longest-lived connection, still-open count).
      Bad args → usage + exit 2 (Linux-verified); off-Windows → clean
      platform-guard message (Linux-verified).
- [x] `dotnet build -c Release` → **Build succeeded, 0 warnings, 0 errors**
      (all 4 projects: Appraan.Windows, winscope, winload, wintcplife)
- [x] RUNBOOK.md: "Step 2 — wintcplife" section with the two-window test.

## Runtime-unverified for Step 2 (user test — RUNBOOK.md Step 2)
`winload --wait` in window A; `wintcplife --pid <winload pid> --seconds 25`
in an admin window B; press Enter in A. Success = closed-connection lines
appear, ≈2 per winload iteration (winload owns BOTH loopback ends), each
with TX_KB ≈ RX_KB ≈ 0.03 (winload sends 32 B and reads a 32 B echo per
connection; ETW `size` sums may differ slightly), small MS durations, and
the exit summary's totals/longest-lived line. Open questions the run will
answer: whether ETW send/recv `size` carries payload only, and whether the
per-kind saddr/daddr orientation assumptions hold on real events (the
connid index makes correlation robust either way; swapped byte direction
on accepted sockets is the symptom to watch for).

## Next (per DESIGN.md build order)
3. Kernel ops by category + latency (FileIO/DiskIO/Registry/TCPIP pairs).

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
