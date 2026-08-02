# ADR-0059: Schedule jitter is derived from the firing, never drawn at random

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** platform architecture, runtime

## Context

`[CronTrigger(Jitter = "PT120S")]` exists to stop five hundred tenants hitting one downstream at
02:00:00. The obvious implementation is the one every job framework ships: each node sleeps a
random slice of the window before firing.

It does not work here, and the reason is [ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md).
There is no leader. Every node computes the same occurrence, derives the same instance id from
it, and races; the lease store and the journal's primary key refuse all but one. So a fleet of
*n* nodes each drawing an independent delay fires at **min(*n* draws)** — the expected offset is
`window / (n + 1)`, which collapses towards zero exactly as the fleet grows large enough to need
the spread. Worse, it is invisible: the schedule does fire, once, at roughly the right time, and
the only symptom is a downstream that still sees a spike.

Options considered:

- **Random per node.** Rejected: `min` of *n* draws, above. The spread disappears as the fleet
  grows.
- **Random once, on whichever node fires, persisted.** Rejected: the offset would have to be
  written before the race is settled, so it needs its own store and its own contention, to
  decide a value that is not a business fact.
- **Random per node, with the losers waiting out the winner.** Rejected: this is a leader
  election with extra steps, and ADR-0031 exists because an election has three mechanisms and a
  window in each where a schedule fires twice or not at all.
- **Fold the offset into the occurrence.** Rejected — see the Negative section: it would make
  the jitter part of the instance id.

## Decision

We will derive a firing's release offset from the instance id the firing already has —
`offset = window × (leading 53 bits of the id / 2⁵³)` — because the id is already a pure function
of every term that distinguishes this firing from the next one, and a pure function is the only
kind of offset every node can agree on without talking to any other node.

The offset moves **when a firing is acted on** and nothing else. The occurrence, the derived
instance id, the journal row and the `ScheduledFire.OccurrenceAt` the flow binds are all the
un-jittered instant. `FlowScheduleScan` holds an occurrence back until `now` reaches its release
instant, and everything downstream of that sees the schedule it always saw.

## Consequences

**Positive:**

- The spread is a property of the *firing*, so it holds on one node and on a hundred. Two
  tenants of one `PerTenant` schedule have different ids and therefore different offsets, which
  is the fan-out spread the option exists for; two consecutive occurrences have different ids
  too, so a fleet does not simply run five minutes late every night.
- Nothing is stored, nothing is coordinated, and no node has to be elected to own the value.
- The offset is recomputable from the manifest: the id is `SHA-256` over the published address,
  so an operator asking "why did this run at 02:01:37" can derive the same number.
- `MissedFirePolicy` is untouched. A missed firing's release instant is long past, so a
  catch-up fires immediately rather than re-waiting a window it already waited.

**Negative / accepted trade-offs:**

- **The offset cannot be part of the instance id, and this is a real limit rather than an
  oversight.** Folding it in would make the id depend on the jitter, so widening `PT60S` to
  `PT120S` would re-fire every occurrence inside the catch-up horizon under new ids. The id is
  the occurrence's address; the jitter is when a node acts on it. A consequence is that a
  deployment mid-rollout, with one replica on the old jitter and one on the new, has two
  replicas that disagree about when to release one firing — the earlier one wins, and the firing
  is correct but less spread than either declaration asks for. It resolves when the rollout does.
- **The offset is stable, so it is predictable.** A tenant whose id hashes into the first second
  of the window is first every single night, for ever. Where the spread is protecting a
  downstream this is exactly right — the load is flat — and where somebody wanted fairness it is
  wrong, and there is nothing here that rotates.
- **A jitter wider than the gap between occurrences produces firings that overtake each other.**
  Nothing refuses it: the analyzer would need a cron evaluator to know, and `OverlapPolicy` is
  already the declaration that says what happens when two runs meet. `FLOWX1045` refuses only a
  value that is not a positive duration.
- One `TimeSpan` and one hash read per due occurrence per sweep. Both are free relative to the
  journal read beside them.

**Revisit when:** the fleet gains a coordinator for some *other* reason — a leader for
partition assignment, say — at which point an offset drawn once and published is cheaper than
one derived per node; or a deployment is measured to need the offsets to rotate rather than to
be flat, which would make the id the wrong material and a windowed term the right one.
