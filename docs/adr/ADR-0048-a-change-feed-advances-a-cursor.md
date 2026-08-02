# ADR-0048: A change feed advances a cursor past a committed barrier; nothing is acknowledged

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0018](ADR-0018-outbox-publication-and-ordering.md) ·
[ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md)

> [ADR-0036](ADR-0036-a-message-is-acknowledged-when-its-flow-is-journalled.md) settled *when* a
> delivery is finished with. This record settles *what finishing with it means* when the
> transport is a table rather than a broker — because a table has no pending-entries list, no
> visibility timeout and nothing to acknowledge to.

## Context

1. **A broker owns its deliveries; a feed owns nothing.** `IBusConsumer` can acknowledge because
   the broker is holding the message on the consumer's behalf. `outbox_event` holds nothing on
   anybody's behalf: its one marker, `published_at`, belongs to `PostgresOutboxPublisher`
   ([ADR-0047](ADR-0050-a-change-trigger-observes-the-outbox.md) decision 2). A change
   subscription's progress therefore has to be written somewhere of its own, and the only shape
   that is is a cursor.

2. **`staged_seq` is not safe to put a cursor on, and the reason is written in the migration
   that added it.** `0004_outbox_publication.sql` says so in as many words: *"Sequence values are
   allocated before commit and transactions commit out of order, so a reader can see 7 before 6
   exists and 6 can appear afterwards."* The outbox publisher is immune because it does not use
   a cursor — a row stays pending until it is marked. A cursor advanced to 7 skips 6 for ever,
   and does it silently.

3. **PostgreSQL states the barrier that makes a cursor safe.**
   `pg_snapshot_xmin(pg_current_snapshot())` is the oldest transaction id still in flight.
   Every transaction below it has finished, so once a row stamped with `pg_current_xact_id()` is
   below the barrier, **no row with an equal or lower stamp can ever appear again**. A cursor
   over that stamp is gap-free by construction rather than by timing.

4. **A cursor over `staged_seq` under the same barrier is *not* safe**, which is the trap worth
   writing down. A transaction's id is assigned at its first write, which may be a `flow_step`
   row long before its outbox insert; so a row inserted earlier can carry a *higher* xid than one
   inserted later. Under the barrier the later row becomes visible while the earlier one is still
   hidden, and a `staged_seq` cursor walks straight past it. The stamp and the cursor have to be
   the same column.

5. **The dispositions are already classified.** `FlowBusScan.DispositionFor` reads
   `FlowExecutionResult` into "recorded" and "not recorded" from ADR-0036's table. Nothing about
   that classification is broker-specific.

Options rejected:

- **Mark the outbox row** — [ADR-0047](ADR-0050-a-change-trigger-observes-the-outbox.md) force 2:
  one marker, two consumers, and every event reaches one of them.
- **A column per subscription** — a schema migration per declaration.
- **A consumed-set table, anti-joined against the outbox** — permanent storage proportional to
  every event ever staged, to answer a question a cursor answers in one row.
- **Re-offer every retained row each pass and let the journal refuse the duplicates** — correct,
  and one journal round trip per retained row per pass. Bounded by retention rather than by
  progress, which is not a bound.
- **A safety lag — read only rows older than N seconds** — turns correctness into a race with a
  configured number in it. Long transactions are exactly the case, and they are exactly the case
  a fixed lag gets wrong.
- **`SELECT … FOR UPDATE SKIP LOCKED` like the publisher** — needs a marker to claim into, which
  is force 1 again.

## Decision

### 1. Every outbox row carries the transaction that staged it

Migration `0009` adds `staged_xid xid8 NOT NULL DEFAULT pg_current_xact_id()`, allocated inside
the same transaction as the step row for the same reason `staged_seq` is. Expand-only: the
column is `NOT NULL` with a default, so a pod running the previous release keeps inserting and
the default fills it.

### 2. A feed reads only what is below the barrier, ordered by `(staged_xid, staged_seq)`

