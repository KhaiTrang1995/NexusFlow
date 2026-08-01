-- Expand: the two tables stage 1 and stage 3 keep their state in.
--
-- Both are new tables, so this migration is additive in the strongest sense
-- (docs/11-Distributed-Runtime.md §7.4): a pod running the previous release does not know
-- either table exists, never reads or writes one, and is unaffected by their presence. There
-- is no backfill, because there is nothing to derive — a rate limiter starts with full buckets
-- and an idempotency store starts with no records, and both are the correct initial state.
--
-- Neither table has a foreign key to flow_instance, and that is deliberate rather than an
-- omission. ADR-0016 already argues it for the lease table: "a lease carries no foreign key to
-- the instance it precedes", because the lease is taken before the instance row exists. The
-- same holds twice over here. A rate-limit bucket belongs to a capability and a tenant, not to
-- any one instance; and an idempotency record outlives the instance that wrote it — that is the
-- point of a window, and a cascade from a retention sweep would delete records that are still
-- being replayed.

-- One row per bucket. `tokens` is the level at the instant in `touched`, and the refill is
-- computed on read rather than by a sweep — so an untouched bucket costs nothing and there is
-- no background job to run or to forget to run.
--
-- `double precision` for the level, because a token bucket refills continuously: with 20
-- permits a second, one millisecond is a fiftieth of a token, and an integer column would floor
-- every partial refill to zero and never refill at all under steady load. The value is floored
-- once, on the way out, so a caller never sees a fractional permit it could not spend.
--
-- `admitted` is the answer the last caller was given, and it is a column rather than a derived
-- value because PostgreSQL cannot tell a caller otherwise. The refill, the decision and the
-- decrement have to be one statement — two would let a second connection decide against a level
-- the first has already spent — so the decision has to travel out through RETURNING, and
-- RETURNING sees only the row as this statement wrote it. The new level alone is ambiguous: a
-- caller admitted from 1.5 tokens and a caller refused at 0.5 both leave 0.5 behind. Writing the
-- answer down removes the ambiguity, gives each caller its own row version to read, and has the
-- side benefit that an operator selecting from this table sees what the bucket last decided.
CREATE TABLE ratelimit_bucket (
    bucket_key text             NOT NULL PRIMARY KEY,
    tokens     double precision NOT NULL,
    touched_at timestamptz      NOT NULL,
    admitted   boolean          NOT NULL
);

-- One row per idempotency key. Exactly one of the two states is live at a time and the CHECK
-- says so: `claimed_until` in the future with no record is a caller inside the step, and a
-- record present is an outcome to replay.
--
-- `expires_at` is the record's window and `claimed_until` is the claim's lease, and they are
-- two columns rather than one because they are two lifetimes with different jobs. A claim
-- lapses in seconds — a node that takes a key and dies must not wedge every repeat of it for a
-- declared PT24H — while a record lives for the whole declared window. ADR-0037 §2.2.
--
-- `text` rather than `json` or `jsonb` for the record, and for ADR-0016's reason applied to a
-- different column: the store is not entitled to parse what it holds. What it is handed has
-- already been through JournalPayload's single redacting exit, and a store that parsed it would
-- be a store that could reshape it — so a replay would hand back something the engine's
-- generated reader might read differently from what was written. `text` is the shape that makes
-- "byte for byte" checkable, which is what IdempotencyStoreConformance asserts.
CREATE TABLE idempotency_record (
    idempotency_key text        NOT NULL PRIMARY KEY,
    record          text,
    claimed_until   timestamptz,
    expires_at      timestamptz,

    CONSTRAINT idempotency_record_state_check CHECK (
        (record IS NULL     AND claimed_until IS NOT NULL AND expires_at IS NULL) OR
        (record IS NOT NULL AND expires_at    IS NOT NULL))
);

-- Both tables accumulate rows nobody will read again — a bucket for a tenant that stopped
-- calling, a record whose window closed — and neither is cleaned up by the write path, because
-- a write that also swept would make every caller pay for somebody else's history. These two
-- indexes are what a retention sweep reads, and they are partial on the same predicate a sweep
-- states so the planner can serve it as an ordered index scan rather than a sequential scan of
-- the whole table. Same decision as flow_instance_due_idx in 0005.
CREATE INDEX ratelimit_bucket_stale_idx ON ratelimit_bucket (touched_at);

CREATE INDEX idempotency_record_expired_idx ON idempotency_record (expires_at)
    WHERE expires_at IS NOT NULL;
