-- Expand: announce the two things a sweep is waiting for, so it stops having to wait out its
-- interval to discover them.
--
-- Both sweeps this schema serves are polls. The change feed reads its cursor every second and
-- the timer sweep reads the parked rows every ten, which makes a flow-emits-flow-consumes chain
-- cost about a second and a `.Delay(1s)` come due up to ten seconds late — the largest latency
-- item in the platform, and paid by every idle node as one query per interval as well.
--
-- The database already knows the answer at the moment it becomes true: it is committing the row.
-- These two triggers say so. `pg_notify` is transactional — the announcement is delivered when
-- the transaction commits and never if it rolls back — so a listener is told about a staged event
-- exactly when a reader could first see it, and about a parked wake exactly when the row that
-- records it exists.
--
-- WHAT THIS IS NOT. It is not a queue and nothing may treat it as one. `LISTEN` delivers only to
-- sessions that are connected at the moment of the commit; a node that is reconnecting, or that
-- was never registered to listen at all, hears nothing and is *supposed* to hear nothing. The
-- interval remains the correctness backstop, so a dropped announcement costs latency and never an
-- event. That is also why nothing durable is written here — a table would need a reader, a
-- retention rule and a delivery guarantee, which is the outbox, which is the table this trigger
-- is already announcing.
--
-- THE PAYLOAD IS THE SCHEMA, AND IT IS A FILTER RATHER THAN DATA. A channel name is
-- database-wide, so two deployments sharing one database — or one deployment's tenant schemas,
-- which each get their own copy of these objects — would wake each other's listeners. The
-- announcement carries `TG_TABLE_SCHEMA` so a listener can ignore the ones that are not its own.
-- Getting that wrong in either direction costs a pass that finds nothing, which is why the
-- listener may filter on it and nothing may conclude anything else from it.
--
--   The timer's payload carries one more field, the wake instant in epoch milliseconds, and it
--   exists because "a wake was written" is not the event the timer sweep is waiting for. A wake
--   is written *before* it is due — that is what a wait is — so a listener woken at write time
--   would sweep, find nothing due, and go back to sleep for a full interval, which is the poll it
--   started with plus one query. The instant is what lets the listener wake its sweep when the
--   wait actually ends. It is a hint and is treated as one: unreadable, absent or wrong, the
--   listener falls back to waking immediately or not at all, and the interval still fires.
--
-- STATEMENT-LEVEL ON THE OUTBOX, ROW-LEVEL ON THE INSTANCE. A step that stages four events wants
-- one announcement, not four, and the change sweep reads the whole cursor either way; PostgreSQL
-- would collapse the duplicates at commit regardless, and doing it here means the function is
-- entered once. The instance trigger has to be row-level because it reads NEW.wake_at.
--
-- WHY `UPDATE OF wake_at` AND A `WHEN` CLAUSE. `JournalSql.CompleteInstance` assigns all three
-- wake columns on every instance that comes to rest, including the ones that are finishing, so
-- without the guard every completion would announce a timer that does not exist. `NEW.wake_at IS
-- NOT NULL` is the whole of "this instance parked", and the check constraint added in 0005 is
-- what makes that one column enough to say it.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Additive in the strongest sense
-- available: no column changes, no index, no row rewritten, and nothing a reader of this schema
-- can observe in a query. A pod running the previous release keeps staging events and parking
-- instances exactly as it did — it simply also announces them, to a listener it has never heard
-- of, which is at worst nobody. There is no backfill and there cannot be one: an announcement is
-- an instant, and the instants before this migration have passed.
--
-- Deliberately NOT in this migration:
--
--   * A trigger on `flow_step` or on `change_cursor`. Neither is what a sweep waits for: the
--     change sweep waits for an event to become readable, which is the outbox insert, and the
--     cursor moves *after* the flows have run.
--
--   * An announcement on publication. `PostgresOutboxPublisher` drains this table on its own
--     loop and owns its own interval; waking it here would make one node's publisher a consumer
--     of another node's commits, which is the coupling `published_at` exists to avoid.
--
--   * Anything payload-bearing. See above: the sweep re-reads its own cursor, so a change
--     carried on the wire would be a second copy of a row that has one authoritative version.

CREATE OR REPLACE FUNCTION flowx_notify_staged() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    PERFORM pg_notify('flowx_sweep_change', TG_TABLE_SCHEMA);
    RETURN NULL;
END;
$$;

CREATE TRIGGER outbox_event_staged_notify
    AFTER INSERT ON outbox_event
    FOR EACH STATEMENT
    EXECUTE FUNCTION flowx_notify_staged();

CREATE OR REPLACE FUNCTION flowx_notify_parked() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    PERFORM pg_notify(
        'flowx_sweep_timer',
        TG_TABLE_SCHEMA || '|' || (extract(epoch FROM NEW.wake_at) * 1000)::bigint);
    RETURN NULL;
END;
$$;

CREATE TRIGGER flow_instance_parked_notify
    AFTER INSERT OR UPDATE OF wake_at ON flow_instance
    FOR EACH ROW
    WHEN (NEW.wake_at IS NOT NULL)
    EXECUTE FUNCTION flowx_notify_parked();
