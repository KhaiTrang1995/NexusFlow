-- Expand: give an instance row a handle its data subject can be found by.
--
-- Erasure is the one obligation a journal makes harder rather than easier. Everything else a
-- retention policy does is time-shaped — "older than N days" — and the instance row already
-- carries created_at. "Everything about this person" is not time-shaped, and until this
-- migration the only way to answer it was to read every input and every state bag in the table
-- and deserialise each one: the slowest query the schema admits, and a second disclosure of the
-- data the request exists to destroy.
--
-- So the runtime writes a digest of the [Subject]-marked member of the flow's input onto the
-- row, and this is the column and the index it goes in. Three properties of that arrangement
-- are worth stating where the DDL is, because a reader of this file is the person most likely
-- to try to improve it:
--
--   1. THE COLUMN IS NOT THE IDENTIFIER, AND IT IS STILL PERSONAL DATA. It is SHA-256 over a
--      domain-separated form of the identifier, so a SELECT * does not hand out national
--      identifiers — but the space of national identifiers is small enough to enumerate, so
--      anybody holding a candidate can confirm it. That is why the column sits inside the same
--      row-level security every other column of this table sits inside, and why nothing here
--      grants it to a role that could not already read the row. docs/adr/ADR-0061 argues it.
--
--   2. THE INDEX LEADS ON tenant_id. The erasure's predicate is (tenant, digest) and never
--      (digest) alone: a digest is unsalted, so the same person known to two tenants has the
--      same handle in both, and an index that let one erasure sweep both would be a
--      cross-tenant write with a plausible-looking WHERE clause. Row-level security refuses it
--      independently — the erasure runs on a scoped connection like every other statement — and
--      the index leads on the tenant so the refusal is not the only thing standing there.
--
--   3. PARTIAL, ON subject_digest IS NOT NULL. Most flows identify nobody: a funds transfer's
--      journal is about an account, and the column is null on every row of it. A partial index
--      costs those deployments nothing at all rather than costing them one entry per instance.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Additive and reversible: the column is
-- nullable with no default, so ADD COLUMN is a catalogue-only operation on PostgreSQL 11 and
-- later and rewrites nothing, and a pod running the previous release neither writes it nor
-- reads it. There is no backfill and there cannot be one — the identifiers the handles would be
-- computed from were redacted on the way in, which is the whole reason the digest is taken
-- before the redaction pass rather than after it.
--
-- Deliberately NOT in this migration:
--
--   * A NOT NULL constraint. A flow that names no subject is the ordinary case, and a
--     constraint would make [Subject] mandatory on every flow in the deployment.
--
--   * A column on flow_step or outbox_event. They inherit the instance's subject through the
--     foreign key that already exists, exactly as they inherit its tenant — a denormalised copy
--     is a second source of truth that can disagree with the first about whose record this is.
--
--   * Anything on the result cache or the idempotency store. Both are keyed by a hash of a
--     step's input and carry no subject, so there is nothing here that could be indexed. What
--     bounds them is the window and the TTL their declarations already carry, and
--     ISubjectErasure's own remarks say so rather than leaving it to be assumed.

ALTER TABLE flow_instance ADD COLUMN IF NOT EXISTS subject_digest text;

CREATE INDEX IF NOT EXISTS flow_instance_subject_idx
    ON flow_instance (tenant_id, subject_digest)
    WHERE subject_digest IS NOT NULL;

-- And the one grant the erasure needs that 0008 did not give.
--
-- An erasure runs on a scoped connection like every other statement, and it asks whether each of
-- the instance's staged events is still owed to a consumer — the same predicate PostgresRetention
-- refuses to purge an instance with. That predicate reads change_cursor, and 0008 granted
-- flowx_tenant exactly the three tables the journal touches, because at the time nothing scoped
-- read anything else. Retention never hit it: the sweeper runs node-wide as the schema's owner.
--
-- SELECT only, and it is not a widening of what a tenant can see about another tenant. A cursor
-- row holds a subscription id and a position in the outbox; it carries no tenant column and no
-- payload, and a change subscription is a deployment-wide consumer rather than one tenant's. What
-- the grant buys is that the scoped connection can answer "has anybody read this yet" instead of
-- failing with 42501 — which is the erasure refusing every request for a reason that has nothing
-- to do with the request.
GRANT SELECT ON change_cursor TO flowx_tenant;
