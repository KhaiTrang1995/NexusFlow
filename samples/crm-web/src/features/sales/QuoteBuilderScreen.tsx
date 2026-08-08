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
import { useEntityRecord, useRelatedRecords, useSubmitForApproval } from '@/api/queries/hooks'
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
 * NOTHING RE-PRICES AN ISSUED QUOTE, SO NOTHING HERE PRETENDS TO. The build's whole quote surface
 * is three capabilities — `crm.quote.issue`, `crm.quote.approve_discount`, `crm.order.place` — and
 * the first inserts a quote with its lines. There is no update. This screen used to answer that by
 * putting an input in every cell and an "Add line" button above them, and disclaiming it in a
 * panel-header note: "editing here changes nothing on it". A reader who types into a table expects
 * the typing to mean something, and a sentence over the table is not their agreement to lose it.
 *
 * SO THE TABLE IS READ-ONLY UNTIL THE READER ASKS FOR A SANDBOX. "Model a change" is the asking,
 * the banner is what they agreed to, and "Discard" puts the issued lines back. Before it is
 * pressed, every figure on this screen is one the server sent.
 *
 * AND THE SANDBOX HAS SOMEWHERE TO GO. Issuing the modelled lines makes a NEW quote against the
 * same opportunity, which is the only thing this build can do with them — said in those words,
 * because a seller who thinks they revised the quote the customer holds has been misled by the
 * one screen that could have told them otherwise.
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

  function openSandbox() {
    setDraft(issued)
    setDraftDiscount(String(recorded.given))
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

      {draft !== null ? (
        <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
          <div className={styles.warn}>
            <strong>This is a sandbox, and nothing in it is written.</strong> This build has no
            capability that re-prices an issued quote, so the lines below cannot replace the ones
            the customer holds. Issuing them makes a new quote against the same opportunity, and
            leaves this one exactly as it is.
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
                  The one capability that takes lines. It inserts a quote; it does not update one,
                  and the button says which of those it is about to do.
                */}
                <Button
                  tone="primary"
                  disabled={opportunityId === null || lines.length === 0}
                  title={
                    opportunityId === null
                      ? 'This quote names no opportunity to issue another against.'
                      : lines.length === 0
                        ? 'There are no lines to price.'
                        : 'Prices these lines as a new quote on the same opportunity. This one is unchanged.'
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
              <FieldRow label="Expires">{date(quote?.['valid_until'])}</FieldRow>
              <FieldRow label="Recorded discount">
                {quote === null ? '—' : fullMoney(recorded.given)}
              </FieldRow>
              <FieldRow label="Recorded total">
                {quote === null ? '—' : fullMoney(recorded.net)}
                <div className={styles.sub}>
                  What the server holds, whatever the panel above is modelling. This build cannot
                  re-price an issued quote.
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
