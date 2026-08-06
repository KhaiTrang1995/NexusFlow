-- Row-level security on every CRM table, so the database is what refuses a cross-tenant read.
--
-- This is migration 0008 of plugins/FlowX.Postgres applied to the sample's own tables, and it
-- is deliberately the same three decisions, for the same three reasons. They are restated
-- rather than referenced, because a policy is read at the moment somebody is deciding whether
-- to change it and that reader will be looking at this file.
--
--   1. FORCE, and the unprivileged role. A superuser bypasses row-level security
--      unconditionally, and a table's owner bypasses it unless the table declares FORCE ROW
--      LEVEL SECURITY. This sample connects as the role that created the schema — that is what
--      FLOWX_POSTGRES_CONNECTION points at — so ENABLE plus a policy would install a wall with
--      a documented gate beside it and report success. The runtime narrows itself to
--      flowx_tenant, which cannot bypass, and only then is the policy load-bearing.
--
--   2. current_setting(…, true). The one-argument form raises 42704 when the setting is
--      absent, which would take out every statement on an unscoped connection. The
--      two-argument form returns NULL.
--
--   3. IS NOT DISTINCT FROM, over nullif(…, ''). RESET restores a custom setting to the empty
--      string rather than to NULL, so the two spellings of "no tenant" are folded together
--      before they are compared and no two policies can disagree about which they saw. The
--      predicate is fail-closed: a connection nobody scoped matches tenant_id IS NULL, and no
--      CRM row has one, so a runtime that forgets to scope reads nothing rather than
--      everything.
--
-- What is new here and is not in 0008: five of the thirteen tables carry no tenant_id, and
-- they are the same five §6 draws without one. They inherit their tenant through the foreign
-- key, and the policy restates the predicate through it rather than relying on the parent's
-- own policy applying inside the subquery. It does apply — policies compose through a subquery
-- for a role that neither owns nor bypasses — but a rule whose correctness depends on a reader
-- knowing that is a rule that will be broken by whoever next edits it.

-- Cluster-wide, so this is written to be safe when a second schema on the same server has
-- already run it or the platform's own 0008. Every test in tests/Crm.Tests gets its own schema
-- and migrates it from nothing, which is not a hypothetical arrangement.
--
-- The existence check alone was not enough, and the way it failed is worth stating. CrmMigrator
-- takes pg_advisory_xact_lock(namespace, hashtext(current_schema())) — one lock per schema,
-- which is right for everything else in this file and wrong for exactly this statement, because
-- a role is not in a schema. Two test schemas migrating at once therefore hold different locks,
-- both see no role, and both issue CREATE ROLE; the loser gets 42710. That is a first run
-- against a fresh cluster failing three tests and every run afterwards passing, which is the
-- shape of a flake nobody can reproduce.
--
-- Catching the exception rather than widening the lock: the check is still worth making, since
-- the common case is that the role is already there, and the handler is what makes the window
-- between the check and the CREATE harmless.
--
-- BOTH SQLSTATES, and the second is the one that actually fires. `duplicate_object` (42710) is
-- what CREATE ROLE raises when the role is already committed and visible — which this block's
-- own IF has just ruled out. What a genuine race produces is `unique_violation` (23505) on
-- pg_authid_rolname_index, because the losing transaction gets as far as inserting into the
-- catalogue before the index refuses it. A handler for 42710 alone reads like a fix and leaves
-- the flake exactly where it was; this was written that way first and the fresh-cluster run
-- still failed three tests.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flowx_tenant') THEN
        CREATE ROLE flowx_tenant NOLOGIN NOBYPASSRLS;
    END IF;
EXCEPTION
    WHEN duplicate_object OR unique_violation THEN
        NULL;
END
$$;

-- USAGE is granted through format('%I', …) against current_schema() rather than named
-- literally, because the schema is a deployment setting and a migration cannot know it. An
-- identifier cannot be a bind parameter, so it is passed to the server as a value and format()
-- does the escaping — the same route PostgresMigrator takes for the same reason.
DO $$
BEGIN
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO flowx_tenant', current_schema());
END
$$;

GRANT SELECT, INSERT, UPDATE, DELETE ON account            TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON contact            TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON lead               TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON opportunity        TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON quote              TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON quote_line         TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON sales_order        TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON activity           TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON process_definition TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON process_stage      TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON process_transition TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON transition_guard   TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON transition_action  TO flowx_tenant;

-- The view, which carries no policy of its own: security_invoker means it is the account
-- table's policy that decides, evaluated for whoever is asking.
GRANT SELECT ON customer_account TO flowx_tenant;

-- Read-only, and it is not tenant data: it is which migrations this schema has seen. The
-- readiness probe reports it, and reporting it on the same scoped connection that counts the
-- rows is what keeps that capability to one connection.
GRANT SELECT ON crm_schema_migration TO flowx_tenant;

