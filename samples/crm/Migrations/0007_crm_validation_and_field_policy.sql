-- Four things an administrator can do to a field without a deployment: constrain what a record
-- may say, decide who may write it, require it to be unique, and keep what it used to be.
--
-- WHAT THESE HAVE IN COMMON. Each is a rule about data that a tenant writes down and the
-- application then has to obey — the same shape as custom_field itself. What makes them
-- tractable rather than an embedded language is that each reuses a vocabulary already closed
-- somewhere else: the validation operators are GuardOperator, which ProcessRules already
-- evaluates and ProcessPublishing already refuses at publish time; the permission is a scope
-- string the capability stance already understands.

-- ------------------------------------------------------------------ validation rules
--
-- A rule REFUSES when its condition holds. That is Salesforce's polarity and it is the useful
-- one: an administrator writes down what is wrong, not the negation of everything that is right.
-- `amount > 100000 AND approver IS NULL` is a sentence somebody can check; its inverse is not.
--
-- One field per rule, deliberately. Two fields joined by AND is a second rule; joined by OR it is
-- a language, and the argument ProcessRules makes about a general expression engine applies here
-- for the same reasons — an untyped second execution engine, invisible to the manifest, whose
-- mistakes surface at three in the morning instead of at publish time.
CREATE TABLE custom_validation_rule (
    rule_id     uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    -- The same two owners custom_field has, and the same rule about naming exactly one.
    applies_to  text        CHECK (applies_to IN ('Lead', 'Account', 'Contact', 'Opportunity')),
    object_id   uuid,

    name        text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),

    -- What it reads. A declared field, or one of the built-in ones on a built-in entity;
    -- CustomValidationRules.Validate refuses anything else when the rule is declared.
    field       text        NOT NULL,

    -- GuardOperator, and the CHECK is what keeps the two in step.
    operator    text        NOT NULL
        CHECK (operator IN ('Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet')),

    value       text        NOT NULL,

    -- What the caller is told. The whole reason a rule is worth having over a CHECK constraint:
    -- an administrator writes the sentence the person who tripped it reads.
    message     text        NOT NULL CHECK (length(message) BETWEEN 1 AND 500),

    is_active   boolean     NOT NULL,
    created_at  timestamptz NOT NULL,

    CHECK ((applies_to IS NULL) <> (object_id IS NULL)),

    UNIQUE (tenant_id, name),
    FOREIGN KEY (object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

CREATE INDEX custom_validation_rule_by_entity ON custom_validation_rule (tenant_id, applies_to)
    WHERE applies_to IS NOT NULL AND is_active;

CREATE INDEX custom_validation_rule_by_object ON custom_validation_rule (object_id)
    WHERE object_id IS NOT NULL AND is_active;

-- ------------------------------------------------------------------ field-level security

-- The scope a caller must hold to write this field, or null when crm.write is enough.
--
-- WRITE AND NOT READ, and the difference is worth being honest about. Withholding a field from a
-- reader means filtering it out of every projection that carries it — the jsonb column travels
-- with its row, so a read rule that is not applied everywhere is a read rule that leaks the first
-- time somebody adds a projection. Restricting the write is a single check at a single door, and
-- it is the half that stops a representative changing a field their manager owns. Read-side
-- masking is a real feature and it is not this one; CustomFieldPolicy says so where it is
-- enforced rather than implying the column does more than it does.
ALTER TABLE custom_field ADD COLUMN required_permission text;

-- ------------------------------------------------------------------ unique fields

ALTER TABLE custom_field ADD COLUMN is_unique boolean NOT NULL DEFAULT false;

-- WHY A TABLE AND NOT A UNIQUE INDEX. A unique index over `(object_id, (values->>'code'))` is the
-- obvious answer and it needs one index per declared field — which is DDL, per tenant, at run
-- time, and every argument migration 0005 makes against that applies here. A row per claimed
-- value gives the same guarantee from one static index: the uniqueness is the primary key.
--
-- The cost accepted is that this table and `custom_record.values` can disagree if a writer
-- updates one and not the other. Every writer goes through CustomSchemaStore, and the claim is
-- taken in the same transaction as the record.
CREATE TABLE custom_unique_value (
    field_id  uuid NOT NULL,
    value     text NOT NULL,
    tenant_id text NOT NULL,

    -- Which row holds it, so releasing a claim on update knows what it is releasing.
    record_id uuid NOT NULL,

    PRIMARY KEY (field_id, value),
    FOREIGN KEY (field_id) REFERENCES custom_field (field_id) ON DELETE CASCADE
);

CREATE INDEX custom_unique_value_by_record ON custom_unique_value (record_id);

-- ------------------------------------------------------------------ field history

-- What a custom field used to be, and who changed it.
--
-- Append-only by construction: there is no UPDATE statement against this table anywhere, and the
-- policy below grants no DELETE. A history a tenant can rewrite answers no question anybody asks
-- it.
CREATE TABLE custom_field_history (
    history_id  uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    entity_kind text        NOT NULL,
    entity_id   uuid        NOT NULL,
    field_name  text        NOT NULL,

    -- Both nullable and both meaningful: null to a value is a field being set, a value to null is
    -- one being cleared, and the pair is what makes either readable without the row before it.
    old_value   text,
    new_value   text,

    -- Derived from the caller's claims by Approvers.Of, exactly as an approval is. Never from a
    -- body field, for ADR-0046's reason.
    changed_by  uuid        NOT NULL,
    changed_at  timestamptz NOT NULL
);

CREATE INDEX custom_field_history_by_entity
    ON custom_field_history (entity_kind, entity_id, changed_at DESC);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_validation_rule TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON custom_unique_value    TO flowx_tenant;

-- No DELETE and no UPDATE. The append-only claim above is this line, and a grant is the only
-- place it can be made true rather than merely intended.
GRANT SELECT, INSERT ON custom_field_history TO flowx_tenant;

ALTER TABLE custom_validation_rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_validation_rule FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_validation_rule_tenant_isolation ON custom_validation_rule
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE custom_unique_value ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_unique_value FORCE  ROW LEVEL SECURITY;

-- The policy narrows what a tenant can see and write; the primary key is global to the field,
-- which is what makes the uniqueness real. A tenant cannot read another's claimed values and
-- cannot take one either — a claim on a field belonging to another tenant is a claim on a
-- field_id its own custom_field policy already hides.
CREATE POLICY custom_unique_value_tenant_isolation ON custom_unique_value
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE custom_field_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_field_history FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_field_history_tenant_isolation ON custom_field_history
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
