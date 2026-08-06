-- Schema an administrator writes at run time: custom fields on the built-in entities, custom
-- objects with their own fields, and relationships between them.
--
-- WHY NOT DDL PER TENANT. The obvious reading of "let a tenant add a column" is ALTER TABLE, and
-- it is the wrong one for this shape. A tenant that can cause DDL can take an ACCESS EXCLUSIVE
-- lock on a shared table, invalidate every cached plan, exhaust the relation cache at a few
-- thousand tenants, and make every migration in this directory conditional on what somebody
-- added at four o'clock. Row-level security also stops being one rule per table.
--
-- So the dynamic part is DATA, in three shapes:
--
--   custom_field         what an administrator declared: a name, a type, and what it applies to
--   <entity>.custom_fields  a jsonb column on lead, account and opportunity holding the values
--   custom_object /      a whole entity an administrator invented, and its rows
--   custom_record
--   custom_relationship  a named edge between two object kinds, with a cardinality
--   custom_link          one instance of that edge
--
-- WHY jsonb AND NOT AN EAV VALUES TABLE. Both were reasonable and the deciding argument is
-- reads: every path that touches a lead wants all of its custom values, and a values table makes
-- that a join per entity plus a pivot, while the jsonb column travels with the row it belongs to
-- and is indexed by one GIN index. EAV wins when a query filters on one attribute across many
-- rows, which is what the guards in ProcessRules do — and those read one entity at a time by id.
-- The cost accepted here is that a value's type is checked at the write and not by the column;
-- CustomValues.Validate does that check, and custom_field is what it reads.
--
-- WHY custom_record IS ONE TABLE AND NOT ONE PER OBJECT. Same reason as above, one level up.

-- ------------------------------------------------------------------ what was declared

-- A field an administrator added, either to one of the built-in entity kinds or to a custom
-- object. Exactly one of the two, which is what the CHECK says.
CREATE TABLE custom_object (
    object_id   uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    -- The identifier a caller uses in a URL and a guard. Constrained rather than free text: it
    -- ends up in a jsonb key and in an administrator's guard, and a name that needed quoting in
    -- one of those places would be a name that behaved differently in the other.
    name        text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label       text        NOT NULL,
    created_at  timestamptz NOT NULL,

    UNIQUE (tenant_id, name),
    UNIQUE (object_id, tenant_id)
);

