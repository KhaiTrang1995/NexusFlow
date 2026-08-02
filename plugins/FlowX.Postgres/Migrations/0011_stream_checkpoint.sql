-- Expand: where a stream subscription's progress is kept.
--
-- WHAT IS IN THIS TABLE, AND WHAT DELIBERATELY IS NOT. One row per subscription, holding one
-- opaque position — and no window state at all. A window's accumulating records live in the
-- reading node's memory and nowhere else (ADR-0055): a node that dies rebuilds every open window
-- by re-reading the source from this position, because window assignment is a pure function of a
-- record's event time, and a window whose flow had already committed derives the same instance id
-- and is refused by flow_step's primary key rather than aggregated twice.
--
-- That is why there is no window table, no partial-aggregate column and no schema for a value
-- that changes on every record. What this table costs a node death is stated where it belongs, in
-- ADR-0055: the records between this position and the crash are read again, the side output may
-- see a late record twice, and a source that has trimmed past this position stops the
-- subscription rather than resuming with a gap.
--
--   subscription_id  derived by the host from (flow id, flow version, source, group) — the same
--                    four terms the window's instance id is derived from. Opaque here, exactly as
--                    change_cursor's is.
--   position         rendered by the IStreamSource that produced it, and never parsed, compared
--                    or ordered by this store. A store that had opinions about a position's shape
--                    would be a store the source could not change the rendering of.
--   updated_at       when the checkpoint last moved, for the operator watching a subscription
--                    that has stopped making progress. Nothing in the code reads it.
--
-- MONOTONICITY IS NOT ENFORCED HERE, AND CANNOT BE. The position is an opaque string, so this
-- table cannot tell an older one from a newer one; a `WHERE position < excluded.position` would
-- be lexicographic nonsense over a rendering it does not own. What makes the checkpoint monotonic
-- is upstream: one node reads one subscription at a time under a lease, and it only ever commits
-- the last position of its own settled prefix, which advances by construction.
--
-- No foreign key to anything. A checkpoint outlives the records it has read past — that is what a
-- checkpoint is — and outlives the source trimming them.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4): one new table, referenced by nothing
-- that already exists. A pod running the previous release neither reads nor writes it.

CREATE TABLE stream_checkpoint (
    subscription_id uuid        NOT NULL,
    position        text        NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT stream_checkpoint_pkey PRIMARY KEY (subscription_id)
);
