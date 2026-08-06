-- Campaigns, what they touched, what they cost, and who gets the credit.
--
-- WHY THE CREDIT IS NOT A COLUMN. Every marketing department eventually asks a database "which
-- campaign won this deal", and every database that answers with a stored number is answering a
-- different question from the one asked. Credit is not a fact about a deal — it is the output of a
-- model somebody chose, and four reasonable people will choose four different models. First touch
-- flatters the campaign that filled the top of the funnel; last touch flatters the one that sent
-- the final email; linear flatters everybody equally and nobody in particular. So what is stored
-- here is the touch — a thing that happened, at a time — and the credit is computed at the moment
-- somebody asks, under a model they named, and reported alongside the model's name so the number
-- can never be quoted without it.
--
-- WHAT THIS MAKES POSSIBLE. Two people can run the same report under two models and argue about
-- the models rather than about the data. A stored attribution number turns that argument into one
-- about whether the database is broken.
--
-- THE CUTOFF THAT NOBODY IMPLEMENTS. A touch after the deal closed did not win the deal. A
-- campaign that emailed a customer a month after they bought is claiming credit for a decision
-- already made, and a last-touch model with no cutoff will happily give it all of one. The cutoff
-- lives in the split, and it has a test whose only job is to fail if it goes.
--
-- WHY SPEND IS A LEDGER AND BUDGET IS A COLUMN. They are different questions. The budget is what
-- was approved; the spend is what happened. A return computed against the budget is a return
-- against a plan, which is the number that looks best and means least.

CREATE TABLE campaign (
    campaign_id uuid          NOT NULL PRIMARY KEY,
    tenant_id   text          NOT NULL,

    name        text          NOT NULL CHECK (name ~ '^[a-z][a-z0-9_]{0,62}$'),
    label       text          NOT NULL,

    channel     text          NOT NULL
        CHECK (channel IN ('Email', 'Event', 'Webinar', 'Paid', 'Content', 'Outbound', 'Partner')),

    starts_on   date          NOT NULL,
    ends_on     date          NOT NULL,

    -- What was approved. Not what was spent — that is the ledger below, and a return computed
    -- against this one is a return against a plan.
    budget      numeric(19,4) NOT NULL DEFAULT 0 CHECK (budget >= 0),

    is_active   boolean       NOT NULL DEFAULT true,
    created_at  timestamptz   NOT NULL,

    CHECK (ends_on >= starts_on),

    UNIQUE (tenant_id, name),
    UNIQUE (campaign_id, tenant_id)
);

CREATE INDEX campaign_by_window ON campaign (tenant_id, starts_on, ends_on);

-- WHAT ACTUALLY HAPPENED. One row per interaction between a campaign and a person. This is the
-- only fact in this file: everything a report says about influence is derived from these rows and
-- a model, and nothing here records an opinion about who won anything.
CREATE TABLE campaign_touch (
    touch_id    uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,
    campaign_id uuid        NOT NULL,

    -- A touch reaches a lead or a contact, never both and never neither. Before conversion the
    -- person is a lead; afterwards they are a contact, and the same campaign may have touched
    -- them in both lives.
    lead_id     uuid,
    contact_id  uuid,

    kind        text        NOT NULL
        CHECK (kind IN ('Sent', 'Opened', 'Clicked', 'Attended', 'Responded')),

    touched_at  timestamptz NOT NULL,

    CHECK ((lead_id IS NULL) <> (contact_id IS NULL)),

    -- One row per campaign, person and kind at an instant. A pipeline that replays a batch twice
    -- would otherwise double every open in the report, and an open is the denominator of a rate.
    --
    -- NULLS NOT DISTINCT is the whole point of writing it out. Every row here has one of the two
    -- person columns null, so an ordinary unique constraint — which treats each null as its own
    -- value — would permit every duplicate this is meant to stop, and would do it silently.
    UNIQUE NULLS NOT DISTINCT (tenant_id, campaign_id, lead_id, contact_id, kind, touched_at),

    FOREIGN KEY (campaign_id, tenant_id) REFERENCES campaign (campaign_id, tenant_id)
        ON DELETE CASCADE,
    FOREIGN KEY (lead_id, tenant_id)    REFERENCES lead (lead_id, tenant_id),
    FOREIGN KEY (contact_id, tenant_id) REFERENCES contact (contact_id, tenant_id)
);

CREATE INDEX campaign_touch_by_contact ON campaign_touch (tenant_id, contact_id, touched_at);
CREATE INDEX campaign_touch_by_lead    ON campaign_touch (tenant_id, lead_id, touched_at);

-- What was actually spent, as it was spent. Append-only by grant: a marketing spend somebody can
-- quietly revise downwards after the return is reported is not a ledger.
CREATE TABLE campaign_cost (
    campaign_id uuid          NOT NULL,
    tenant_id   text          NOT NULL,
    ordinal     int           NOT NULL CHECK (ordinal >= 0),

    incurred_on date          NOT NULL,
    amount      numeric(19,4) NOT NULL CHECK (amount > 0),
    note        text          NOT NULL CHECK (length(note) <= 500),
    recorded_by text          NOT NULL,
    recorded_at timestamptz   NOT NULL,

    PRIMARY KEY (campaign_id, ordinal),
    FOREIGN KEY (campaign_id, tenant_id) REFERENCES campaign (campaign_id, tenant_id)
        ON DELETE CASCADE
);

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON campaign       TO flowx_tenant;
GRANT SELECT, INSERT, UPDATE, DELETE ON campaign_touch TO flowx_tenant;

-- No UPDATE and no DELETE, for the reason above the table.
GRANT SELECT, INSERT ON campaign_cost TO flowx_tenant;

ALTER TABLE campaign ENABLE ROW LEVEL SECURITY;
ALTER TABLE campaign FORCE  ROW LEVEL SECURITY;

CREATE POLICY campaign_tenant_isolation ON campaign
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE campaign_touch ENABLE ROW LEVEL SECURITY;
ALTER TABLE campaign_touch FORCE  ROW LEVEL SECURITY;

CREATE POLICY campaign_touch_tenant_isolation ON campaign_touch
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

ALTER TABLE campaign_cost ENABLE ROW LEVEL SECURITY;
ALTER TABLE campaign_cost FORCE  ROW LEVEL SECURITY;

CREATE POLICY campaign_cost_tenant_isolation ON campaign_cost
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
