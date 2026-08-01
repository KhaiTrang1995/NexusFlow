# ADR-0032: A missed schedule fires late, inside a bounded catch-up horizon

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Repository owner · Platform architecture
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md)) §4

> **[ADR-0004](ADR-0004-universal-trigger-model.md))'s own table commits a `Schedule` trigger to
> **at-least-once** delivery, with *"missed-fire policy"* as its failure handling.** Nothing was
> bound, so nothing had to answer for that. Binding it makes the row a promise, and this record
> is what the promise costs.
>
> **The two answers are both defensible and the wrong one silently loses work.** *Skip* means a
> nightly reconciliation that was due while a cluster was being upgraded simply does not happen,
> and the first symptom is in next month's numbers. *Fire late* means a job written to assume it
> runs at 02:00 runs at 06:40, which for a job that reads "yesterday" from a clock is a different
> and equally silent kind of wrong — and is why
> [ADR-0033](ADR-0033-a-scheduled-flows-input-is-its-occurrence.md)) exists and had to be decided
> alongside this.

---

## 1. Context

### 1.1 What at-least-once means for a transport with no sender

For every other kind, at-least-once is a property of the sender: a broker holds the record until
an offset is committed, and redelivers if it is not. The receiver's job is to be idempotent.

A schedule has no sender and nothing holds anything. If every node in a fleet is down at 02:00,
there is no queue that remembers 02:00 happened — the only evidence that a firing was owed is
that the expression names an instant in the past and no instance exists for it. So
"at-least-once" for a schedule is entirely a statement about **what a node does when it comes
back**, and it is unimplementable without a way to ask "did this occurrence already fire".

[ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)) supplies that as a
side-effect: the instance id is a function of the occurrence, so the question is a primary-key
read.

### 1.2 What a bare horizon gets wrong, in both directions

The obvious sweep is *"consider every occurrence in `(now − horizon, now]` and fire what has not
fired"*. It fails on the first deployment of any application:

* A new application is deployed at 12:34 with `0 * * * *` and a one-day horizon.
* The sweep computes 24 occurrences in the window, finds no instance for any of them, and fires
  every one — 24 runs of a job that had never run before, of work nobody missed because nobody
  was ever going to do it.

Shortening the horizon does not fix it; it trades that failure for the other one, where a fleet
down for longer than the horizon loses its firings. The two cases — *"the fleet was down"* and
*"this schedule is new"* — are indistinguishable from a node's own state, and both look
identical to a bare horizon.

### 1.3 What `MissedFirePolicy` already declared

`CronTriggerAttribute` has shipped three values since it was written, and nothing read any of
them:

| Value | Documented as |
|---|---|
| `Skip` | *"Ignore them."* |
| `RunOnce` | *"Run once, regardless of how many were missed. The default."* |
| `RunAll` | *"Run every missed firing — a two-hour outage becomes 120 minute-jobs at once."* |

That is a well-chosen set and this record does not change it. Its existence also settles a
question this record would otherwise have to: **the platform does not get to decide whether late
work is wanted.** An author who writes `Skip` has said so.

---

## 2. Decision

**A firing that fell due while nothing was running happens late rather than never, bounded by a
declared horizon and narrowed by the declared `MissedFirePolicy`. The journal, not the horizon,
decides whether a schedule was running at all.**

### 2.1 The floor comes from one of three places

Each sweep computes, per schedule, a *floor* — the newest occurrence already accounted for —
and fires what the expression names after it.

1. **This process already accounted for an occurrence.** The steady state, and it asks the
   stores nothing: a ten-second sweep over an hourly schedule finds nothing due 359 times an
   hour and takes no lease doing it. This memory is **an optimisation and never a correctness
   mechanism** — it is lost on restart, and cases 2 and 3 are what make the answer true.
2. **First sweep of this schedule, and the journal holds an earlier occurrence.** The fleet was
   running and stopped. The floor is that occurrence, so everything after it is genuinely
   missed. The probe walks backwards from the newest occurrence and stops at the first the
   journal has, so the ordinary restart costs exactly one read.
