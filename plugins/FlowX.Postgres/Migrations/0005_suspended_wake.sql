-- Expand: record which wait a suspended instance is parked at, and when it is due.
--
-- 0001 gave flow_instance a `Suspended` state and nothing that says what the instance is
-- suspended *until*. That was accurate for the release that shipped it: a wait ended when a
-- signal arrived, and the duration an author wrote on `.AwaitSignal<T>(timeout)` reached the
-- compiled plan and was armed by nothing at all. `.Delay(...)` did not exist as a step. So
-- there was nothing to record, and a parked instance was — correctly — a row nothing on a
-- clock would ever look at again.
--
-- These three columns are the whole of a durable timer, and there is deliberately no timer
-- table. A wait is a property of the instance that is waiting: one row, no thread, no pooled
-- context and no lease. A separate table would need its own key, its own foreign key, its own
-- retention rule and its own transaction with the instance write that creates it — and that
-- last one is not optional, because an instance parked with no timer scheduled is a wait that
-- never ends, which is the defect this migration exists to remove. Putting the values on the
-- row it describes makes the two writes one write, for free.
--
--   wake_at      when the instance must be picked up again.
--   wake_step_id which step in the compiled plan it is parked at.
--   wake_scope   which iteration of which loop that step is running in, spelled exactly as
--                flow_step.scope spells it: '' for the flow body, '7' for the eighth element
--                of a ForEach, '7/2' for an element of a loop nested inside it.
--
-- The step and the scope are not for the sweep — that asks only "is this due". They are for
-- the engine, which walks the plan from the frontier, arrives at a suspension point, and has
-- to know whether the instant on the row belongs to *that* wait or to one the instance has
-- since finished. Without the identity the answer is a guess, and the guess is wrong in
-- exactly the case that matters: a flow whose first wait was satisfied late, reaching its
-- second, would read a stale instant already in the past and walk straight through a wait
-- that had not started.
--
-- Expand/contract (docs/11-Distributed-Runtime.md §7.4). Every statement here is additive.
-- All three columns are nullable with no default, so a pod running the previous release keeps
-- inserting and updating flow_instance without knowing they exist — its rows simply carry
-- NULL, which reads as "nothing is due to wake this instance" and is precisely what was true
-- of every instance that release ever parked. There is no backfill for the same reason: a
-- wait the previous release did not arm has no instant to derive, and inventing one would
-- wake instances whose flows have no timer in them. Those instances keep the behaviour they
-- were written under — a signal, or the flow's own [FlowDeadline] — until something moves
-- them on.
--
-- One operational note, the same one 0003 and 0004 carry. ADD COLUMN with no default rewrites
-- no rows in PostgreSQL 11 and later, so the three ALTERs are catalogue-only; the CREATE INDEX
-- below builds in the migrator's transaction and holds a lock excluding writers on
-- flow_instance for the duration. On a table with a large backlog that is a pause in step
-- commits, and it is the reason a migration is a deployment step rather than a side effect of
-- a container starting.

ALTER TABLE flow_instance ADD COLUMN wake_at      timestamptz;
ALTER TABLE flow_instance ADD COLUMN wake_step_id int;
ALTER TABLE flow_instance ADD COLUMN wake_scope   text;

-- A wake instant with no step to belong to would be an instance the engine cannot decide
-- about, and a step with no instant would be a wait nothing is due to end. Neither is a shape
-- a writer should be able to produce, and CHECK is where "cannot be produced" is written down
-- in a database rather than in a comment.
ALTER TABLE flow_instance ADD CONSTRAINT flow_instance_wake_check CHECK (
    (wake_at IS NULL AND wake_step_id IS NULL AND wake_scope IS NULL) OR
    (wake_at IS NOT NULL AND wake_step_id IS NOT NULL AND wake_scope IS NOT NULL));

ALTER TABLE flow_instance ADD CONSTRAINT flow_instance_wake_step_check CHECK (
    wake_step_id IS NULL OR wake_step_id >= 0);

-- The timer sweep's query, exactly as PostgresTimerIndex asks it: parked instances that are
-- due, longest overdue first, bounded by a page.
--
-- Partial on the same two facts the query states, so the planner can prove the query's
-- predicate implies the index's and serve it as an ordered index scan rather than a
-- sequential scan and a top-N sort over every instance ever written. `wake_at <= @due_before`
-- implies `wake_at IS NOT NULL`, which is what makes the second half of the predicate
-- provable rather than merely true. PostgresTimerIndex.Suspended is the same decision written
-- on the other side, and TheDueQueryIsServedByAnIndexAndNotBySortingTheTable is what keeps
-- the two in step.
--
-- Deliberately not folded into flow_instance_abandoned_idx from 0003. That index covers
-- ('Pending', 'Running', 'Compensating') ordered by updated_at — the instances a node died
-- holding, ordered by staleness. This one covers the state that one excludes, ordered by an
-- instant the instance chose. One index cannot serve both, and a disjunction over the two
-- predicates would serve neither.
CREATE INDEX flow_instance_due_idx ON flow_instance (wake_at)
    WHERE state = 'Suspended' AND wake_at IS NOT NULL;