-- ------------------------------------------------------------ the eight roots

ALTER TABLE account ENABLE ROW LEVEL SECURITY;
ALTER TABLE account FORCE  ROW LEVEL SECURITY;

CREATE POLICY account_tenant_isolation ON account
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE contact ENABLE ROW LEVEL SECURITY;
ALTER TABLE contact FORCE  ROW LEVEL SECURITY;

CREATE POLICY contact_tenant_isolation ON contact
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE lead ENABLE ROW LEVEL SECURITY;
ALTER TABLE lead FORCE  ROW LEVEL SECURITY;

CREATE POLICY lead_tenant_isolation ON lead
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE opportunity ENABLE ROW LEVEL SECURITY;
ALTER TABLE opportunity FORCE  ROW LEVEL SECURITY;

CREATE POLICY opportunity_tenant_isolation ON opportunity
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE quote ENABLE ROW LEVEL SECURITY;
ALTER TABLE quote FORCE  ROW LEVEL SECURITY;

CREATE POLICY quote_tenant_isolation ON quote
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE sales_order ENABLE ROW LEVEL SECURITY;
ALTER TABLE sales_order FORCE  ROW LEVEL SECURITY;

CREATE POLICY sales_order_tenant_isolation ON sales_order
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE activity ENABLE ROW LEVEL SECURITY;
ALTER TABLE activity FORCE  ROW LEVEL SECURITY;

CREATE POLICY activity_tenant_isolation ON activity
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE process_definition ENABLE ROW LEVEL SECURITY;
ALTER TABLE process_definition FORCE  ROW LEVEL SECURITY;

CREATE POLICY process_definition_tenant_isolation ON process_definition
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

-- ------------------------------------------------------------ the five that inherit

-- A quote line's tenant is its quote's. One hop.
ALTER TABLE quote_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE quote_line FORCE  ROW LEVEL SECURITY;

CREATE POLICY quote_line_tenant_isolation ON quote_line
    USING (EXISTS (
        SELECT 1 FROM quote q
         WHERE q.quote_id = quote_line.quote_id
           AND q.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM quote q
         WHERE q.quote_id = quote_line.quote_id
           AND q.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));

-- A stage's tenant is its definition's. One hop.
ALTER TABLE process_stage ENABLE ROW LEVEL SECURITY;
ALTER TABLE process_stage FORCE  ROW LEVEL SECURITY;

CREATE POLICY process_stage_tenant_isolation ON process_stage
    USING (EXISTS (
        SELECT 1 FROM process_definition d
         WHERE d.process_id = process_stage.process_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM process_definition d
         WHERE d.process_id = process_stage.process_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));

-- A transition's tenant is the definition its FROM stage belongs to. Two hops, and the walk is
-- up the containment chain §5.4 draws — it terminates because process_definition's own policy
-- reads a setting rather than another table.
ALTER TABLE process_transition ENABLE ROW LEVEL SECURITY;
ALTER TABLE process_transition FORCE  ROW LEVEL SECURITY;

CREATE POLICY process_transition_tenant_isolation ON process_transition
    USING (EXISTS (
        SELECT 1 FROM process_stage s
          JOIN process_definition d ON d.process_id = s.process_id
         WHERE s.stage_id = process_transition.from_stage_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM process_stage s
          JOIN process_definition d ON d.process_id = s.process_id
         WHERE s.stage_id = process_transition.from_stage_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));

ALTER TABLE transition_guard ENABLE ROW LEVEL SECURITY;
ALTER TABLE transition_guard FORCE  ROW LEVEL SECURITY;

CREATE POLICY transition_guard_tenant_isolation ON transition_guard
    USING (EXISTS (
        SELECT 1 FROM process_transition t
          JOIN process_stage s      ON s.stage_id  = t.from_stage_id
          JOIN process_definition d ON d.process_id = s.process_id
         WHERE t.transition_id = transition_guard.transition_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM process_transition t
          JOIN process_stage s      ON s.stage_id  = t.from_stage_id
          JOIN process_definition d ON d.process_id = s.process_id
         WHERE t.transition_id = transition_guard.transition_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));

ALTER TABLE transition_action ENABLE ROW LEVEL SECURITY;
ALTER TABLE transition_action FORCE  ROW LEVEL SECURITY;

CREATE POLICY transition_action_tenant_isolation ON transition_action
    USING (EXISTS (
        SELECT 1 FROM process_transition t
          JOIN process_stage s      ON s.stage_id  = t.from_stage_id
          JOIN process_definition d ON d.process_id = s.process_id
         WHERE t.transition_id = transition_action.transition_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM process_transition t
          JOIN process_stage s      ON s.stage_id  = t.from_stage_id
          JOIN process_definition d ON d.process_id = s.process_id
         WHERE t.transition_id = transition_action.transition_id
           AND d.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));
