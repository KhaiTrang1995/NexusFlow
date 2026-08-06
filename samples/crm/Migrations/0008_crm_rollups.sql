-- Roll-up summary fields: a field on a parent whose value is an aggregate over its children.
--
-- WHICH END IS THE PARENT, and it is decided by migration 0006's trigger rather than by a name.
-- custom_link_cardinality refuses a second link naming the same `to_record_id` when a
-- relationship is OneToMany — so a `to` record has at most one link, and a `from` record may have
-- many. `from` is the parent, `to` is the child. Stating it here because "one to many" reads
-- either way round and a roll-up that got it backwards would aggregate one row for ever.
--
-- WHEN IT IS RECOMPUTED, and what that does not cover. The value is written when a link is made,
-- which is complete for this sample's write surface: a record is created, then linked, and there
-- is no capability that changes a record's values afterwards. Add one and it must recompute the
-- parents of what it touched — RollupStore.RecomputeAsync is the call, and there is exactly one
-- place that would need it. That is a real limit and it is written down rather than discovered.
--
-- WHY MATERIALISED AND NOT COMPUTED ON READ. A view would always be correct and could not be
-- guarded on: ProcessFacts reads `custom_fields` out of the row, and a transition guard over a
-- roll-up is the thing an administrator actually wants — "advance when the account has more than
-- three open sites". Storing it puts the value where every existing reader already looks.

CREATE TABLE custom_rollup (
    rollup_id       uuid NOT NULL PRIMARY KEY,
    tenant_id       text NOT NULL,

    -- The field on the parent object that holds the answer. Declared like any other field, and
    -- marked computed below so nobody can write it by hand.
    field_id        uuid NOT NULL,

    -- The edge to walk. Its `from` object must be the one the field belongs to; the capability
    -- checks that, because a roll-up over an unrelated edge would silently aggregate nothing.
    relationship_id uuid NOT NULL,

    -- Closed, because each is a SQL aggregate this build emits. An open set would be a string
    -- nothing dispatches on.
    aggregate       text NOT NULL CHECK (aggregate IN ('Count', 'Sum', 'Min', 'Max', 'Average')),

    -- Which field of the child to aggregate. Null for Count, which counts rows rather than
    -- values, and required for the other four — the CHECK is what says so.
    source_field_id uuid,

    -- An optional filter on the child, in the same vocabulary a guard and a validation rule use.
    -- Null together or set together.
    filter_field    text,
    filter_operator text CHECK (filter_operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),
    filter_value    text,

    created_at      timestamptz NOT NULL,

    CHECK ((aggregate = 'Count') = (source_field_id IS NULL)),
    CHECK (num_nonnulls(filter_field, filter_operator, filter_value) IN (0, 3)),

    -- One roll-up per field. A field with two would have two answers and no rule about which
    -- wins, which is a defect nobody would find until the numbers disagreed.
    UNIQUE (field_id),

    FOREIGN KEY (field_id) REFERENCES custom_field (field_id) ON DELETE CASCADE,
    FOREIGN KEY (source_field_id) REFERENCES custom_field (field_id) ON DELETE CASCADE,
    FOREIGN KEY (relationship_id, tenant_id)
        REFERENCES custom_relationship (relationship_id, tenant_id) ON DELETE CASCADE
);

CREATE INDEX custom_rollup_by_relationship ON custom_rollup (relationship_id);

-- ------------------------------------------------------------------ computed fields

-- Whether this build writes the field rather than a caller.
--
-- A roll-up a caller can write is a lie: the next recompute overwrites it, so the write appears
-- to succeed and silently does nothing. CustomFieldPolicy refuses it by name instead.
ALTER TABLE custom_field ADD COLUMN is_computed boolean NOT NULL DEFAULT false;

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_rollup TO flowx_tenant;

ALTER TABLE custom_rollup ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_rollup FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_rollup_tenant_isolation ON custom_rollup
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
