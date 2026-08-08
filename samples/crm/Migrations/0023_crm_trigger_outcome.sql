-- What the configured process did with a trigger somebody applied, and when it did it.
--
-- WHAT WAS WRONG. `crm.opportunity.advance` checks the opportunity is the caller's, stages
-- `opportunity.stage.changed` and answers 200. The transition is decided afterwards, off the
-- change feed, by the definition an administrator wrote — and `RunConfiguredTransition` returned
-- its answer to a flow that threw it away. So two completely different things were
-- indistinguishable to every caller: the feed has not reached the change yet, and the engine ran
-- and declined to move anything, because no transition matched the trigger from that stage or a
-- guard did not hold. Both are successful outcomes of an event-driven engine, so there was no
-- error to catch; the web client papered over it by polling the record six times and reporting
-- whatever stage it happened to find, which is a guess in the first case and right only in the
-- second.
--
-- WHAT THIS DOES NOT CHANGE. Not when the transition runs. The advance writes a row here in the
-- same breath as it stages the event, and the engine updates that row when it decides. The seam
-- between the two is exactly where it was; what is new is that the seam now leaves a record
-- somebody can read.
--
-- WHY `outcome` DEFAULTS TO 'Pending' RATHER THAN BEING NULL. Undecided is a state of the
-- application, not an absent value, and it is the state the whole table exists to make sayable.
-- A nullable column would put the three-way answer back into the reader's head — which is where
-- it was, and where it was got wrong.
--
-- WHY from_stage_id IS RECORDED RATHER THAN LOOKED UP LATER. Where the deal is now is not where
-- it was when the trigger was applied: a second application moves it, and a sweep can. A declined
-- application has to be able to say which stage the engine declined to move it out of, and a
-- reader who joined to the live row would be told about somebody else's move.

CREATE TABLE opportunity_trigger (
    -- One application of one trigger. Minted by the advance and handed back to the caller, so a
    -- client asks about the act it performed rather than about the opportunity — two people
    -- pressing advance a second apart are two rows, and neither reads the other's answer.
    application_id uuid        NOT NULL PRIMARY KEY,
    tenant_id      text        NOT NULL,
    opportunity_id uuid        NOT NULL,

    trigger        text        NOT NULL,
    from_stage_id  uuid        NOT NULL REFERENCES process_stage (stage_id),
    applied_at     timestamptz NOT NULL,

    outcome        text        NOT NULL DEFAULT 'Pending'
        CHECK (outcome IN ('Pending', 'Moved', 'Declined')),

    transition_id  uuid        REFERENCES process_transition (transition_id),
    to_stage_id    uuid        REFERENCES process_stage (stage_id),
    actions_run    int         NOT NULL DEFAULT 0 CHECK (actions_run >= 0),
    decided_at     timestamptz,

    -- The three states, spelt out so a half-written decision cannot be stored. An engine that
    -- moved a deal and forgot to say where, or declared a move with no transition behind it, is a
    -- bug this refuses rather than a row somebody reads later and cannot explain.
    CHECK ((outcome = 'Pending') = (decided_at IS NULL)),
    CHECK ((outcome = 'Moved')   = (to_stage_id IS NOT NULL)),
    CHECK ((outcome = 'Moved')   = (transition_id IS NOT NULL)),
    CHECK (outcome <> 'Declined' OR actions_run = 0),

    FOREIGN KEY (opportunity_id, tenant_id) REFERENCES opportunity (opportunity_id, tenant_id)
        ON DELETE CASCADE
);

-- What a record screen asks: what has been applied to this deal, most recent first.
CREATE INDEX opportunity_trigger_by_opportunity
    ON opportunity_trigger (opportunity_id, applied_at DESC);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON opportunity_trigger TO flowx_tenant;

ALTER TABLE opportunity_trigger ENABLE ROW LEVEL SECURITY;
ALTER TABLE opportunity_trigger FORCE  ROW LEVEL SECURITY;

CREATE POLICY opportunity_trigger_tenant_isolation ON opportunity_trigger
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
