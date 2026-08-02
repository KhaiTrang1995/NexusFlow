-- The CRM's own tables: docs/26-CRM-Sample.md §6, as DDL.
--
-- These are the *sample's* tables, not the platform's, which is why they are here and not in
-- plugins/FlowX.Postgres/Migrations. That directory is the journal's schema — flow_instance,
-- flow_step, outbox_event and the policy stores — and PostgresMigrator.Migrations is the list
-- every FlowX deployment applies at start-up. Putting eleven CRM tables in it would give
-- samples/banking and samples/healthcare an opportunity pipeline they have no use for, and
-- would move PostgresMigrator.TargetVersion for a change that belongs to one sample. Sample
-- tables, sample migrator, sample ledger (crm_schema_migration); the two schedules are
-- independent and neither blocks the other.
--
-- Three departures from §6's ER diagram, each stated here because a reader comparing the two
-- will otherwise think the file is wrong.
--
--   1. tenant_id IS NOT NULL. §6 draws it nullable, as flow_instance's is, because the journal
--      must serve single-tenant deployments whose rows carry no tenant at all. This sample
--      declares tenancy mandatory (Program.cs sets TenantIsolation.Row and every caller's tid
--      claim supplies one), so a CRM row with no tenant is a row nothing could ever have
--      written. NOT NULL is also what makes the composite foreign keys below enforceable: a
--      MATCH SIMPLE reference containing a NULL is not checked at all, so a nullable tenant_id
--      would turn every one of them into decoration.
--
--   2. QUOTE_LINE carries a currency. §6 gives it `numeric unit_price` and no currency column.
--      §5.2 is the argument against that in this document's own words — "the event-driven
--      sample already found what happens when a currency is hard-coded: four transports agreed
--      with each other and all four were wrong. Amounts here are never bare decimals." A line
--      whose currency has to be inferred from its quote is a bare decimal with an extra join.
--
--   3. LEAD carries converted_at. §5.1 puts `DateTimeOffset At` on ConvertedTo and §6's LEAD
--      has the three converted_* ids without it, so the value object could not be
--      materialised. One column, and the record in Contracts.cs is whole.
--
-- What is *not* departed from: the five child tables of §6 that carry no tenant_id — quote_line,
-- process_stage, process_transition, transition_guard, transition_action — do not gain one.
-- §6's prose says every table carries tenant_id and §6's diagram does not, and migration 0008
-- of the platform settles the disagreement: "it would be a denormalisation that can disagree
-- with the instance row it copies". They inherit their tenant through the foreign key, and
-- 0002 restates the predicate through it.

-- ---------------------------------------------------------------- the configurable process

-- §5.4. Versioned, and the versioning is the whole of what §6's second note asks for: an
-- administrator publishes a new version rather than editing the one in use, and the partial
-- unique index below is what stops two of them being active for the same entity kind at once.
--
-- An in-flight opportunity stays on the version it started with, and there is no pinning
-- column for it because none is needed: OPPORTUNITY.stage_id points at a PROCESS_STAGE, a
-- stage belongs to exactly one PROCESS_DEFINITION, and publishing version n+1 creates new
-- stage rows. The opportunity's own foreign key is the pin, the same way flow_instance pins
-- flow_version by carrying it.
CREATE TABLE process_definition (
    process_id   uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,
    applies_to   text        NOT NULL CHECK (applies_to IN ('Lead', 'Account', 'Contact', 'Opportunity')),
    version      int         NOT NULL CHECK (version >= 1),
    is_active    boolean     NOT NULL DEFAULT false,
    published_at timestamptz NOT NULL,

    UNIQUE (process_id, tenant_id),
    UNIQUE (tenant_id, applies_to, version)
);

-- One active definition per tenant per entity kind, and no more.
--
-- Partial rather than a plain unique over (tenant_id, applies_to, is_active): every superseded
-- version is inactive, so a total index would allow exactly one of those too, and an
-- administrator publishing a fourth version could not keep the three before it.
--
-- NULLS NOT DISTINCT is stated even though tenant_id is NOT NULL. It costs nothing, and it is
-- what stops this index quietly permitting two active definitions on the day somebody relaxes
-- that column — the index would still be here and would no longer mean what its name says.
CREATE UNIQUE INDEX process_definition_one_active_per_kind
    ON process_definition (tenant_id, applies_to) NULLS NOT DISTINCT
    WHERE is_active;

