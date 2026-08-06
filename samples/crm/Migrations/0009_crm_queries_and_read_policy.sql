-- Reading what has been written: a query surface, saved views, and the read half of
-- field-level security.
--
-- WHAT THIS FINISHES. Migration 0007 gave a field a `required_permission` and enforced it on the
-- write, and said in as many words that the read half was not done and why: withholding a field
-- from a reader means filtering it out of every projection that carries it, and a jsonb column
-- travels with its row. That argument is only decisive while there are many projections. There
-- was in fact no read projection at all — a caller could write a custom record and never see one
-- again — so the honest completion is to build exactly one door and mask at it.
--
-- ONE DOOR, AND IT IS QueryCustomRecords. If a second projection of custom values is ever added,
-- it masks or it leaks; CustomFieldPolicy.Mask is the call, and this comment is the reason it
-- exists as a function rather than as three lines inline.

-- ------------------------------------------------------------------ the read half of FLS

-- The scope a caller must hold to see this field, or null when crm.read is enough.
--
-- SEPARATE FROM required_permission, because "may change it" and "may see it" are different
-- questions with different answers. A representative may read a discount they cannot approve; a
-- clerk may write a case note nobody but compliance may read back. Salesforce splits them for the
-- same reason, and collapsing them would make one of the two wrong wherever they differ.
ALTER TABLE custom_field ADD COLUMN read_permission text;

-- ------------------------------------------------------------------ saved views

-- A named query an administrator saved: which object, which children of it, in what order.
--
-- WHY THE FILTER IS THE SAME THREE COLUMNS AGAIN. custom_validation_rule and custom_rollup both
-- hold a (field, operator, value) triple, and this is the third. Factoring them into one
-- `predicate` table was considered and rejected: they have different owners, different lifetimes
-- and different cascade rules, and the join would be there to save nine lines of DDL at the cost
-- of every reader having to follow it. The operators are the same five in all three places, and
-- that is what actually has to stay true — GuardOperator, and ProcessRules.Holds evaluates it.
CREATE TABLE custom_list_view (
    view_id         uuid        NOT NULL PRIMARY KEY,
    tenant_id       text        NOT NULL,
    object_id       uuid        NOT NULL,

    name            text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label           text        NOT NULL,

    filter_field    text,
    filter_operator text CHECK (filter_operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),
    filter_value    text,

    -- Which field to order by, or null for insertion order. Text ordering on a jsonb value, which
    -- is what `->>` gives; a numeric sort would need the field's declared type and is a real gap
    -- rather than an oversight. QueryStore says so where it emits the ORDER BY.
    order_by        text,

    -- Bounded here as well as at the request, because a saved view is what somebody presses a
    -- button on repeatedly.
    row_limit       int         NOT NULL CHECK (row_limit BETWEEN 1 AND 500),

    created_at      timestamptz NOT NULL,

    CHECK (num_nonnulls(filter_field, filter_operator, filter_value) IN (0, 3)),

    UNIQUE (tenant_id, name),
    FOREIGN KEY (object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

CREATE INDEX custom_list_view_by_object ON custom_list_view (object_id);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_list_view TO flowx_tenant;

ALTER TABLE custom_list_view ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_list_view FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_list_view_tenant_isolation ON custom_list_view
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
