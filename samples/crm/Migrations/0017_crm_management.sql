-- Who reports to whom, what a plan actually contains, and the numbers a leadership team reviews.
--
-- WHY THE REPORTING LINE IS HERE AT ALL. A director asking "how is my organisation doing" and a
-- manager asking the same question want different answers, and the difference is not a permission
-- — both may read. It is a *scope*: whose plans are in the total. Without a line, the only two
-- answers available are "mine" and "the whole tenant", and a company with nine sales managers
-- gets neither of the ones it wanted.
--
-- WHY OWNERSHIP MOVES FROM uuid TO text IN THIS MIGRATION. 0016 gave a plan a `uuid` owner. That
-- was wrong and it is being corrected rather than lived with: the only identity this system has
-- for a person is their subject claim, which is text, so a uuid owner is a field that resolves to
-- nobody. It looked fine, which is why it needed fixing before anything depended on it.

ALTER TABLE plan      ALTER COLUMN owner_id TYPE text USING owner_id::text;
ALTER TABLE plan_step ALTER COLUMN owner_id TYPE text USING owner_id::text;

-- ------------------------------------------------------------------ who reports to whom

CREATE TABLE org_member (
    tenant_id    text NOT NULL,

    -- The subject claim. The same string a token carries, so a plan's owner and a person are one
    -- identity rather than two that have to be kept in step.
    user_id      text NOT NULL CHECK (length(user_id) BETWEEN 1 AND 200),

    display_name text NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),

    -- What this person sees by default, which the tenant sets. A Representative sees their own
    -- plans, a Manager their line's, a Director the tenant's.
    role         text NOT NULL CHECK (role IN ('Representative', 'Manager', 'Director')),

    -- Their manager, or null at the top. Self-reference guarded below; longer cycles cannot be
    -- prevented by a constraint and are broken by the recursive read instead, which is where the
    -- comment about them lives.
    reports_to   text,

    PRIMARY KEY (tenant_id, user_id),
    CHECK (reports_to IS NULL OR reports_to <> user_id),
    FOREIGN KEY (tenant_id, reports_to) REFERENCES org_member (tenant_id, user_id)
);

CREATE INDEX org_member_by_manager ON org_member (tenant_id, reports_to);

-- ------------------------------------------------------------------ what an account plan says

-- WHAT AN ACCOUNT PLAN IS FOR, AND WHY A TARGET IS NOT ENOUGH. A number against an account says
-- what is wanted and nothing about how. These three tables are the "how": what is to be achieved,
-- who has to agree to it, and what could stop it.

CREATE TABLE plan_objective (
    plan_id     uuid          NOT NULL,
    tenant_id   text          NOT NULL,
    ordinal     int           NOT NULL CHECK (ordinal >= 0),

    description text          NOT NULL CHECK (length(description) BETWEEN 1 AND 500),

    -- What "done" is measured in. A closed list, because an objective measured in whatever
    -- somebody typed is an objective no two account plans can be compared on — and comparing them
    -- is what a quarterly review is.
    measure     text          NOT NULL
        CHECK (measure IN ('Revenue', 'Deals', 'Meetings', 'Products', 'Referrals')),

    target      numeric(19,4) NOT NULL CHECK (target >= 0),
    status      text          NOT NULL
        CHECK (status IN ('NotStarted', 'InProgress', 'Achieved', 'Abandoned')),

    PRIMARY KEY (plan_id, ordinal),
    FOREIGN KEY (plan_id, tenant_id) REFERENCES plan (plan_id, tenant_id) ON DELETE CASCADE
);

-- The relationship map. In a large B2B sale the question that loses deals is not "what do they
-- need" but "who has not agreed yet", and a map with a sentiment against each name is the only
-- form of that question a manager can review in two minutes.
CREATE TABLE account_stakeholder (
    plan_id    uuid NOT NULL,
    tenant_id  text NOT NULL,
    contact_id uuid NOT NULL,

    role       text NOT NULL
        CHECK (role IN ('EconomicBuyer', 'Champion', 'Influencer', 'User', 'Blocker', 'Legal',
                        'Procurement', 'Technical')),

    sentiment  text NOT NULL CHECK (sentiment IN ('Advocate', 'Supportive', 'Neutral', 'Sceptical', 'Opposed')),

    -- One to five. A scale rather than a flag, because "who matters most" is the ordering a
    -- coverage review actually walks.
    influence  int  NOT NULL CHECK (influence BETWEEN 1 AND 5),

    PRIMARY KEY (plan_id, contact_id),
    FOREIGN KEY (plan_id, tenant_id) REFERENCES plan (plan_id, tenant_id) ON DELETE CASCADE,
    FOREIGN KEY (contact_id, tenant_id) REFERENCES contact (contact_id, tenant_id) ON DELETE CASCADE
);

