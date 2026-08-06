-- Cases, and the clock a service organisation is actually measured on.
--
-- WHY THE CLOCK IS THE HARD PART, NOT THE CASE. A case is a row with a status. What makes a
-- service desk a service desk is the promise attached to it — "we answer within four hours" —
-- and almost every home-grown version computes that promise in calendar time. A case raised at
-- half past four on a Friday then breaches a four-hour response target at half past eight on the
-- Friday evening, when nobody was ever meant to be working, and the desk starts Monday already in
-- breach of something it never had a chance to meet. The number is wrong, everybody knows it is
-- wrong, and so the report stops being read.
--
-- So the week is a table. `business_hours` is what the tenant says its desk is open for, and the
-- due dates on a case are worked out by walking a minute budget through those windows. The walk
-- itself is in `BusinessCalendar` — a pure function over a week and an instant, with tests that
-- need no database — because a due date computed in SQL is a due date nobody can unit-test.
--
-- WHY THE DUE DATES ARE STORED AND NOT DERIVED. The policy that governed a case is the one that
-- was in force when it was raised. An administrator lowering the response target on a Tuesday must
-- not retroactively breach every case opened on Monday, which is exactly what recomputing from the
-- live policy would do. The promise is stamped once, at the moment it is made.
--
-- WHY THE CASE NUMBER IS NOT A SEQUENCE. A sequence shared by every tenant leaks volume: a client
-- who opens two cases a week and sees them numbered 4,102 and 4,181 has just learnt how busy
-- somebody else is. The number is allocated per tenant, and the unique constraint is what settles
-- a race rather than a lock.

-- The week the desk is open. One row per day the desk opens at all; a day with no row is a day
-- the clock does not run — which is how a weekend, or a four-day week, is expressed without a
-- flag for either.
CREATE TABLE business_hours (
    tenant_id   text     NOT NULL,

    -- 0 is Sunday, matching both `DayOfWeek` and `extract(dow ...)`. Picked because the two
    -- agreeing means nothing has to be translated at the boundary.
    day_of_week smallint NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),

    opens_at    time     NOT NULL,
    closes_at   time     NOT NULL,

    PRIMARY KEY (tenant_id, day_of_week),

    -- A window that closes before it opens is one that never elapses, and a minute budget walked
    -- through it would loop for ever. Refused here rather than defended against in the walk.
    CHECK (closes_at > opens_at)
);

CREATE TABLE sla_policy (
    policy_id              uuid    NOT NULL PRIMARY KEY,
    tenant_id              text    NOT NULL,

    name                   text    NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label                  text    NOT NULL,

    -- Which cases it governs. One policy per priority, which is why this is unique rather than a
    -- criteria list: "the urgent promise" is a single thing a desk can say out loud.
    priority               text    NOT NULL
        CHECK (priority IN ('Low', 'Normal', 'High', 'Urgent')),

    first_response_minutes int     NOT NULL CHECK (first_response_minutes > 0),
    resolution_minutes     int     NOT NULL CHECK (resolution_minutes > 0),

    -- False means the clock runs around the clock, which is the right answer for a desk that
    -- staffs nights and the wrong one for every desk that does not.
    business_hours_only    boolean NOT NULL DEFAULT true,

    is_active              boolean NOT NULL DEFAULT true,

    -- Resolution cannot be sooner than the response it contains. A policy promising a fix in an
    -- hour and an acknowledgement in four is not a stricter promise, it is an unmeetable one.
    CHECK (resolution_minutes >= first_response_minutes),

    UNIQUE (tenant_id, name),
    UNIQUE (policy_id, tenant_id)
);

-- One live policy per priority. Two would mean a case's promise depended on which row a query
-- happened to read first.
CREATE UNIQUE INDEX sla_policy_one_per_priority
    ON sla_policy (tenant_id, priority)
    WHERE is_active;

