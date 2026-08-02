-- Expand: let a tenant-scoped connection stage an outbox row.
--
-- 0008 granted flowx_tenant the three tables a scoped journal touches and stopped there. A
-- table grant does not carry the sequences the table's defaults call: outbox_event.staged_seq
-- defaults to nextval('outbox_event_staged_seq') (0004), and nextval requires USAGE on the
-- sequence in its own right. So a scoped journal could read, insert and update every row it was
-- meant to — and failed with 42501 on the one INSERT whose column has a sequence behind it,
-- which is the outbox row a step stages as part of its commit.
--
-- The gap was not visible from the tenant tests, which never staged an outbox row through a
-- scoped journal, and it is not visible from the outbox tests, which stage through the
-- unscoped one. It is the journal conformance suite run through a tenant's connection that
-- reaches it: AStepItsStateBagAndItsOutboxRowsCommitTogether.
--
-- The consequence in a deployment is worse than a failed insert. Staging is inside the step's
-- transaction, so the whole commit rolls back: a tenant on TenantIsolation.Row whose flow emits
-- an event cannot complete that step at all, and retries reach the same permission.
--
-- Expand/contract (docs/11 §7.4). One GRANT, catalogue-only, no rewrite, no lock beyond the
-- catalogue row. A pod running the previous release is unaffected — it connects as the
-- application's own role and never assumes flowx_tenant — and a pod running the next one needs
-- this grant to exist before it starts, which is what expanding first is for.
--
-- Written against the sequence by name rather than ALL SEQUENCES IN SCHEMA, so that a schema
-- shared with an application does not have that application's sequences handed to a role the
-- journal controls. The GRANT is conditional because 0004 created the sequence and a schema
-- migrated only as far as 0003 has no such object -- which cannot happen through the migrator,
-- since it applies in order, but does happen to anyone applying these files by hand.
DO $$
BEGIN
    IF to_regclass('outbox_event_staged_seq') IS NOT NULL THEN
        GRANT USAGE, SELECT ON SEQUENCE outbox_event_staged_seq TO flowx_tenant;
    END IF;
END
$$;
