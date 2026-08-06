-- What a large B2B organisation does before the quarter starts, and checks every week afterwards.
--
-- THE ONE NUMBER THIS EXISTS FOR. A group sets a number for the year. Sales commits account by
-- account, marketing commits leads by channel. The interesting quantity is not any of those — it
-- is the difference between the number and the sum of what was committed against it. Every
-- planning spreadsheet in the world exists to compute that difference, and every one of them
-- eventually gets a formula that hides it: a rounding, a "stretch" column, a child scaled to make
-- the parent add up. So:
--
--   THE GAP IS REPORTED AND NEVER CLOSED AUTOMATICALLY. `strategy.target_amount` minus the sum of
--   the plans is whatever it is, positive or negative, and this schema has nowhere to put a
--   reconciling adjustment. A planning tool that balanced itself would be a tool that told a board
--   the number was covered when it was not.
--
--   ACTUALS ARE NEVER COPIED ONTO A PLAN. There is no `actual_amount` column here and that is
--   deliberate. Coverage is read from `opportunity` and attainment from `lead`, live, at the
--   moment the roll-up is asked for. A cached actual is a number that is wrong between refreshes
--   and gives nobody a way to know which side of a refresh they are on.
--
-- WHY THREE KINDS IN ONE TABLE. An account plan, an opportunity plan and a lead-generation plan
-- are the same thing to everything that matters here: a commitment, by somebody, for a period,
-- that rolls up. What differs is which column carries the target, and CHECK constraints say which
-- — the shape `custom_field` already uses for "belongs to an entity or to an object, exactly one".

-- ------------------------------------------------------------------ when

CREATE TABLE plan_period (
    period_id  uuid        NOT NULL PRIMARY KEY,
    tenant_id  text        NOT NULL,

    name       text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label      text        NOT NULL,

    starts_on  date        NOT NULL,
    ends_on    date        NOT NULL,

    -- A quarter inside its year. Optional, because a tenant that plans annually should not have to
    -- invent quarters to say so.
    parent_period_id uuid,

    created_at timestamptz NOT NULL,

    CHECK (ends_on >= starts_on),
    UNIQUE (tenant_id, name),
    UNIQUE (period_id, tenant_id),
    FOREIGN KEY (parent_period_id, tenant_id) REFERENCES plan_period (period_id, tenant_id)
);

-- ------------------------------------------------------------------ the number, and the words