CREATE TABLE process_stage (
    stage_id    uuid    NOT NULL PRIMARY KEY,
    process_id  uuid    NOT NULL REFERENCES process_definition (process_id) ON DELETE CASCADE,
    name        text    NOT NULL,
    ordinal     int     NOT NULL,
    is_terminal boolean NOT NULL DEFAULT false,

    UNIQUE (process_id, ordinal)
);

CREATE INDEX process_stage_by_process ON process_stage (process_id);

CREATE TABLE process_transition (
    transition_id uuid NOT NULL PRIMARY KEY,
    from_stage_id uuid NOT NULL REFERENCES process_stage (stage_id) ON DELETE CASCADE,
    to_stage_id   uuid NOT NULL REFERENCES process_stage (stage_id) ON DELETE CASCADE,
    trigger       text NOT NULL,
    ordinal       int  NOT NULL,

    -- A transition out of a stage and back into it is a loop nothing can leave, and a
    -- configured process that contains one is a support call rather than a bug report.
    CHECK (from_stage_id <> to_stage_id)
);

CREATE INDEX process_transition_by_from_stage ON process_transition (from_stage_id);

-- §5.4's five operators, closed. §7.4 is why it is five and not an expression language, and
-- the CHECK is what makes "closed" a property of the data rather than of the code that happens
-- to write it.
CREATE TABLE transition_guard (
    guard_id      uuid NOT NULL PRIMARY KEY,
    transition_id uuid NOT NULL REFERENCES process_transition (transition_id) ON DELETE CASCADE,
    field         text NOT NULL,
    operator      text NOT NULL CHECK (operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),
    value         text NOT NULL
);

CREATE INDEX transition_guard_by_transition ON transition_guard (transition_id);

-- §5.4's five action kinds, closed, and §7.2 is why a sixth needs a build. The CHECK says the
-- same thing to the database that the enum says to the compiler.
CREATE TABLE transition_action (
    action_id     uuid  NOT NULL PRIMARY KEY,
    transition_id uuid  NOT NULL REFERENCES process_transition (transition_id) ON DELETE CASCADE,
    kind          text  NOT NULL CHECK (kind IN ('CreateTask', 'SendNotification', 'SetField', 'RequestApproval', 'EmitEvent')),
    parameters    jsonb NOT NULL DEFAULT '{}'::jsonb,
    ordinal       int   NOT NULL
);

CREATE INDEX transition_action_by_transition ON transition_action (transition_id);

-- ---------------------------------------------------------------------------- the parties

-- §5.1. There is no customer table and that is the decision: a customer is an ACCOUNT whose
-- lifecycle has reached 'Customer'. The view at the foot of this file is what a report that
-- wants one reads.
CREATE TABLE account (
    account_id uuid NOT NULL PRIMARY KEY,
    tenant_id  text NOT NULL,
    name       text NOT NULL,
    industry   text NOT NULL,
    lifecycle  text NOT NULL CHECK (lifecycle IN ('Prospect', 'Customer', 'Churned')),
    region     text NOT NULL,
    owner_id   uuid NOT NULL,

    -- Not redundant with the primary key. It is what the composite foreign keys below
    -- reference, and it is why a contact cannot point at another tenant's account.
    UNIQUE (account_id, tenant_id)
);

CREATE INDEX account_by_tenant_lifecycle ON account (tenant_id, lifecycle);

CREATE TABLE contact (
    contact_id uuid    NOT NULL PRIMARY KEY,
    tenant_id  text    NOT NULL,
    account_id uuid    NOT NULL,
    full_name  text    NOT NULL,

    -- Both carry [Sensitive] on Contact in Contracts.cs. That is a statement about every
    -- FlowX artifact — journal row, outbox row, audit record, problem document — and not
    -- about this column: what is written here is written here. §5.1 says as much.
    email      text,
    phone      text,
    is_primary boolean NOT NULL DEFAULT false,

    UNIQUE (contact_id, tenant_id),
    FOREIGN KEY (account_id, tenant_id) REFERENCES account (account_id, tenant_id)
);

CREATE INDEX contact_by_account ON contact (account_id);

