-- Expand: give outbox_event a staging order a publisher can read it back in, and index
-- the two questions WP-56 asks of the table.
--
-- 0001 created outbox_event with (sequence, ordinal) — the commit sequence of the step that
-- staged the event, and the position within that step. That pair orders one instance's
-- events and nothing else: `sequence` is instance-local, handed out under flow_instance's
-- row lock, so instance A's sequence 4 and instance B's sequence 4 are unrelated numbers.
-- docs/11-Distributed-Runtime.md §5 draws the publisher as
-- `SELECT unpublished ORDER BY id LIMIT 500`, and there was no id to order by: event_id is a
-- random uuid. A publisher ordering by it would hand the broker one instance's events in an
-- order the instance never staged them in, which is precisely the guarantee
-- per-partition_key ordering is supposed to be.
--
-- staged_seq is that order. It is allocated by a sequence at INSERT, inside the same
-- transaction as the step row, so it records the order in which events were *staged*.
--
--   What it is not: a gap-free log. Sequence values are allocated before commit and
--   transactions commit out of order, so a reader can see 7 before 6 exists and 6 can appear
--   afterwards. PostgresOutboxPublisher does not assume otherwise — an event stays pending
--   until it is published, and the per-key guard in its claim query holds back an event whose
--   key has an older pending sibling the claim did not take. Late arrival costs a pass, not
--   an ordering violation.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Every statement here is additive
-- and a pod running the previous release survives all of them:
--
--   * The column is NOT NULL with a DEFAULT, which is the one shape that lets a writer that
--     has never heard of it keep inserting — PostgresFlowJournal.StageOutboxAsync in the
--     previous release names its columns explicitly and simply omits this one, and the
--     default fills it. A nullable column would have been worse, not better: it would leave
--     the publisher with rows it cannot order.
--   * Both indexes are invisible to an older reader. An index changes no row and no column.
--
-- One operational note, the same one 0003 carries. `ADD COLUMN … DEFAULT nextval(…)` has a
-- volatile default, so PostgreSQL rewrites the table and holds ACCESS EXCLUSIVE on
-- outbox_event for the duration — step commits that stage an event block while it runs. The
-- table is a queue rather than a history, so it is normally near-empty; on a deployment with
-- a large unpublished backlog this is a pause, and it is the reason a migration is a
-- deployment step rather than a side effect of a container starting.

CREATE SEQUENCE outbox_event_staged_seq;

ALTER TABLE outbox_event
    ADD COLUMN staged_seq bigint NOT NULL DEFAULT nextval('outbox_event_staged_seq');

-- Tied to the column, so the sequence's lifetime is the column's rather than the schema's.
ALTER SEQUENCE outbox_event_staged_seq OWNED BY outbox_event.staged_seq;

-- The publisher's claim, exactly as it asks it: pending rows, oldest staging order first,
-- bounded by a batch. 0001's outbox_event_pending_idx is on (published_at) and answers
-- "which rows are pending" without an order to inherit, so the claim would read every
-- pending row and top-N sort it. This one is an ordered index scan over the same predicate.
-- outbox_event_pending_idx is left where 0001 put it: superseded is not unused, and a drop
-- belongs to a release after one that ships with nothing planning against it.
CREATE INDEX outbox_event_claim_idx ON outbox_event (staged_seq) WHERE published_at IS NULL;

-- The retention guard's question, which is a different one: "does this instance have any
-- unpublished event at all". Asked once per candidate instance by every sweep, and answered
-- against the pending rows alone rather than against every event the instance ever emitted.
CREATE INDEX outbox_event_pending_instance_idx ON outbox_event (instance_id)
    WHERE published_at IS NULL;