-- The risk register. Open risks are what a review is for; closed ones are what makes the register
-- worth keeping, because a risk that was raised and dealt with is the evidence that raising them
-- works.
CREATE TABLE plan_risk (
    plan_id     uuid NOT NULL,
    tenant_id   text NOT NULL,
    ordinal     int  NOT NULL CHECK (ordinal >= 0),

    description text NOT NULL CHECK (length(description) BETWEEN 1 AND 500),
    severity    text NOT NULL CHECK (severity IN ('Low', 'Medium', 'High', 'Critical')),
    mitigation  text NOT NULL CHECK (length(mitigation) <= 1000),
    is_open     boolean NOT NULL,

    PRIMARY KEY (plan_id, ordinal),
    FOREIGN KEY (plan_id, tenant_id) REFERENCES plan (plan_id, tenant_id) ON DELETE CASCADE
);

-- ------------------------------------------------------------------ the numbers reviewed

-- WHY A KPI IS A DEFINITION AND NOT A NUMBER. A stored number is a number that was true once. A
-- KPI here is a *source* — one of a closed list this build knows how to compute — plus a target
-- and a direction, so the actual is read live every time it is asked for and the target is the
-- only thing a person maintains.
CREATE TABLE kpi (
    kpi_id     uuid          NOT NULL PRIMARY KEY,
    tenant_id  text          NOT NULL,

    name       text          NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label      text          NOT NULL,

    source     text          NOT NULL
        CHECK (source IN ('OpenPipeline', 'WonRevenue', 'LeadsCaptured', 'OpenTasks',
                          'OverduePlanSteps')),

    target     numeric(19,4) NOT NULL CHECK (target >= 0),

    -- Which way is good. Without it a target of "5 overdue steps" and one of "5 million in
    -- pipeline" would be scored the same way, and one of the two answers would be exactly wrong.
    direction  text          NOT NULL CHECK (direction IN ('HigherIsBetter', 'LowerIsBetter')),

    created_at timestamptz   NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (kpi_id, tenant_id)
);

-- A review is a minute of a meeting, and that is why it *does* store the actual — unlike a plan,
-- which must not. The distinction is what the number means: a plan's cached actual claims to be
-- current and is wrong between refreshes; a review's actual claims only to be what the number was
-- when somebody wrote the commentary next to it, which is exactly what a minute is for.
CREATE TABLE kpi_review (
    kpi_id      uuid          NOT NULL,
    tenant_id   text          NOT NULL,
    period_id   uuid          NOT NULL,

    reviewed_at timestamptz   NOT NULL,
    reviewed_by text          NOT NULL,

    actual      numeric(19,4) NOT NULL,
    commentary  text          NOT NULL CHECK (length(commentary) BETWEEN 1 AND 4000),

    PRIMARY KEY (kpi_id, period_id),
    FOREIGN KEY (kpi_id, tenant_id) REFERENCES kpi (kpi_id, tenant_id) ON DELETE CASCADE,
    FOREIGN KEY (period_id, tenant_id) REFERENCES plan_period (period_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON org_member          TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON plan_objective      TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON account_stakeholder TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON plan_risk           TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON kpi                 TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON kpi_review          TO flowx_tenant;

ALTER TABLE org_member ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_member FORCE  ROW LEVEL SECURITY;

CREATE POLICY org_member_tenant_isolation ON org_member
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE plan_objective ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_objective FORCE  ROW LEVEL SECURITY;

CREATE POLICY plan_objective_tenant_isolation ON plan_objective
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE account_stakeholder ENABLE ROW LEVEL SECURITY;
ALTER TABLE account_stakeholder FORCE  ROW LEVEL SECURITY;

CREATE POLICY account_stakeholder_tenant_isolation ON account_stakeholder
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE plan_risk ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_risk FORCE  ROW LEVEL SECURITY;

CREATE POLICY plan_risk_tenant_isolation ON plan_risk
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE kpi ENABLE ROW LEVEL SECURITY;
ALTER TABLE kpi FORCE  ROW LEVEL SECURITY;

CREATE POLICY kpi_tenant_isolation ON kpi
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE kpi_review ENABLE ROW LEVEL SECURITY;
ALTER TABLE kpi_review FORCE  ROW LEVEL SECURITY;

CREATE POLICY kpi_review_tenant_isolation ON kpi_review
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
