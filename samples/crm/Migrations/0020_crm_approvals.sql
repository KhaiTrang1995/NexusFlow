-- Approvals that an administrator configures, rather than a permission compiled into a route.
--
-- WHAT THIS IS NOT REPLACING. `crm.discount.approve` stays exactly as it is. That grant answers
-- "may this person approve a discount at all", which is an authorisation question and belongs on
-- the capability. What this adds is the orthogonal question a large organisation also asks: *which
-- particular people*, in *what order*, for *this particular quote* — which is configuration, and
-- changes on a Tuesday without a deployment.
--
-- THE CONTROL EVERY HOME-GROWN APPROVAL SYSTEM FORGETS. A submitter must not be able to approve
-- their own request. It is the whole point of the mechanism, it is the first thing an auditor
-- asks, and it is almost never written down — because the happy path works perfectly without it
-- and nothing fails until somebody notices they can. It is enforced in the capability and it has
-- a test whose only job is to fail if it is removed.
--
-- WHY THE ENTRY CRITERIA REUSE GuardOperator. "A quote whose discount is over twenty per cent
-- needs approval" is the same shape as a transition guard, a validation rule, a roll-up filter, a
-- list-view criterion and a territory rule. Six features, one vocabulary, one evaluator —
-- `ProcessRules.Holds` — and so no criterion is ever assembled into SQL.

CREATE TABLE approval_process (
    process_id uuid        NOT NULL PRIMARY KEY,
    tenant_id  text        NOT NULL,

    name       text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label      text        NOT NULL,

    -- What can be submitted. Closed, because each subject's attributes are a fixed list this
    -- build can check a criterion against when the process is saved.
    subject    text        NOT NULL CHECK (subject IN ('Quote', 'Opportunity', 'Plan')),

    -- Lower runs first. Two processes can both apply to a quote — "over twenty per cent" and
    -- "over fifty" — and the first match is the one that governs it.
    priority   int         NOT NULL DEFAULT 100,

    -- Turned off without being deleted, so the requests it already governs keep their history.
    is_active  boolean     NOT NULL DEFAULT true,

    created_at timestamptz NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (process_id, tenant_id)
);

-- Every criterion must hold for the process to apply. AND and not OR, for the reason a territory's
-- rules are: a process whose criteria were alternatives is two processes.
CREATE TABLE approval_criterion (
    process_id uuid NOT NULL,
    tenant_id  text NOT NULL,
    ordinal    int  NOT NULL CHECK (ordinal >= 0),

    attribute  text NOT NULL CHECK (attribute ~ '^[a-z][a-z0-9_]{0,62}$'),
    operator   text NOT NULL
        CHECK (operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),
    value      text NOT NULL,

    PRIMARY KEY (process_id, ordinal),
    FOREIGN KEY (process_id, tenant_id) REFERENCES approval_process (process_id, tenant_id)
        ON DELETE CASCADE
);

-- The people who have to say yes, in the order they are asked.
CREATE TABLE approval_step (
    process_id    uuid NOT NULL,
    tenant_id     text NOT NULL,
    ordinal       int  NOT NULL CHECK (ordinal >= 0),

    label         text NOT NULL,

    -- WHO IS ASKED, AND WHY THIS IS NOT JUST A USER ID. A named person is a step that breaks when
    -- they leave. `SubmittersManager` reads the reporting line 0017 already keeps, so the same
    -- process works for every seller in the company and keeps working after a reorganisation.
    approver_kind text NOT NULL CHECK (approver_kind IN ('Named', 'SubmittersManager', 'RoleHolder')),

    -- A subject for Named, a role for RoleHolder, null for SubmittersManager.
    approver      text,

    PRIMARY KEY (process_id, ordinal),
    CHECK ((approver_kind = 'SubmittersManager') = (approver IS NULL)),
    FOREIGN KEY (process_id, tenant_id) REFERENCES approval_process (process_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ what was actually asked

CREATE TABLE approval_request (
    request_id   uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,
    process_id   uuid        NOT NULL,

    subject      text        NOT NULL CHECK (subject IN ('Quote', 'Opportunity', 'Plan')),
    subject_id   uuid        NOT NULL,

    submitted_by text        NOT NULL,
    submitted_at timestamptz NOT NULL,

    status       text        NOT NULL
        CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Withdrawn')),

    -- Which step is being waited on. The decision surface reads it rather than inferring it from
    -- the decisions, because inferring means two answers when a decision lands twice.
    current_step int         NOT NULL CHECK (current_step >= 0),

    decided_at   timestamptz,

    CHECK ((status = 'Pending') = (decided_at IS NULL)),

    -- One live request per thing. A quote with two pending approvals is a quote whose fate depends
    -- on which one somebody happens to open, and this is what stops it.
    UNIQUE (request_id, tenant_id),
    FOREIGN KEY (process_id, tenant_id) REFERENCES approval_process (process_id, tenant_id)
);

CREATE UNIQUE INDEX approval_request_one_live
    ON approval_request (tenant_id, subject, subject_id)
    WHERE status = 'Pending';

CREATE INDEX approval_request_pending ON approval_request (tenant_id, status, submitted_at);

-- WHY A DECISION IS A ROW AND NOT A COLUMN ON THE REQUEST. The register is the audit: who said
-- yes, when, and what they wrote. A column would keep only the last one, and the question an
-- auditor actually asks — "who approved this, and had they seen the rejection before it" — needs
-- all of them. The primary key is what makes a step undecidable twice.
CREATE TABLE approval_decision (
    request_id uuid        NOT NULL,
    tenant_id  text        NOT NULL,
    ordinal    int         NOT NULL CHECK (ordinal >= 0),

    decided_by text        NOT NULL,
    decision   text        NOT NULL CHECK (decision IN ('Approved', 'Rejected')),
    note       text        NOT NULL CHECK (length(note) <= 2000),
    decided_at timestamptz NOT NULL,

    PRIMARY KEY (request_id, ordinal),
    FOREIGN KEY (request_id, tenant_id) REFERENCES approval_request (request_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON approval_process   TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON approval_criterion TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON approval_step      TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON approval_request   TO flowx_tenant;

-- No UPDATE and no DELETE: a decision is a record of what somebody said, and a register somebody
-- can edit is not one. Append-only by grant rather than by convention.
GRANT SELECT, INSERT ON approval_decision TO flowx_tenant;

ALTER TABLE approval_process ENABLE ROW LEVEL SECURITY;
ALTER TABLE approval_process FORCE  ROW LEVEL SECURITY;

CREATE POLICY approval_process_tenant_isolation ON approval_process
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE approval_criterion ENABLE ROW LEVEL SECURITY;
ALTER TABLE approval_criterion FORCE  ROW LEVEL SECURITY;

CREATE POLICY approval_criterion_tenant_isolation ON approval_criterion
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE approval_step ENABLE ROW LEVEL SECURITY;
ALTER TABLE approval_step FORCE  ROW LEVEL SECURITY;

CREATE POLICY approval_step_tenant_isolation ON approval_step
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE approval_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE approval_request FORCE  ROW LEVEL SECURITY;

CREATE POLICY approval_request_tenant_isolation ON approval_request
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE approval_decision ENABLE ROW LEVEL SECURITY;
ALTER TABLE approval_decision FORCE  ROW LEVEL SECURITY;

CREATE POLICY approval_decision_tenant_isolation ON approval_decision
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
