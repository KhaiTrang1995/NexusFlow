# QR2 — the chaos rig, and what SIGKILL at a step boundary actually does

> **Scope, first, because this package is deliberately incomplete.** `PLAN.md` §5 **WP-50**
> is *"B7, B8 and the QR2 chaos rig"*. **Only the rig is built.** B7 and B8 are latency
> budgets and performance is set aside for this phase. Nothing below closes them, and WP-50
> must not be read as complete.
>
> **What the rig kills.** Separate operating-system processes, against a shared PostgreSQL,
> with `SIGKILL` — not a dropped lease, not a cancellation token, not a graceful stop. The
> signal is taken at an instruction chosen so that a step's effect has happened and the
> engine's commit has not. Every killed worker is observed by the coordinator to exit **137**,
> which is what a process killed by signal 9 looks like from outside; a run in which no worker
> exits 137 is refused rather than passed.
>
> **Verdict: PASS on both correctness clauses, at QR2's own scale.** **10 000 flows** per
> arm, 30 000 non-idempotent effects, **97 processes killed by `SIGKILL` per arm**, **zero
> duplicate effects against the guarantee**, **zero lost instances**, zero orphan effects,
> zero instances executed by two live nodes.
>
> **The exposure is exactly the one ADR-0006 wrote down, and no larger.** 260 and 186
> duplicate applications, every one of them a step whose commit was not in the journal when
> the recovering node took over. Run at concurrency 1, where each kill abandons exactly one
> flow, the count is exact in both directions: killing **after** the effect and **before** the
> commit produced **20 duplicates from 20 kills**; killing **after** the commit and **before**
> the next effect produced **0 duplicates from 20 kills** — 180 applications for 60 flows ×
> 3 steps, not one more.
>
> **Resume p99 is 32.9 s and 32.6 s against QR2's 45 s** on the recorded run. Measured,
> reported, **not gated on** — see the scope note above. **Two other runs of the same rig
> missed it**: 42.9 s / 48.1 s at the shipped defaults and 69.9 s / 68.8 s in a 500-flow pilot
> on a *quieter* machine, both with every correctness row still zero. That spread is not
> noise, and §4.4 is the explanation: the number is ~30 s of lease TTL plus however long a
> backlog takes to drain through `MaxConcurrentRecoveries`. **Read as a single figure the p99
> is not a property of FlowX, and this document does not present it as one.**
>
> **One number that is not zero and is not a duplicate:** 61 and 57 of 10 000 instances were
> claimed by a worker that was killed before it could open a journal row. No effect happened
> and nothing was lost, but the flow did not run. §3.2 says why that is counted separately
> rather than netted off.
>
> **Recorded:** 2026-08-01 · **P2** · `./scripts/run-chaos-qr2.sh` ·
> raw samples [`QR2-chaos.json`](QR2-chaos.json), [`QR2-chaos-isolated.json`](QR2-chaos-isolated.json)

---

## 1. Why the previous evidence was not evidence

[11-Distributed-Runtime](../11-Distributed-Runtime.md)'s header box said it without
softening:

> nothing has killed a process, nothing has crossed a process boundary, and nothing has run
> ten thousand of anything

Every durability claim in this repository rested on tests where the kill is a dropped lease
inside one process. `PostgresRecoveryHostTests.AHostWithAPostgresJournalRecoversAnAbandonedInstance`
is the best of them, and its own remarks are candid: *"the death itself … is a lease that is
never renewed"*. `DurableHostTests` runs two hosts as two objects. Both are real tests of
real code and neither one kills anything.

