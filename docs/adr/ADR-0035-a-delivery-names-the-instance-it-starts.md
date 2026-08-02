# ADR-0035: A bus delivery names the instance it starts

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md)

> `Bus` is the first bound trigger kind that is **at-least-once**. HTTP avoids the question by
> making the caller retry; cron avoids it by deriving an instance id every node agrees on. A
> broker does not let you avoid it: it *will* deliver the same message twice, and the only
> decision available is whether that starts one flow or two.

## Context

1. **The broker redelivers, by contract.** A Redis Streams consumer group hands an entry to a
   consumer and keeps it in the pending-entries list until it is acknowledged; a consumer that
   dies mid-flow has its entry reclaimed and given out again. The publisher half is already
   at-least-once for the same reason —
   [ADR-0018](ADR-0018-outbox-publication-and-ordering.md) decision 4 rolls the outbox mark back
   with the claim, so *"a crash anywhere before the commit — including after the broker
   acknowledged — … republishes"*. Duplicates are not an error path. They are the steady state.

2. **The repository already has the mechanism, twice over.** `FlowHost.RunAsync(…, Guid
   instanceId)` takes a caller-supplied id, and `OpenAsync`'s own remarks have named this path
   since WP-55: *"a trigger that wants a redelivery to be idempotent supplies its own id"*.
   Underneath it, `ILeaseStore.AcquireAsync` refuses a second holder while the first is running
   and `IFlowJournal.StartAsync` refuses a duplicate primary key for ever afterwards.

3. **ADR-0031 answered the same question for cron and left the shape behind.** A schedule
   occurrence derives its instance id from five values every node agrees on, and its own
   "Revisit when" names this case in advance: *"a second transport needs occurrence-derived
   identity, at which point the derivation belongs to the abstraction rather than to the
   sweep."*

4. **An `Idempotency-Key` exists on the HTTP path and is not this.** It is a caller-supplied
   header enforced at admission by `FlowEndpointExtensions`. A broker message has no caller to
   supply one, and inventing one from the payload would key on a body that may legitimately
   repeat.

Options rejected:

- **Mint a fresh id per delivery and deduplicate elsewhere** — needs a store with a retention
  window, and the journal already answers the same question permanently and for free.
- **Deduplicate on `partition_key`** — a key is an ordering, not an identity. Every event a
  `Durable` flow emits carries the emitting instance's id as its key
  (`FlowEmitter.EmitDispatcherDescribe`), so keying on it would fold every event one instance
  ever emitted into one flow.
- **Ask the broker to deduplicate** — Redis Streams does not, and a decision that only holds on
  brokers that do is not a decision about `Bus`.
- **Deduplicate in the consumer's memory** — a process-resident set is lost on restart, which is
  exactly when redelivery happens.

## Decision

### 1. The instance id is derived from the delivery, in `BusDelivery.InstanceIdFor`

```csharp
Guid InstanceIdFor(string flowId, string flowVersion, string topic, string group, Guid eventId)
```

SHA-256 over the five terms NUL-separated, laid out as a **UUID version 8** — RFC 9562's slot
for a derived id, the same rendering `ScheduleOccurrence.InstanceIdFor` uses and for the same
reason: an operator reading a journal row is entitled to tell a derived key from a minted one.

**Every term is in the id, and leaving any one out would fold two subscriptions into one.**
The flow id and version because an instance is pinned to the version it started with, so two
versions deployed side by side are two subscribers and a shared id would let the older one's
delivery suppress the newer one's for the whole of a canary — ADR-0031's argument, unchanged.
The topic and the group because they are the subscription's *address*: two flows subscribing to
one topic under different groups are two subscribers and both must run. The event id because it
is the message's identity, assigned by the store at staging time
(`OutboxRecord.EventId`) and carried on the wire in the `event-id` field
(`RedisKeys.EventIdField`), whose own summary already said what it was for: *"the field holding
an event's identity, which a consumer deduplicates on."*

### 2. A redelivery is refused rather than executed, and the refusal is the answer

`FlowBusScan` passes the derived id to `FlowHost.RunAsync`. The second delivery of one message
gets `lease.held` while the first is still running and `journal.instance_exists` once it has
finished. Neither is an error: they are what the scan asked for, and they are what
[ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md) acknowledges on.

### 3. A `Bus`-triggered flow must declare `Durable`, and `FLOWX1039` refuses one that does not

An `Ephemeral` flow journals nothing, so the derived id is inert: `FlowHost` takes no lease,
writes no row, and every delivery runs the flow — with no error, no duplicate-key refusal and
nothing anywhere to count. That is the same silent failure `FLOWX1038` exists to stop for a
schedule, and it is refused the same way: at compile time by the analyzer, and again at
registration by `FlowBusCatalog.Add` for a hand-written call site.

## Consequences

**Positive**

- **One message starts one flow, across a fleet, across restarts, for ever.** The permanence is
  the journal's primary key rather than a cache, so a redelivery six weeks later is refused by
  the same mechanism as one six milliseconds later.
- **No new primitive.** The derivation is thirty lines; everything underneath it already existed
  and is already held to `JournalConformance` and `LeaseStoreConformance`.
- **ADR-0031's revisit condition is discharged as it was written.** A second transport needed
  derived identity, and the derivation moved into a type of its own beside the schedule's rather
  than being copied into the sweep.

**Negative / accepted trade-offs**

- **`Bus` costs a journal.** A subscription cannot be served by a host with no `IFlowJournal`,
  and `FLOWX1039` makes that a build error rather than a silent double-run. A team that wanted a
  cheap ephemeral consumer does not get one.
- **The id is derived from the *staged* event id, so two staged events with identical bodies are
  two flows.** Correct — they are two events — but it means an emitter that stages the same
  business fact twice starts the flow twice, and nothing in this record stops that. The
  emitter's own idempotence is `FLOWX1014`'s business.
- **A subscription rename is a re-run.** Changing the declared `Group` changes every future
  delivery's id, so messages already consumed under the old group are consumed again under the
  new one. That is the correct reading of "a new subscriber", and it is a foot-gun for anyone who
  renames a group to tidy it up.
- **The derivation is not the broker's.** A broker that assigns its own message identity — Kafka's
  `(topic, partition, offset)` — would give a cheaper id, and this one deliberately ignores it so
  that the same derivation holds for a transport that does not.

## Revisit when

- A transport delivers a message with no stable identity of its own, at which point the
  `event-id` field this depends on has to be synthesised by the publisher and the derivation
  moves upstream of the consumer.
- A subscription needs to run the same message twice on purpose — a replay tool, a shadow
  consumer — at which point the group is no longer enough to distinguish them and a nonce enters
  the derivation.
- `PerTenant` becomes declarable on a bus trigger and one delivery has to name many instances,
  which is the same condition ADR-0031 carries.
- A store weakens `StartAsync`'s duplicate refusal, which would remove the permanent half of the
  mechanism and leave only the lease's TTL.
