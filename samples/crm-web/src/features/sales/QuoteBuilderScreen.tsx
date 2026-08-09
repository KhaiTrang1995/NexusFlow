import { useMemo, useState } from 'react'
import {
  Button,
  Columns,
  DataTable,
  FieldRow,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  Tag,
  TextField,
} from '@/design/primitives'
import { useNavigate } from '@tanstack/react-router'
import {
  useEntityRecord,
  useRelatedRecords,
  useRepriceQuote,
  useSubmitForApproval,
} from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { useSession } from '@/session/SessionProvider'
import { date, fullMoney, percent } from '@/lib/format'
import { IssueQuoteDrawer } from './IssueQuoteDrawer'
import styles from './QuoteBuilderScreen.module.css'

interface Line {
  id: string
  product: string
  quantity: number
  unitPrice: number
}

/**
 * The quote builder — what was issued, and a sandbox for asking what else it could have been.
 *
 * THE LINES ARE THE QUOTE'S OWN. Four written-out products used to sit here on every quote in the
 * tenant: a platform licence, a residency add-on, onboarding and support, priced identically
 * whatever the record. Reached from a real quote's "Open builder", that is not a placeholder — it
 * is four rows that look like the customer's and are not.
 *
 * A RE-PRICE SUPERSEDES, AND THE SCREEN SAYS SO IN THOSE WORDS. `crm.quote.reprice` writes a
 * revision carrying the new lines and retires the quote it replaces: the customer's copy keeps its
 * lines and its total, moves to Superseded, and no order can be taken against it. There is still
 * no update — nothing edits a quote in place, because a quote is a document that was sent — so a
 * reader who presses this gets two rows and a link between them, which is what they are told.
 *
 * THE TABLE IS READ-ONLY UNTIL THE READER ASKS FOR A SANDBOX. It used to be an input in every cell
 * with an "Add line" button above them, disclaimed in a panel-header note: "editing here changes
 * nothing on it". A reader who types into a table expects the typing to mean something, and a
 * sentence over the table is not their agreement to lose it. "Model a change" is the asking, the
 * banner is what they agreed to, and "Discard" puts the issued lines back. Before it is pressed,
 * every figure on this screen is one the server sent.
 *
 * AND THE SANDBOX HAS TWO PLACES TO GO, WHICH ARE NOT THE SAME ACT. Re-pricing replaces this
 * quote; issuing leaves it live and makes a second offer against the same opportunity. Both
 * existed before as one button that could only do the second, described as the first by anybody
 * who did not read its tooltip.
 *
 * ONE DISCOUNT, NOT ONE PER LINE, BECAUSE THAT IS WHAT THE SCHEMA HAS. A "Disc %" column used to
 * sit on each row; `quote_line` carries a sku, a quantity and a unit price, and the discount is
 * the quote's. A sandbox shaped unlike the server's model is a sandbox whose contents cannot be
 * issued, which is how a what-if becomes a dead end.
 *
 * WHETHER IT NEEDS APPROVING IS THE SERVER'S ANSWER, NOT A THRESHOLD WRITTEN HERE. It used to be
 * `rate > 0.2` in this file — a second copy of the one rule this sample keeps proving nobody
 * should write twice, on the screen where a seller reads it. The recorded quote is Draft or it is
 * not, and the server decided which.
 */
