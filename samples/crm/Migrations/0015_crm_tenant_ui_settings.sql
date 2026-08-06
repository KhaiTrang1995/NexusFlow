-- What a tenant changes about how their CRM looks, without changing what it is.
--
-- Two things belong here and they are related only by who does them: an administrator in the
-- settings screen, at run time, with no deployment.
--
--   WHAT THINGS ARE CALLED. A custom object and a custom field have carried a `label` since 0005
--   and nothing has ever read it back — the describe API returned the identifier twice, so every
--   client drew `floor_area` where somebody had typed "Floor area". The built-in entities had no
--   label at all: a tenant who calls a Lead an "Enquiry" had no way to say so. Both are fixed
--   here, and renaming is an update rather than a new row, because a name is one fact.
--
--   HOW A LIST IS DRAWN. A saved view has been a query with a name. A client that gets a query
--   back has to decide by itself whether to draw a table, a board or a stack of cards — so the
--   decision was made three times, once per client, differently. The view now carries it, along
--   with what each shape needs: the lane a board groups by, the fields a card shows, the columns
--   a table shows in order.
--
-- WHY THE PER-SHAPE SETTINGS ARE COLUMNS AND NOT A jsonb BLOB. The obvious shortcut is one
-- `settings jsonb` and a client that knows what to find in it. Columns instead, because every one
-- of these names a field, and a view naming a field that was never declared is a board that
-- renders one empty lane at nine in the morning. A column can be checked when the view is saved;
-- a blob can only be checked by whoever opens it.

-- ------------------------------------------------------------------ what things are called

CREATE TABLE entity_label (
    tenant_id text NOT NULL,

    kind      text NOT NULL CHECK (kind IN ('Lead', 'Account', 'Contact', 'Opportunity')),

    -- The built-in column being renamed, or the empty string for the entity itself. Empty and not
    -- null: a null cannot be part of a primary key, and a partial unique index over a nullable
    -- column would let one tenant give one entity two names.
    field     text NOT NULL CHECK (field = '' OR field ~ '^[a-z][a-z0-9_]{0,62}$'),

    label     text NOT NULL CHECK (length(label) BETWEEN 1 AND 128),

    PRIMARY KEY (tenant_id, kind, field)
);

-- ------------------------------------------------------------------ how a list is drawn

-- List is the default, so every view saved before this migration keeps behaving as it did.
ALTER TABLE custom_list_view ADD COLUMN kind text NOT NULL DEFAULT 'List'
    CHECK (kind IN ('List', 'Kanban', 'Card'));

-- Kanban: which field's value is the lane, the order the lanes appear in, and what counts as too
-- many cards in one of them.
ALTER TABLE custom_list_view ADD COLUMN group_by  text;
ALTER TABLE custom_list_view ADD COLUMN lanes     text[];
ALTER TABLE custom_list_view ADD COLUMN wip_limit int
    CHECK (wip_limit IS NULL OR wip_limit > 0);

-- Card: the two lines a card shows. The subtitle is optional; a card with no title is a card with
-- nothing on it, so a Card view without one is refused.
ALTER TABLE custom_list_view ADD COLUMN title_field    text;
ALTER TABLE custom_list_view ADD COLUMN subtitle_field text;

-- List: which fields are columns, in the order they appear. Null means every declared field,
-- which is what a view saved before this migration meant and still means.
ALTER TABLE custom_list_view ADD COLUMN display_columns text[];

-- A board with no lane field and a card with no title are the two shapes that cannot be drawn at
-- all. Enforced here as well as at the save, because the save is one code path and the table is
-- what every code path shares.
ALTER TABLE custom_list_view
    ADD CONSTRAINT custom_list_view_kanban_groups_by
    CHECK ((kind = 'Kanban') = (group_by IS NOT NULL));

ALTER TABLE custom_list_view
    ADD CONSTRAINT custom_list_view_card_has_a_title
    CHECK ((kind = 'Card') = (title_field IS NOT NULL));

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON entity_label TO flowx_tenant;

ALTER TABLE entity_label ENABLE ROW LEVEL SECURITY;
ALTER TABLE entity_label FORCE  ROW LEVEL SECURITY;

CREATE POLICY entity_label_tenant_isolation ON entity_label
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));

-- ------------------------------------------------------------------ multiple choices

-- A field whose value is several of the declared options rather than one. The value is a jsonb
-- array in `values`, so the GIN index over that column already answers "which records chose gold"
-- — which is why it is an array and not a delimited string.
--
-- The option rows are the same ones a Picklist uses: 0006's custom_field_option, unchanged. What
-- differs is how many of them one record may hold, and that is a property of the field's type.
ALTER TABLE custom_field DROP CONSTRAINT custom_field_data_type_check;

ALTER TABLE custom_field ADD CONSTRAINT custom_field_data_type_check
    CHECK (data_type IN ('Text', 'Number', 'Boolean', 'Date',
                         'Picklist', 'Reference', 'MultiPicklist'));
