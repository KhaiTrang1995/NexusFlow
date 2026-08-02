-- Expand: make the database, rather than the runtime, the thing that refuses a cross-tenant read.
--
-- `tenant_id` has been on flow_instance since 0001, and its index since 0001, and until this
-- migration nothing anywhere read either for the purpose of keeping one tenant out of another's
-- rows. docs/16-Multi-Tenant.md §5 specifies the repair as row-level security and writes the DDL
-- out; this is that DDL, corrected in three places where it does not hold as written. The
-- corrections are the substance of this file, so they are stated before the statements.
--
--   1. THE POLICY AS WRITTEN ISOLATES NOTHING FROM THE ACCOUNT THAT RUNS THE MIGRATIONS.
--      A superuser bypasses row-level security unconditionally, and a table's owner bypasses it
--      unless the table declares FORCE ROW LEVEL SECURITY. The ordinary arrangement — and the
--      one FLOWX_POSTGRES_CONNECTION points at in this repository's own runs — is a journal
--      connecting as the role that created the schema, so ENABLE plus a policy would have
--      installed a wall with a documented gate beside it and reported success. Hence both
--      FORCE below and the flowx_tenant role above: the runtime narrows itself to a role that
--      cannot bypass, and only then is the policy load-bearing.
--
--   2. `current_setting('flowx.tenant_id')` RAISES 42704 WHEN THE SETTING IS ABSENT.
--      Every statement on an unscoped connection would error rather than return nothing, which
--      would take out every single-tenant deployment that ever ran this migration. The
--      two-argument form returns NULL instead, and that is what is used.
--
--   3. `=` IS THE WRONG COMPARISON FOR A NULLABLE PARTITION KEY.
--      A single-tenant deployment's rows carry tenant_id IS NULL, and `NULL = NULL` is NULL,
--      which a policy reads as "no". IS NOT DISTINCT FROM is the comparison that means what
--      the documentation's `=` was written to mean. nullif(…, '') is the other half of it:
--      RESET restores a custom setting to the empty string rather than to NULL, so the two
--      spellings of "no tenant" are folded together before they are compared and no policy can
--      disagree with another about which of them it saw.
--
-- The predicate is therefore fail-closed in the case that matters. A connection that has not
-- been scoped sees rows whose tenant_id IS NULL — its own deployment's, if it is single-tenant,
-- and nobody's at all in a deployment where every row carries a tenant. A runtime that forgets
-- to scope reads no tenant's data rather than every tenant's, which is the opposite of how this
-- defect behaves today.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Every statement here is additive and
-- reversible, and a pod running the previous release is unaffected by all of it: it connects as
-- the application's own role, never assumes flowx_tenant, and therefore never has a policy
-- applied to it — ENABLE ROW LEVEL SECURITY changes nothing for a role that bypasses. That is
-- what makes this safe to apply ahead of the release that uses it, which is the whole point of
-- expanding first. There is no backfill and no column, so there is no rewrite: the ALTERs are
-- catalogue-only and the whole migration takes an ACCESS EXCLUSIVE lock only for as long as it
-- takes to update five catalogue rows.
--
-- Deliberately NOT in this migration:
--
--   * flow_lease. A lease is acquired BEFORE the instance row exists — 0001 says so, and it is
--     why flow_lease carries no foreign key — so a policy joining it to flow_instance would
--     refuse the very first acquisition of every instance and durability would stop. A lease row
--     holds an id, a node name, a token and an expiry: no tenant data, and the fence already
--     stops one node writing another's instance. Isolating it would cost correctness to protect
--     nothing.
--
--   * retention_policy. Deployment-wide configuration, not tenant data.
--
--   * Journal partitioning. docs/11 §6 names sharding as a lever and stops there, so building it
--     would be invention rather than implementation. docs/25 §1 says so explicitly.

-- The role a scoped connection narrows itself to. NOLOGIN: nothing connects as it, and it is
-- reachable only by a session that has already authenticated as the application's own role.
-- NOBYPASSRLS is the default and is stated anyway, because it is the single property this whole
-- migration depends on and a reader should not have to know a default to see that.
--
-- A role is cluster-wide rather than schema-local, so this is written to be safe when a second
-- schema on the same server runs the same migration — which is not a hypothetical: the test
-- suite gives every test its own schema and migrates each one from nothing.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flowx_tenant') THEN
        CREATE ROLE flowx_tenant NOLOGIN NOBYPASSRLS;
    END IF;
END
$$;

-- The role needs the schema and exactly the three tables a scoped journal touches. It is granted
-- no DDL, no ownership, and nothing on flow_lease or retention_policy — the lease store and the
-- retention sweeper are node-wide and are never scoped, so a grant there would be privilege the
-- role has no path to use. A scoped connection can read and write the rows of one tenant's
-- instances and can do nothing else at all.
--
-- USAGE is granted through format('%I', …) against current_schema() rather than named literally,
-- because the schema is a deployment setting (PostgresJournalOptions.Schema) and a migration
-- cannot know it. That is the same quoting route PostgresMigrator already takes for the same
-- reason: an identifier cannot be a parameter, so it is passed to the server as a value and let
-- format() do the escaping.
DO $$
BEGIN
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO flowx_tenant', current_schema());
END
$$;

GRANT SELECT, INSERT, UPDATE, DELETE ON flow_instance TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON flow_step     TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON outbox_event  TO flowx_tenant;

-- flow_instance is where the partition key lives, so its policy is the only one that reads the
-- setting directly. WITH CHECK as well as USING: without it a scoped connection could INSERT a
-- row naming another tenant and then be unable to read back what it had just written, which is
-- a worse state than being refused. PostgresFlowJournal.StartAsync catches the 42501 that
-- raises and returns it as tenant.cross_tenant_denied.
ALTER TABLE flow_instance ENABLE ROW LEVEL SECURITY;
ALTER TABLE flow_instance FORCE  ROW LEVEL SECURITY;

CREATE POLICY flow_instance_tenant_isolation ON flow_instance
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

-- flow_step and outbox_event carry no tenant_id of their own, and deliberately are not given
-- one: it would be a denormalisation that can disagree with the instance row it copies, and the
-- append-only table's primary key would then have two sources of truth about one instance. They
-- inherit the instance's tenant through the foreign key that already exists.
--
-- The subquery restates the tenant predicate rather than relying on flow_instance's own policy
-- applying inside it. It does apply — policies compose through a subquery for a role that
-- neither owns nor bypasses — but a rule whose correctness depends on a reader knowing that is a
-- rule that will be broken by whoever next edits it. Restating it costs an index lookup on a
-- primary key.
ALTER TABLE flow_step ENABLE ROW LEVEL SECURITY;
ALTER TABLE flow_step FORCE  ROW LEVEL SECURITY;

CREATE POLICY flow_step_tenant_isolation ON flow_step
    USING (EXISTS (
        SELECT 1 FROM flow_instance i
         WHERE i.instance_id = flow_step.instance_id
           AND i.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM flow_instance i
         WHERE i.instance_id = flow_step.instance_id
           AND i.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));

ALTER TABLE outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox_event FORCE  ROW LEVEL SECURITY;

CREATE POLICY outbox_event_tenant_isolation ON outbox_event
    USING (EXISTS (
        SELECT 1 FROM flow_instance i
         WHERE i.instance_id = outbox_event.instance_id
           AND i.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')))
    WITH CHECK (EXISTS (
        SELECT 1 FROM flow_instance i
         WHERE i.instance_id = outbox_event.instance_id
           AND i.tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), '')));
