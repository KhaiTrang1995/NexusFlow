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
import { useSubmitForApproval } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { useSession } from '@/session/SessionProvider'
import { fullMoney, percent } from '@/lib/format'
import { modelFor } from '@/fixtures/objects'
import styles from './QuoteBuilderScreen.module.css'

interface Line {
  id: string
  product: string
  quantity: number
  unitPrice: number
  discount: number
}

const DEFAULT_LINES: readonly Line[] = [
  { id: 'l1', product: 'Platform licence — Enterprise', quantity: 120, unitPrice: 1_150, discount: 8 },
  { id: 'l2', product: 'Data residency add-on (EU)', quantity: 1, unitPrice: 24_000, discount: 0 },
  { id: 'l3', product: 'Onboarding — managed', quantity: 1, unitPrice: 18_000, discount: 15 },
  { id: 'l4', product: 'Premium support', quantity: 12, unitPrice: 1_400, discount: 5 },
]

/**
 * The quote builder.
 *
 * THE THRESHOLD IS THE FEATURE. A quote over the discount threshold needs an approval, and the
 * screen says so *before* the seller sends it rather than refusing afterwards. The submission goes
 * to the real approvals surface, which answers whether an approval is needed at all — and "no
 * approval needed" is a real answer that is said out loud rather than looking like a failed call.
 *
 * THE TOTAL IS DERIVED, NEVER TYPED. Every line's discount folds into one number, and the header
 * discount is that number expressed against the list price. A quote whose total could be edited
 * independently of its lines is a quote nobody can reconcile.
 */
export function QuoteBuilderScreen({ quoteId }: { quoteId: string }) {
  const toast = useToast()
  const session = useSession()
  const submit = useSubmitForApproval()

  const quote = modelFor('quote').records.find((row) => row.id === quoteId)
  const [lines, setLines] = useState<readonly Line[]>(DEFAULT_LINES)

  const totals = useMemo(() => {
    const list = lines.reduce((sum, line) => sum + line.quantity * line.unitPrice, 0)
    const net = lines.reduce(
      (sum, line) => sum + line.quantity * line.unitPrice * (1 - line.discount / 100),
      0,
    )
    return { list, net, given: list - net, rate: list === 0 ? 0 : (list - net) / list }
  }, [lines])

  // The sample's own threshold: over twenty per cent needs somebody senior to say yes.
  const needsApproval = totals.rate > 0.2

  return (
    <Page>
      <PageHeader
        eyebrow={`Quote · ${quoteId}`}
        title={String(quote?.['opportunity'] ?? 'Quote builder')}
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

      {needsApproval ? (
        <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
          <div className={styles.warn}>
            <strong>{percent(totals.rate, 1)} discount.</strong> Over the twenty per cent threshold,
            so this quote needs an approval before it can be sent. Submitting it asks the configured
            process who has to say yes — and a submitter can never be the one who approves it.
            {!session.can('crm.discount.approve') ? ' You do not hold the approval grant yourself.' : ''}
          </div>
        </Panel>
      ) : null}

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="Lines"
            note={`${lines.length} · edit a quantity or a discount and the total follows`}
            actions={
              <Button
                size="sm"
                onClick={() =>
                  setLines((current) => [
                    ...current,
                    {
                      id: `l${current.length + 1}`,
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
                <Tag tone={needsApproval ? 'warning' : 'positive'}>{percent(totals.rate, 1)}</Tag>
              </span>
              <span className={styles.grandLabel}>Net total</span>
              <span className={styles.grand}>{fullMoney(totals.net)}</span>
            </div>
          </Panel>

          <Panel padding="flush">
            <PanelHeader title="Quote" note={String(quote?.['status'] ?? 'Draft')} />
            <PanelBody style={{ padding: 0 }}>
              <FieldRow label="Number">{quoteId}</FieldRow>
              <FieldRow label="Opportunity">{String(quote?.['opportunity'] ?? '—')}</FieldRow>
              <FieldRow label="Owner">{String(quote?.['owner'] ?? '—')}</FieldRow>
              <FieldRow label="Expires">{String(quote?.['expires'] ?? '—')}</FieldRow>
              <FieldRow label="Recorded total">
                {quote ? fullMoney(Number(quote['total'])) : '—'}
                <div className={styles.sub}>
                  What is on the record. The builder above is what it would become.
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
