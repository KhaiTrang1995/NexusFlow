-- Global search, and filters with more than one criterion.
--
-- ------------------------------------------------------------------ the search index
--
-- WHY A GENERATED COLUMN AND NOT A SEARCH TABLE. The obvious design is a `search_document` table
-- the application writes on every insert, and it has one failure mode that dominates all others:
-- it drifts. A row written by a repair script, by a future capability, or by an administrator
-- with a psql session is a row the index never hears about, and a search that silently omits a
-- record is worse than one that does not exist — nobody can tell the difference from the outside.
--
-- GENERATED ALWAYS AS ... STORED removes the failure mode rather than testing for it. There is no
-- maintenance code anywhere in this repository for these columns, and there cannot be: PostgreSQL
-- computes them inside the same write, so the index is a property of the row rather than of
-- whether somebody remembered.
--
-- 'simple' AND NOT 'english'. A stemmer belongs to a language, and these rows hold company names
-- and site codes across whatever languages a tenant sells in. 'simple' lower-cases and splits and
-- does nothing else, which is right for identifiers and names; a per-tenant language column and a
-- configuration chosen from it is the real answer and is not built.
--
-- WHAT IS NOT INDEXED: email addresses. `Lead.Email` carries [Sensitive] and the outbox redaction
-- exists to keep it off the wire; putting it in a searchable index would make it retrievable by
-- prefix to anybody who may search, which is a wider audience than anybody who may read the lead.

ALTER TABLE lead ADD COLUMN search_document tsvector
    GENERATED ALWAYS AS (to_tsvector('simple',
        coalesce(company, '') || ' ' || coalesce(contact_name, '') || ' ' || coalesce(source, ''))) STORED;

ALTER TABLE account ADD COLUMN search_document tsvector
    GENERATED ALWAYS AS (to_tsvector('simple',
        coalesce(name, '') || ' ' || coalesce(industry, '') || ' ' || coalesce(region, ''))) STORED;

ALTER TABLE contact ADD COLUMN search_document tsvector
    GENERATED ALWAYS AS (to_tsvector('simple', coalesce(full_name, ''))) STORED;

ALTER TABLE opportunity ADD COLUMN search_document tsvector
    GENERATED ALWAYS AS (to_tsvector('simple', coalesce(name, ''))) STORED;

-- to_tsvector(regconfig, jsonb) reads every string value of the document and is immutable, which
-- is what lets a generated column use it. So a custom object's records are searchable on whatever
-- an administrator invented, with no code that knows which fields those are.
ALTER TABLE custom_record ADD COLUMN search_document tsvector
    GENERATED ALWAYS AS (to_tsvector('simple', values)) STORED;

CREATE INDEX lead_search        ON lead        USING gin (search_document);
CREATE INDEX account_search     ON account     USING gin (search_document);
CREATE INDEX contact_search     ON contact     USING gin (search_document);
CREATE INDEX opportunity_search ON opportunity USING gin (search_document);
CREATE INDEX custom_record_search ON custom_record USING gin (search_document);

-- ------------------------------------------------------------------ compound filters

-- How the criteria of one view combine.
--
-- TWO CONNECTIVES AND NO NESTING, deliberately. `(a AND b) OR (c AND d)` is a tree, and a tree is
-- a parser, an evaluator and a precedence rule — which is the expression language ProcessRules
-- spends its remarks refusing. A flat list joined by one connective covers what a list view
-- actually needs and stays something the publish-time check can read in one pass.
ALTER TABLE custom_list_view ADD COLUMN match_mode text NOT NULL DEFAULT 'All'
    CHECK (match_mode IN ('All', 'Any'));

-- One criterion of a view's filter.
--
-- The three columns are the same (field, operator, value) triple custom_validation_rule and
-- custom_rollup hold, and the operators are the same five GuardOperator declares. That agreement
-- is the whole reason none of these is a language: one vocabulary, evaluated by
-- ProcessRules.Holds in the process and by the same CASE in SQL.
CREATE TABLE custom_filter_criterion (
    criterion_id uuid NOT NULL PRIMARY KEY,
    tenant_id    text NOT NULL,
    view_id      uuid NOT NULL,

    -- The order they were written in. Not significant to the answer — bool_and and bool_or are
    -- commutative — and significant to the reader who has to check the view says what they meant.
    ordinal      int  NOT NULL,

    field        text NOT NULL,
    operator     text NOT NULL
        CHECK (operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),
    value        text NOT NULL,

    UNIQUE (view_id, ordinal),
    FOREIGN KEY (view_id) REFERENCES custom_list_view (view_id) ON DELETE CASCADE
);

CREATE INDEX custom_filter_criterion_by_view ON custom_filter_criterion (view_id, ordinal);

-- The single-criterion columns of 0009 stay where they are, holding what was already saved. A
-- view now carries either those three or a set of criteria; QueryStore reads the criteria and
-- falls back to the columns, so a view saved before this migration keeps working and one saved
-- after it uses the table. Migrating the old rows into the new table is a data migration this
-- sample does not need to demonstrate twice.

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_filter_criterion TO flowx_tenant;

ALTER TABLE custom_filter_criterion ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_filter_criterion FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_filter_criterion_tenant_isolation ON custom_filter_criterion
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
