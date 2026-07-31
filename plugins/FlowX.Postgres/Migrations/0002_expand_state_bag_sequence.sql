-- Expand: record which commit the instance's state-bag snapshot belongs to.
--
-- ADR-0015 names the state-bag snapshot as the mitigation for budget B8 — "the mitigation
-- is the state-bag snapshot on the instance row, which bounds the scan to rows committed
-- after it" — and then gives it no column to be bounded by. A snapshot whose position is
-- unknown bounds nothing: a resume still has to read every row to find out which ones the
-- snapshot already covers. This adds the position.
--
-- It is also this schema's worked example of the expand/contract rule in
-- docs/11-Distributed-Runtime.md §7.4: add nullable, backfill, switch reads, drop later —
-- never a breaking migration in one release. Every statement below is additive. A pod
-- running the previous release keeps inserting and updating rows without knowing this
-- column exists, because it is nullable and has no default that would change a row it
-- did not write. That coexistence is asserted, not asserted-to, by
-- MigrationTests.TheExpandMigrationDoesNotBreakTheReleaseBeforeIt.

ALTER TABLE flow_instance ADD COLUMN state_bag_sequence bigint;

-- Backfill. An instance that already carries a snapshot got it from its most recent
-- commit, because that is the only writer of the column. An instance with no snapshot is
-- left NULL rather than zero: "never snapshotted" and "snapshotted at sequence 0" are
-- different facts, and the resume scan reads them differently.
UPDATE flow_instance i
SET state_bag_sequence = (
        SELECT max(s.sequence) FROM flow_step s WHERE s.instance_id = i.instance_id)
WHERE i.state_bag IS NOT NULL
  AND i.state_bag_sequence IS NULL;

-- Supports the recovery scan WP-55 adds: find instances that are not finished and have
-- not been touched recently. Partial, so it indexes the live minority rather than the
-- completed majority that retention will remove anyway.
CREATE INDEX flow_instance_recovery_idx ON flow_instance (state, updated_at)
    WHERE state IN ('Pending', 'Running', 'Suspended', 'Compensating');
