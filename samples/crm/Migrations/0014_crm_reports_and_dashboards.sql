-- What sales, marketing and operations actually ask the database, saved so that they stop asking
-- an engineer: a grouped aggregate, and an ordered set of them on one screen.
--
-- WHY THIS IS A CLOSED VOCABULARY AND NOT A QUERY LANGUAGE. Every reporting feature ends up at the
-- same fork. One road is an expression the user writes, which is a parser, an evaluator, a
-- sandbox, an injection surface and a support queue. The other is a fixed set of sources,
-- dimensions and measures, where the user's choice is a *value* — bound as a parameter, never
-- concatenated into an identifier position — and the statement is a constant the build can see.
--
-- This takes the second road, for the same reason `GuardOperator` did: what a person actually
-- wants from a report is "opportunities by stage, totalled" far more often than an arbitrary
-- expression, and the closed version can be checked when it is saved rather than when it is run.
-- `SqlFitnessTests` is what makes the choice binding rather than a preference.
--
-- WHAT THE FOUR SOURCES ARE FOR. Opportunity is the sales pipeline, Lead is the marketing funnel,
-- Activity is the operations queue, and CustomObject is whatever this tenant invented. The three
-- built-in ones have real columns and therefore a fixed list of dimensions; the custom one reads
-- a jsonb key, where the dimension can be a bound value because it already is one.

CREATE TABLE custom_report (
    report_id   uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    name        text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label       text        NOT NULL,

    source      text        NOT NULL
        CHECK (source IN ('Opportunity', 'Lead', 'Activity', 'CustomObject')),

    -- Set for a CustomObject report and null for the other three, which is what the CHECK says.
    object_id   uuid,

    -- What to group by. One of the source's dimensions for a built-in source, or a field name for
    -- a custom object. Validated against the source when the report is saved — a report that
    -- names a dimension its source does not have is a report that fails on a dashboard at nine in
    -- the morning rather than at the moment somebody wrote it.
    dimension   text        NOT NULL,

    measure     text        NOT NULL CHECK (measure IN ('Count', 'Sum', 'Average', 'Min', 'Max')),

    -- What to aggregate. Null for Count, which needs nothing; required for the other four.
    measure_of  text,

    created_at  timestamptz NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (report_id, tenant_id),
    CHECK ((source = 'CustomObject') = (object_id IS NOT NULL)),
    CHECK ((measure = 'Count') = (measure_of IS NULL)),
    FOREIGN KEY (object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ the screen

CREATE TABLE dashboard (
    dashboard_id uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,

    name         text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label        text        NOT NULL,
    created_at   timestamptz NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (dashboard_id, tenant_id)
);

-- A report on a dashboard, at a position. The foreign key is what stops a dashboard outliving the
-- report it shows: a tile pointing at nothing is a panel that renders an error every morning, and
-- an administrator deleting a report is entitled to be told what is using it.
CREATE TABLE dashboard_tile (
    dashboard_id uuid NOT NULL,
    tenant_id    text NOT NULL,
    ordinal      int  NOT NULL CHECK (ordinal >= 0),
    report_id    uuid NOT NULL,

    PRIMARY KEY (dashboard_id, ordinal),
    FOREIGN KEY (dashboard_id, tenant_id) REFERENCES dashboard (dashboard_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (report_id, tenant_id) REFERENCES custom_report (report_id, tenant_id)
        ON DELETE RESTRICT
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_report   TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON dashboard       TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON dashboard_tile  TO flowx_tenant;

ALTER TABLE custom_report ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_report FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_report_tenant_isolation ON custom_report
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE dashboard ENABLE ROW LEVEL SECURITY;
ALTER TABLE dashboard FORCE  ROW LEVEL SECURITY;

CREATE POLICY dashboard_tenant_isolation ON dashboard
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE dashboard_tile ENABLE ROW LEVEL SECURITY;
ALTER TABLE dashboard_tile FORCE  ROW LEVEL SECURITY;

CREATE POLICY dashboard_tile_tenant_isolation ON dashboard_tile
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
