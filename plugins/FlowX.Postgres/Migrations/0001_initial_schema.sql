-- FlowX journal, leases, outbox and retention — the initial schema.
--
-- ADR-0015 fixes the shape: one append-only row per (instance_id, scope, step_id, attempt),
-- a fence checked on every write, a sub-flow child as its own instance row, and one
-- transaction spanning the step row, the instance update and the outbox rows.
--
-- Two columns depart from the ERD drawn in docs/11-Distributed-Runtime.md §2, and both
-- departures are load-bearing rather than cosmetic. They are recorded in
-- docs/adr/ADR-0016-postgres-journal-adapter.md.
--
--   1. Every payload column is `json`, not `jsonb`. `jsonb` is a parsed representation:
--      it reorders object keys, re-renders whitespace and silently discards duplicate
--      keys. ADR-0015 commitment 5 says payloads are written through the generated
--      System.Text.Json context, and the conformance suite reads that as "what the
--      context produced is what comes back". `jsonb` cannot honour it. `json` stores the
--      document verbatim.
--
--   2. flow_lease.instance_id carries no foreign key to flow_instance. The ERD makes it
--      `PK,FK`, which cannot hold: a lease is acquired *before* the instance row exists —
--      FlowInstanceStart.Token is "the token of the lease held while starting" — so the
--      FK is inverted in time. It would also make a lease store unusable for an instance
--      whose journal lives in a different store, which is the arrangement ILeaseStore's
--      own remarks describe.

CREATE TABLE flow_instance (
    instance_id         uuid        NOT NULL,
    flow_id             text        NOT NULL,
    flow_version        text        NOT NULL,
    tenant_id           text,
    state               text        NOT NULL,
    -- The highest fencing token this instance has been shown. A write below it is
    -- refused. Zero is FencingToken.None: no lease has ever been acquired.
    fence               bigint      NOT NULL DEFAULT 0,
    -- The next value flow_step.sequence takes. Instance-local commit order, handed out
    -- under the row lock the commit already takes, so it cannot skip or collide.
    next_sequence       bigint      NOT NULL DEFAULT 1,
    -- A denormalised hint for operator queries and nothing else. The engine derives the
    -- resume position from committed rows (ADR-0015 commitment 2).
    resume_from_step    int,
    input               json,
    state_bag           json,
    correlation_id      text        NOT NULL DEFAULT '',
    trace_id            text,
    deadline_at         timestamptz,
    -- Deliberately unconstrained by a foreign key. A Detached sub-flow can outlive its
    -- parent, so retention can archive a parent while a child is still running, and
    -- ADR-0015 requires an orphan to be legible rather than a foreign-key error.
    parent_instance_id  uuid,
    parent_scope        text        NOT NULL DEFAULT '',
    parent_step_id      int,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    version             bigint      NOT NULL DEFAULT 0,

    CONSTRAINT flow_instance_pkey PRIMARY KEY (instance_id),
    -- The closed set from FlowInstanceState. Adding a member is a schema change rather
    -- than an enum edit, which is what this constraint exists to make true.
    CONSTRAINT flow_instance_state_check CHECK (state IN (
        'Pending', 'Running', 'Suspended', 'Compensating',
        'Completed', 'Failed', 'TimedOut', 'CompensationFailed')),
    CONSTRAINT flow_instance_fence_check CHECK (fence >= 0),
    CONSTRAINT flow_instance_parent_step_check CHECK (parent_step_id IS NULL OR parent_step_id >= 0)
);

CREATE INDEX flow_instance_tenant_state_idx ON flow_instance (tenant_id, state);

CREATE INDEX flow_instance_parent_idx ON flow_instance (parent_instance_id)
    WHERE parent_instance_id IS NOT NULL;

