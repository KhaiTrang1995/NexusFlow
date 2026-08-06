-- Work a caller submits and comes back for: bulk import and bulk export.
--
-- WHY THESE ARE JOBS AND NOT REQUESTS. Ten thousand rows is not a request. Doing it inline holds
-- one HTTP connection, one database transaction and one caller's patience for as long as the
-- slowest row takes, and a client that times out at sixty seconds cannot tell a slow import from
-- a lost one. Submitting work and reading its progress is the only shape in which "how far did it
-- get" is a question with an answer.
--
-- WHAT MAKES THIS RELIABLE RATHER THAN MERELY ASYNCHRONOUS. Three things, and none of them is a
-- retry count:
--
--   PROGRESS IS DURABLE, NOT IN MEMORY. `processed` is the row count already dealt with, written
--   with the chunk that dealt with them. A sweeper that dies resumes at `processed` rather than
--   at zero, so a job is not a thing that has to succeed in one attempt.
--
--   A ROW'S IDENTITY IS DERIVED, NOT MINTED. The record an imported row becomes is keyed on
--   (job_id, ordinal), so re-running a chunk writes the same ids and the insert is a no-op. That
--   is what makes the crash *between* writing a chunk and recording it cost nothing — which is
--   the crash that would otherwise duplicate every row in the chunk, and the one nobody tests for.
--
--   THE JOB CARRIES THE SUBMITTER'S AUTHORITY. `scopes` is what the caller held when they
--   submitted, and the sweep decides on that and never on its own. A sweeper running as itself
--   would let anyone export a field they cannot read by asking a background process to read it.

CREATE TABLE bulk_job (
    job_id      uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    kind        text        NOT NULL CHECK (kind IN ('Import', 'Export')),
    object_id   uuid        NOT NULL,

    status      text        NOT NULL CHECK (status IN ('Pending', 'Succeeded', 'Failed')),

    -- How many rows the job is, how many are dealt with, and how many of those were refused.
    -- `failed` counts rows, not attempts: a job of 10 000 rows with 3 bad ones succeeded, and a
    -- caller needs to be told which 3 rather than that something went wrong.
    total       int         NOT NULL CHECK (total >= 0),
    processed   int         NOT NULL DEFAULT 0,
    failed      int         NOT NULL DEFAULT 0,

    -- Attempts at a *chunk*, reset whenever one succeeds. A job that cannot make progress stops
    -- rather than being retried until somebody notices the queue.
    attempts    int         NOT NULL DEFAULT 0,

    -- What the caller holds, frozen at submission. See the note above.
    scopes      text[]      NOT NULL,

    -- The rows for an import; the criteria for an export.
    request     jsonb       NOT NULL,

    -- The rows for an export, once it has run. Null until then and for an import, because an
    -- import's outcome is `failed` and the error rows, not a document.
    result      jsonb,

    created_at  timestamptz NOT NULL,
    finished_at timestamptz,

    CHECK (processed <= total),
    CHECK ((status IN ('Succeeded', 'Failed')) = (finished_at IS NOT NULL)),

    UNIQUE (job_id, tenant_id),
    FOREIGN KEY (object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

-- The sweep's claim reads exactly this: unfinished work, oldest first.
CREATE INDEX bulk_job_pending ON bulk_job (tenant_id, created_at) WHERE status = 'Pending';

-- Which rows were refused and why. A row of its own rather than a message on the job, because a
-- caller fixing a spreadsheet needs the ordinal to find the line — an import that reports "3 rows
-- failed" and not which three is an import somebody redoes by hand.
CREATE TABLE bulk_job_error (
    job_id    uuid NOT NULL,
    tenant_id text NOT NULL,
    ordinal   int  NOT NULL CHECK (ordinal >= 0),
    message   text NOT NULL,

    PRIMARY KEY (job_id, ordinal),
    FOREIGN KEY (job_id, tenant_id) REFERENCES bulk_job (job_id, tenant_id) ON DELETE CASCADE
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON bulk_job       TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON bulk_job_error TO flowx_tenant;

ALTER TABLE bulk_job ENABLE ROW LEVEL SECURITY;
ALTER TABLE bulk_job FORCE  ROW LEVEL SECURITY;

CREATE POLICY bulk_job_tenant_isolation ON bulk_job
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE bulk_job_error ENABLE ROW LEVEL SECURITY;
ALTER TABLE bulk_job_error FORCE  ROW LEVEL SECURITY;

CREATE POLICY bulk_job_error_tenant_isolation ON bulk_job_error
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
