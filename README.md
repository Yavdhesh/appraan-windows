# Appraan.Windows

**Kernel-level observability for Windows — the functionality Linux gets from eBPF/BCC, built on Windows' own kernel tracing (ETW + eBPF-for-Windows).**

Linux engineers reach for BCC tools — `tcplife`, `syscount`, `biolatency`,
`offcputime` — to see what a process is really doing inside the kernel.
Windows has no direct equivalent. Appraan.Windows is that equivalent: a
.NET library plus a set of small, focused CLIs that answer the same
questions per process — what is this app asking the kernel to do, how
often, and how long does it take?

Appraan.Windows is the Windows collector layer of
[Appraan](https://github.com/Yavdhesh) — *the life of your application,
live* — which pairs it with a JVM observer (JFR + Attach API) and a Linux
eBPF collector behind one metric model and one UI. Features are defined by
their **meaning**, never by one OS's implementation; each platform fulfils
them with its native source. See [DESIGN.md](DESIGN.md) for the parity
contract, including the one documented gap (per-syscall-*name* tracing has
no supported Windows source — the cross-platform feature is *kernel
operations by category, with latency*).

## Tools

| Tool | BCC analogue | What it does | Status |
|---|---|---|---|
| `winscope` | — (foundation) | Live per-category kernel event counts for one PID (TCP / File / Disk / other) | ✅ verified on Windows 11 |
| `wintcplife` | `tcplife` | One line per closed TCP connection: endpoints, bytes sent/received, lifetime, retransmits | ✅ verified — byte totals arithmetically exact vs a known workload |
| `winops` | `syscount` + latency | Kernel operations by category (File, Disk, Network, Process/Thread, optional Registry) with average/max latency and latency histograms for File and Disk | built, verification in progress |
| `winload` | — (test workload) | Deterministic TCP + file workload used to verify the tools against known numbers | ✅ |

Every "verified" claim is backed by a run in [`evidence/`](evidence/) —
the workload, the exact commands, the observed output, the expected
values, and the verdict with the arithmetic shown. Failed verifications
are kept there too; they are how the tools got correct.

## Quick start (Windows 11, admin PowerShell)

```powershell
winget install Microsoft.DotNet.SDK.8   # if dotnet is not installed; reopen PowerShell after
git clone https://github.com/Yavdhesh/appraan-windows.git
cd appraan-windows
dotnet build -c Release

# window A — a workload to observe (prints its PID, waits for Enter)
dotnet run -c Release --project .\src\winload -- --wait

# window B — observe it
dotnet run -c Release --project .\src\wintcplife -- --pid <winload-pid> --seconds 25
# then press Enter in window A
```

Full walkthroughs, expected outputs and troubleshooting: [RUNBOOK.md](RUNBOOK.md).
Design and parity contract: [DESIGN.md](DESIGN.md). Build/verification
state per step: [STATUS.md](STATUS.md).

## Requirements

- Windows 11 (developed and verified on build 10.0.26300)
- .NET 8 SDK
- Administrator rights — ETW kernel tracing requires elevation

## Roadmap

- IO latency detail (per-operation-type histograms) — in progress
- Off-CPU analysis with wait reasons (ETW context switches)
- Per-core CPU and thread core occupancy; per-process network throughput
- eBPF-for-Windows utilities for the socket-level hooks ETW cannot provide
- Appraan integration: this library becomes Appraan's Windows collector

## License

Apache License 2.0 — see [LICENSE](LICENSE).