-- ---------------------------------------------------------------------------- the pipeline

CREATE TABLE opportunity (
    opportunity_id     uuid          NOT NULL PRIMARY KEY,
    tenant_id          text          NOT NULL,
    account_id         uuid          NOT NULL,
    primary_contact_id uuid          NOT NULL,
    name               text          NOT NULL,
    amount             numeric(19,4) NOT NULL,
    currency           text          NOT NULL,
    stage_id           uuid          NOT NULL REFERENCES process_stage (stage_id),
    probability        int           NOT NULL CHECK (probability BETWEEN 0 AND 100),
    expected_close     date          NOT NULL,
    owner_id           uuid          NOT NULL,
    outcome            text          CHECK (outcome IS NULL OR outcome IN ('Won', 'Lost', 'Abandoned')),
    stage_entered_at   timestamptz   NOT NULL,

    UNIQUE (opportunity_id, tenant_id),
    FOREIGN KEY (account_id, tenant_id)         REFERENCES account (account_id, tenant_id),
    FOREIGN KEY (primary_contact_id, tenant_id) REFERENCES contact (contact_id, tenant_id)
);

CREATE INDEX opportunity_by_account ON opportunity (account_id);
CREATE INDEX opportunity_by_stage   ON opportunity (stage_id);

-- §5.1. The three converted_* columns are ConvertedTo, and they are nullable together: a lead
-- that has not been converted has none of them. The composite references are what make
-- "converted into another tenant's account" unrepresentable rather than merely unlikely.
CREATE TABLE lead (
    lead_id                  uuid        NOT NULL PRIMARY KEY,
    tenant_id                text        NOT NULL,
    company                  text        NOT NULL,
    contact_name             text        NOT NULL,

    -- [Sensitive] on Lead.Email, for Contact.Email's reason.
    email                    text,
    source                   text        NOT NULL CHECK (source IN ('Web', 'Referral', 'Event', 'Outbound', 'Partner')),
    status                   text        NOT NULL CHECK (status IN ('New', 'Working', 'Qualified', 'Disqualified', 'Converted')),
    score                    int         NOT NULL DEFAULT 0 CHECK (score BETWEEN 0 AND 100),
    owner_id                 uuid,
    captured_at              timestamptz NOT NULL,
    converted_account_id     uuid,
    converted_contact_id     uuid,
    converted_opportunity_id uuid,
    converted_at             timestamptz,

    FOREIGN KEY (converted_account_id, tenant_id)     REFERENCES account (account_id, tenant_id),
    FOREIGN KEY (converted_contact_id, tenant_id)     REFERENCES contact (contact_id, tenant_id),
    FOREIGN KEY (converted_opportunity_id, tenant_id) REFERENCES opportunity (opportunity_id, tenant_id),

    -- Converted is all four or none. A lead in status 'Converted' with no account is the state
    -- the compensating saga in §8.1 exists to avoid leaving behind, and this is what says so
    -- to the database rather than to a reviewer.
    CHECK (
        (converted_account_id IS NULL AND converted_contact_id IS NULL
            AND converted_opportunity_id IS NULL AND converted_at IS NULL)
        OR (converted_account_id IS NOT NULL AND converted_contact_id IS NOT NULL
            AND converted_opportunity_id IS NOT NULL AND converted_at IS NOT NULL))
);

CREATE INDEX lead_by_tenant_status ON lead (tenant_id, status);

CREATE TABLE quote (
    quote_id       uuid          NOT NULL PRIMARY KEY,
    tenant_id      text          NOT NULL,
    opportunity_id uuid          NOT NULL,
    status         text          NOT NULL CHECK (status IN ('Draft', 'Issued', 'Accepted', 'Rejected', 'Expired')),
    subtotal       numeric(19,4) NOT NULL,
    discount       numeric(19,4) NOT NULL DEFAULT 0,
    total          numeric(19,4) NOT NULL,
    currency       text          NOT NULL,
    valid_until    timestamptz   NOT NULL,

    -- Null until a manager approves. §5.2: the discount threshold is the sample's second
    -- authorisation stance, and it is Permission("crm.discount.approve") rather than a column
    -- constraint — a rule about who may act is not a rule about what may be stored.
    approved_by    uuid,

    UNIQUE (quote_id, tenant_id),
    FOREIGN KEY (opportunity_id, tenant_id) REFERENCES opportunity (opportunity_id, tenant_id)
);

