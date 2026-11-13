# Appraan.Windows — STATUS

## Step 2: wintcplife (tcplife equivalent) — FUNCTIONAL on Win11, byte-accounting fix applied, RE-VERIFICATION pending
Functional on Win11 (2026-11-13 run: lines/endpoints/durations correct,
222+ closed tracked), byte-accounting fix applied, RE-VERIFICATION pending.

### What the first Win11 run proved (winload pid 54732, 132 iterations)
- ✅ Connection lines print on close with correct endpoints and plausible
      durations (~0.3–0.8 ms); 222+ connections closed and tracked.
- ✅ ETW `size` on send/recv is payload size — settled by the numbers
      below (each socket moves exactly 32 B each way per iteration).
- ❌ Byte attribution was WRONG, in a very specific pattern: client-side
      records showed TX_KB=RX_KB=0.06 (exactly double the expected
      0.03/0.03), accepted server-side records showed 0.00/0.00, and
      phantom duplicate lines appeared (same tuple as a real record,
      0.00/0.00 bytes, 0.0 ms).

### Root cause (confirmed in the old code)
The old table was keyed by (ProcessId, local, remote) and `FindEntry`
searched it in BOTH orientations, with connid as a guarded secondary
index. On same-process loopback the client entry (53623→53567) and the
accepted entry (53567→53623) are exact mirror tuples, so:
1. `OpenConnection` for the Accept event found the CLIENT entry via the
   swapped-orientation search and "adopted" it — the accepted entry was
   never created.
2. Worse, that lookup called `RegisterConnId`, binding the ACCEPTED
   connection's connid to the client entry. The `Fits` endpoint guard
   could not catch the poisoning: mirror tuples fit in either orientation.
3. From then on the server's Send (echo) and Recv (request) events hit the
   poisoned connid index first and landed on the client record → client
   TX and RX each grew by the server's 32 B → the symmetric 0.06/0.06.
4. The accepted side's Disconnect then matched nothing (or an
   already-closed entry), and `CloseConnection` fabricated the
   zero-duration orphan stub → the phantom duplicate lines.

### The fix (this commit)
- **connid is now the PRIMARY key**: the open table is keyed by
  (ProcessId, connid); endpoints are needed only as a fallback key for
  events whose connid is 0 (kept in the fixed per-event-kind orientation).
- Endpoints and direction (`IsOutbound`: Connect → true, Accept → false,
  data-created → unknown) are fixed ONCE at entry creation. Send/Recv for
  a known connid just add bytes — Send is always the owning socket's TX,
  Recv its RX — so no orientation is ever re-derived per data event.
- A Disconnect for an unknown connid creates NO record; it increments the
  new `TcpConnectionTracker.UnmatchedDisconnects` diagnostics counter,
  printed in wintcplife's exit summary.
- connid reuse: closed connids are remembered — trailing data events for
  them are dropped as late arrivals; a Connect/Accept for a closed connid
  starts a fresh generation; a Connect/Accept contradicting a still-open
  entry's endpoints displaces it (old entry closed as estimated).
- UDP: the tracker subscribes only to TcpIp* parser events (verified in
  `TcpConnectionTracker.Attach`); UDP events never enter it. The UdpIp
  subscriptions in KernelSession serve winscope's category counts only.
- winscope untouched; its build and Linux-verified behaviours (platform
  guard exit 1, usage exit 2 — re-checked for wintcplife too) unchanged.

### Build after the fix (real output, this VM, .NET SDK 8.0.428)
```
  Appraan.Windows -> .../src/Appraan.Windows/bin/Release/net8.0/Appraan.Windows.dll
  winscope -> .../src/winscope/bin/Release/net8.0/winscope.dll
  winload -> .../src/winload/bin/Release/net8.0/winload.dll
  wintcplife -> .../src/wintcplife/bin/Release/net8.0/wintcplife.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Re-verification (user test — RUNBOOK.md Step 2)
`git pull`, rebuild, then `winload --wait` in window A and
`wintcplife --pid <winload pid> --seconds 25` in admin window B; press
Enter in A. Expected after the fix, per winload iteration exactly 2
records (client + accepted server): client TX≈RX≈0.03 KB, server
TX≈RX≈0.03 KB, NO zero-byte phantom lines, durations ~0.3–0.8 ms, and an
`unmatched disconnects` count (0 or small) in the exit summary.

### Original Step 2 build notes (kept for reference)
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
- [x] CLI: src/wintcplife — `wintcplife [--pid N] [--seconds N]` (no
      --seconds = until Ctrl+C, like BCC). Prints a tcplife-style line per
      closed connection (PID COMM LADDR:LPORT RADDR:RPORT TX_KB RX_KB MS),
      a 5 s live summary (open count), and an exit summary (closed count,
      total TX/RX bytes, longest-lived connection, still-open count, and —
      since the fix — the unmatched-disconnects count).
- [x] RUNBOOK.md: "Step 2 — wintcplife" section with the two-window test;
      expected-output notes updated for the fix (0.03 KB on BOTH sides,
      no phantom lines).

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
