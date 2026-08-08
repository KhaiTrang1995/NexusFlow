import { useEffect, useMemo, useState } from 'react'
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
import styles from './QuoteBuilderScreen.module.css'

interface Line {
  id: string
  product: string
  quantity: number
  unitPrice: number
  discount: number
}

/**
 * The quote builder — a what-if over a quote that exists.
 *
 * THE LINES ARE THE QUOTE'S OWN. Four written-out products used to sit here on every quote in the
 * tenant: a platform licence, a residency add-on, onboarding and support, priced identically
 * whatever the record. Reached from a real quote's "Open builder", that is not a placeholder — it
 * is four rows that look like the customer's and are not.
 *
 * WHAT IS EDITED HERE IS NOT SAVED, AND THE PANEL ON THE RIGHT IS WHY. The recorded status, total
 * and expiry come from the server; the arithmetic on the left is what the quote would become. This
 * build has no capability to re-price an issued quote, so a builder that appeared to save would be
 * the worst of the three possibilities.
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

  const [lines, setLines] = useState<readonly Line[]>([])

  // Seeded from the server once, then the reader's to edit. A `useMemo` would throw away every
  // change the moment anything else on the page refetched.
  useEffect(() => {
    if (priced.data === undefined) {
      return
    }

    setLines(
      priced.data.records.map((row) => ({
        id: row.recordId,
        product: row.values['sku'] ?? '—',
        quantity: Number(row.values['quantity'] ?? 0),
        unitPrice: Number(row.values['unit_price'] ?? 0),

        // A line's discount is not a column: the schema discounts the quote, not the line. Zero
        // here is the truth, and the reader may change it to ask what-if.
        discount: 0,
      })),
    )
  }, [priced.data])

  const totals = useMemo(() => {
    const list = lines.reduce((sum, line) => sum + line.quantity * line.unitPrice, 0)
    const net = lines.reduce(
      (sum, line) => sum + line.quantity * line.unitPrice * (1 - line.discount / 100),
      0,
    )
    return { list, net, given: list - net, rate: list === 0 ? 0 : (list - net) / list }
  }, [lines])

  // The server's answer, not this file's. A quote it left in Draft is one it decided needs
  // clearing; anything else it issued.
  const isDraft = quote?.['status'] === 'Draft'

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

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="Lines"
            note={`${lines.length} · from the record; editing here changes nothing on it`}
            actions={
              <Button
                size="sm"
                onClick={() =>
                  setLines((current) => [
                    ...current,
                    {
                      // Not a server id: this row exists only in the sandbox. A key derived from
                      // the length repeats itself after a delete, which React resolves by
                      // reusing the wrong input.
                      id: `sandbox-${current.length}-${current.reduce((n, l) => n + l.quantity, 0)}`,
                      product: 'New line',
                      quantity: 1,
                      unitPrice: 0,
                      discount: 0,
                    },
                  ])
                }
              >
                Add line
              </Button>
            }
          />
          <DataTable
            caption="Quote lines"
            className={styles.lines}
            rows={lines}
            rowKey={(row) => row.id}
            columns={[
              {
                id: 'product',
                header: 'Product',
                cell: (row) => (
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
                cell: (row) => (
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
                cell: (row) => (
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
                id: 'discount',
                header: 'Disc %',
                numeric: true,
                cell: (row) => (
                  <TextField
                    label="Discount"
                    hideLabel
                    type="number"
                    min={0}
                    max={100}
                    className={styles.discountInput}
                    value={String(row.discount)}
                    onChange={(event) => patch(row.id, { discount: Number(event.target.value) })}
                  />
                ),
              },
              {
                id: 'net',
                header: 'Net',
                numeric: true,
                cell: (row) => fullMoney(row.quantity * row.unitPrice * (1 - row.discount / 100)),
              },
            ]}
          />
        </Panel>

        <div>
          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader title="Totals" note="derived, never typed" />
            <div className={styles.totals}>
              <span className={styles.totalLabel}>List price</span>
              <span>{fullMoney(totals.list)}</span>
              <span className={styles.totalLabel}>Discount given</span>
              <span>−{fullMoney(totals.given)}</span>
              <span className={styles.totalLabel}>Effective rate</span>
              <span>
                <Tag tone="outline">{percent(totals.rate, 1)}</Tag>
              </span>
              <span className={styles.grandLabel}>Net total</span>
              <span className={styles.grand}>{fullMoney(totals.net)}</span>
            </div>
          </Panel>

          <Panel padding="flush">
            <PanelHeader title="On the record" note={quote?.['status'] ?? '—'} />
            <PanelBody style={{ padding: 0 }}>
              <FieldRow label="Quote">{quoteId}</FieldRow>
              <FieldRow label="Opportunity">{quote?.['opportunity_id'] ?? '—'}</FieldRow>
              <FieldRow label="Expires">{date(quote?.['valid_until'])}</FieldRow>
              <FieldRow label="Recorded discount">
                {quote === null ? '—' : fullMoney(Number(quote['discount']))}
              </FieldRow>
              <FieldRow label="Recorded total">
                {quote === null ? '—' : fullMoney(Number(quote['total']))}
                <div className={styles.sub}>
                  What the server holds. The builder on the left is arithmetic about what it could
                  become, and this build cannot re-price an issued quote.
                </div>
              </FieldRow>
            </PanelBody>
          </Panel>
        </div>
      </Columns>
    </Page>
  )

  function patch(id: string, change: Partial<Line>) {
    setLines((current) => current.map((line) => (line.id === id ? { ...line, ...change } : line)))
  }
}
