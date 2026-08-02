# ADR-0036: A message is acknowledged when its flow is journalled, not when it succeeds

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0007](ADR-0007-result-over-exceptions.md) ·
[ADR-0018](ADR-0018-outbox-publication-and-ordering.md)

> "Acknowledge on success" is the obvious rule and it is wrong here, because
> [ADR-0007](ADR-0007-result-over-exceptions.md) says a business failure is a `Result` and not an
> exception. A flow that ran, compensated and ended `Failure` is a flow that **happened**. Making
> the broker redeliver it turns a declined payment into an infinite loop.

## Context

1. **There are four outcomes, not two.** `FlowExecutionResult` can be `IsSuccess`,
   `IsSuspended` (the flow parked at an `AwaitSignal`), a `Result` failure carrying the flow's
   own `Error`, or a *rejection* — the host or the stores refusing to run it at all
   (`host.draining`, `lease.held`, `journal.instance_exists`, `journal.fenced_out`,
   `flow.durability_not_configured`). Only the last group means "this delivery did not happen".

2. **A business failure is a completed flow.** ADR-0007's whole argument. The instance row is
   terminal, the compensations ran, the errors are on the instance. There is nothing left to
   retry, and — because
   [ADR-0035](ADR-0035-a-delivery-names-the-instance-it-starts.md) derives the instance id from
   the delivery — a redelivery could not retry it anyway: it would be refused by the primary key
   and spin until the delivery limit dead-lettered it.

3. **A suspension is a completed *delivery*.** The flow parked; the journal holds it; a signal
   will resume it. Holding the message unacknowledged until the flow finally completes would
   mean a message pending for the human-scale duration of an approval, and Redis would reclaim
   and redeliver it long before then.

4. **Acknowledging before the flow starts loses work.** The symmetric error, and the one every
   naive consumer makes: XACK on receipt, then crash, and the message is gone with no instance
   row anywhere.

Options rejected:

- **Acknowledge on `IsSuccess` only** — force 2. Every declined payment redelivers for ever.
- **Acknowledge on receipt, before running** — force 4. At-most-once, which is not what the
  publisher offers and not what a saga can be built on.
- **Acknowledge inside the flow, as a step** — puts the transport in the flow body, which
  ADR-0004 and quality goal Q4 forbid outright.
- **Two-phase: acknowledge, then journal** — the second write is exactly the crash window the
  outbox's decision 4 exists to avoid, restated on the consumer side.

## Decision

### 1. A delivery is acknowledged when it reached a **recorded outcome**

Acknowledge when the run returned:

| Outcome | Why it is acknowledged |
|---|---|
| `IsSuccess` | the flow ran and committed |
| `IsSuspended` | the flow ran, parked, and its wait is a journal row a signal resumes |
| a `Result` failure | the flow ran and decided against; ADR-0007 |
| `journal.instance_exists` | an earlier delivery of *this* message already ran it (ADR-0035) |

### 2. A delivery is left pending when nothing was recorded

Leave unacknowledged — so the broker reclaims and redelivers it — on `lease.held`,
`lease.lost`, `journal.fenced_out`, `host.draining`, `flow.durability_not_configured`, and on
any exception escaping the run. In each of these the flow did not reach the engine, or reached
it and lost the right to commit, so no instance row describes this message.

### 3. The two rules compose into a bounded worst case

Because the id is derived, a delivery that was run but whose acknowledgement was lost — the
node died between commit and XACK — comes back, finds `journal.instance_exists`, and is
acknowledged on rule 1. So the cost of a lost acknowledgement is **one extra delivery and one
journal read**, not a re-run and not an unbounded loop.

### 4. `BusScanReport` counts the four dispositions separately

`Started`, `Deduplicated`, `Requeued`, `DeadLettered`. An operator watching `Deduplicated`
expecting zero is watching the wrong number — it is the ordinary result of a fleet — and
`Requeued` climbing without `Started` moving is the shape of a subscription that cannot commit.

## Consequences

**Positive**

- **At-least-once delivery becomes exactly-once *execution*** when read together with ADR-0035:
  the broker may deliver a message any number of times and the journal admits one flow.
- **A poisonous *business* outcome is not a poison message.** A flow that fails cleanly is
  acknowledged and does not consume the delivery budget that
  [ADR-0038](ADR-0038-a-poison-message-is-dead-lettered.md) spends on messages that genuinely
  cannot be processed.
- **The rule is expressed as a switch on `Error.Code`**, the same set `FlowScheduleScan.FireAsync`
  already branches on, rather than as a new classification the two could disagree about.

**Negative / accepted trade-offs**

- **A flow that suspends is acknowledged before it finishes.** If the signal never arrives the
  instance sits parked for ever and the broker has forgotten the message that started it. That is
  correct — the journal, not the broker, is what holds a suspended instance — but it means the
  broker is no longer a backstop for a flow that stalls after its first step.
- **A `Result` failure is invisible to the broker.** An operator watching a dead-letter stream
  for "messages that went wrong" will not find a flow that declined every order it was sent. The
  place to watch is the flow's own failure metric, and this record is where that is written down
  rather than discovered.
- **`journal.instance_exists` is acknowledged without reading what the earlier run decided.** The
  scan does not distinguish "already succeeded" from "already failed", because in both cases this
  delivery has nothing left to do. A consumer that wanted to react to the earlier outcome would
  have to read the instance, which is a second round trip on the common path.
- **An exception leaves the message pending, and a deterministic exception therefore redelivers.**
  Bounded by ADR-0038's delivery limit and not by this record.

## Revisit when

- A wait gains a timeout that fires, at which point a suspended instance has a terminal outcome
  the broker could in principle have waited for — and this record's rule 1 row for `IsSuspended`
  is worth re-arguing.
- A store returns a refusal code this switch does not name, which would fall to the default and
  be acknowledged; the default is deliberately "acknowledge" and that is the assumption to
  re-check when the code set grows.
- A subscription needs ordered *delivery* semantics stronger than
  [ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md) offers, at which point acknowledging a
  suspended flow reorders that key.
- A transport acknowledges asynchronously and out of order — Kafka's offset commit — at which
  point "acknowledge this one message" stops being an operation the broker has.