CREATE INDEX quote_by_opportunity ON quote (opportunity_id);

CREATE TABLE quote_line (
    quote_line_id uuid          NOT NULL PRIMARY KEY,
    quote_id      uuid          NOT NULL REFERENCES quote (quote_id) ON DELETE CASCADE,
    sku           text          NOT NULL,
    quantity      int           NOT NULL CHECK (quantity > 0),
    unit_price    numeric(19,4) NOT NULL,
    currency      text          NOT NULL
);

CREATE INDEX quote_line_by_quote ON quote_line (quote_id);

CREATE TABLE sales_order (
    order_id   uuid          NOT NULL PRIMARY KEY,
    tenant_id  text          NOT NULL,
    quote_id   uuid          NOT NULL,
    account_id uuid          NOT NULL,
    status     text          NOT NULL CHECK (status IN ('Placed', 'Fulfilled', 'Cancelled')),
    total      numeric(19,4) NOT NULL,
    currency   text          NOT NULL,
    placed_at  timestamptz   NOT NULL,

    -- §6 draws QUOTE |o--o| SALES_ORDER: at most one order per quote.
    UNIQUE (quote_id),
    FOREIGN KEY (quote_id, tenant_id)   REFERENCES quote (quote_id, tenant_id),
    FOREIGN KEY (account_id, tenant_id) REFERENCES account (account_id, tenant_id)
);

CREATE INDEX sales_order_by_account ON sales_order (account_id);

-- --------------------------------------------------------------------------------- work

-- §5.3. relates_to_kind + relates_to_id is a discriminated reference with four possible
-- parents and therefore no foreign key. §6 takes that over four nullable columns because the
-- alternative makes every query against activities read four columns to find the one that is
-- set; what it costs is that integrity is a trigger, and migration 0003 is that trigger.
CREATE TABLE activity (
    activity_id      uuid        NOT NULL PRIMARY KEY,
    tenant_id        text        NOT NULL,
    kind             text        NOT NULL CHECK (kind IN ('Task', 'Call', 'Meeting', 'Note')),
    subject          text        NOT NULL,
    relates_to_kind  text        NOT NULL CHECK (relates_to_kind IN ('Lead', 'Account', 'Contact', 'Opportunity')),
    relates_to_id    uuid        NOT NULL,
    owner_id         uuid        NOT NULL,
    due_at           timestamptz,
    status           text        NOT NULL CHECK (status IN ('Open', 'Completed', 'Cancelled', 'Escalated')),
    completed_at     timestamptz,
    escalation_count int         NOT NULL DEFAULT 0 CHECK (escalation_count >= 0),

    -- A completed activity has an instant it completed at, and nothing else does.
    CHECK ((status = 'Completed') = (completed_at IS NOT NULL))
);

CREATE INDEX activity_by_parent ON activity (tenant_id, relates_to_kind, relates_to_id);

-- The index the SLA timer in §8.4 reads: open activities whose deadline has passed. Partial,
-- because a completed task is never a candidate and there are eventually many more of those.
CREATE INDEX activity_overdue ON activity (tenant_id, due_at) WHERE status = 'Open';

-- ---------------------------------------------------------------------------- the read model

-- §5.1's "the read model in §6 exposes customer_account as a view for the reports that want
-- one". One row per account that has become a customer, with the same identity it had as a
-- prospect — which is the point: becoming a customer is a transition, not a move.
--
-- security_invoker, and it is load-bearing. A view without it runs its query as the view's
-- OWNER, which is the role that ran this migration; the account table's policies would then be
-- evaluated for that role rather than for the caller, and a scoped connection would read
-- through the view what the policy refuses it on the table. FORCE ROW LEVEL SECURITY in 0002
-- keeps the owner under the policy, so the two together would still filter by
-- flowx.tenant_id — but resting a tenant boundary on that composition is resting it on a
-- reader knowing both halves. Invoker rights make the view transparent instead.
CREATE VIEW customer_account WITH (security_invoker = true) AS
    SELECT account_id, tenant_id, name, industry, region, owner_id
      FROM account
     WHERE lifecycle = 'Customer';
