-- Who owns which accounts, what number each person carries, and what a part-year seller carries.
--
-- WHY A TERRITORY IS RULES AND NOT A LIST. The obvious model is a list of account ids per person,
-- and it is wrong for the same reason a hard-coded schema is: every new account has to be assigned
-- by somebody, so the list is always one import behind, and nobody can say which accounts are in
-- no territory at all. Rules answer both — a new account routes the moment it exists, and "which
-- accounts match nothing" is a question with an answer.
--
-- WHY THE RULES REUSE GuardOperator. The five operators are already shared by transition guards,
-- validation rules, roll-up filters and list-view criteria, and `ProcessRules.Holds` is the single
-- evaluator. A sixth vocabulary for routing would be a sixth thing to explain and a fifth place
-- for the comparison of "9" and "42" to be got wrong. Routing evaluates in the application against
-- the same function, so no rule is ever spliced into SQL.
--
-- WHY QUOTA IS NOT THE SAME AS A PLAN. A plan is committed bottom-up by the person who owns it; a
-- quota is assigned top-down by the person above them. They are different numbers on purpose, and
-- the difference between them is itself what a sales-operations review is about: a seller carrying
-- 500 who has committed 380 has a 120 hole that no roll-up of commitments can show, because every
-- commitment in it is real.

CREATE TABLE territory (
    territory_id uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,

    name         text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label        text        NOT NULL,

    -- Territories nest, like plans and like the reporting line. EMEA contains DACH contains
    -- Bavaria, and a coverage question is asked at each level.
    parent_territory_id uuid,

    -- Lower runs first. Two territories can both match an account — "German manufacturing" and
    -- "German" — and without an order the winner would be whichever the planner happened to
    -- return, which is a routing that changes when the statistics do.
    priority     int         NOT NULL DEFAULT 100,

    created_at   timestamptz NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (territory_id, tenant_id),
    CHECK (parent_territory_id IS DISTINCT FROM territory_id),
    FOREIGN KEY (parent_territory_id, tenant_id) REFERENCES territory (territory_id, tenant_id)
);

CREATE INDEX territory_by_priority ON territory (tenant_id, priority, name);

-- Every rule of a territory must hold for it to match. AND and not OR: a territory whose rules
-- were alternatives would be two territories, and saying so is cheaper than a connective.
CREATE TABLE territory_rule (
    territory_id uuid NOT NULL,
    tenant_id    text NOT NULL,
    ordinal      int  NOT NULL CHECK (ordinal >= 0),

    -- What the rule is about. An account and a lead have different attributes, so the subject
    -- decides which closed list the attribute is checked against.
    subject      text NOT NULL CHECK (subject IN ('Account', 'Lead')),

    attribute    text NOT NULL CHECK (attribute ~ '^[a-z][a-z0-9_]{0,62}$'),

    -- The same five as everywhere else. GuardOperator, and ProcessRules.Holds evaluates it.
    operator     text NOT NULL
        CHECK (operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),

    value        text NOT NULL,

    PRIMARY KEY (territory_id, ordinal),
    FOREIGN KEY (territory_id, tenant_id) REFERENCES territory (territory_id, tenant_id)
        ON DELETE CASCADE
);

-- Who covers a territory. More than one person may: a named-account team is three people on one
-- patch, and a model that allowed only one would make the second an exception somebody works
-- around with a second territory.
CREATE TABLE territory_assignment (
    territory_id uuid NOT NULL,
    tenant_id    text NOT NULL,
    user_id      text NOT NULL,

    PRIMARY KEY (territory_id, user_id),
    FOREIGN KEY (territory_id, tenant_id) REFERENCES territory (territory_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, user_id) REFERENCES org_member (tenant_id, user_id) ON DELETE CASCADE
);

-- ------------------------------------------------------------------ what each person carries

CREATE TABLE quota (
    quota_id    uuid          NOT NULL PRIMARY KEY,
    tenant_id   text          NOT NULL,
    period_id   uuid          NOT NULL,
    user_id     text          NOT NULL,

    measure     text          NOT NULL CHECK (measure IN ('Revenue', 'Leads', 'Activities')),
    target      numeric(19,4) NOT NULL CHECK (target >= 0),

    -- THE CAPACITY MODEL, AND IT IS ONE NUMBER. A seller who joined in the second month of a
    -- quarter does not carry the whole quarter, and a model that pretended otherwise reports every
    -- new hire as failing for their first two reviews. One fraction of the period, applied to the
    -- target — which is what a ramp actually is once the spreadsheet is thrown away.
    ramp_factor numeric(5,4)  NOT NULL DEFAULT 1.0
        CHECK (ramp_factor > 0 AND ramp_factor <= 1),

    created_at  timestamptz   NOT NULL,

    -- One quota per person per measure per period, so "their number" is a fact rather than a
    -- question about which row.
    UNIQUE (tenant_id, period_id, user_id, measure),
    FOREIGN KEY (period_id, tenant_id) REFERENCES plan_period (period_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, user_id) REFERENCES org_member (tenant_id, user_id) ON DELETE CASCADE
);

CREATE INDEX quota_by_period ON quota (period_id, measure);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON territory            TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON territory_rule       TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON territory_assignment TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON quota                TO flowx_tenant;

ALTER TABLE territory ENABLE ROW LEVEL SECURITY;
ALTER TABLE territory FORCE  ROW LEVEL SECURITY;

CREATE POLICY territory_tenant_isolation ON territory
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE territory_rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE territory_rule FORCE  ROW LEVEL SECURITY;

CREATE POLICY territory_rule_tenant_isolation ON territory_rule
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE territory_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE territory_assignment FORCE  ROW LEVEL SECURITY;

CREATE POLICY territory_assignment_tenant_isolation ON territory_assignment
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE quota ENABLE ROW LEVEL SECURITY;
ALTER TABLE quota FORCE  ROW LEVEL SECURITY;

CREATE POLICY quota_tenant_isolation ON quota
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
