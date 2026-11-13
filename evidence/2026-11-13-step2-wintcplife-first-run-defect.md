# Step 2 — wintcplife first run: DEFECT FOUND (kept as evidence)

- **Date:** 2026-11-13
- **Machine:** user's Windows 11, build 10.0.26300, admin PowerShell
- **Code:** commit `fa83e4b` (original tuple-keyed tracker)
- **Workload:** `winload --wait`, PID 54732, **132 iterations**. Each
  iteration = 1 client connection that sends **32 bytes** and receives a
  **32-byte echo**, plus the accepted server-side socket in the same process.
  Therefore the correct per-connection values are TX ≈ RX ≈ **0.03 KB**.

## Observed output (excerpt — representative lines)

```
54732  winload        127.0.0.1:53623          127.0.0.1:53567              0.06     0.06       0.6
54732  winload        127.0.0.1:53623          127.0.0.1:53567              0.00     0.00       0.0
54732  winload        127.0.0.1:53624          127.0.0.1:53567              0.06     0.06       0.6
54732  winload        127.0.0.1:53624          127.0.0.1:53567              0.00     0.00       0.0
54732  winload        127.0.0.1:53567          127.0.0.1:53683              0.00     0.00       0.0
[t+  20s] open connections: 0, closed so far: 134
[t+  25s] open connections: 0, closed so far: 222
```

(53567 = winload's listener port. Full output was ~260 lines, pasted by the
user in chat on 2026-11-13; the pattern below repeats throughout.)

## What was right

- Connections tracked with correct endpoints and process.
- Lifetimes plausible for loopback echo: ~0.3–0.8 ms.

## What was wrong

1. **Bytes exactly doubled:** client records read TX 0.06 / RX 0.06 KB
   instead of 0.03 / 0.03. The symmetry is the fingerprint: the accepted
   (server) socket's send/recv bytes were merged into the client record.
2. **Phantom twin records:** most connections printed a second line with
   identical addresses, 0.00 bytes, 0.0 ms.
3. **Accepted-side records carried 0.00 bytes** — their data events never
   reached them (they had been merged into the client record, see 1).

## Root cause (confirmed by code inspection afterwards)

The tracker correlated events by address tuple and searched the table in
*both* orientations. On same-process loopback the client tuple and the
accepted tuple are exact mirrors, so when the Accept event arrived, the
lookup found the *client* entry "backwards" and adopted it — binding the
server connection's `connid` to the client entry. All server bytes then
landed on the client record (+32 B TX, +32 B RX → the symmetric doubling),
and the server's later Disconnect matched nothing open, so the close path
fabricated a zero-duration stub record (the phantom lines).

## Fix

Commit `2b8193f`: correlation keyed strictly on **(PID, connid)** — the
kernel's own per-connection id; direction fixed once at connection creation
(Connect = outbound, Accept = inbound); a Disconnect with no known connid is
counted as an "unmatched disconnects" diagnostic instead of fabricating a
record.

## Verdict

**FAIL (by design of the process)** — connection tracking proven, byte
accounting rejected. Step 2 was NOT marked verified on this run. See
`2026-11-13-step2-wintcplife-reverification.md` for the post-fix run.
