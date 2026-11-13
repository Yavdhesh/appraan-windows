# Appraan.Windows — Windows OS-level observability library

Goal (user, 2026-11-13): a Windows library/utilities set with the SAME OS-level
functionality Linux gets from eBPF/BCC, built FIRST; Appraan (the JVM tool)
then consumes it as its Windows collector layer. Linux fulfils the same
feature contract via eBPF. Features are defined by MEANING, never by the
Linux implementation; nothing is emulated or faked.

## Stack decision
- C# / .NET 8 (LTS), Microsoft.Diagnostics.Tracing.TraceEvent for ETW
  consumption (the library PerfView is built on — maintained, proven).
- eBPF-for-Windows components (C, clang) only where ETW has no source
  (later phase, socket-level hooks).
- Every utility ships twice: library API (for Appraan) + standalone CLI exe
  (BCC-style), in one solution.

## Feature contract — BCC/Linux equivalent → Windows source
| Feature (meaning) | Linux (reference) | Windows source |
|---|---|---|
| TCP connection life per process (tcplife) | eBPF TCP state tracepoint | ETW Microsoft-Windows-TCPIP + Kernel-Network |
| Kernel operations by category + latency ("syscount" meaning) | raw_syscalls tracepoints (names = Linux-only extra) | ETW FileIO / DiskIO / Registry / TCPIP providers, event-pair latency |
| IO ops + latency per process (biolatency/biosnoop) | eBPF block/file tracepoints | ETW DiskIO + FileIO (init/completion pairs) |
| Off-CPU time, stacks, wait reasons (offcputime) | eBPF sched_switch + stacks | ETW CSwitch (incl. wait reason) + stack walking |
| Per-core CPU; thread core occupancy | /proc, sched events | PDH counters; ETW CSwitch CPU ids |
| Network throughput per process | eBPF socket counters | ETW TCPIP byte counts per PID |
| Page faults, context switches | /proc, perf | ETW PageFault / CSwitch |
| Memory bandwidth | perf / Intel PCM (system-level) | Intel PCM on Windows (system-level) |

Documented gap (accepted by user): per-syscall-NAME count/latency has no
supported Windows source. The cross-platform feature is "kernel operations
by category, with latency"; raw syscall names are a Linux-only drill-down.

## Build order (feature by feature)
1. Solution skeleton + ETW session plumbing: start/stop a real-time ETW
   session as admin, per-PID filtering, event→record model shared by all
   utilities. Smoke test: count events for one PID for 10 s.
2. tcplife equivalent (most self-contained ETW surface).
3. Kernel ops by category + latency (FileIO/DiskIO/Registry/TCPIP pairs).
4. IO latency histograms per process.
5. Off-CPU (CSwitch + wait reasons; stacks need symbol handling — hardest).
6. Per-core CPU + thread occupancy; per-process network throughput.
7. eBPF-for-Windows utilities where ETW is blind (socket hooks).
8. Appraan integration: Appraan's WindowsOsCollector delegates here.

## Working arrangement
- Assistant's VM is Linux: this library is WRITTEN by the assistant but
  BUILT AND TESTED on the user's Windows machine (same pattern as the
  Dragonfly signed push). Each step ships with exact build/run commands and
  an expected output; user pastes results back.
- Needs from user (asked 2026-11-13, awaiting answer): Windows 10 or 11?
  Visual Studio / .NET SDK installed? Admin on the machine?

## Verification rule
A utility is "done" only with a real run on the user's machine against a
known workload (e.g. a test process doing controlled IO/TCP), with counts
that match the workload — never by code inspection alone.
