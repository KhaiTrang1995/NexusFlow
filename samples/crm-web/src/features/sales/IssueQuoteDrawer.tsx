import { useState } from 'react'
import {
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  FieldRow,
  Tag,
  TextField,
} from '@/design/primitives'
import { useIssueQuote } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { fullMoney } from '@/lib/format'
import type { QuoteRequestLine } from '@/api/contracts'

/** One line as the form holds it, before it is a number. */
interface DraftLine {
  sku: string
  quantity: string
  unitPrice: string
}

const EMPTY: DraftLine = { sku: '', quantity: '1', unitPrice: '' }

/**
 * Prices and issues a quote against an opportunity.
 *
 * THE TOTAL IS NOT COMPUTED HERE, AND THAT IS THE POINT. Lines and a discount go up; the
 * subtotal, the total and whether it needs approval come back. The discount threshold is exactly
 * the rule a second implementation gets wrong — and a client that showed its own total would
 * disagree with the quote the customer receives.
 *
 * The figure below the lines is labelled as an estimate for that reason: it is arithmetic to
 * help somebody type, not the price.
 */
export function IssueQuoteDrawer({
  opportunityId,
  opportunityName,
  currency,
  onClose,
}: {
  opportunityId: string
  opportunityName: string
  currency: string
  onClose: () => void
}) {
  const issue = useIssueQuote()
  const toast = useToast()

  const [lines, setLines] = useState<DraftLine[]>([{ ...EMPTY }])
  const [discount, setDiscount] = useState('0')
  const [validForDays, setValidForDays] = useState('21')

  const priced: QuoteRequestLine[] = lines
    .filter((line) => line.sku.trim().length > 0)
    .map((line) => ({
      sku: line.sku.trim(),
      quantity: Number(line.quantity) || 0,
      unitPrice: { amount: Number(line.unitPrice) || 0, currency },
    }))

  const estimate = priced.reduce(
    (sum, line) => sum + line.quantity * line.unitPrice.amount,
    0,
  )

  const ready = priced.length > 0 && priced.every((line) => line.quantity > 0)

  function edit(index: number, patch: Partial<DraftLine>) {
    setLines((current) =>
      current.map((line, at) => (at === index ? { ...line, ...patch } : line)),
    )
  }

  function submit() {
    issue.mutate(
      {
        opportunityId,
        lines: priced,
        discount: Number(discount) || 0,
        validForDays: Number(validForDays) || 21,
      },
      {
        onSuccess: (result) => {
          // The server's answer, said back. "Draft, needs approval" is not a failure and a toast
          // that only said "saved" would let somebody tell a customer the price is agreed.
          toast.saved(
            result.needsApproval
              ? `Quote ${fullMoney(result.total.amount)} is ${result.status} — a manager has to approve the discount.`
              : `Quote ${fullMoney(result.total.amount)} ${result.status.toLowerCase()}.`,
          )
          onClose()
        },
      },
    )
  }

  return (
    <Drawer
      eyebrow="New quote"
      title={opportunityName}
      subtitle="Pure pricing; the discount threshold decides Draft or Issued"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || issue.isPending} onClick={submit}>
            {issue.isPending ? 'Pricing…' : 'Issue quote'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Lines" />

      {lines.map((line, index) => (
        <div key={index}>
          <TextField
            label={`Item ${index + 1}`}
            value={line.sku}
            placeholder="SKU"
            onChange={(event) => edit(index, { sku: event.target.value })}
          />
          <TextField
            label="Quantity"
            type="number"
            value={line.quantity}
            onChange={(event) => edit(index, { quantity: event.target.value })}
          />
          <TextField
            label={`Unit price (${currency})`}
            type="number"
            value={line.unitPrice}
            onChange={(event) => edit(index, { unitPrice: event.target.value })}
          />
        </div>
      ))}

      <Button size="sm" onClick={() => setLines((current) => [...current, { ...EMPTY }])}>
        Add a line
      </Button>

      <DrawerSection label="Terms" />
      <TextField
        label={`Discount (${currency})`}
        type="number"
        value={discount}
        hint="Past the threshold this becomes a Draft until a manager approves it."
        onChange={(event) => setDiscount(event.target.value)}
      />
      <TextField
        label="Valid for (days)"
        type="number"
        value={validForDays}
        onChange={(event) => setValidForDays(event.target.value)}
      />

      <DrawerSection label="Before the server prices it" />
      <FieldRow label="Lines add up to">
        {fullMoney(estimate)} {currency} <Tag tone="outline">an estimate, not the price</Tag>
      </FieldRow>

      {issue.isError ? <ErrorState error={issue.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
