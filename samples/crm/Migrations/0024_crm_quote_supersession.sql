-- Re-pricing a quote, without touching the one the customer already holds.
--
-- WHAT WAS WRONG. The whole quote surface was issue, approve-the-discount and place-the-order.
-- `IssueQuote` inserts a quote with its lines and there is no update, so a customer who asked for
-- a different price left the seller two options: neither of them right. Issuing a second quote
-- leaves two live offers against one opportunity with nothing saying which is current — and the
-- superseded one can still be ordered against, at the old price. Updating the row in place would
-- rewrite the document somebody is holding, which is not a re-price, it is a denial that the
-- first price was ever quoted.
--
-- WHY A NEW ROW AND A POINTER, RATHER THAN A VERSION COLUMN. A quote is a document that was sent.
-- Its lines, its total and the discount somebody approved are the record of what was offered on
-- the day it was offered, and an order joins to it. Bumping a version on the same row loses all
-- of that; a second row that names the one it replaces keeps both, and the chain reads in the
-- direction somebody asks it in — "what replaced this?" is one index lookup and "what did this
-- replace?" is the column itself.
--
-- WHY 'Superseded' IS A STATUS AND NOT A FLAG. `quote.status` is already where "this quote can no
-- longer be acted on" lives: `PlaceOrderForQuote` refuses anything that is not `Issued`, and
-- `ApproveQuoteDiscountCapability` refuses anything that is not `Draft`. A boolean beside the
-- status would be a second answer to a question the status answers, and the two disagree the
-- first time one of them is set without the other. Terminal, like Rejected and Expired: nothing
-- moves a quote out of it.

ALTER TABLE quote ADD COLUMN supersedes uuid;

-- Whose quote it replaces has to be a quote of the same tenant. The pair is what `quote`'s
-- `UNIQUE (quote_id, tenant_id)` exists for, and the composite key is what makes crossing a
-- tenant boundary impossible rather than merely refused by the application.
ALTER TABLE quote
    ADD CONSTRAINT quote_supersedes_a_quote_of_this_tenant
    FOREIGN KEY (supersedes, tenant_id) REFERENCES quote (quote_id, tenant_id);

ALTER TABLE quote
    ADD CONSTRAINT quote_does_not_supersede_itself CHECK (supersedes IS DISTINCT FROM quote_id);

-- AT MOST ONE REPLACEMENT PER QUOTE, which is the same shape §6 draws between a quote and its
-- order. Two revisions of one quote is two current prices and a superseded row that cannot say
-- which of them the customer should be reading. Nulls are distinct in a unique index, so every
-- quote that replaces nothing is unaffected.
CREATE UNIQUE INDEX quote_is_superseded_once ON quote (supersedes);

-- The name is PostgreSQL's, generated for the column CHECK in 0001, and it was read out of
-- pg_constraint rather than guessed — dropping the wrong one would silently remove a rule nothing
-- fails on until the row it was there to stop.
ALTER TABLE quote DROP CONSTRAINT quote_status_check;

ALTER TABLE quote ADD CONSTRAINT quote_status_check
    CHECK (status IN ('Draft', 'Issued', 'Accepted', 'Rejected', 'Expired', 'Superseded'));

-- ------------------------------------------------------------------ isolation
--
-- No GRANT and no policy: `quote` has both from 0001 and 0002, and a column added to a table
-- inherits the row-level security of the rows it sits in. A migration that re-granted here would
-- read as though this table's isolation were this migration's doing.
