-- Two things a CRM is not customisable without, and one it is not integrable without.
--
-- PICKLISTS. The most-used custom field type there is, and the reason is not fashion: a field
-- whose values are a closed, ordered, labelled set is the only kind an administrator can build a
-- report or a guard on and be sure of the answer. Free text with a convention is the same field
-- with the closure removed.
--
-- REFERENCES. A field that points at a record of another object. custom_relationship already
-- models an edge as a row of its own, which is right for many-to-many and for an edge with its
-- own attributes; a reference is the other shape — one value on the record, resolved by reading
-- the record. Both exist because collapsing them means either a link table for every lookup or a
-- jsonb key for every many-to-many.
--
-- CONNECTORS. A tenant names the systems it sends to, and the configured process names one when
-- a transition fires. What makes this a connector registry rather than a URL in a parameter is
-- that the endpoint, the enablement and the delivery record live in tables an administrator can
-- see: a delivery that failed is a row with an error on it, not a line in a log.

-- ------------------------------------------------------------------ picklists and references

-- A field may now be a Picklist or a Reference, which the 0005 CHECK did not allow. A CHECK
-- constraint cannot be extended in place, so it is dropped and rewritten — the constraint name
-- is the one PostgreSQL generated for the column CHECK in 0005.
ALTER TABLE custom_field DROP CONSTRAINT custom_field_data_type_check;

ALTER TABLE custom_field ADD CONSTRAINT custom_field_data_type_check
    CHECK (data_type IN ('Text', 'Number', 'Boolean', 'Date', 'Picklist', 'Reference'));

-- What a Reference points at. Null for every other type, which is what the CHECK below says:
-- a Reference with no target is a field nothing can resolve, and a Text field with one is a
-- target nothing reads.
ALTER TABLE custom_field ADD COLUMN references_object_id uuid;

ALTER TABLE custom_field ADD CONSTRAINT custom_field_reference_has_a_target
    CHECK ((data_type = 'Reference') = (references_object_id IS NOT NULL));

ALTER TABLE custom_field ADD CONSTRAINT custom_field_reference_fkey
    FOREIGN KEY (references_object_id, tenant_id)
    REFERENCES custom_object (object_id, tenant_id) ON DELETE CASCADE;

-- One allowed value of a picklist.
--
-- A table and not a jsonb array on custom_field, because these are read by name to validate a
-- write, ordered to render a list, and relabelled without changing what is stored. All three are
-- rows doing what rows do; an array would make the second and third a rewrite of the whole field.
CREATE TABLE custom_field_option (
    option_id uuid NOT NULL PRIMARY KEY,
    tenant_id text NOT NULL,
    field_id  uuid NOT NULL,

    -- What is stored on the record. Constrained like a name, for the same reason: it lands in a
    -- jsonb value that a guard compares as text.
    value     text NOT NULL CHECK (value ~ '^[a-z][a-z0-9_]{0,62}$'),

    -- What a person sees. Free text, because nothing compares it.
    label     text NOT NULL,
    ordinal   int  NOT NULL,

    UNIQUE (field_id, value),
    FOREIGN KEY (field_id) REFERENCES custom_field (field_id) ON DELETE CASCADE
);

CREATE INDEX custom_field_option_by_field ON custom_field_option (field_id, ordinal);

-- ------------------------------------------------------------------ connectors

-- A system this tenant sends to.
--
-- No credential column. A secret in a row is a secret in every backup, every replica and every
-- support session; `secret_name` is the name of one held wherever the deployment holds secrets,
-- and this table records which one to fetch rather than what it is.
CREATE TABLE connector (
    connector_id uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,
    name         text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),

    -- What kind of thing is on the other end. Closed, because each kind is a shape this build
    -- knows how to render a payload for — an open set would be a string nothing dispatches on.
    kind         text        NOT NULL CHECK (kind IN ('Webhook', 'Slack', 'Email', 'Salesforce', 'HubSpot')),

    endpoint     text        NOT NULL,
    secret_name  text,

    -- Disabled rather than deleted is how an integration is turned off: the deliveries already
    -- recorded against it stay readable, which is the whole reason anybody looks.
    is_enabled   boolean     NOT NULL,
    created_at   timestamptz NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (connector_id, tenant_id)
);

-- One thing to send, and what happened to it.
--
-- WHY A TABLE AND NOT A DIRECT CALL. A transition that POSTed to a connector inline would hold a
-- database transaction open across somebody else's network, fail the transition when their
-- gateway is slow, and lose the notification when it is down. The row is written in the
-- transition's own transaction and drained afterwards — the same argument the platform's outbox
-- makes, for the same reason, at the sample's own level.
CREATE TABLE connector_delivery (
    delivery_id  uuid        NOT NULL PRIMARY KEY,
    tenant_id    text        NOT NULL,
    connector_id uuid        NOT NULL,

    -- What it is about, in the administrator's words. Free text: it is rendered, never matched.
    subject      text        NOT NULL,
    payload      jsonb       NOT NULL,

    status       text        NOT NULL CHECK (status IN ('Pending', 'Delivered', 'Failed')),
    attempts     int         NOT NULL CHECK (attempts >= 0),
    created_at   timestamptz NOT NULL,
    delivered_at timestamptz,
    last_error   text,

    -- Delivered is the instant and nothing else; Failed carries why. A row that claimed both, or
    -- neither, would be a delivery nobody could answer a question about.
    CHECK (
        (status = 'Delivered' AND delivered_at IS NOT NULL AND last_error IS NULL)
        OR (status = 'Failed'    AND delivered_at IS NULL)
        OR (status = 'Pending'   AND delivered_at IS NULL AND last_error IS NULL)),

    FOREIGN KEY (connector_id, tenant_id) REFERENCES connector (connector_id, tenant_id)
        ON DELETE CASCADE
);

-- The sweep reads exactly this: what is still owed, oldest first.
CREATE INDEX connector_delivery_pending ON connector_delivery (created_at)
    WHERE status = 'Pending';

CREATE INDEX connector_delivery_by_connector ON connector_delivery (connector_id, created_at);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_field_option  TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON connector            TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON connector_delivery   TO flowx_tenant;

ALTER TABLE custom_field_option ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_field_option FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_field_option_tenant_isolation ON custom_field_option
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE connector ENABLE ROW LEVEL SECURITY;
ALTER TABLE connector FORCE  ROW LEVEL SECURITY;

CREATE POLICY connector_tenant_isolation ON connector
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE connector_delivery ENABLE ROW LEVEL SECURITY;
ALTER TABLE connector_delivery FORCE  ROW LEVEL SECURITY;

CREATE POLICY connector_delivery_tenant_isolation ON connector_delivery
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
