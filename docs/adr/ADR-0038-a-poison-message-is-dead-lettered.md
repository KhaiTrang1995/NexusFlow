# ADR-0038: A poison message is dead-lettered rather than left to block its partition

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0018](ADR-0018-outbox-publication-and-ordering.md)

> [ADR-0018](ADR-0018-outbox-publication-and-ordering.md) states the gap in its own words:
> *"One event nobody can publish stops everything behind it … There is no dead-letter path; DLQ
> is named in §4's description of the unwritten conformance suite and is not part of this
> package."* Its "Revisit when" then names the condition: *"a deployment needs a dead-letter
> path."* Binding `Bus` is that deployment, on the consumption side.

## Context

1. **`KafkaTriggerAttribute` has declared a `DeadLetter` property since it was written**, with
   the argument already on it: *"a poison message is dead-lettered rather than retried for ever:
   head-of-line blocking is a bug, not a durability strategy."* Nothing read it.

2. **[ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md) makes head-of-line blocking
   total for a key.** Entries of one stream are processed serially and in order, so an entry that
   never reaches a disposition stops every later event for that `partition_key` — which, given
   `FlowEmitter` writes `PartitionKey = ctx.FlowInstanceId`, means every later event of one
   emitting instance.

3. **[ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md) already
   removes the largest class of would-be poison.** A flow that fails cleanly is acknowledged. What
   is left is genuinely undeliverable: an entry that cannot be read as a message at all, and a
   delivery whose flow neither commits nor fails but keeps being rejected.

4. **Redis Streams counts deliveries for free.** The pending-entries list carries a delivery
   count per entry, returned by `XPENDING` and by `XAUTOCLAIM`. No bookkeeping of our own is
   needed to know that an entry has been tried *n* times.

Options rejected:

- **State that a poison message blocks its partition and stop there.** Legitimate, cheap, and
  what ADR-0018 does today — but it makes the first malformed entry an outage for that key with
  no bound and no alarm, and the mechanism to avoid it is four commands.
- **Drop a poison message.** Silent data loss, and indistinguishable from a message that was
  processed.
- **Retry for ever with backoff.** Unbounded, and the retry cannot succeed for the case that
  matters — an entry whose `event-id` is not a GUID will never become one.
- **Divert to a second FlowX flow.** Makes the dead-letter path itself a thing that can be poisoned.

## Decision

### 1. The consumer side gets a dead-letter path. The publisher side still does not

Two halves of ADR-0018's negative consequence, and only one is closed here. A publisher offered
an event a broker permanently refuses still retries it for ever and still blocks its batch;
that remains true and remains stated. What changes is that a *consumer* handed an entry it can
never process no longer blocks its key.

### 2. Two conditions dead-letter, and they are different failures

- **Unreadable.** The stream entry cannot be read as a `BusMessage` — no `event-id` field, or one
  that is not a GUID, or no `type`. Dead-lettered on the **first** delivery, because no number of
  retries turns it into a message.
- **Undeliverable.** The delivery count reported by the broker exceeds
  `FlowXOptions.BusMaxDeliveries` (default 5) and the flow has still not reached a recorded
  outcome. This is the one that catches a deterministic exception and a store that refuses this
  one instance for ever.

### 3. Dead-lettering appends the entry verbatim, then acknowledges the original

The entry's own fields are copied unchanged into `{prefix}:{key}:events:dead`, plus two the
consumer adds — `dead-letter-reason` and `dead-letter-at` — and the original is then `XACK`ed so
the key advances. **Copy before acknowledge, never the reverse:** a crash between the two
redelivers the original, which is at-least-once behaving as it always does; a crash the other way
round would be the loss this whole record exists to prevent.

The dead-letter stream is a stream, so it is readable with `XRANGE`, drainable by hand, and
subject to the same `MaxStreamLength` trimming as any other.

### 4. A dead-lettered event leaves a **visible** gap in its key's order

[ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md) offers per-key order over the events
that were delivered. A dead-lettered event was not delivered, so the events after it arrive
without it. That is a weaker guarantee than "per-key order over every staged event", and it is
the trade this record takes deliberately: the alternative is a key that stops for ever. The gap
is a row in the dead-letter stream rather than an inference, which is the difference between a
trade and a bug.

## Consequences

**Positive**

- **ADR-0018's "Revisit when" is discharged for consumption**, and the prefix contract it worried
  about is untouched: nothing about publication changes.
- **`KafkaTriggerAttribute.DeadLetter`'s argument is now executed** rather than being a comment on
  an unread property.
- **The blocking case is bounded and observable.** `BusScanReport.DeadLettered` is a counter and
  the entries are readable; an operator can answer "what did we give up on" with `XRANGE`.

**Negative / accepted trade-offs**

- **The dead-letter stream is unbounded unless trimmed.** A subscription that dead-letters
  steadily grows a stream nothing reads. `RedisStreamOptions.MaxStreamLength` applies to it and
  defaults to no trimming, so the default is "keep everything", which is the safe direction and
  the expensive one.
- **`DeadLetter` on `KafkaTriggerAttribute` names a destination and this ignores it.** The
  destination is derived from the source stream instead, because a per-subscription destination is
  deployment configuration and the manifest deliberately does not publish it
  ([ADR-0039](ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md)). The property stays
  unread, which `docs/09-Trigger-Model.md §8` records rather than this record pretending otherwise.
- **The delivery count is the broker's, and it counts *deliveries*, not attempts.** An entry
  claimed by a node that then dies before running anything has still been delivered. So
  `BusMaxDeliveries` is an upper bound on attempts and not a count of them, and a flapping node
  can spend the budget without the flow ever having been tried.
- **Nothing replays a dead-lettered entry.** There is no `flowx` verb for it and no automatic
  return path; recovery is `XRANGE`, judgement, and `XADD` back onto the source stream by hand.
- **An unreadable entry is dead-lettered without ever being attributed to a flow.** It never
  became a `BusMessage`, so there is no instance, no correlation id and nothing in the journal —
  only the raw entry and a reason.

## Revisit when

- A dead-lettered entry has to be replayed often enough to want a verb, at which point `flowx`
  gains one and it needs to answer what happens when the replayed entry is refused by the
  primary key ADR-0035 derives.
- The publisher side needs the same path, which changes ADR-0018's prefix contract from "stop" to
  "divert and continue" — the condition that record already names.
- A transport carries its own dead-letter destination (Azure Service Bus, SQS redrive), at which
  point deriving one from the source is the wrong shape and `DeadLetter` becomes readable.
- `BusMaxDeliveries` is found to be spent by node churn rather than by real failure, which would
  mean counting attempts here rather than trusting the broker's delivery count.