CREATE TABLE custom_field (
    field_id    uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    -- One of EntityKind, for a field on a built-in entity.
    applies_to  text        CHECK (applies_to IN ('Lead', 'Account', 'Contact', 'Opportunity')),

    -- Or a custom object, for a field on one of those.
    object_id   uuid,

    name        text        NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label       text        NOT NULL,
    data_type   text        NOT NULL CHECK (data_type IN ('Text', 'Number', 'Boolean', 'Date')),
    is_required boolean     NOT NULL,
    created_at  timestamptz NOT NULL,

    -- A field belongs to a built-in kind or to a custom object, never to both and never to
    -- neither. Written as a CHECK rather than as two tables because everything downstream —
    -- validation, the guard catalogue, the API — treats them identically once declared.
    CHECK ((applies_to IS NULL) <> (object_id IS NULL)),

    FOREIGN KEY (object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

-- Two names on one owner is the collision that matters, and the owner is nullable on both
-- sides, so it is two partial indexes rather than one constraint.
CREATE UNIQUE INDEX custom_field_by_entity ON custom_field (tenant_id, applies_to, name)
    WHERE applies_to IS NOT NULL;

CREATE UNIQUE INDEX custom_field_by_object ON custom_field (object_id, name)
    WHERE object_id IS NOT NULL;

-- ------------------------------------------------------------------ what was written

-- A row of a custom object. `values` holds only keys custom_field declared for the object —
-- checked by CustomValues.Validate above the database, and by custom_record_keys_declared below
-- it, for the reason 0003 states about the activity trigger: a caller deserves an error naming
-- the field, and the trigger is what makes the check unskippable.
CREATE TABLE custom_record (
    record_id  uuid        NOT NULL PRIMARY KEY,
    tenant_id  text        NOT NULL,
    object_id  uuid        NOT NULL,
    values     jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL,

    UNIQUE (record_id, tenant_id),
    FOREIGN KEY (object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

CREATE INDEX custom_record_by_object ON custom_record (object_id);
CREATE INDEX custom_record_values ON custom_record USING gin (values jsonb_path_ops);

-- ------------------------------------------------------------------ how they relate

-- A named edge. Both ends are custom objects: an edge to a built-in entity would need a
-- polymorphic reference of the kind 0003's trigger already exists to hold up, and one of those
-- in this schema is enough to demonstrate the pattern.
CREATE TABLE custom_relationship (
    relationship_id uuid NOT NULL PRIMARY KEY,
    tenant_id       text NOT NULL,
    name            text NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    from_object_id  uuid NOT NULL,
    to_object_id    uuid NOT NULL,

    -- What the administrator promised. Enforced by custom_link_cardinality below, because a
    -- promise nothing checks is a promise the first duplicate breaks.
    cardinality     text NOT NULL CHECK (cardinality IN ('OneToOne', 'OneToMany', 'ManyToMany')),

    UNIQUE (tenant_id, name),
    UNIQUE (relationship_id, tenant_id),
    FOREIGN KEY (from_object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (to_object_id, tenant_id) REFERENCES custom_object (object_id, tenant_id)
        ON DELETE CASCADE
);

CREATE TABLE custom_link (
    link_id         uuid        NOT NULL PRIMARY KEY,
    tenant_id       text        NOT NULL,
    relationship_id uuid        NOT NULL,
    from_record_id  uuid        NOT NULL,
    to_record_id    uuid        NOT NULL,
    created_at      timestamptz NOT NULL,

    -- The same edge twice is not a second edge. This holds for every cardinality and is the one
    -- rule an index can express; the other two need the trigger.
    UNIQUE (relationship_id, from_record_id, to_record_id),

    FOREIGN KEY (relationship_id, tenant_id)
        REFERENCES custom_relationship (relationship_id, tenant_id) ON DELETE CASCADE,
    FOREIGN KEY (from_record_id, tenant_id) REFERENCES custom_record (record_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (to_record_id, tenant_id) REFERENCES custom_record (record_id, tenant_id)
        ON DELETE CASCADE
);

CREATE INDEX custom_link_from ON custom_link (relationship_id, from_record_id);
CREATE INDEX custom_link_to ON custom_link (relationship_id, to_record_id);

-- ------------------------------------------------------------------ the dynamic columns

-- The custom values of a built-in entity, on the row they describe.
--
-- DEFAULT '{}' and NOT NULL, so every read is a read of an object and no consumer has to hold a
-- null case that means the same as empty.
ALTER TABLE lead        ADD COLUMN custom_fields jsonb NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE account     ADD COLUMN custom_fields jsonb NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE contact     ADD COLUMN custom_fields jsonb NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE opportunity ADD COLUMN custom_fields jsonb NOT NULL DEFAULT '{}'::jsonb;

CREATE INDEX lead_custom_fields ON lead USING gin (custom_fields jsonb_path_ops);
CREATE INDEX opportunity_custom_fields ON opportunity USING gin (custom_fields jsonb_path_ops);

-- ------------------------------------------------------------------ what the database enforces

-- Every key of a record's `values` is a field somebody declared for its object.
--
-- The capability checks this first and raises an error naming the field. This is the half that
-- cannot be skipped: a record written by a repair script, by a future capability, or by an
-- administrator with a psql session goes through here.
CREATE OR REPLACE FUNCTION custom_record_keys_declared() RETURNS trigger AS $$
DECLARE
    undeclared text;
BEGIN
    SELECT string_agg(key, ', ' ORDER BY key) INTO undeclared
    FROM jsonb_object_keys(NEW.values) AS key
    WHERE NOT EXISTS (
        SELECT 1 FROM custom_field f
        WHERE f.object_id = NEW.object_id AND f.name = key);

    IF undeclared IS NOT NULL THEN
        RAISE EXCEPTION
            'custom_record % holds values for undeclared fields: %', NEW.record_id, undeclared
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END
$$ LANGUAGE plpgsql
-- Pinned for 0003's reason: the function runs under whatever search_path the caller has, and an
-- unqualified custom_field would otherwise resolve in somebody else's schema.
SET search_path FROM CURRENT;

CREATE TRIGGER custom_record_values_are_declared
    BEFORE INSERT OR UPDATE ON custom_record
    FOR EACH ROW EXECUTE FUNCTION custom_record_keys_declared();

-- The cardinality the relationship promised.
--
-- OneToMany means one parent per child, so a second link naming the same `to` record is refused.
-- OneToOne means that in both directions. ManyToMany constrains nothing beyond the unique index
-- above, which is why it is not mentioned below.
CREATE OR REPLACE FUNCTION custom_link_cardinality() RETURNS trigger AS $$
DECLARE
    kind text;
BEGIN
    SELECT cardinality INTO kind
    FROM custom_relationship WHERE relationship_id = NEW.relationship_id;

    IF kind IN ('OneToOne', 'OneToMany') AND EXISTS (
        SELECT 1 FROM custom_link
        WHERE relationship_id = NEW.relationship_id
          AND to_record_id = NEW.to_record_id
          AND link_id <> NEW.link_id)
    THEN
        RAISE EXCEPTION
            'relationship % is %, and % is already on the other end of one'
            , NEW.relationship_id, kind, NEW.to_record_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    IF kind = 'OneToOne' AND EXISTS (
        SELECT 1 FROM custom_link
        WHERE relationship_id = NEW.relationship_id
          AND from_record_id = NEW.from_record_id
          AND link_id <> NEW.link_id)
    THEN
        RAISE EXCEPTION
            'relationship % is OneToOne, and % already has a link'
            , NEW.relationship_id, NEW.from_record_id
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;

    RETURN NEW;
END
$$ LANGUAGE plpgsql
SET search_path FROM CURRENT;

CREATE TRIGGER custom_link_honours_cardinality
    BEFORE INSERT OR UPDATE ON custom_link
    FOR EACH ROW EXECUTE FUNCTION custom_link_cardinality();

-- ------------------------------------------------------------------ isolation

-- The same two walls every other table in this schema stands behind. A table added after 0002
-- that nobody remembered to enrol is a table with no isolation at all — and these five hold
-- exactly the shape an administrator invented, which is the most tenant-specific data here.
GRANT SELECT, INSERT, UPDATE, DELETE ON custom_object       TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON custom_field        TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON custom_record       TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON custom_relationship TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON custom_link         TO flowx_tenant;

ALTER TABLE custom_object ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_object FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_object_tenant_isolation ON custom_object
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE custom_field ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_field FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_field_tenant_isolation ON custom_field
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE custom_record ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_record FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_record_tenant_isolation ON custom_record
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE custom_relationship ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_relationship FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_relationship_tenant_isolation ON custom_relationship
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE custom_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_link FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_link_tenant_isolation ON custom_link
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
