-- What an outside data provider said about a lead's company, and the ticket it was asked under.
--
-- §8.2's wait needs somewhere to put two facts: the ticket the provider handed back, and the
-- answer when it eventually comes. Both are the CRM's record of a conversation with somebody
-- else — the provider's own store is the provider's, and this sample stands one up in memory
-- rather than pretending to own it.
--
-- One row per lead, keyed on the lead, because a lead is enriched once. A second request for a
-- lead already enriched is answered from this row, which is what makes the flow idempotent
-- without an idempotency table: the ticket is derived from the lead and the row is its own
-- deduplication.
--
-- The tenant column is NOT NULL and part of the composite reference, for 0001's reason: a
-- MATCH SIMPLE composite foreign key containing a NULL is not checked at all, so a nullable
-- tenant_id here would be a reference that silently stops being one.

-- The composite reference below needs one, and 0001 gave LEAD only its primary key: nothing
-- pointed at a lead until now, so nothing had asked for the pair the other parties carry. Added
-- here rather than edited into 0001, because a migration already applied to a deployment is a
-- record of what that deployment did and not a draft.
ALTER TABLE lead ADD CONSTRAINT lead_id_tenant_unique UNIQUE (lead_id, tenant_id);

CREATE TABLE lead_enrichment (
    lead_id      uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,
    ticket       text        NOT NULL,
    status       text        NOT NULL CHECK (status IN ('Pending', 'Answered', 'Abandoned')),

    -- Null until the provider answers, and all three together: a partial answer is not an
    -- answer, and a row that could hold one would need a rule nothing enforces.
    industry     text,
    employees    int         CHECK (employees IS NULL OR employees > 0),
    region       text,

    requested_at timestamptz NOT NULL,
    answered_at  timestamptz,

    UNIQUE (lead_id, tenant_id),
    FOREIGN KEY (lead_id, tenant_id) REFERENCES lead (lead_id, tenant_id),

    -- Answered is the answer and the instant, together. Pending and Abandoned carry neither.
    CHECK (
        (status = 'Answered'
            AND industry IS NOT NULL AND employees IS NOT NULL
            AND region IS NOT NULL AND answered_at IS NOT NULL)
        OR (status <> 'Answered'
            AND industry IS NULL AND employees IS NULL
            AND region IS NULL AND answered_at IS NULL))
);

CREATE INDEX lead_enrichment_by_ticket ON lead_enrichment (ticket);

-- The same two walls every other table in this schema stands behind, and for the same reason:
-- a table added after 0002 that nobody remembered to enrol is a table with no isolation at all.
GRANT SELECT, INSERT, UPDATE, DELETE ON lead_enrichment TO flowx_tenant;

ALTER TABLE lead_enrichment ENABLE ROW LEVEL SECURITY;
ALTER TABLE lead_enrichment FORCE  ROW LEVEL SECURITY;

CREATE POLICY lead_enrichment_tenant_isolation ON lead_enrichment
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