-- The append-only history. Rows are inserted and never updated.
CREATE TABLE flow_step (
    instance_id        uuid        NOT NULL,
    -- The IterationScope chain as text: '' for the flow body, '7' for the eighth element
    -- of a ForEach, '7/2' for an element of a loop nested inside it. In the key because
    -- (instance, step) stopped being unique when ForEach shipped.
    scope              text        NOT NULL,
    step_id            int         NOT NULL,
    attempt            int         NOT NULL,
    sequence           bigint      NOT NULL,
    capability_id      text        NOT NULL,
    capability_version text        NOT NULL,
    outcome            text        NOT NULL,
    result             json,
    nondeterministic   json,
    duration_ms        bigint      NOT NULL DEFAULT 0,
    committed_at       timestamptz NOT NULL DEFAULT now(),

    -- ADR-0015 commitment 1. This is the constraint that has to reject a duplicate; it is
    -- what an append-only table means in a database rather than in a dictionary.
    CONSTRAINT flow_step_pkey PRIMARY KEY (instance_id, scope, step_id, attempt),
    CONSTRAINT flow_step_instance_fkey FOREIGN KEY (instance_id)
        REFERENCES flow_instance (instance_id) ON DELETE CASCADE,
    CONSTRAINT flow_step_attempt_check CHECK (attempt >= 1),
    CONSTRAINT flow_step_step_check CHECK (step_id >= 0),
    CONSTRAINT flow_step_outcome_check CHECK (outcome IN ('Success', 'Failure', 'Compensated'))
);

-- Commit order is read back through this, and it is unique so two rows can never claim
-- the same position in one instance's history.
CREATE UNIQUE INDEX flow_step_sequence_idx ON flow_step (instance_id, sequence);

-- Events staged in the same transaction as the step that emitted them. Publishing,
-- retrying and marking published belong to the publisher (WP-56), not here.
CREATE TABLE outbox_event (
    event_id       uuid   NOT NULL,
    instance_id    uuid   NOT NULL,
    -- The sequence of the step that staged it, and the position within that step, so
    -- "in commit order" survives a read that did not observe the writes.
    sequence       bigint NOT NULL,
    ordinal        int    NOT NULL,
    type           text   NOT NULL,
    schema_version text   NOT NULL,
    partition_key  text,
    payload        json,
    published_at   timestamptz,

    CONSTRAINT outbox_event_pkey PRIMARY KEY (event_id),
    CONSTRAINT outbox_event_instance_fkey FOREIGN KEY (instance_id)
        REFERENCES flow_instance (instance_id) ON DELETE CASCADE
);

CREATE INDEX outbox_event_instance_idx ON outbox_event (instance_id, sequence, ordinal);

CREATE INDEX outbox_event_pending_idx ON outbox_event (published_at) WHERE published_at IS NULL;

-- Time-bounded exclusive ownership, with the token that makes exclusivity true when a
-- clock is wrong.
CREATE TABLE flow_lease (
    instance_id   uuid        NOT NULL,
    owner_node    text        NOT NULL,
    -- Monotonic per instance and never restarted. The row survives release and expiry
    -- precisely so this counter does: a store that deleted the row on release would hand
    -- a returning zombie a token equal to its successor's.
    fencing_token bigint      NOT NULL,
    expires_at    timestamptz NOT NULL,

    CONSTRAINT flow_lease_pkey PRIMARY KEY (instance_id),
    CONSTRAINT flow_lease_token_check CHECK (fencing_token >= 1)
);

CREATE INDEX flow_lease_expiry_idx ON flow_lease (expires_at);

-- The retention policy from docs/11-Distributed-Runtime.md §2, as data rather than as a
-- table in a document. A policy nothing can read is a policy nothing applies.
CREATE TABLE retention_policy (
    -- '*' is the default that applies to every flow without its own row.
    flow_id     text NOT NULL,
    state_class text NOT NULL,
    -- NULL means "not on a timer" — a Suspended instance is kept until it completes or
    -- its deadline passes, which is a question about the instance, not about the clock.
    retain_for  interval,

    CONSTRAINT retention_policy_pkey PRIMARY KEY (flow_id, state_class),
    CONSTRAINT retention_policy_class_check CHECK (state_class IN (
        'Completed', 'Failed', 'Suspended', 'OutboxPublished')),
    CONSTRAINT retention_policy_window_check CHECK (retain_for IS NULL OR retain_for >= interval '0')
);

INSERT INTO retention_policy (flow_id, state_class, retain_for) VALUES
    ('*', 'Completed',       interval '30 days'),
    ('*', 'Failed',          interval '180 days'),
    ('*', 'Suspended',       NULL),
    ('*', 'OutboxPublished', interval '7 days');