3. **First sweep, and the journal holds nothing inside the horizon.** There is no evidence this
   schedule has ever run here, so there is nothing to catch up on: the floor is **when this node
   started**. A first deployment therefore fires from its next occurrence rather than replaying
   the horizon, which is §1.2's failure, and it starts firing on the following sweep rather than
   never — which the naïve fix of "floor = now, always" would have made impossible, because
   there would never be a first instance for case 2 to find.

`ScheduleScanTests.ARestartedNodeCatchesUpWhatFellDueWhileNothingWasRunning` and
`ANewScheduleDoesNotReplayTheHorizon` are the two halves, and they are the same fleet with and
without a row in the journal.

### 2.2 `MissedFirePolicy` then narrows what is fired

| Value | Fires | The author is saying |
|---|---|---|
| `Skip` | only an occurrence within two sweep intervals of now | do not do this work late |
| `RunOnce` *(default)* | the most recent occurrence after the floor | catch up, but do not stampede |
| `RunAll` | every occurrence after the floor, up to `ScheduleFireBatchSize` | each firing does different work |

**`Skip` is about work that is *late*, not work that is *due*.** A sweep arriving five seconds
after 02:00 has missed nothing, so it fires; one arriving at 06:40 has, so it does not. Two
intervals rather than one, because the sweep is jittered by ±25 % and the preceding tick may
have run early.

**`RunAll`'s batch is a cap on one sweep, not on the catch-up.** What it does not reach stays
inside the horizon and is taken on the next tick, so a two-hour outage of a minutely schedule
drains at 32 firings per sweep rather than arriving as 120 at once.

### 2.3 The horizon is the bound, and it is a declared number

`FlowXOptions.ScheduleCatchUp`, one day by default. It bounds two things: how far back the
probe in case 2 will look for evidence, and therefore how far back a firing can be recovered
from.

**A fleet down for longer than the horizon loses the firings that fell outside it, silently.**
That is the one place a schedule's at-least-once has a hole, and it is stated rather than
hidden. A day covers a rolling deploy, a node outage and a night; a deployment that expects to
survive longer raises it, and the cost of raising it is bounded because `RunOnce` — the default
— fires one occurrence however many were missed.

There is a second, smaller bound in the same place: the probe reads at most
`ScheduleFireBatchSize` occurrences looking for evidence, so a schedule dense enough that 32
occurrences do not reach back to the last one that fired is treated as case 3 and starts from
the node's own start. A minutely schedule after a 40-minute outage is inside that; after a
three-hour outage it is not.

---

## 3. Options rejected

- **A. Skip everything that is late — fire only if the occurrence is fresh.** *Rejected:* it
  contradicts ADR-0004's own table, and it is the answer that loses work with no signal at all.
  It also makes a rolling deployment lossy: a node restarting takes tens of seconds, and a
  schedule whose occurrence falls in that gap would be dropped by a fleet that was never
  actually unavailable. It survives as `MissedFirePolicy.Skip`, which is the right place for it
  — the author's decision, not the platform's.
- **B. An unbounded walk back until the journal says an occurrence fired.** *Rejected:* it is
  unbounded on a fresh database by construction. There is no occurrence to stop at, so the walk
  runs to whatever limit it is given anyway — and if given none, a first deployment enumerates
  every occurrence the expression has ever named. The horizon is that limit, made explicit and
  configurable instead of accidental.
- **C. A durable per-schedule cursor — a `flowx_schedule` table with `last_fired_at`.**
  *Rejected*, and it is [ADR-0031 §3](ADR-0031-an-occurrence-names-the-instance-it-starts.md))'s
  option B seen from this side. It would remove the horizon, which is genuinely attractive. What
  it costs is a second durable store for a fact `flow_instance` already holds, holding it worse:
  a cursor records only the newest firing, so `RunAll` could not be implemented against it after
  a restart. And it needs its own answer to "the cursor advanced and the flow did not start",
  which is a two-phase commit between two tables that a derived primary key does not need.
