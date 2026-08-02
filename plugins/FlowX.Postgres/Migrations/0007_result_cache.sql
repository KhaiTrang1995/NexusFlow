-- Expand: a table for the result cache, which is a store like any other plugin contract.
--
-- `IResultCache` is stage 5's seam (ADR-0036), and this is the second implementation of it —
-- the one that makes `ResultCacheConformance` a statement about the contract rather than a
-- description of Redis. A relational cache is an unusual thing to want and the reason to have
-- one is deployment rather than performance: a team already running PostgreSQL for the journal
-- should not have to introduce a second piece of infrastructure to turn a policy on, and a
-- team that wants Redis should not be the only one who can.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Every statement here is additive: a
-- new table and its index, referenced by nothing that already exists. A pod running the
-- previous release neither reads nor writes it, and a pod running this one finds an empty
-- cache, which is a cold cache and is indistinguishable from correct.
--
--   cache_key   what the engine derived: SHA-256 hex over the capability id, its version, the
--               tenant, the principal's permission set under CacheScope.Principal, and the
--               input document the journal would have written. Opaque here on purpose — a
--               store that had opinions about a key's shape would be a store the engine could
--               not change the derivation of.
--   payload     exactly what JournalPayload.ToJson produced. `text`, not `jsonb`, for the
--               reason ADR-0016 gives for the journal's columns: nothing indexes inside it,
--               jsonb reorders keys and drops duplicates, and the engine hands what comes back
--               to the flow's own generated deserialiser. A document that came back
--               re-encoded would be one that deserialiser never produced.
--   stored_at   when the entry was written. Read by an operator rather than by the engine: a
--               hit that is seconds old and a hit that has been served for a whole TTL are
--               different facts about a dependency.
--   expires_at  when it stops being readable. An absolute instant rather than a duration,
--               because the comparison happens in the database against now() — the store's own
--               clock, which is the only clock both a reader and a writer share. A TTL
--               enforced by the caller's clock is a TTL that depends on NTP.

CREATE TABLE IF NOT EXISTS flowx_cache_entry (
    cache_key   text        NOT NULL PRIMARY KEY,
    payload     text        NOT NULL,
    stored_at   timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL
);

-- Lapsed entries are found by a sweep, not by the read path: a read is keyed and finds at most
-- one row, so it needs no index beyond the primary key. This one exists so that deleting what
-- has expired is a range scan rather than a sequential scan of a table whose whole purpose is
-- to be large.
--
-- There is deliberately no sweep in this package. A read already refuses an expired row, so a
-- deployment that never sweeps is correct and merely wastes space — the same position
-- PostgresRetention takes about the journal, and for the same reason: when data stops
-- mattering is an operator's decision and not an adapter's.
CREATE INDEX IF NOT EXISTS ix_flowx_cache_entry_expires_at
    ON flowx_cache_entry (expires_at);
