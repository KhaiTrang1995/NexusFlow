-- Expand: announce when the next lease lapses, so a node taking over from a dead one stops
-- waiting out the recovery sweep's interval to notice that it has.
--
-- The recovery sweep is the third poll, and the most expensive one to be late on. A dead node's
-- instances wait a lease TTL to become candidates — that part is the design and is not what this
-- changes — and then wait up to a whole `RecoveryScanInterval` on top, because the only thing
-- that looks is a timer. Measured on the chaos rig at a 30 s TTL, that second half is a third of
-- the time an instance spends abandoned.
--
-- The instant is known when the row is written: `expires_at` is on the lease and nothing read it.
-- This trigger says it out loud. `pg_notify` is transactional, so the announcement is delivered
-- when the acquisition or the renewal commits and never if it rolls back.
--
-- WHAT IS ANNOUNCED IS THE EARLIEST LIVE EXPIRY, NOT THE ROW'S OWN, and that is the decision in
-- this file. The obvious version announces NEW.expires_at and leaves the listener to keep the
-- earliest it has heard. It does not work, for two reasons that pull in opposite directions:
--
--   * A renewal EXTENDS an expiry. The instant a listener armed at acquisition is superseded ten
--     seconds later by a renewal that moves it, so an armed instant that is merely kept would
--     fire on a lease that is alive and well — a pass that can only find nothing, once per TTL
--     per instance under load. So an announcement has to be able to move an armed instant
--     *later*, which "keep the earliest" cannot do.
--   * But a listener that replaced its armed instant with every announcement would follow
--     whichever lease was written last, and the lease a recovery sweep is waiting for is by
--     definition the one that is NOT being written — its holder is dead. Under any load at all
--     the armed instant would be a live lease's, permanently.
--
-- Both are answered by announcing the earliest expiry that is still in the future, over the whole
-- table, computed here. Then the announcement is a statement about the schema rather than about
-- one row: it is always the next moment anything could become recoverable, a renewal that moves
-- an expiry moves it, and the listener's rule collapses to "arm the instant you were told",
-- holding no per-lease state at all. A lease that stops being renewed keeps its place at the head
-- of that query until it lapses, which is precisely the case this package exists for.
--
-- The cost is one lookup per lease write. It is an ordered index scan stopping at the first row —
-- `flow_lease_expiry_idx` on `expires_at` has been there since 0001 — so the added cost of a
-- renewal is one index descent, and no index was added here to make it so.
--
-- NOTHING IS ANNOUNCED WHEN NO LEASE IS LIVE. A release moves `expires_at` into the past rather
-- than deleting the row (PostgresLeaseStore's remarks say why), so a completing instance writes
-- this table too. Nothing holds a lease then, so nothing can lapse, and there is no instant to
-- arm: the query returns null and this returns without announcing. Waking the sweep on a release
-- would put a recovery pass behind every completed flow in the deployment, which is a stampede
-- built out of the ordinary case.
--
-- THE PAYLOAD IS THE SCHEMA AND AN INSTANT, exactly as 0013's timer announcement is, and for the
-- same two reasons: a channel name is database-wide so a listener has to be able to tell whose
-- announcement it is, and the instant is what the listener is actually waiting for. It carries no
-- instance id, because the sweep re-reads its own candidates and a lease id on the wire would be
-- a second version of a row that has an authoritative one.
--
-- ROW-LEVEL AND `UPDATE OF expires_at`, like 0013's instance trigger. The lease store writes one
-- row at a time — acquisition, renewal, release — so a statement-level trigger would save
-- nothing, and the column list keeps a future write of some other column on this table from
-- announcing something that did not happen.
--
-- `SET search_path FROM CURRENT` because this function, unlike 0013's two, reads a table. The
-- schema it must read is the one it was created in — under `TenantIsolation.Schema` every tenant
-- schema gets its own copy of this trigger and each must count its own leases — and pinning the
-- path at definition puts that beyond the reach of whatever the writing session's path happens to
-- be. The alternative, `format('%I', TG_TABLE_SCHEMA)` with `EXECUTE`, re-plans the lookup on
-- every renewal to answer a question that was already settled when the schema was migrated.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Additive in the same sense 0013 is: no
-- column changes, no index, no row rewritten, nothing a reader can observe in a query. A pod
-- running the previous release keeps acquiring, renewing and releasing leases exactly as it did —
-- it simply also announces them, to a listener that may be nobody. There is no backfill and there
-- cannot be one: an announcement is an instant, and the instants before this migration have
-- passed.
--
-- Deliberately NOT in this migration:
--
--   * An announcement of when the instance row goes idle. `FlowRecoveryScan` tests two things —
--     the lease is free AND `flow_instance.updated_at` is a TTL old — and only the first is on
--     this table. They coincide within a journal write for an instance whose holder died early,
--     which is what makes this hint worth having; they can be a renewal interval apart for one
--     that died mid-step, and there the wake is early, the pass finds nothing and the interval
--     does what it always did. Announcing the second would mean a trigger on every step boundary
--     of every flow, to buy back a case the backstop already covers.
--
--   * A trigger on delete. The row is never deleted (0001, and PostgresLeaseStore's first
--     paragraph): the fencing token has to survive release, so there is no delete to announce.

CREATE OR REPLACE FUNCTION flowx_notify_lease() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path FROM CURRENT
    AS $$
DECLARE
    lapses timestamptz;
BEGIN
    SELECT expires_at INTO lapses
      FROM flow_lease
     WHERE expires_at > now()
     ORDER BY expires_at
     LIMIT 1;

    IF lapses IS NULL THEN
        RETURN NULL;
    END IF;

    PERFORM pg_notify(
        'flowx_sweep_recovery',
        TG_TABLE_SCHEMA || '|' || (extract(epoch FROM lapses) * 1000)::bigint);

    RETURN NULL;
END;
$$;

CREATE TRIGGER flow_lease_expiry_notify
    AFTER INSERT OR UPDATE OF expires_at ON flow_lease
    FOR EACH ROW
    EXECUTE FUNCTION flowx_notify_lease();