CREATE TABLE support_case (
    case_id                uuid        NOT NULL PRIMARY KEY,
    tenant_id              text        NOT NULL,

    -- What a person says on the telephone. Per tenant, for the reason at the top of this file.
    case_number            bigint      NOT NULL CHECK (case_number > 0),

    account_id             uuid        NOT NULL,
    contact_id             uuid,

    subject                text        NOT NULL CHECK (length(subject) BETWEEN 1 AND 200),
    description            text        NOT NULL CHECK (length(description) <= 8000),

    status                 text        NOT NULL
        CHECK (status IN ('New', 'Working', 'Waiting', 'Escalated', 'Closed')),
    priority               text        NOT NULL
        CHECK (priority IN ('Low', 'Normal', 'High', 'Urgent')),
    origin                 text        NOT NULL
        CHECK (origin IN ('Email', 'Phone', 'Web', 'Chat')),

    owner_id               text        NOT NULL,
    opened_by              text        NOT NULL,
    opened_at              timestamptz NOT NULL,

    -- The policy that was in force when the promise was made, and the promise itself. Null when
    -- the tenant has configured no policy for the priority, which is a legitimate state and not a
    -- missing row: a desk may promise nothing about its low-priority queue.
    sla_policy_id          uuid,
    first_response_due_at  timestamptz,
    resolution_due_at      timestamptz,

    -- When somebody other than the person who raised it first said something the customer can
    -- see. An internal note is not a response, and a desk that counted one would report a first
    -- response time of nine seconds for every case.
    first_responded_at     timestamptz,

    closed_at              timestamptz,

    CHECK ((status = 'Closed') = (closed_at IS NOT NULL)),

    -- A promise needs a policy to have come from. Kept together so a case can never carry a due
    -- date nobody can explain.
    CHECK ((sla_policy_id IS NULL) = (first_response_due_at IS NULL)),
    CHECK ((first_response_due_at IS NULL) = (resolution_due_at IS NULL)),

    UNIQUE (tenant_id, case_number),
    UNIQUE (case_id, tenant_id),

    FOREIGN KEY (account_id, tenant_id) REFERENCES account (account_id, tenant_id),
    FOREIGN KEY (contact_id, tenant_id) REFERENCES contact (contact_id, tenant_id),
    FOREIGN KEY (sla_policy_id, tenant_id) REFERENCES sla_policy (policy_id, tenant_id)
);

-- The queue a console opens on: what is still live, worst promise first.
CREATE INDEX support_case_open ON support_case (tenant_id, status, resolution_due_at)
    WHERE status <> 'Closed';

CREATE INDEX support_case_by_account ON support_case (tenant_id, account_id);

-- WHY A COMMENT IS APPEND-ONLY. What was said to a customer is a record of what was said to a
-- customer. A desk that can edit yesterday's reply cannot answer the only question a complaint
-- ever asks, which is what they were actually told.
CREATE TABLE case_comment (
    case_id    uuid        NOT NULL,
    tenant_id  text        NOT NULL,
    ordinal    int         NOT NULL CHECK (ordinal >= 0),

    author_id  text        NOT NULL,
    body       text        NOT NULL CHECK (length(body) BETWEEN 1 AND 8000),

    -- Public means the customer sees it, and only a public comment stops the response clock.
    is_public  boolean     NOT NULL,

    created_at timestamptz NOT NULL,

    PRIMARY KEY (case_id, ordinal),
    FOREIGN KEY (case_id, tenant_id) REFERENCES support_case (case_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON business_hours TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON sla_policy     TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON support_case   TO flowx_tenant;

-- No UPDATE and no DELETE, for the reason above the table.
GRANT SELECT, INSERT ON case_comment TO flowx_tenant;

ALTER TABLE business_hours ENABLE ROW LEVEL SECURITY;
ALTER TABLE business_hours FORCE  ROW LEVEL SECURITY;

CREATE POLICY business_hours_tenant_isolation ON business_hours
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE sla_policy ENABLE ROW LEVEL SECURITY;
ALTER TABLE sla_policy FORCE  ROW LEVEL SECURITY;

CREATE POLICY sla_policy_tenant_isolation ON sla_policy
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE support_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE support_case FORCE  ROW LEVEL SECURITY;

CREATE POLICY support_case_tenant_isolation ON support_case
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE case_comment ENABLE ROW LEVEL SECURITY;
ALTER TABLE case_comment FORCE  ROW LEVEL SECURITY;

CREATE POLICY case_comment_tenant_isolation ON case_comment
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