-- One per period. The vision is prose on purpose: it is what a leadership team wrote down, and a
-- structured version of it would be a form nobody fills in truthfully.
CREATE TABLE sales_strategy (
    strategy_id   uuid          NOT NULL PRIMARY KEY,
    tenant_id     text          NOT NULL,
    period_id     uuid          NOT NULL,

    vision        text          NOT NULL CHECK (length(vision) BETWEEN 1 AND 4000),
    target_amount numeric(19,4) NOT NULL CHECK (target_amount >= 0),
    currency      text          NOT NULL CHECK (length(currency) = 3),

    created_at    timestamptz   NOT NULL,

    -- One strategy per period, so "the number" is a fact rather than a question about which row.
    UNIQUE (tenant_id, period_id),
    FOREIGN KEY (period_id, tenant_id) REFERENCES plan_period (period_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ what was committed

CREATE TABLE plan (
    plan_id        uuid          NOT NULL PRIMARY KEY,
    tenant_id      text          NOT NULL,
    period_id      uuid          NOT NULL,

    kind           text          NOT NULL
        CHECK (kind IN ('Account', 'Opportunity', 'MarketingLead')),

    name           text          NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label          text          NOT NULL,
    owner_id       uuid          NOT NULL,

    -- Exactly one of these three, by kind.
    account_id     uuid,
    opportunity_id uuid,
    channel        text CHECK (channel IS NULL
        OR channel IN ('Web', 'Referral', 'Event', 'Outbound', 'Partner')),

    -- Free text beside the channel, because a segment is whatever this organisation slices by —
    -- industry, size, region — and a closed list would be this build's opinion about somebody
    -- else's go-to-market.
    segment        text,

    target_amount  numeric(19,4) CHECK (target_amount IS NULL OR target_amount >= 0),
    currency       text          CHECK (currency IS NULL OR length(currency) = 3),
    target_leads   int           CHECK (target_leads IS NULL OR target_leads >= 0),

    created_at     timestamptz   NOT NULL,

    CHECK ((kind = 'Account') = (account_id IS NOT NULL)),
    CHECK ((kind = 'Opportunity') = (opportunity_id IS NOT NULL)),
    CHECK ((kind = 'MarketingLead') = (channel IS NOT NULL)),
    CHECK ((kind = 'MarketingLead') = (target_leads IS NOT NULL)),
    CHECK ((kind = 'MarketingLead') = (target_amount IS NULL)),
    CHECK ((target_amount IS NULL) = (currency IS NULL)),

    UNIQUE (tenant_id, name),
    UNIQUE (plan_id, tenant_id),
    FOREIGN KEY (period_id, tenant_id) REFERENCES plan_period (period_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (account_id, tenant_id) REFERENCES account (account_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (opportunity_id, tenant_id) REFERENCES opportunity (opportunity_id, tenant_id)
        ON DELETE CASCADE
);

CREATE INDEX plan_by_period ON plan (period_id, kind);

-- ------------------------------------------------------------------ how well a deal is understood

-- One row per element of the qualification the organisation uses. The elements are a closed list
-- in `QualificationElement` and not free text: a checklist whose items differ per deal is a
-- checklist no two managers can compare, and comparing them across a pipeline is the entire point.
--
-- `is_answered` and a note rather than a score out of ten. A seller asked to rate their own deal
-- rates it high; asked whether they know who signs it, they either do or they do not.
CREATE TABLE plan_qualification (
    plan_id     uuid NOT NULL,
    tenant_id   text NOT NULL,
    element     text NOT NULL,
    is_answered boolean NOT NULL,
    note        text NOT NULL CHECK (length(note) <= 2000),

    PRIMARY KEY (plan_id, element),
    FOREIGN KEY (plan_id, tenant_id) REFERENCES plan (plan_id, tenant_id) ON DELETE CASCADE
);

-- ------------------------------------------------------------------ what both sides agreed to do

-- The mutual action plan. A step has an owner and a date, and an overdue one is the earliest
-- honest signal that a deal has stopped — earlier than the stage, which a seller moves, and
-- earlier than the close date, which a seller also moves.
CREATE TABLE plan_step (
    plan_id      uuid        NOT NULL,
    tenant_id    text        NOT NULL,
    ordinal      int         NOT NULL CHECK (ordinal >= 0),

    description  text        NOT NULL CHECK (length(description) BETWEEN 1 AND 500),
    owner_id     uuid        NOT NULL,
    due_on       date        NOT NULL,
    completed_at timestamptz,

    PRIMARY KEY (plan_id, ordinal),
    FOREIGN KEY (plan_id, tenant_id) REFERENCES plan (plan_id, tenant_id) ON DELETE CASCADE
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON plan_period        TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON sales_strategy     TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON plan               TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON plan_qualification TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON plan_step          TO flowx_tenant;

ALTER TABLE plan_period ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_period FORCE  ROW LEVEL SECURITY;

CREATE POLICY plan_period_tenant_isolation ON plan_period
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE sales_strategy ENABLE ROW LEVEL SECURITY;
ALTER TABLE sales_strategy FORCE  ROW LEVEL SECURITY;

CREATE POLICY sales_strategy_tenant_isolation ON sales_strategy
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE plan ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan FORCE  ROW LEVEL SECURITY;

CREATE POLICY plan_tenant_isolation ON plan
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE plan_qualification ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_qualification FORCE  ROW LEVEL SECURITY;

CREATE POLICY plan_qualification_tenant_isolation ON plan_qualification
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE plan_step ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_step FORCE  ROW LEVEL SECURITY;

CREATE POLICY plan_step_tenant_isolation ON plan_step
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
