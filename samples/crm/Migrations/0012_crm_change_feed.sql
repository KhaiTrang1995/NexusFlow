-- What a client that was offline has to be told, and how it is told without losing anything.
--
-- A mobile client cannot re-read every record on every launch, so it asks what changed since it
-- last looked. Two things make that harder than a `WHERE updated_at > @since`:
--
--   A DELETE LEAVES NOTHING TO SELECT. A feed built on the records themselves can only ever say
--   what exists, so a client that saw a record once keeps it forever. The tombstone has to be a
--   row of its own, which is what custom_change is.
--
--   A TIMESTAMP IS NOT A SAFE CURSOR. Two transactions take `now()` at their start; the one with
--   the earlier stamp can commit second. A client that read up to the later stamp in between
--   never sees the earlier row again — it is behind the cursor and was not visible when the
--   cursor passed it. Clock skew across writers makes it worse, and neither shows up in testing
--   because both need a concurrent commit to happen at the wrong moment.
--
-- WHAT IS USED INSTEAD. Every change records `pg_current_xact_id()`, and the read serves only
-- changes below `pg_snapshot_xmin(pg_current_snapshot())` — the id under which no transaction is
-- still running. A change written by a transaction that has not committed yet is therefore never
-- behind the cursor: it is not served at all until nothing can still be in flight before it. The
-- cost is that the newest changes are held back for as long as the oldest open transaction runs,
-- which is a bounded lag rather than a silent loss.

CREATE TABLE custom_change (
    -- Insertion order within a transaction. An identity column and not a uuid, because the whole
    -- table exists to be read in an order, and a random primary key would have none.
    seq        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,

    tenant_id  text        NOT NULL,

    -- Which object the record belonged to, so a client syncing one object is not sent every
    -- other object's changes. Copied rather than joined: after a delete there is nothing to join
    -- to, and that is exactly the row a sync client most needs.
    object_id  uuid        NOT NULL,
    record_id  uuid        NOT NULL,

    kind       text        NOT NULL CHECK (kind IN ('Upserted', 'Deleted')),

    -- The watermark this is served against. See the note above.
    xact_id    xid8        NOT NULL,

    changed_at timestamptz NOT NULL
);

-- NO FOREIGN KEYS, DELIBERATELY. A reference to custom_record could not survive the delete it
-- exists to record, and one to custom_object would cascade the tombstones away at the moment an
-- object is dropped — leaving every client that was offline holding rows nothing will ever
-- retract.

CREATE INDEX custom_change_by_tenant ON custom_change (tenant_id, xact_id, seq);
CREATE INDEX custom_change_by_object ON custom_change (object_id, xact_id, seq);

-- ------------------------------------------------------------------ who writes it
--
-- A trigger and not the application. Records are written by the record capability, and updated
-- by the roll-up recomputation in RollupStore, and deleted by the cascade when an object goes —
-- three paths today and the third is not even a statement anyone wrote about records. A log the
-- application appends to is a log that is correct until somebody adds the fourth path.

CREATE FUNCTION custom_record_changed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        INSERT INTO custom_change (tenant_id, object_id, record_id, kind, xact_id, changed_at)
        VALUES (OLD.tenant_id, OLD.object_id, OLD.record_id, 'Deleted', pg_current_xact_id(), now());

        RETURN OLD;
    END IF;

    INSERT INTO custom_change (tenant_id, object_id, record_id, kind, xact_id, changed_at)
    VALUES (NEW.tenant_id, NEW.object_id, NEW.record_id, 'Upserted', pg_current_xact_id(), now());

    RETURN NEW;
END;
$$;

CREATE TRIGGER custom_record_change_log
    AFTER INSERT OR UPDATE OR DELETE ON custom_record
    FOR EACH ROW EXECUTE FUNCTION custom_record_changed();

-- ------------------------------------------------------------------ isolation

-- No UPDATE and no DELETE, so the log is append-only by grant rather than by convention. The
-- trigger runs as the calling role, so the tenant policy below applies to it as well: a change
-- row can only ever be written for the tenant whose record produced it.
GRANT SELECT, INSERT ON custom_change TO flowx_tenant;

ALTER TABLE custom_change ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_change FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_change_tenant_isolation ON custom_change
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
