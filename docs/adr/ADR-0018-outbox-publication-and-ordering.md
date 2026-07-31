# ADR-0018: Declare `IEventPublisher`; offer per-key ordering and no global order

**Status:** Accepted
**Date:** 2026-07-31
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0016](ADR-0016-postgres-journal-adapter.md), Retention ·
[11 §5](../11-Distributed-Runtime.md#5-the-transactional-outbox)

> WP-56 was supposed to make constraint **C4** — "no 2-phase commit; consistency is
> saga-based, **outbox for atomic publish**" — describe the system rather than the intent.
> Half of it already did: WP-53 writes the step row, the instance update and the outbox rows
> in one transaction. The other half named a table nothing read.

## Context

Four forces met here and two of them pull against each other.

1. **`outbox_event` had no publisher and no read order.**
   [11 §5](../11-Distributed-Runtime.md#5-the-transactional-outbox) draws the publisher as
   `SELECT unpublished ORDER BY id LIMIT 500 FOR UPDATE SKIP LOCKED`, and the table has no
   such id: `event_id` is a random uuid and `sequence` is **instance-local**, allocated under
   `flow_instance`'s row lock. Ordering by either would hand a broker one instance's events
   in an order that instance never staged them in.

2. **There is no broker plugin, and `IEventPublisher` was not declared.**
   [17 §2](../17-Plugin-System.md#2-extension-contracts) has listed `IEventPublisher` as an
   extension point since before `FlowX.Abstractions` existed, alongside ten other contracts
   that also do not exist. `plugins/FlowX.Http` is the only transport in the repository.

3. **[ADR-0009](ADR-0009-plugin-contracts.md) forbids the obvious shortcut.** A publisher
   contract defined inside `plugins/FlowX.Postgres` would make every future broker plugin
   depend on the PostgreSQL adapter to implement an interface that has nothing to do with
   PostgreSQL.

4. **`SKIP LOCKED` and per-key ordering are in tension, and §5's table asserts both.**
   `SKIP LOCKED` is what lets two publishers run without contention. Per-key ordering is the
   guarantee the same table offers a consumer. Naively combined they contradict: publisher A
   claims key `K`'s older event, publisher B skips the locked row, claims K's newer one, and
   reaches the broker first.

Options rejected:

- **Define the contract in the plugin** — violates force 3.
- **Publish outside the claim transaction and mark afterwards** — the mark is then a second
  write that a crash loses, which is the same at-least-once guarantee with a longer window
  and no benefit; or it needs a `claimed_by` column and a reaper for the publisher that died
  holding a claim.
- **Offer a global order** — serialises every key through one publisher, and is the property
  §5 already declines to offer for exactly that reason.
- **Order by `now()` at staging time** — a timestamp is not monotonic across nodes and ties
  are ordinary at commit granularity, so the order would be approximately right, which for an
  ordering guarantee is the same as wrong.

## Decision

### 1. `IEventPublisher` is declared in `FlowX.Abstractions`, taking a batch and returning a prefix

```csharp
ValueTask<Result<int>> PublishAsync(IReadOnlyList<OutboxRecord> batch, CancellationToken ct);
```

The implementation publishes `batch[0]`, then `batch[1]`, and reports how many reached the
broker before it stopped. Anything it does not report stays pending and is offered again.

**A prefix rather than a per-event set**, because the count is what makes ordering
expressible without an ordering protocol in the interface: "the first *n* arrived, in this
order, and nothing after them did" is a statement a caller can act on, and a set of
acknowledged ids is not — a publisher that acknowledged events 1 and 3 of a key would have
already broken the guarantee by the time it said so.

**The batch is `OutboxRecord`, the journal's own read type**, rather than a new envelope
shape. It carries exactly what a broker client needs — `EventId` for the consumer's
idempotency key, `Type` for the topic, `PartitionKey` for the key, `SchemaVersion` and
`PayloadJson` for the body — and a parallel record would be the same five fields with a
mapping between them to keep in step.

### 2. Migration `0004` adds `staged_seq`, and it is a staging order rather than a log

A `bigint` defaulted from a sequence, allocated at `INSERT` inside the step's transaction.
It is deliberately **not** claimed to be gap-free or commit-ordered: values are allocated
before commit and transactions commit out of order, so a reader can see 7 before 6 exists.
The publisher does not assume otherwise — an event stays pending until it is published, so a
late arrival costs a pass rather than an ordering violation.

### 3. Ordering offered: per `partition_key`. Global ordering is not offered

**Per-key holds under any number of publishers**, and force 4 is why that needs more than
`SKIP LOCKED`. The claim query drops a claimed row when its key has an older pending event
this claim did not take — locked by another publisher, or beyond the batch limit. The row
stays pending and is published later, behind the sibling it must follow.

**A null `partition_key` is exempt.** `OutboxWrite.PartitionKey` documents null as an
unordered event; holding one behind another would invent a guarantee nobody declared and
serialise the whole unkeyed stream to do it.

**Global ordering is not offered, and no setting turns it on.** Two events with different
keys arrive in either order. This is the same sentence §5's table has carried since it was
written; what changes here is that there is now an implementation it is true of.

### 4. The broker call happens inside the claim transaction

Claim, publish, mark and commit are one transaction. A crash anywhere before the commit —
including after the broker acknowledged — rolls the mark back and leaves the rows pending, so
the next pass republishes. **At-least-once, never zero.** The cost is that rows stay locked
while the publisher is on the network, which is precisely the condition `SKIP LOCKED` exists
to make survivable.

### 5. Retention refuses to purge an instance with an unpublished event

ADR-0016 recorded this as a note for WP-56: *"purging an instance cascades to its outbox
rows, including any that were never published. Today nothing publishes them, so nothing is
lost; when WP-56 lands a publisher, the purge needs a guard against removing a pending
event."* The publisher landed, so the premise is spent. Both purges now carry a
`NOT EXISTS` over pending events, and it is **not** scoped to a window: there is no age at
which discarding an unsent event becomes correct.

`RetentionSweep.HeldForPendingEvents` counts what the guard withheld, because the guard's own
failure mode — a deployment with no publisher wired, accumulating instances that are never
purged — is otherwise invisible until the disk is.

## Consequences

**Positive**

- **C4 describes the system.** State and event commit together and the event now leaves the
  database, proved by a crash test that asserts a duplicate rather than a loss and a
  two-publisher test that asserts six deliveries of six events.
- **`FlowX.Abstractions` gains a contract [17 §2](../17-Plugin-System.md) has promised for
  three phases**, with no dependency: the interface names `OutboxRecord`, `Result` and
  `Error`, all of which already lived there.
- **Per-key ordering survives horizontal scaling.** The naive `SKIP LOCKED` publisher would
  have broken it silently under load — the failure only appears with two publishers and a
  shared key, which is the configuration nobody tests first.
- **ADR-0016's accepted risk is discharged rather than restated.**

**Negative / accepted trade-offs**

- **`.Emit<T>()` still publishes nothing, and `FLOWX1024` is still raised.** This record
  closes the store-and-publish half. The engine does not stage an emitted event into
  `StepCommit.Outbox` and the generated dispatcher produces no event payload for it to
  stage, so the chain is connected everywhere except at its first link. That is stated on
  [FLOWX1024](../diagnostics/FLOWX1024.md) rather than left for a reader to discover.
- **No broker integration is proved.** The only `IEventPublisher` in the repository is a
  recording test double. Acknowledgement semantics, broker-side partitioning and what a real
  client does with a half-accepted batch are all unproved, and
  [17 §4](../17-Plugin-System.md#4-compatibility-policy)'s `PublisherConformance` — the suite
  that would hold a second implementation to this contract — is still not written. Writing it
  against one test double would have been a suite that encodes its only implementation.
- **One event nobody can publish stops everything behind it.** The prefix contract means a
  permanently refused event is retried forever and blocks its batch. There is no dead-letter
  path; DLQ is named in §4's description of the unwritten conformance suite and is not part
  of this package.
- **An instance with a pending event is retained indefinitely.** Correct, and it converts a
  silent data loss into an unbounded table for a deployment that stages events and never
  publishes them. `HeldForPendingEvents` makes it observable; it does not make it bounded.
- **The claim query is not free.** The per-key `NOT EXISTS` is a second probe per claimed row
  with a `partition_key`. It is answered from `outbox_event_claim_idx`, and it is the price
  of decision 3 being true rather than aspirational.
- **`0004` rewrites `outbox_event`.** A volatile column default forces a table rewrite under
  `ACCESS EXCLUSIVE`, so step commits that stage events block while the migration runs. The
  table is a queue and normally near-empty; on a large unpublished backlog it is a pause.

## Revisit when

- A broker plugin exists and `PublisherConformance` can hold two implementations to this
  contract — at which point the prefix return is tested against a real client's batching
  semantics and may not survive.
- A deployment needs a dead-letter path, which changes the prefix contract from "stop" to
  "divert and continue".
- The polling publisher becomes the latency bottleneck, at which point §5's CDC alternative
  stops being a row in a table.
