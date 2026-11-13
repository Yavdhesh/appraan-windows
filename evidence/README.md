# Evidence

Every feature in this repo is verified by a real run on a real Windows
machine against a workload whose behaviour is known in advance — never by
code inspection alone. This folder holds those runs.

Convention: one file per verification run, named
`YYYY-MM-DD-step<N>-<tool>-<what>.md`, containing the environment, the exact
commands, the observed output (verbatim where it matters; long outputs are
excerpted and marked as such), the expected values, and the verdict with the
arithmetic shown.

| File | What it proves |
|---|---|
| `2026-11-13-step1-winscope-smoke-test.md` | Step 1: ETW kernel session + per-PID filtering work on Windows 11 (Tcp/File events for a known workload process). |
| `2026-11-13-step2-wintcplife-first-run-defect.md` | Step 2, first run: connection tracking works, but byte counts are doubled and phantom records appear — the defect record that drove the connid re-keying fix. Kept deliberately: failed verification is evidence too. |
| `2026-11-13-step2-wintcplife-reverification.md` | Step 2, after the fix: byte totals arithmetically exact (262 × 32 B), zero phantoms, zero unmatched disconnects. Step 2 verified. |
| `2026-11-13-step3-winops-verification.md` | Step 3: ops by category with latency vs winload — File.Write exactly N, latency pairing covers 100% of file ops, network kinds internally exact. Step 3 verified. |