That matters because the failure they cannot reach is the interesting one. A process that
stops renewing a lease has run its `finally` blocks, released its connections and left its
own memory consistent. A process that receives `SIGKILL` has done none of those things, and
whatever it was in the middle of stays in the middle of itself for ever. **A rig that kills
nothing would be worse than no rig**, because it would let [21 §8](../21-Quality-Gates.md#8-reliability-gates)'s
chaos row be retired on evidence that does not exist.

The criterion is written in two places and this document is held to both.
[05 §10](../05-Architecture.md#10-quality-requirements-stimulus--response--measure) states
the stimulus and the measure:

> | QR2 | Chaos | `SIGKILL` a worker mid-flow | 3 nodes, durable profile | Flow resumes
> elsewhere | resume p99 ≤ 45 s (lease TTL 30 s); 0 duplicate non-idempotent effects |

and [21 §8](../21-Quality-Gates.md#8-reliability-gates) states the scale and the second
correctness clause: *"10 000 flows, zero duplicate non-idempotent effects, zero lost
instances"*. **The parenthesis in the first one — `(lease TTL 30 s)` — turns out to be the
whole of the latency answer**; see §4.4.

---

## 2. The rig

`tests/FlowX.Chaos` is a console program with three roles. It is **not** part of the
ordinary test suite and must not become one: it kills processes, and the run recorded below
took 328 s of wall clock and spawned 203 child processes. It is gated on `FLOWX_CHAOS`
exactly the way `tests/FlowX.Postgres.Tests` is gated on
`FLOWX_POSTGRES_CONNECTION` — **skip with a reason when nobody asked for it, fail rather
than skip when somebody did and the database is absent** — because a skip in the second case
would report a chaos run that never happened as a green job.

| Role | Process | What it does |
|---|---|---|
| **coordinator** | one, never dies | registers every instance id *before* any worker exists, spawns the others, waits for convergence, reads the verdict out of the database |
| **worker** | `--workers` at a time, respawned as they die | claims instances, runs them durably through `DurableLease` → `BeginAsync` → `FlowEngine.ExecuteAsync`, and **kills itself** on schedule |
| **recovery** | `--recovery-nodes`, never die | a real `FlowHost` + `FlowCatalog` + `FlowRecoveryScan` over the real PostgreSQL adapters, sweeping on a jittered timer |

Nothing in the recovery path is rig-specific. If this loop recovers an instance, the shipped
`FlowRecoveryScan` recovered it.

### 2.1 Where the kill lands, and why it has to be there

`FlowEngine` commits a step's journal row immediately after `IStepDispatcher.ExecuteAsync`
returns. So the window between an effect and its commit is the return path of that method,
and the only place a kill can be aimed at it is inside the dispatcher. The rig has two arms:

| Arm | The signal is raised | So at the moment of death |
|---|---|---|
| **`before-commit`** | after the ledger row is written, before returning to the engine | the effect has happened and **no journal row records it** |
| **`after-commit`** | on entry to the next step, before its ledger row | the previous step's row **is** in the journal and this step's effect has not happened |

The first arm is aimed straight at the hole
[ADR-0006](../adr/ADR-0006-journal-and-leases.md) names in its own consequences:

> A capability that is genuinely non-idempotent can still execute twice if the process dies
> after the effect but before the commit. We state this honestly rather than claim
> exactly-once.

The second is aimed at the guarantee: a committed step must never run again.

### 2.2 The kill is a kill, and that is checked rather than asserted

`Process.Kill()` on Unix is `kill(2)` with `SIGKILL`, which cannot be caught, blocked or
ignored. No `finally` runs, no finalizer runs, the lease is never released, and the pooled
PostgreSQL connections are never returned — the server discovers them when the socket dies.
Neither `Environment.FailFast` nor `Exit` would do: the first runs the runtime's shutdown,
the second runs finalizers and `ProcessExit` handlers, and both would give the process a
chance to be tidy that a lost node does not get.

**The evidence is the exit code.** The coordinator records what every worker it spawned
exited with, and `scripts/check-chaos-qr2.py` returns `INCONCLUSIVE` — not `PASS` — for a run
in which no worker exited 137, however many kills the rig believes it took.

---

## 3. What "zero duplicate non-idempotent effects" required

**Counting completions cannot see a duplicate effect.** A flow that charged a customer twice
and then completed once counts as one completion. So the rig runs a three-step flow whose
every step is declared `isIdempotent: false` and whose effect is a row in a PostgreSQL table
with **no unique key and no `ON CONFLICT`**:

```sql
CREATE TABLE chaos_effect (
    seq         bigserial   NOT NULL PRIMARY KEY,
    instance_id uuid        NOT NULL,
    step_index  int         NOT NULL,
    phase       text        NOT NULL,   -- 'initial' or 'resumed'
    node        text        NOT NULL,
    pid         int         NOT NULL,
    applied_at  timestamptz NOT NULL DEFAULT clock_timestamp()
);
```

A duplicate is then a **row**, not an inference. Adding a primary key on
`(instance_id, step_index)` would have made the database silently prevent the thing being
measured, which is the shape of mistake this document exists to avoid.

### 3.1 Three kinds of duplicate, and only two of them are defects

Two applications of one step are not automatically a failure — §2.1's quotation from
ADR-0006 is the repository saying so in advance. A rig that reported the documented window as
a bug would be as useless as one that reported nothing. So every recovery node records the
committed prefix it was **actually handed** — `count(distinct step_id)` from `flow_step`,
read before it executes anything — and each duplicate is judged against that number:

| Kind | Definition | Verdict |
|---|---|---|
| **against the guarantee** | an effect applied for a step whose commit was **already in the journal** when the recovering node took over | **gated at zero.** The journal's whole promise |
| **in the documented window** | an effect applied where no commit for that step existed | counted and reported, **not gated** — this is ADR-0006's stated exposure |
| **between live workers** | two *first-run* applications of one step: two live nodes executing one instance | **gated at zero.** What the lease and the fencing token exist to prevent |

The classification depends on no bookkeeping about the kill schedule, so it stays exact at
any concurrency and with any amount of collateral — the flows that happened to be in flight
in a worker when it died are judged by the same rule as the one the kill was aimed at.

### 3.2 And "zero lost instances" required pre-registration

The coordinator writes every instance id into `chaos_instance` **before any worker process
exists**, and a worker claims one with `FOR UPDATE SKIP LOCKED`, writing the claim *before*
it acquires the lease. Without that, an id minted inside a process that then died could not
be enumerated afterwards, and a rig that cannot enumerate what it asked for cannot notice
that something went missing. `FlowHost.RunAsync` mints its own id, which is why the worker
drives `DurableLease` → `BeginAsync` → `FlowEngine.ExecuteAsync` directly — the same three
calls `FlowHost` makes internally, with the id chosen outside.

Two numbers come out of that, and they are different questions:

* **lost instances** — a journal row that never reached a terminal state. QR2's figure.
* **claimed but never opened** — a worker was killed between its claim and `StartAsync`, so
  no journal row was ever created and there is nothing for a scan to find. No effect
  happened, so no work was lost; but the rig asked for the flow and it did not run, and that
  is reported rather than quietly netted off.

---

## 4. The measurement

```bash
./scripts/run-chaos-qr2.sh --flows 10000 --kill-every 100 --kill-step 1 \
    --workers 3 --concurrency 6 --recovery-nodes 2 --max-concurrent-recoveries 24 \
    --lease-ttl 30 --scan-interval 3
```

Two arms, 10 000 flows each, 328 s of wall clock for both. Three worker processes at a time,
each running six flows concurrently and killing itself once a hundred flows had reached step
1 in it; two recovery nodes sweeping throughout. PostgreSQL 16.13, four logical cores, and a
**one-minute load average of 36.2** — three sibling agents were building and testing in other
worktrees for the whole run. §4.4 says which of these numbers that ruins and which it does
not.

### 4.1 The correctness clauses

| | **kill `before-commit`** | **kill `after-commit`** |
|---|---:|---:|
| flows registered / claimed | 10 000 / 10 000 | 10 000 / 10 000 |
| journal rows opened | 9 939 | 9 943 |
| **completed** | **9 939** | **9 943** |
| **lost instances** | **0** | **0** |
| claimed but never opened | 61 | 57 |
| **worker processes killed** | **97** | **97** |
| — of which observed to exit **137** | **97** | **97** |
| effect applications | 30 077 | 30 015 |
| duplicate applications | 260 | 186 |
| — **against the guarantee** | **0** | **0** |
| — in ADR-0006's documented window | 260 | 186 |
| — **between two live workers** | **0** | **0** |
| **orphan effects** | **0** | **0** |
| instances resumed by more than one node | 0 | 0 |
| instances recovered by a sweep | 489 | 502 |
| resume p50 / p95 / **p99** / max | 30.7 / 32.6 / **32.9** / 33.1 s | 30.8 / 32.3 / **32.6** / 32.7 s |
| takeover p50 / p95 / p99 | 30.7 / 32.5 / 32.9 s | 30.9 / 32.3 / 32.5 s |

**Every instance that reached a journal row reached `Completed`.** Not one ended `Failed`,
`TimedOut` or `CompensationFailed`, and not one was still `Running` when the fleet went quiet.

The arithmetic closes: 9 939 × 3 = 29 817 steps, plus 260 duplicates, is 30 077 ledger rows.
9 943 × 3 = 29 829, plus 186, is 30 015. **Nothing is unaccounted for in either direction** —
no step went unapplied, and every application above the minimum is classified.

One arm was killed 97 times over 99 worker processes and the other 97 times over 100; the two
and three workers that exited 0 are the ones that ran out of instances to claim before they
reached their hundredth arrival at the kill point.

### 4.2 The exposure, isolated

The 10 000-flow arms kill a process running six flows at once, so most of what they abandon is
collateral — flows that happened to be somewhere in the middle when the signal arrived. The
classification in §3.1 handles that correctly, but it makes the *count* a function of timing.
Running the same rig at **concurrency 1** removes the timing, because then each kill abandons
exactly one flow and that flow is exactly where the rig aimed:

```bash
./scripts/run-chaos-qr2.sh --flows 60 --kill-every 3 --workers 1 --concurrency 1 \
    --recovery-nodes 1 --lease-ttl 10 --scan-interval 2
```

| | **kill `before-commit`** | **kill `after-commit`** |
|---|---:|---:|
| flows / kills | 60 / **20** | 60 / **20** |
| effect applications | **200** | **180** |
| expected if nothing repeated (60 × 3) | 180 | 180 |
| **duplicate applications** | **20** | **0** |
| — against the guarantee | 0 | 0 |
| lost instances | 0 | 0 |

**Twenty kills, twenty duplicates, one each — and zero when the kill moves to the other side
of the commit.** That is the whole of ADR-0006's negative consequence, reproduced as an exact
integer and bounded by it:

> A capability that is genuinely non-idempotent can still execute twice if the process dies
> after the effect but before the commit.

And it is the guarantee, reproduced as the other exact integer: 180 applications for 180
steps, under twenty real `SIGKILL`s, with no application of any step whose row was already in
the journal.

**This is a finding, not a failure.** [11 §4](../11-Distributed-Runtime.md#4-exactly-once-honestly)
does not claim exactly-once and never has; it claims *at-least-once delivery + idempotent
capabilities + fenced journal writes*. The rig's flow declares `isIdempotent: false` on every
step precisely so the middle term is missing, and what it measures is what the composition
costs when a user leaves it out. A capability that deduplicates on
`FlowInvocation.IdempotencyKey` — which the resumed instance carries unchanged, because the
invocation is rebuilt from the instance row — collapses all 260 of those rows into 0.

### 4.3 What the fence did, and how it is visible here

Nothing in either arm shows a step applied by two live workers, and nothing shows an instance
two recovery nodes both ran. With 97 kills, 2 sweeping nodes and a shared candidate query,
the sweeps contended constantly — `FlowRecoveryScan` walks its page from a random offset and
treats `lease.held` as a skip — and the outcome of that contention is that 489 and 502
instances were each taken over exactly once.

The `SIGKILL`ed workers never released their leases, so every one of those takeovers waited
out a real expiry rather than being handed the instance politely, which is the difference
between this and `DurableHostTests`.

### 4.4 The resume p99, which is measured and not gated

**32.9 s and 32.6 s against QR2's 45 s.** Reported here and not gated on: B7 and B8 are
latency budgets set aside for this phase, and this rig is correctness infrastructure.

Two things are worth reading out of the distribution, and neither is flattering to the number
taken on its own.

**Almost all of it is the lease TTL, and the takeover column proves it.** Takeover p99 is
32.9 s where resume p99 is 32.9 s — to the resolution of this instrument the resumed flow
finishes the instant a node picks it up, and everything before that is *detection*: a row has
to be idle for a lease TTL before `AbandonedInstanceQuery.IdleBefore` will consider it, and
then a sweep has to come round. With `LeaseTtl = 30 s` and a 3 s jittered interval, 30.7 s at
the median and 33.1 s at the maximum is that arithmetic and nothing else.
[ADR-0006](../adr/ADR-0006-journal-and-leases.md) says so in advance — *"recovery latency is
bounded by the lease TTL (~30 s default)"* — and this is the first measurement of it.
**The 45 s budget therefore has about 15 s of headroom over a setting, not over an
implementation**, and any deployment that raises `LeaseTtl` above 45 s fails QR2 by
configuration.

**The other part is queueing, and three runs of this same rig disagree by a factor of two.**

| Run | flows | recovery capacity | kills | resume p99 | correctness |
|---|---:|---|---:|---:|---|
| **recorded, §4.1** | 10 000 | 2 nodes × 24, 3 s interval | 97 | **32.9 / 32.6 s** | all zero |
| the shipped default | 200 | 2 nodes × 8, 5 s interval | 16 | **42.9 / 48.1 s** | all zero |
| pilot, quieter machine | 500 | 2 nodes × 4, 5 s interval | 18 | **69.9 / 68.8 s** | all zero |

**Nothing was wrong with any of them**, and the pilot was taken at load 2.5 — a *quieter*
machine than the fastest of the three. A sweep awaits its takeovers
before asking for a second page, so a fleet drains a backlog at roughly
`nodes × MaxConcurrentRecoveries ÷ (sweep + interval)` instances a second, and the tail of a
backlog larger than that waits. **The resume p99 is a capacity number as much as a latency
one**, which is why `--max-concurrent-recoveries` is a parameter of the rig and why it is
printed in the JSON beside the result. Quoting 32.9 s without that setting beside it would be
quoting a configuration as a property.

**The load average of 36 is the reason this section can be short.** On a four-core box at
load 36 the wall clock is largely a measurement of the other tenants — that is
[B12-scale §3](B12-scale.md#3-the-measurement)'s whole subject — and it is exactly why this
document does not gate on the p99. It is also why the p99 *survived*: a latency dominated by
a 30 s timer is not very interested in the CPU. **The correctness numbers are indifferent to
load in a stronger sense**: a duplicate row is a duplicate row on any hardware, and if
contention made the fleet flakier it would push those counts up, not down.

*(The load figure is `/proc/loadavg` read as the report is written, so it is the load at the
end of the run rather than an average over it. The pilot's 2.53 and this run's 36.23 are the
same measurement taken in two very different sessions.)*

### 4.5 Sixty-one flows that never happened

**61 of 10 000 in one arm and 57 in the other were claimed by a worker that was killed before
it opened a journal row.** The claim is written before `DurableLease.AcquireAsync`, so some
of them will have a `flow_lease` row and some will not; what none of them has is a
`flow_instance` row, and therefore anything for a recovery scan to find. **No effect was
applied for any of them** — that part is measured rather than assumed, and it is the
*orphan effects* row: 0 in both arms, meaning no ledger row anywhere belongs to an instance
with no journal row.

**They are not lost instances and they are not counted as any**, because nothing durable ever
existed to lose. They are reported because the alternative is to net them off, and 0.6 % of a
fleet's work silently not starting is the kind of thing a rig should say out loud. In a
deployment the trigger owns that window: the instance id is the caller's
(`DurableExecution.BeginAsync` takes one so that a redelivery is idempotent), so a trigger
that redelivers gets the same id and the same flow, and one that does not, does not. That is
a property of the trigger and not of the journal, and no amount of chaos testing on this side
of the boundary can close it.

---

## 5. What this does not prove

**One database, one machine, no network.** Every process here talks to a PostgreSQL on
`localhost`. A partition — the failure the fencing token exists for — is not exercised: a
`SIGKILL`ed node is gone, and a zombie node that wakes up believing it still holds a lease is
a different test that this rig does not do. `LeaseTests.AFencedOutNodeStopsWithoutRunningItsCompensations`
is the in-process assertion for it, and it stays the only evidence.

**One flow shape.** Three sequential capability steps, no `Parallel`, no `ForEach`, no
`SubFlow`, no compensation, no failure. The resume frontier for a fork is the shape
`ResumeFrontier` exists for and is not tested here by a kill. A flow that fails and
compensates while its node dies mid-compensation is the case with the most business
consequence in [11 §8](../11-Distributed-Runtime.md#8-failure-catalogue)'s last row, and it
is not covered.

**No broker.** `Emit` is not used, so the outbox is not part of this. Publication after a
kill is a separate question and `PostgresOutboxPublisher` has its own tests.

**The rig kills workers, never recovery nodes.** A kill during a takeover — a node dying
while resuming somebody else's instance — is the recursive case, and it is the obvious next
thing to add.

**Three nodes at a time, not thirty.** [05 §10](../05-Architecture.md#10-quality-requirements-stimulus--response--measure)
sets QR2's environment at three nodes and this run had three workers plus two sweepers alive
at once — about 200 processes over its life, but never more than five together. Contention
between sweepers is exercised; contention between fifty is not.

**This is not a throughput benchmark**, and no number in it should be quoted as one.

**One place the rig could have graded its own homework, and why it does not.** The
"against the guarantee" count is judged against a frontier the recovery node itself wrote,
so a reader is entitled to ask what happens if that row is missing. It cannot be: the
frontier is recorded on the *first dispatch* for an instance, before that dispatch applies
its effect, so any resumed application at all has a frontier recorded before it. An instance
whose resumed run dispatches nothing — because its last step's commit had already landed —
writes no frontier row and applies no effect, so it contributes to neither side. The
population being judged and the population being counted are the same one by construction.

---

## 6. Reproducing it

```bash
FLOWX_CHAOS=1 \
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5433;Database=postgres;Username=postgres" \
  ./scripts/run-chaos-qr2.sh --flows 10000 --kill-every 100 \
      --workers 3 --concurrency 6 --recovery-nodes 2 --max-concurrent-recoveries 24
```

Without `FLOWX_CHAOS` the script prints why it did nothing and exits 0. With `FLOWX_CHAOS`
and no reachable database it fails. Every parameter is echoed into the JSON beside the
results, because a measurement whose configuration is not recorded next to it cannot be
repeated.

`scripts/check-chaos-qr2.py` renders the verdict from that JSON and is the thing CI should
gate on. Exit codes are `analyse-scale-samples.py`'s: **0 PASS, 1 FAIL, 2 INCONCLUSIVE**. It
returns INCONCLUSIVE — never PASS — when no process was killed, when no worker exited 137,
when nothing was recovered, or when an arm ran fewer flows than it registered. `--flows 10000`
makes it refuse a PASS below QR2's scale, so a green run at 200 flows cannot be quoted as a
green run at 10 000.

A smaller default keeps the rig usable: `./scripts/run-chaos-qr2.sh` with no arguments runs
200 flows in both arms and still kills processes. **A CI job should not run the 10 000-flow
configuration on a shared runner and read its p99**, for the reason §4.4 gives; it should run
the correctness clauses at whatever scale the runner affords and leave the latency number to a
recorded run like this one.

---

**Back to:** [Benchmarks](README.md) · [Distributed runtime](../11-Distributed-Runtime.md) ·
[Quality gates](../21-Quality-Gates.md) · [Architecture §10](../05-Architecture.md)
