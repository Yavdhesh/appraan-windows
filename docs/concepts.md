# Understanding the numbers — a plain-language guide

The README's *Reading the output* section explains every label the
tools print. This guide explains the **ideas behind the labels** — what
the kernel is actually telling you, and how to turn a table of numbers
into a diagnosis. Every worked number below comes from a real,
verified run of these tools against the `winload` test workload (see
the `evidence/` folder).

## 1. What is an episode?

An **episode** is one continuous stretch of time that a thread spends
off the CPU. It begins the moment the kernel switches the thread
*out*, and it ends when the thread is switched back *in*. Between
those two events the thread does exactly zero work — an episode is one
unbroken absence, stamped at its start with *why* it began (its wait
reason).

Picture one thread's life as a timeline:

```
[running 2 ms] → [EPISODE: waits 100 ms for the network] → [running 1 ms] → [EPISODE: waits 40 ms on a queue] → [running 3 ms] → …
```

The count and the duration tell different stories, which is why
`winoffcpu` reports both:

- A thread that leaves the CPU **1,000 times for a few microseconds**
  is *busy* — it's churning through work and getting briefly shuffled.
- A thread that leaves **10 times for seconds each** is *waiting* —
  something else is the bottleneck.

In one verified run against `winload`, the process accumulated
**4,113 episodes totalling 138.4 seconds** of off-CPU time — about
34 ms per episode on average. That average hides everything, so split
it by reason: the **Preempted** episodes averaged **~0.01 ms** each,
while the **WrQueue** episodes averaged **~40 ms** each
(88,776 ms ÷ 2,234 episodes). Same word — "episode" — completely
different phenomena.

## 2. The three lives of a thread: running, blocked, ready

At any instant, every thread of your process is in exactly one of
three states:

- **Running** — executing instructions on a core. This is the only
  state in which the thread makes progress.
- **Blocked** — asleep, waiting **on** something: a database query, a
  network reply, a lock another thread holds, a disk read, a timer.
  The thread *cannot* run even if a core sits idle.
- **Ready (preempted)** — runnable and holding work, but **no core is
  free**. The thread *could* run; the machine isn't letting it. It
  waits in the scheduler's queue, not on any resource.

The blocked/ready distinction is the single most useful split in
performance diagnosis, because the fixes are opposites:

| What you see | Diagnosis | Where to look |
|---|---|---|
| Blocked ↑, preempted ≈ 0 | Dependency problem | Your queries, locks, downstream services. A bigger machine changes nothing. |
| Blocked normal, preempted ↑ | Capacity problem | Thread counts vs core counts, a noisy neighbour on the host — or, the classic in Kubernetes, **cgroup CPU throttling**: the kernel parks your runnable threads because the container hit its CPU quota. |
| Both ↑ | The service is drowning | Preempted rising is usually your *earliest* warning that capacity, not code, is the wall. |

Beware the trap: a thread dump shows ready-but-starved threads as
`RUNNABLE`, so everything can look healthy inside the runtime while
the scheduler view shows your threads queueing for cores. Only the
off-CPU accounting sees it.

This is why these tools track all three numbers per thread — and why
**Appraan** (the Java tool this library supports) will show running,
blocked, and ready time side by side for every thread. Most tools only
ever show you the first.

## 3. Wait reasons in plain words

When a thread is switched out, the kernel records *why*. `winoffcpu`
groups every episode by that reason. The ones seen in the verified
runs:

- **WrQueue** — the thread is parked on a *queue*, waiting for work to
  be handed to it. This is where thread-pool and completion-port
  threads sit between tasks. **Idle hands, not stuck threads.**
- **UserRequest** — the thread is waiting for something the program
  itself asked for: a task to complete, a socket operation to finish,
  an event to be signalled. Waiting on purpose, for real work to come
  back.
- **ExecutionDelay** — deliberately sleeping: a timer wait, the
  `Sleep`/`Delay` bucket.
- **Executive** — waiting on a kernel synchronisation object in kernel
  mode; the generic "blocked in the kernel" reason.
- **Preempted** — **not a block at all**: the thread was ready to run
  but was switched out and had to wait for a free CPU. This is
  scheduling delay (see §2) — the cure is less CPU contention, not
  faster IO.
- **Standby / Running** — scheduler bookkeeping states (next in line
  for a core; switched out mid-quantum). Normally ~0 ms; ignore them.
- **WrResource** — waiting to acquire a kernel resource lock.
- **WrLpcReply** — waiting for a reply from another process via
  LPC/ALPC — i.e. talking to a Windows service.
- **WrPushLock** — waiting on a push lock, a lightweight kernel
  synchronisation primitive.
- **WrAlertByThreadId** — waiting in an alertable wait addressed by
  thread id (some timer/APC paths surface here).
- **Unknown** — a reason code the tool honestly does not map. It is
  shown, not hidden, so the totals always reconcile.

The story they tell, from the verified `winload` run: **WrQueue took
64%** of all off-CPU time and **UserRequest 35%** — a healthy async
workload whose threads spend their lives either idle awaiting work or
awaiting operations they requested. One subtlety worth knowing:
.NET's `Task.Delay` mostly surfaces under **UserRequest/WrQueue**, not
ExecutionDelay, because `Delay` is implemented as a timer plus
queueing a continuation — the calling thread is never parked for the
duration.

## 4. Why Disk can honestly be 0

New users often read `Disk: 0` in `winops` as "the tool is broken".
It usually means the opposite: the tool is honest.

Windows serves most file IO from the **file cache in RAM**. When your
4 KB write lands in the cache and your read is answered from the
cache, the physical disk device is never involved — and a `DiskIO`
kernel event only exists when an operation actually reaches the
device. No device IO happens, so no event, so the count is 0. The
event stream cannot be "missing" IO that never occurred.

To see real disk numbers you must **defeat the cache** — which is
what `winload --disk` does, writing 256 MiB with write-through so
every block is pushed to the device. The verified contrast, from the
same machine:

- Cached file operations: average **15.5 µs**.
- Physical disk operations (write-through): average **251.1 µs**, max
  **6.1 ms**.

That ~16× gap *is* the memory hierarchy, measured: RAM-speed answers
vs device-speed answers. One caveat in the other direction: disk
*reads* can appear even for cached workloads when cache pressure (say,
256 MiB of write-through traffic evicting cached pages) forces some
reads back to the device. A small non-zero Disk Read count during a
"cached" test is real, and this is why.

## 5. Units cheat sheet

- **µs** (written `us` in the tables, e.g. `AVG_US`, `<10us`) —
  microseconds. 1 µs is a millionth of a second; **1,000 µs = 1 ms**.
- **ms** — milliseconds. 1,000 ms = 1 s. `winoffcpu`'s times
  (`TOTAL_MS`, `OFFCPU_MS`, `MAX_MS`) are all in ms; `winops`
  latencies are in µs. Both tools now say so in a line under their
  banner.
- **KB** in `wintcplife` — kibibytes: payload bytes counted by the
  kernel ÷ 1,024. So **0.03 KB = 32 bytes** — exactly the test
  workload's payload, which is how the byte counts were verified.
- **Counts are exact** counts of kernel events — never samples,
  rates, or estimates. If a number surprises you, the workload really
  did that.
- **Off-CPU totals are summed across threads.** Fifteen threads each
  waiting 1 second is **15 s of off-CPU time in 1 s of wall-clock** —
  which is why a many-threaded process can show an off-CPU % near
  100 and hundreds of seconds of wait inside a 25 s watch. Judge the
  *reasons*, not the raw size.
