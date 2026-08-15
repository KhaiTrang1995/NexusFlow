-- Expand: the table stage 1's long-window quota keeps its counters in.
--
-- A new table, so this migration is additive in the strongest sense
-- (docs/11-Distributed-Runtime.md §7.4): a pod running the previous release does not know it
-- exists, never reads or writes it, and is unaffected by its presence. There is no backfill,
-- because a quota starts with every budget unspent and that is the correct initial state.
--
-- No foreign key to flow_instance, for 0006's reason applied to the same kind of thing: a
-- budget belongs to a capability and a tenant, not to any one instance, and it outlives every
-- instance that spent from it.

-- One row per key per window, and the window is part of the key rather than a column that is
-- rewritten. `window_start` is the instant the current period began, floored from the server's
-- own now() to a multiple of the declared period, so every node agrees where the boundary is
-- without agreeing about anything else. A caller arriving after the boundary writes a new
-- window_start and resets `spent` in the same statement, which is what makes the reset free:
-- there is no sweep to run and none to forget to run.
--
-- `spent` is a bigint counter and not a level, which is the whole difference between this table
-- and ratelimit_bucket beside it. A token bucket refills continuously and needs a fractional
-- level; a quota grants the whole budget at a boundary and nothing in between, so the state is
-- a count of calls made in the current period. That is the fixed window IQuotaStore documents,
-- and it is the shape TenantFairness.QuotaPerWindow's remarks say IRateLimiterStore cannot
-- express — "a quota that resets on a calendar boundary is a fixed-window counter and is not
-- this contract".
--
-- `admitted` is the answer the last caller was given, and it is a column for exactly the reason
-- ratelimit_bucket has one: the read, the roll and the increment have to be one statement, so
-- the decision travels out through RETURNING, and RETURNING sees only the row as this statement
-- wrote it. A caller admitted to the last unit and a caller refused after it both leave the
-- same `spent` behind.
CREATE TABLE quota_counter (
    quota_key    text        NOT NULL PRIMARY KEY,
    window_start timestamptz NOT NULL,
    spent        bigint      NOT NULL,
    admitted     boolean     NOT NULL
);

-- Rows accumulate for keys nobody will spend from again — a tenant that churned, a capability
-- a flow stopped calling — and the write path does not clean up, because a write that also
-- swept would make every caller pay for somebody else's history. This is the index a retention
-- sweep reads, on the same predicate a sweep states, so the planner serves it as an ordered
-- index scan. Same decision as ratelimit_bucket_stale_idx in 0006.
CREATE INDEX quota_counter_stale_idx ON quota_counter (window_start);
