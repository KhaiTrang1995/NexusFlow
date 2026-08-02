# ADR-0052: An outbox row is retained until every declared consumer is past it

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Runtime team, Platform architecture
**Amends:** [ADR-0018](ADR-0018-outbox-publication-and-ordering.md) decision 5 ·
[ADR-0050](ADR-0050-a-change-trigger-observes-the-outbox.md), third negative

> ADR-0050 listed "retention learns about change cursors" under *Revisit when*. This is that,
> and it is a record rather than a comment because the alternatives are all defensible and the
> wrong one is silent in both directions.

## Context

1. **`published_at` stopped meaning "nobody needs this row".** ADR-0018 read *owed* off that
   column, which was the whole answer while `PostgresOutboxPublisher` was the only consumer.
   `PostgresChangeFeed` reads the same table and deliberately never writes it, so a host with a
   `[ChangeTrigger]` subscription and no broker never sets it at all — and the guard held every
   instance that host ever ran, for ever, reported only as `HeldForPendingEvents`.

2. **The same column is wrong in the other direction too.** The seven-day `OutboxPublished`
   window deletes rows a *publisher* is finished with. A subscription reading the same rows has
   a position and no window, so a subscription down for a week lost changes it never read —
   ADR-0050's third negative, silent, with the cursor as the only evidence.

3. **The database cannot tell an absent consumer from a stalled one.** An unpublished row means
   "not sent yet" where a publisher is wired and "never will be" where none is. A cursor that has
   stopped moving means "the subscription is down" where the flow is deployed and "nobody will
   ever read this" where it is not.

Options rejected:

- **Purge on the lowest cursor position across `change_cursor`** — needs no configuration and is
  wrong: a subscription over `x` that has read every `x` sits at the last one for ever, so a
  later `y` is behind the watermark and the whole outbox stalls on a quiet subscription.
- **Give `change_cursor` a `source` column and join on it** — the feed writes that table and
  would have to fill the column; and the row would then record what a *deployment* declares
  rather than what the cursor is, which is how a stale row becomes a permanent hold.
- **Infer the consumer set from the container** — `AddFlowXPostgresOutbox` says *this process*
  publishes. It does not say no other process does, and a node inferring "no publisher" from its
  own registrations would purge events another node's publisher owed a broker.
- **Keep a window over unconsumed rows** — an age at which discarding an unread event is correct
  does not exist, which is ADR-0018's own argument and is unchanged.

## Decision

**An outbox row is owed while any consumer this deployment declares has not consumed it, and
retention deletes nothing that is owed.** The consumer set is `RetentionConsumers`: whether a
publisher drains the table, and every `ChangeSubscription` served over the schema. The publisher's
progress is `published_at`; a subscription's is its `change_cursor` row, compared as the feed
compares it — a row at or before the cursor has been read, a subscription with no row has read
nothing. One predicate, spliced into both instance purges and into the published-row window.

**The default is one publisher and no subscriptions**, which is what ADR-0018 shipped. A
deployment that has not said what reads its outbox holds unpublished rows rather than discarding
them: the wrong answer here is data loss in one direction and unbounded growth in the other, and
growth is the one an operator can see — `HeldForPendingEvents` is that number.

**The set is stated at registration** (`AddFlowXPostgresRetentionConsumers`) rather than read from
the host's change catalogue, because a cursor is a cluster-wide row and retention deletes
cluster-wide rows. A node declaring only what it happens to serve would purge for the cluster.

## Consequences

**Positive**

- A host with change subscriptions and no broker purges again, and purges exactly what its
  cursors are past — `RetentionTests.AHostWithNoBrokerPurgesWhatItsCursorIsPast` arranges two
  instances and one committed position, so neither "purge everything" nor "purge nothing" passes.
- ADR-0050's third negative expires: the published window now waits for the subscriptions that
  read the same rows.
- The publisher-only deployment is byte-for-byte unchanged, because its declared set is the
  default and the second disjunct is a join over empty arrays.

**Negative / accepted trade-offs**

- **A wrong declaration is a data loss.** Declaring a subscription that does not exist only costs
  growth; *omitting* one that does means its unread events are discardable. Nothing detects it —
  the omitted subscription's cursor is the evidence and retention no longer reads it.
- **The cursor key is derived in two places.** `PostgresChangeFeed` keeps its derivation private,
  as a store should, so `PostgresRetention` derives it again. They are pinned by an integration
  test that drives the real feed rather than by a shared function.
- **A change subscription still has no window of its own.** A subscription that never runs holds
  its type's rows for ever, which is the same shape as the defect this fixes, one level up.

**Revisit when** a second `IChangeFeed` exists, at which point "the consumer is a cursor in this
schema" stops being general enough; or cursor lag becomes a metric, at which point retention could
refuse to be held by a subscription that has been dead longer than a declared window.