- **D. Make the horizon infinite and rely on `RunOnce` to bound the damage.** *Rejected:* it
  only bounds the damage under the default. Under `RunAll` a first deployment fires the entire
  history, and under `Skip` nothing fires — so the safety of the setting would depend on an
  attribute property, which is the wrong coupling for a host option.
- **E. Record the node's start instant durably, so a whole-fleet restart still knows.**
  *Rejected:* what would be recorded is "some node was up at time T", which is not the question.
  The question is "was this schedule live", and the journal answers exactly that with no new
  state.

---

## 4. Consequences

**Positive**

- **ADR-0004's at-least-once row is true for `Schedule`, within a stated bound.** It was a table
  cell with nothing behind it; it is now a behaviour with two tests and a number an operator can
  set.
- **A first deployment does not replay history**, and it does not need anyone to remember to
  configure it not to. §1.2 is the failure this design is shaped around.
- **The three `MissedFirePolicy` values all execute.** Three of `CronTriggerAttribute`'s six
  properties now reach code; before this, none did.
- **The steady-state cost is zero store round trips.** A sweep with an in-process floor and
  nothing due queries nothing, which is what makes a two-second sweep interval affordable.

**Negative / accepted trade-offs**

- **At-least-once is bounded, and the bound is silent when it is exceeded.** A fleet down for
  longer than `ScheduleCatchUp` loses the firings outside it, and nothing reports that: there is
  no record of a firing that nothing was there to observe. The mitigation is that the number is
  declared, defaulted conservatively and documented here — not that the failure is detectable.
- **A late firing runs with the occurrence's data and the present's world.** `ScheduledFire`
  carries the instant that was due ([ADR-0033](ADR-0033-a-scheduled-flows-input-is-its-occurrence.md))),
  so a flow that reasons from its input closes the right window — but the capabilities it calls
  see the system as it is now. A 06:40 run of an 02:00 job reads balances that have moved since.
  That is inherent to running late at all and is the reason `Skip` exists.
- **`Overlap` is declared and does nothing, so a late `RunAll` catch-up can run its firings
  concurrently.** `OverlapPolicy.Skip` is the attribute's default and reads as though it
  prevented that; it does not, in this release. Distinct occurrences have distinct instance ids,
  so nothing refuses them and `MaxConcurrentRecoveries` is the only bound. Recorded in
  [09 §8](../09-Trigger-Model.md#8-schedule-trigger) as one of the three properties that still
  bind nothing.
- **The probe reads the journal on the first sweep after every restart**, once per schedule
  under normal conditions and up to `ScheduleFireBatchSize` times after an outage. It is a
  primary-key read against `flow_instance` and is negligible, but it means a node's readiness is
  now coupled to the journal being reachable in one more place.

**Revisit when:** any one of —
- **a deployment is found running with a horizon shorter than its longest outage**, which is the
  configuration this record makes it possible to get wrong and gives no signal about. At that
  point the missing piece is detection, not a different default;
- **`RunAll` is declared on a schedule dense enough that one sweep's catch-up exceeds
  `MaxConcurrentRecoveries`** — the two bounds are set independently and nothing checks them
  against each other, so a batch of 32 on a host that runs 8 at a time queues 24 firings inside
  one sweep;
- **a durable per-schedule cursor is built** for another reason — an operator's "when did this
  last run" view, say — at which point option C is no longer a second store for one fact and the
  horizon can go;
- **`Overlap` becomes executable**, at which point a late firing has to ask whether the previous
  one finished, and `RunAll`'s concurrent catch-up becomes a declaration rather than a
  consequence.

---

**Back to:** [ADR index](README.md) · [ADR-0004](ADR-0004-universal-trigger-model.md)) ·
[ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)) ·
[ADR-0033](ADR-0033-a-scheduled-flows-input-is-its-occurrence.md)) ·
[09 §8](../09-Trigger-Model.md#8-schedule-trigger)