```sql
WHERE type = @type
  AND staged_xid < pg_snapshot_xmin(pg_current_snapshot())
  AND (staged_xid, staged_seq) > (@position_xid, @position_seq)
ORDER BY staged_xid, staged_seq
```

`staged_seq` breaks ties *within* one transaction, which is the only place it orders anything
that the pair does not already order. Per-`partition_key` order survives because a key is the
emitting instance (ADR-0035) and one instance's steps commit serially, so its events are
ordered by transaction and then by staging position — which is what ADR-0018 offers and no more.

### 3. The cursor advances past the longest prefix that reached a **recorded outcome**

ADR-0036's rule 1 unchanged: success, suspension, a `Result` failure and
`journal.instance_exists` are recorded; `lease.held`, `lease.lost`, `journal.fenced_out`,
`host.draining` and `flow.durability_not_configured` are not. `FlowChangeScan` stops at the
first change that did not reach one and commits the position of the one before it. The rest are
read again next pass, behind the sibling they must follow.

**Nothing is acknowledged, and there is no dead-letter path.** A change whose flow *ran and
failed* is recorded, so it does not block: only the host and store refusals do, and every one of
them is transient by construction. A poison change in the broker's sense — an entry that can
never be processed — does not exist here, because the row was written by this system's own
serialiser rather than by a stranger.

### 4. One subscription is read by one node at a time, under a lease

`ChangeIdentity.SubscriptionLeaseIdFor` derives the lease; a node that does not win it does
nothing and tries next pass. A cursor is a single-reader structure and pretending otherwise
would have two nodes committing two positions over one row. This is weaker than
[ADR-0037](ADR-0037-the-consumer-offers-per-key-order.md)'s per-partition concurrency and is the
honest cost of a log with a cursor: a subscription scales by adding subscriptions, not nodes.

## Consequences

**Positive**

- **A skipped change is impossible rather than unlikely.** The barrier is an invariant
  PostgreSQL maintains, not a window this code chose, so the guarantee does not degrade under
  load, long transactions or clock skew.
- **At-least-once falls out for free.** A crash between running a flow and committing the cursor
  re-offers the change; the derived instance id
  ([ADR-0049](ADR-0049-a-change-names-the-instance-it-starts.md)) makes that a
  `journal.instance_exists`, which is recorded, so the cursor advances on the retry.
- **One classification of outcomes, two transports.** `DispositionFor`'s switch is ADR-0036's
  table, and this scan reads it rather than restating it.

**Negative / accepted trade-offs**

- **Latency is bounded below by the oldest open transaction.** A change is invisible to every
  feed while any older transaction is still running, so a long-running transaction anywhere in
  the database delays every subscription. That is the price of the barrier, it is unbounded in
  principle, and no configuration reduces it.
- **A subscription is served by one node.** Throughput is one node's, and a slow flow cannot be
  spread across the fleet the way a partitioned bus subscription can.
- **One change that never reaches a recorded outcome stops its subscription.** With no
  dead-letter path the cursor stays put. The refusals that can cause it are transient, so this is
  a stall rather than a permanent block — but a store that is down stops the subscription
  entirely rather than degrading it.
- **`staged_xid` costs a table rewrite on upgrade**, for `0004`'s reason: a volatile default
  makes `ADD COLUMN` rewrite the table under `ACCESS EXCLUSIVE`. The outbox is a queue rather
  than a history, so it is normally near-empty; on a large backlog it is a pause.
- **`xid8` is PostgreSQL 13 and above.** The adapter already requires more than that elsewhere,
  but this is the first hard dependency on a *visibility* primitive, and a second store cannot
  copy the mechanism unless it has one.

## Revisit when

- A second `IChangeFeed` ships over a store with no snapshot barrier, at which point "the feed
  guarantees no gaps" has to be restated as a contract implementations meet by their own means.
- A subscription needs more throughput than one node, at which point the cursor has to become
  per-partition and this record's decision 4 is what changes.
- Retention learns about cursors, at which point a subscription that is behind holds rows back
  instead of losing them.
- A change is observed stalling behind a refusal this record calls transient, which would mean
  the refusal set has grown a permanent member and a dead-letter path is owed after all.