export function QuoteBuilderScreen({ quoteId }: { quoteId: string }) {
  const toast = useToast()
  const session = useSession()
  const submit = useSubmitForApproval()
  const reprice = useRepriceQuote()
  const navigate = useNavigate()

  const record = useEntityRecord('Quote', 'quote_id', quoteId)
  const priced = useRelatedRecords('QuoteLine', 'quote_id', quoteId)

  const quote = record.data?.records[0]?.values ?? null
  const opportunityId = quote?.['opportunity_id'] ?? null
  const currency = quote?.['currency'] ?? 'EUR'

  // Only so the drawer can name what it is quoting against. The id is the fallback, because a
  // drawer headed with a uuid is still better than one that waits for a name to arrive.
  const opportunity = useEntityRecord('Opportunity', 'opportunity_id', opportunityId)

  const issued = useMemo<readonly Line[]>(
    () =>
      (priced.data?.records ?? []).map((row) => ({
        id: row.recordId,
        product: row.values['sku'] ?? '—',
        quantity: Number(row.values['quantity'] ?? 0),
        unitPrice: Number(row.values['unit_price'] ?? 0),
      })),
    [priced.data],
  )

  // Null until the reader opens a sandbox, which is what makes the table read-only rather than
  // making every cell decide for itself. A `useEffect` seeding editable state from the server was
  // the previous shape, and it threw the reader's typing away whenever anything else refetched.
  const [draft, setDraft] = useState<readonly Line[] | null>(null)
  const [draftDiscount, setDraftDiscount] = useState('0')
  const [issuing, setIssuing] = useState(false)

  const lines = draft ?? issued

  const recorded = {
    list: Number(quote?.['subtotal'] ?? 0),
    given: Number(quote?.['discount'] ?? 0),
    net: Number(quote?.['total'] ?? 0),
  }

  /**
   * What the totals panel shows.
   *
   * READ-ONLY, THESE ARE THE SERVER'S THREE COLUMNS. They used to be arithmetic over the lines
   * with a zero discount, so a quote the server priced at 70,000 after 30,000 off reported a net
   * total of 100,000 and an effective rate of 0.0% — two figures the tenant never held, in the
   * panel headed "derived, never typed", beside the record's own.
   */
  const modelled = useMemo(() => {
    const list = lines.reduce((sum, line) => sum + line.quantity * line.unitPrice, 0)
    const given = Math.min(list, Math.max(0, Number(draftDiscount) || 0))

    return { list, given, net: list - given }
  }, [lines, draftDiscount])

  const totals = draft === null ? recorded : modelled
  const rate = totals.list === 0 ? null : totals.given / totals.list

  // The server's answer, not this file's. A quote it left in Draft is one it decided needs
  // clearing; anything else it issued.
  const isDraft = quote?.['status'] === 'Draft'

  // Terminal. A quote that has been replaced can be read and nothing else — no order, no
  // approval, and no second revision — so the screen offers none of them rather than offering a
  // button whose only outcome is a 409.
  const isSuperseded = quote?.['status'] === 'Superseded'
  const supersedes = quote?.['supersedes'] ?? null

  function openSandbox() {
    setDraft(issued)
    setDraftDiscount(String(recorded.given))
  }

  /**
   * Replaces this quote with the modelled lines.
   *
   * The two ids come back together, which is the only reason the toast can name what happened to
   * the one the reader has open. Then it opens the revision: leaving them on a Superseded quote
   * after they re-priced it is the screen refusing to say where their price went.
   */
  function supersede() {
    reprice.mutate(
      {
        quoteId,
        lines: lines.map((line) => ({
          sku: line.product,
          quantity: line.quantity,
          unitPrice: { amount: line.unitPrice, currency },
        })),
        discount: totals.given,
        validForDays: 21,
      },
      {
        onSuccess: (result) => {
          toast.saved(
            result.needsApproval
              ? `Re-priced at ${fullMoney(result.total.amount)}. The revision is ${result.status} — a manager has to approve the discount — and this quote is superseded.`
              : `Re-priced at ${fullMoney(result.total.amount)}. The revision is ${result.status.toLowerCase()} and this quote is superseded.`,
          )

          setDraft(null)
          void navigate({
            to: '/records/$object/$id',
            params: { object: 'quote', id: result.quoteId },
          })
        },
        onError: (error) => toast.failed(error, 'That quote was not re-priced.'),
      },
    )
  }

  return (
    <Page>
      <PageHeader
        eyebrow={`Quote · ${quoteId}`}
        title={quote === null ? 'Quote builder' : `Quote ${quoteId.slice(0, 8)}`}
        actions={
          <>
            <Button disabled title="This build renders no document for a quote.">
              Preview
            </Button>
            <Button
              tone="primary"
              disabled={submit.isPending}
              onClick={() =>
                submit.mutate(
                  { subject: 'Quote', id: quoteId },
                  {
                    onSuccess: (result) =>
                      toast.saved(
                        result.required
                          ? `Submitted. Waiting on ${result.awaitingLabel} under ${result.process}.`
                          : 'No approval is needed for this quote — it is under every threshold.',
                      ),
                    onError: (error) => toast.failed(error),
                  },
                )
              }
            >
              {submit.isPending ? 'Submitting…' : 'Submit for approval'}
            </Button>
          </>
        }
      />

      {isDraft ? (
        <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
          <div className={styles.warn}>
            <strong>The server left this quote in Draft.</strong> Its discount crossed the
            threshold, so it needs clearing before an order can be taken against it. Submitting
            asks the configured process who has to say yes — and a submitter can never be the one
            who approves it.
            {!session.can('crm.discount.approve') ? ' You do not hold the approval grant yourself.' : ''}
          </div>
        </Panel>
      ) : null}

      {isSuperseded ? (
        <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
          <div className={styles.warn}>
            <strong>This quote has been superseded.</strong> It was replaced by a re-priced quote
            and no order can be taken against it. What is below is what was offered on the day it
            was offered, which is why it is still here.
          </div>
        </Panel>
      ) : null}

      {draft !== null ? (
        <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
          <div className={styles.warn}>
            <strong>This is a sandbox, and nothing in it is written.</strong> Re-pricing replaces
            this quote: the lines below become a new one, and the one the customer holds keeps its
            own lines and moves to Superseded, where nothing can be ordered against it. Issuing
            instead leaves this quote live and makes a second offer beside it.
          </div>
        </Panel>
      ) : null}

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="Lines"
            note={
              draft === null
                ? `${lines.length} · as the server priced them`
                : `${lines.length} · modelled, not saved`
            }
            actions={
              draft === null ? (
                <Button size="sm" onClick={openSandbox}>
                  Model a change
                </Button>
              ) : (
                <>
                  <Button
                    size="sm"
                    onClick={() =>
                      setDraft((current) => [
                        ...(current ?? []),
                        {
                          // Not a server id: this row exists only in the sandbox. A key derived
                          // from the length repeats itself after a delete, which React resolves
                          // by reusing the wrong input.
                          id: `sandbox-${(current ?? []).length}-${(current ?? []).reduce((n, l) => n + l.quantity, 0)}`,
                          product: 'New line',
                          quantity: 1,
                          unitPrice: 0,
                        },
                      ])
                    }
                  >
                    Add line
                  </Button>
                  <Button size="sm" onClick={() => setDraft(null)}>
                    Discard
                  </Button>
                </>
              )
            }
          />
          <DataTable
            caption="Quote lines"
            className={styles.lines}
            rows={lines}
            rowKey={(row) => row.id}
            empty={
              draft === null
                ? 'This quote has no lines on the server.'
                : 'Nothing is being modelled. Add a line.'
            }
            columns={[
              {
                id: 'product',
                header: 'Product',
                cell: (row) =>
                  draft === null ? (
                    row.product
                  ) : (
                    <TextField
                      label="Product"
                      hideLabel
                      className={styles.lineInput}
                      value={row.product}
                      onChange={(event) => patch(row.id, { product: event.target.value })}
                    />
                  ),
              },
              {
                id: 'quantity',
                header: 'Qty',
                numeric: true,
                cell: (row) =>
                  draft === null ? (
                    row.quantity.toLocaleString('en-US')
                  ) : (
                    <TextField
                      label="Quantity"
                      hideLabel
                      type="number"
                      min={1}
                      className={styles.discountInput}
                      value={String(row.quantity)}
                      onChange={(event) => patch(row.id, { quantity: Number(event.target.value) })}
                    />
                  ),
              },
              {
                id: 'price',
                header: 'Unit price',
                numeric: true,
                cell: (row) =>
                  draft === null ? (
                    fullMoney(row.unitPrice)
                  ) : (
                    <TextField
                      label="Unit price"
                      hideLabel
                      type="number"
                      min={0}
                      className={styles.discountInput}
                      value={String(row.unitPrice)}
                      onChange={(event) => patch(row.id, { unitPrice: Number(event.target.value) })}
                    />
                  ),
              },
              {
                id: 'net',
                header: 'Line total',
                numeric: true,
                cell: (row) => fullMoney(row.quantity * row.unitPrice),
              },
            ]}
          />
        </Panel>

        <div>
          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader
              title="Totals"
              note={draft === null ? 'as the server priced it' : 'modelled, not saved'}
            />
            <div className={styles.totals}>
              <span className={styles.totalLabel}>List price</span>
              <span>{fullMoney(totals.list)}</span>
              <span className={styles.totalLabel}>Discount given</span>
              {draft === null ? (
                <span>−{fullMoney(totals.given)}</span>
              ) : (
                <TextField
                  label={`Discount (${currency})`}
                  hideLabel
                  type="number"
                  min={0}
                  className={styles.discountInput}
                  value={draftDiscount}
                  onChange={(event) => setDraftDiscount(event.target.value)}
                />
              )}
              <span className={styles.totalLabel}>Effective rate</span>
              <span>
                <Tag tone="outline">{percent(rate, 1)}</Tag>
              </span>
              <span className={styles.grandLabel}>Net total</span>
              <span className={styles.grand}>{fullMoney(totals.net)}</span>
            </div>

            {draft !== null ? (
              <PanelBody>
                {/*
                  Two acts, not one. Re-pricing replaces this quote and links the two; issuing
                  leaves it live and makes a second offer. Neither edits a quote in place, and
                  each button says which of the two it is.
                */}
                <Button
                  tone="primary"
                  disabled={
                    isSuperseded || lines.length === 0 || reprice.isPending
                  }
                  title={
                    isSuperseded
                      ? 'This quote has already been replaced, so it cannot be re-priced again.'
                      : lines.length === 0
                        ? 'There are no lines to price.'
                        : 'Replaces this quote: these lines become a new one and this is superseded.'
                  }
                  onClick={supersede}
                >
                  {reprice.isPending ? 'Re-pricing…' : 'Re-price and supersede'}
                </Button>
                <Button
                  disabled={opportunityId === null || lines.length === 0}
                  title={
                    opportunityId === null
                      ? 'This quote names no opportunity to issue another against.'
                      : lines.length === 0
                        ? 'There are no lines to price.'
                        : 'Prices these lines as a second quote on the same opportunity. This one stays live.'
                  }
                  onClick={() => setIssuing(true)}
                >
                  Issue as a new quote
                </Button>
              </PanelBody>
            ) : null}
          </Panel>

          <Panel padding="flush">
            <PanelHeader title="On the record" note={quote?.['status'] ?? '—'} />
            <PanelBody style={{ padding: 0 }}>
              <FieldRow label="Quote">{quoteId}</FieldRow>
              <FieldRow label="Opportunity">{quote?.['opportunity_id'] ?? '—'}</FieldRow>
              {/*
                The link between the two rows, from the row itself. A revision that did not say
                what it replaced would look like an unrelated second quote for the same money.
              */}
              {supersedes === null ? null : (
                <FieldRow label="Supersedes">
                  {supersedes}
                  <div className={styles.sub}>
                    This quote re-prices that one, which can no longer be ordered against.
                  </div>
                </FieldRow>
              )}
              <FieldRow label="Expires">{date(quote?.['valid_until'])}</FieldRow>
              <FieldRow label="Recorded discount">
                {quote === null ? '—' : fullMoney(recorded.given)}
              </FieldRow>
              <FieldRow label="Recorded total">
                {quote === null ? '—' : fullMoney(recorded.net)}
                <div className={styles.sub}>
                  What the server holds, whatever the panel above is modelling. Re-pricing writes a
                  second quote and retires this one; nothing edits these figures in place.
                </div>
              </FieldRow>
            </PanelBody>
          </Panel>
        </div>
      </Columns>

      {issuing && opportunityId !== null ? (
        <IssueQuoteDrawer
          opportunityId={opportunityId}
          opportunityName={
            opportunity.data?.records[0]?.values['name'] ?? `Opportunity ${opportunityId.slice(0, 8)}`
          }
          currency={currency}
          initialLines={lines.map((line) => ({
            sku: line.product,
            quantity: line.quantity,
            unitPrice: { amount: line.unitPrice, currency },
          }))}
          initialDiscount={totals.given}
          onClose={() => setIssuing(false)}
        />
      ) : null}
    </Page>
  )

  function patch(id: string, change: Partial<Line>) {
    setDraft((current) =>
      (current ?? []).map((line) => (line.id === id ? { ...line, ...change } : line)),
    )
  }
}
