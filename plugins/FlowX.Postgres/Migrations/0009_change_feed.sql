-- Expand: make the outbox readable as a change feed by a second consumer, without taking
-- anything away from the first.
--
-- `PostgresOutboxPublisher` drains this table destructively: it claims rows FOR UPDATE SKIP
-- LOCKED and writes `published_at`. That column is the publisher's progress and nobody else's,
-- so a change subscription may not write it and may not depend on it (ADR-0047). A change
-- subscription therefore reads the table and keeps its own position — which is what the two
-- objects below are.
--
-- WHY A SECOND STAMP AND NOT `staged_seq`. 0004 added `staged_seq` and said, in as many words,
-- what it is not: "Sequence values are allocated before commit and transactions commit out of
-- order, so a reader can see 7 before 6 exists and 6 can appear afterwards." The publisher is
-- immune because it uses no cursor — a row stays pending until it is marked. A cursor over
-- `staged_seq` is not immune: it advances to 7, and 6 is skipped for ever, silently.
--
-- `pg_current_xact_id()` stamps the row with the transaction that staged it, and
-- `pg_snapshot_xmin(pg_current_snapshot())` is the oldest transaction still in flight. Once a
-- row's stamp is below that barrier, every transaction with an equal or lower id has finished,
-- so no row with an equal or lower stamp can ever appear again. A cursor over the stamp is
-- gap-free by construction rather than by timing (ADR-0048).
--
--   And it must be the stamp the cursor is over, not the sequence. A transaction's id is
--   assigned at its FIRST write, which is normally a flow_step row long before its outbox
--   insert — so a row inserted earlier can carry a higher stamp than one inserted later. Under
--   the barrier the later row becomes visible while the earlier one is still hidden, and a
--   `staged_seq` cursor walks straight past it. `staged_seq` breaks ties within one
--   transaction, which is the only ordering it is asked for here.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Every statement is additive and a pod
-- running the previous release survives all of them:
--
--   * The column is NOT NULL with a DEFAULT, which is the one shape that lets a writer that has
--     never heard of it keep inserting — PostgresFlowJournal.StageOutboxAsync in the previous
--     release names its columns explicitly and simply omits this one.
--   * The new table is referenced by nothing that already exists. A pod running the previous
--     release neither reads nor writes it.
--
-- One operational note, the same one 0004 carries. `ADD COLUMN … DEFAULT pg_current_xact_id()`
-- has a volatile default, so PostgreSQL rewrites the table and holds ACCESS EXCLUSIVE on
-- outbox_event for the duration — step commits that stage an event block while it runs. The
-- table is a queue rather than a history, so it is normally near-empty; on a deployment with a
-- large unpublished backlog this is a pause, and it is the reason a migration is a deployment
-- step rather than a side effect of a container starting.

ALTER TABLE outbox_event
    ADD COLUMN staged_xid xid8 NOT NULL DEFAULT pg_current_xact_id();

-- The feed's read, exactly as it asks it: one type's rows, below the barrier, after the
-- cursor, in (stamp, position) order. Leading on `type` because a subscription observes one
-- type and the table holds every type the application emits.
CREATE INDEX outbox_event_change_idx ON outbox_event (type, staged_xid, staged_seq);

-- One row per change subscription: how far it has read, in the terms the feed reads in.
--
--   subscription_id  derived by the host from (flow id, flow version, source, group) — the same
--                    four terms the instance id is derived from (ADR-0049). Opaque here: a
--                    store that had opinions about a key's shape would be a store the host
--                    could not change the derivation of.
--   position_xid     the stamp of the last change the subscription finished with.
--   position_seq     its position within that transaction, which breaks the tie.
--   updated_at       when the cursor last moved, for the operator watching a subscription
--                    that has stopped making progress. Nothing in the code reads it.
--
-- No foreign key to anything. A cursor outlives the events it has read past — that is what a
-- cursor is — and outlives retention purging them.
CREATE TABLE change_cursor (
    subscription_id uuid        NOT NULL,
    position_xid    xid8        NOT NULL,
    position_seq    bigint      NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT change_cursor_pkey PRIMARY KEY (subscription_id)
);
