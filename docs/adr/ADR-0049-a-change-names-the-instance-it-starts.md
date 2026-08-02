# ADR-0049: An observed change names the instance it starts

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0031](ADR-0031-an-occurrence-names-the-instance-it-starts.md) ·
[ADR-0035](ADR-0035-a-delivery-names-the-instance-it-starts.md)

> This is [ADR-0035](ADR-0035-a-delivery-names-the-instance-it-starts.md) with a different
> subject, and it is recorded as a short record rather than argued again because **the answer is
> the same shape and should be**. A third derivation invented for a third transport would be the
> defect, not the record.

## Context

1. **A change feed is at-least-once, for a reason of its own.**
   [ADR-0048](ADR-0048-a-change-feed-advances-a-cursor.md) commits the cursor after the flows
   have run, so a crash in between re-offers every change since the last committed position.
   Unlike a broker, this is not a contract imposed from outside — it is the direct consequence of
   ordering the two writes the only way that cannot lose work.

2. **ADR-0031 named this case in advance and ADR-0035 discharged it once already.** The
   derivation lives in `DerivedIdentity`, shared by `ScheduleOccurrence` and
   `BusDeliveryIdentity`, so a third caller adds a subject and no mechanism.

3. **Reusing `BusDeliveryIdentity` verbatim would fold two subscribers into one.** A flow
   carrying both `[BusTrigger("order.placed", Group = "g")]` and
   `[ChangeTrigger("order.placed", Group = "g")]` is two subscriptions over two transports; the
   five terms ADR-0035 hashes are identical for both, so the second would be permanently
   suppressed by the first's journal row. `BusDeliveryIdentity.StreamLeaseIdFor` already solved
   the same collision between a partition lease and an instance id, with a scope term.

Options rejected:

- **Reuse the bus derivation unchanged** — force 3.
- **Deduplicate on the cursor alone** — the cursor is committed *after* the run, so the window it
  does not cover is exactly the window a crash lands in.
- **Deduplicate on `partition_key`** — ADR-0035's rejection unchanged: a key is an ordering, and
  every event one instance emits shares it.

## Decision

### 1. The instance id is `DerivedIdentity` over a scope term and ADR-0035's five

`ChangeIdentity.InstanceIdFor(flowId, flowVersion, source, group, changeId)` hashes
`"flowx\0change"` followed by the five terms, NUL-separated, laid out as a UUID version 8 —
the same rendering, the same separator, the same RFC 9562 slot. The scope term is what makes a
change subscription and a bus subscription over one type and one group two subscribers.

`changeId` is the outbox row's `event_id`: the identity the store assigned at staging time,
which every re-read of the row carries.

### 2. A `Change`-triggered flow must declare `Durable`, and FLOWX1041 refuses one that does not

ADR-0035 decision 3 unchanged. An `Ephemeral` flow journals nothing, so the derived id is inert
and every re-read of the cursor's window runs the flow again with nothing recording that it had.
`FlowChangeCatalog.Add` refuses a hand-written registration for the same reason
`FlowBusCatalog.Add` does.

### 3. The subscription's lease is derived the same way, under its own scope term

`ChangeIdentity.SubscriptionLeaseIdFor(flowId, flowVersion, source, group)` — no change id,
because the lease is over the subscription and not over one change
([ADR-0048](ADR-0048-a-change-feed-advances-a-cursor.md) decision 4). Scoped so it can never
collide with an instance id.

## Consequences

**Positive**

- **One change starts one flow, permanently**, by the journal's primary key rather than by the
  cursor — so a change re-offered after a crash, a restart or a cursor rollback is refused by
  the same mechanism as one re-offered a millisecond later.
- **Nothing new exists.** The derivation is one call to `DerivedIdentity.From`; the refusals it
  relies on are already held to `JournalConformance` and `LeaseStoreConformance`.

**Negative / accepted trade-offs**

- **A flow may be started twice over two transports on purpose, and this makes that possible
  rather than preventing it.** A team that declares both `[BusTrigger]` and `[ChangeTrigger]`
  over one type gets two instances per event. That is the correct reading of two subscribers, and
  it is a foot-gun for anyone who adds a change trigger meaning to *replace* a bus one.
- **The group is still in the id**, so ADR-0035's rename trade-off carries over: changing
  `Group` re-runs every change still inside the feed's window.
- **The id says nothing about the cursor.** An operator who rewinds a cursor to replay a window
  gets `journal.instance_exists` for every change in it and no re-execution — which is the
  safety property, and it means "replay" is not available without a nonce this record does not
  define.

## Revisit when

- A change feed delivers something with no stable identity of its own — a file watcher's event —
  at which point the identity has to be synthesised where the change is read, and this record's
  `changeId` term stops being a store's column.
- A subscription needs to observe one change twice on purpose, which is ADR-0035's own revisit
  condition and would introduce the same nonce to both.
- A fourth transport needs derived identity, at which point three scope terms in three types is
  one type too many and the scope belongs in `DerivedIdentity` itself.
