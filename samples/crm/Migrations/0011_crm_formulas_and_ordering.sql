-- Formula fields, and the ordering the query surface said it did not have.
--
-- ------------------------------------------------------------------ formulas
--
-- A formula field is computed from other fields of the SAME record, where a roll-up is computed
-- from other records. Both write into `values` and both set is_computed, so neither can be
-- written by a caller; what differs is where the inputs come from and therefore when it is
-- recomputed — a formula on every write of its own row, a roll-up when a child changes.
--
-- ONE OPERATION, TWO OPERANDS, NO NESTING. `(a + b) * c` is a tree, and this file has now refused
-- a tree three times for the same reason: a tree is a parser, a precedence rule and an evaluator
-- whose mistakes surface at run time. A formula whose operand is itself a formula is refused at
-- declaration, which is what keeps evaluation a single pass with no dependency graph and no
-- possibility of a cycle. An administrator who wants `(a + b) * c` declares `ab` and then
-- multiplies it — the intermediate is visible, nameable and checkable, which is better than a
-- parenthesis nobody can query.
--
-- SIX OPERATIONS. Each is a total function over the two types that reach it, and each has an
-- answer for the case where an operand is missing — see Formulas.Evaluate, which is where that
-- lives, because it has to be the same in the process and in nothing else. Nothing evaluates
-- formulas in SQL: unlike a filter, a formula is computed once at write time and stored.

CREATE TABLE custom_formula (
    formula_id  uuid        NOT NULL PRIMARY KEY,
    tenant_id   text        NOT NULL,

    -- The field that holds the answer. Marked computed, so no caller may write it.
    field_id    uuid        NOT NULL,

    operation   text        NOT NULL
        CHECK (operation IN ('Concat', 'Add', 'Subtract', 'Multiply', 'Divide', 'Coalesce')),

    -- The left operand is always a field of the same owner.
    left_field  text        NOT NULL,

    -- The right operand is a field or a constant, never both and never neither.
    right_field text,
    literal     text,

    created_at  timestamptz NOT NULL,

    CHECK ((right_field IS NULL) <> (literal IS NULL)),

    -- One formula per field, for the reason custom_rollup gives: two would be two answers with no
    -- rule about which wins.
    UNIQUE (field_id),

    FOREIGN KEY (field_id) REFERENCES custom_field (field_id) ON DELETE CASCADE
);

-- ------------------------------------------------------------------ ordering

-- Which way a saved view sorts.
--
-- Added because a list of the largest sites is the one anybody actually wants, and until now a
-- view could only sort ascending.
ALTER TABLE custom_list_view ADD COLUMN order_descending boolean NOT NULL DEFAULT false;

-- Whether the ordering is numeric.
--
-- WHY THIS IS STORED AND NOT DERIVED. QueryStore said the textual ordering was a real limit: `->>`
-- gives text, so a Number field sorts "9" after "42". Deriving it from the field's declared type
-- at query time is one more read and a decision about what to do when the declared type and the
-- stored value disagree — a row whose value will not cast is a row the sort throws on, and the
-- whole list fails. Storing the administrator's intent and casting defensively is what makes the
-- failure a row that sorts last rather than a page that does not load.
ALTER TABLE custom_list_view ADD COLUMN order_numeric boolean NOT NULL DEFAULT false;

-- ------------------------------------------------------------------ isolation

GRANT SELECT, INSERT, UPDATE, DELETE ON custom_formula TO flowx_tenant;

ALTER TABLE custom_formula ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_formula FORCE  ROW LEVEL SECURITY;

CREATE POLICY custom_formula_tenant_isolation ON custom_formula
    USING      (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''))
    WITH CHECK (tenant_id IS NOT DISTINCT FROM nullif(current_setting('flowx.tenant_id', true), ''));
