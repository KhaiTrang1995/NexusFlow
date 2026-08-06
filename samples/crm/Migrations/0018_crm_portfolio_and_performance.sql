-- Plans that nest, two more kinds of them, and what a board actually looks at.
--
-- WHY PLANS NEST. 0016 gave a group one number and a flat list of commitments against it. That is
-- the shape of a company with one sales team. A group with divisions, regions and named-account
-- teams plans at four levels, and the question "does the sum of my children add up to my number"
-- is asked at every one of them — not only at the top. A parent column makes the gap arithmetic
-- the roll-up already does work at any level.
--
-- WHAT A CYCLE WOULD DO, AND WHY THERE IS NO CONSTRAINT FOR IT. Two plans that are each other's
-- parent is two individually legal rows, exactly like the reporting line in 0017. The self-parent
-- case is a CHECK; longer ones are refused at the write and bounded by the recursive read.
--
-- WHY THE TWO NEW KINDS ARE KINDS AND NOT CUSTOM OBJECTS. A Portfolio is a commitment with a
-- number and children; an Operation is a commitment with a number and an activity to count. Both
-- roll up, and rolling up is what `plan` is for. A tenant modelling them as custom objects would
-- get rows that nothing adds together.

ALTER TABLE plan ADD COLUMN parent_plan_id uuid;

ALTER TABLE plan
    ADD CONSTRAINT plan_is_not_its_own_parent CHECK (parent_plan_id IS DISTINCT FROM plan_id);

ALTER TABLE plan
    ADD CONSTRAINT plan_parent_is_of_this_tenant
    FOREIGN KEY (parent_plan_id, tenant_id) REFERENCES plan (plan_id, tenant_id) ON DELETE SET NULL;

CREATE INDEX plan_by_parent ON plan (parent_plan_id);

-- ------------------------------------------------------------------ two more kinds

ALTER TABLE plan DROP CONSTRAINT plan_kind_check;

ALTER TABLE plan ADD CONSTRAINT plan_kind_check
    CHECK (kind IN ('Account', 'Opportunity', 'MarketingLead', 'Portfolio', 'Operation'));

-- What an Operation plan counts. One of the four activity kinds, because an operations team
-- commits to onboardings and support calls in the units the activity table already records — a
-- plan measured in anything else is a plan nothing can report against.
ALTER TABLE plan ADD COLUMN activity_kind text
    CHECK (activity_kind IS NULL OR activity_kind IN ('Task', 'Call', 'Meeting', 'Note'));

ALTER TABLE plan ADD COLUMN target_activities int
    CHECK (target_activities IS NULL OR target_activities >= 0);

ALTER TABLE plan ADD CONSTRAINT plan_operation_has_an_activity
    CHECK ((kind = 'Operation') = (activity_kind IS NOT NULL));

ALTER TABLE plan ADD CONSTRAINT plan_operation_has_an_activity_target
    CHECK ((kind = 'Operation') = (target_activities IS NOT NULL));

-- ONE OF 0016'S SIX AGREEMENTS HAS TO BE REPLACED, AND ONLY ONE. Its constraints are written as
-- `(kind = 'X') = (column IS NOT NULL)`, which stays true for a kind that has neither — so
-- Portfolio and Operation pass five of them unchanged. The exception is the money rule, written
-- as "MarketingLead is exactly the kind without a target amount". Operation is a second kind
-- without one, so that sentence is now false and is replaced by the one it always meant.
--
-- The name is PostgreSQL's, generated for an anonymous table CHECK, and it was read out of
-- pg_constraint rather than guessed. Dropping the wrong one here would silently remove a rule
-- nothing would fail on until the row it was there to stop.
ALTER TABLE plan DROP CONSTRAINT plan_check4;

ALTER TABLE plan ADD CONSTRAINT plan_money_belongs_to_revenue_kinds
    CHECK ((kind IN ('Account', 'Opportunity', 'Portfolio')) = (target_amount IS NOT NULL));
