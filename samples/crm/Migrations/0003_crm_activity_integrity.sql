-- The one relationship in this schema that no foreign key can express, and what holds it up.
--
-- ACTIVITY.relates_to_kind + ACTIVITY.relates_to_id names one of four parents. §6 states the
-- alternative and rejects it: four nullable columns with a check constraint would give the
-- database a real foreign key for each, and would make every query against activities read
-- four columns to find the one that is set. This design takes the polymorphic pair and pays
-- for it here — "referential integrity for this one relationship is enforced by a trigger
-- rather than by a constraint, and that is stated rather than hidden".
--
-- What the trigger enforces, exactly: at INSERT, and at any UPDATE that touches the reference
-- or the tenant, the named row must exist in the named table AND belong to the same tenant. A
-- dangling reference is refused with SQLSTATE 23503, the code a foreign key would have raised,
-- because a caller that catches foreign_key_violation should not have to know that one of its
-- thirteen tables enforces integrity differently from the other twelve.
--
-- What it does NOT enforce, named rather than left for someone to discover: nothing stops a
-- parent being deleted out from under an activity. A real foreign key would offer ON DELETE,
-- and this reference cannot have one. Building the four delete-side triggers would be four
-- more places for the four kinds to disagree, and this sample never deletes a party except in
-- ConvertLeadFlow's compensation (§8.1), which removes an account and a contact created
-- seconds earlier by the same saga — rows nothing has had the chance to hang an activity off.
-- A CRM that deleted accounts in earnest would need the other half; this one would be building
-- it to have built it.
--
-- SECURITY INVOKER, which is the default and is the right default here. The function's
-- lookups run as whoever inserted the activity, so on a scoped connection they run under the
-- policies migration 0002 installed: a parent in another tenant is not merely a different
-- tenant_id, it is a row the lookup cannot see at all. The tenant predicate below is therefore
-- the second of two walls rather than the only one, and it is written out because a rule that
-- holds only through row-level security stops holding the day somebody runs a fix-up script as
-- the schema's owner.

CREATE FUNCTION crm_activity_parent_must_exist() RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    parent_exists boolean;
BEGIN
    CASE NEW.relates_to_kind
        WHEN 'Lead' THEN
            SELECT EXISTS (
                SELECT 1 FROM lead
                 WHERE lead_id = NEW.relates_to_id AND tenant_id = NEW.tenant_id)
              INTO parent_exists;

        WHEN 'Account' THEN
            SELECT EXISTS (
                SELECT 1 FROM account
                 WHERE account_id = NEW.relates_to_id AND tenant_id = NEW.tenant_id)
              INTO parent_exists;

        WHEN 'Contact' THEN
            SELECT EXISTS (
                SELECT 1 FROM contact
                 WHERE contact_id = NEW.relates_to_id AND tenant_id = NEW.tenant_id)
              INTO parent_exists;

        WHEN 'Opportunity' THEN
            SELECT EXISTS (
                SELECT 1 FROM opportunity
                 WHERE opportunity_id = NEW.relates_to_id AND tenant_id = NEW.tenant_id)
              INTO parent_exists;

        ELSE
            -- Unreachable while the CHECK constraint on relates_to_kind stands, and a BEFORE
            -- trigger runs before that constraint is evaluated, so "unreachable" is a claim
            -- about a constraint that could be dropped. A CASE with no ELSE raises 20000
            -- here, which reads as an internal error; this says which value nobody handled.
            RAISE EXCEPTION
                'activity.relates_to_kind ''%'' is not one of Lead, Account, Contact, Opportunity',
                NEW.relates_to_kind
                USING ERRCODE = 'check_violation';
    END CASE;

    IF NOT parent_exists THEN
        RAISE EXCEPTION
            'activity % relates to % % which is not a row this tenant has',
            NEW.activity_id, NEW.relates_to_kind, NEW.relates_to_id
            USING ERRCODE = 'foreign_key_violation';
    END IF;

    RETURN NEW;
END;
$$;

-- The function resolves lead, account, contact and opportunity through search_path, and
-- search_path is a session setting that a caller chooses. Pinning it to the schema this
-- migration ran in makes the four names mean the four tables above, whatever the session is
-- pointed at when the insert happens — including a session that has been pointed at another
-- deployment's schema on the same server, which is what tests/Crm.Tests arranges by design.
--
-- format('%I', …) against current_schema() for the reason 0002 gives: an identifier cannot be
-- a bind parameter, so it is passed to the server as a value and format() does the escaping.
DO $$
BEGIN
    EXECUTE format(
        'ALTER FUNCTION crm_activity_parent_must_exist() SET search_path = %I, pg_catalog',
        current_schema());
END
$$;

-- UPDATE OF names the three columns that can invalidate the reference. An update that moves a
-- due date re-runs nothing; an update that repoints the activity, or moves it between tenants,
-- is checked exactly as an insert is.
CREATE TRIGGER activity_parent_must_exist
    BEFORE INSERT OR UPDATE OF relates_to_kind, relates_to_id, tenant_id ON activity
    FOR EACH ROW
    EXECUTE FUNCTION crm_activity_parent_must_exist();
