import { Button } from '@/design/primitives'
import { useApproveDiscount, usePlaceOrder, useSubmitForApproval } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { useToast } from '@/app/ToastProvider'
import { fullMoney } from '@/lib/format'

/**
 * What a seller does to a quote once it exists: ask for it to be approved, and take the order.
 *
 * TWO DIFFERENT APPROVALS, AND THEY ARE NOT THE SAME THING. `crm.discount.approve` is the grant
 * that lets somebody clear a discounted quote at all; the configured process is which named
 * people have to say yes, in what order, for this particular one. Submitting here starts the
 * second, and "Approve the discount" is the first. A screen that conflated them would let a
 * manager's grant stand in for a director's signature — and deciding a request in the inbox does
 * not move the quote out of Draft, which is why both actions exist on this one header.
 *
 * THE APPROVAL BUTTON IS HIDDEN WITHOUT THE GRANT, and that is the one place hiding beats
 * disabling: it is not a field somebody goes looking for, it is an act they are not party to. A
 * director sees no button here because a director is not in the discount chain.
 *
 * "NOTHING NEEDS APPROVING" IS AN ANSWER, NOT A FAILURE. Most quotes are under every threshold
 * anybody configured, and the server says so out loud — so the toast says so too, rather than
 * leaving a reader to wonder whether the click did anything.
 *
 * THE ORDER IS REFUSED WHILE THE QUOTE IS A DRAFT, and that refusal is the control rather than a
 * bug to route around. The button is enabled anyway: the server owns the rule, and a client that
 * disabled it would be a second copy of the threshold, which is the one rule this sample keeps
 * proving nobody should write twice.
 *
 * SO THE REFUSAL HAS TO BE SAID OUT LOUD. Neither of these buttons navigates or changes the page,
 * and without `onError` a 409 was a click that did nothing at all — indistinguishable from a dead
 * button, which is the reading a person actually arrives at. The toast carries the server's own
 * sentence rather than one invented here.
 */
export function QuoteActions({ quoteId, status }: { quoteId: string; status: string | null }) {
  const submit = useSubmitForApproval()
  const order = usePlaceOrder()
  const approve = useApproveDiscount()
  const session = useSession()
  const toast = useToast()

  function ask() {
    submit.mutate(
      { subject: 'Quote', id: quoteId },
      {
        onSuccess: (result) =>
          toast.saved(
            result.required
              ? `${result.process} is waiting on ${result.awaitingLabel}.`
              : 'No configured process applies to this quote — nothing to approve.',
          ),
        onError: (error) => toast.failed(error, 'That quote could not be submitted.'),
      },
    )
  }

  function place() {
    order.mutate(
      { quoteId },
      {
        onSuccess: (result) => toast.saved(`Order for ${fullMoney(result.total.amount)} placed.`),
        onError: (error) => toast.failed(error, 'That order was refused.'),
      },
    )
  }

  function clear() {
    approve.mutate(
      { quoteId },
      {
        onSuccess: () => toast.saved('Discount approved — the quote is Issued.'),
        onError: (error) => toast.failed(error, 'That approval was refused.'),
      },
    )
  }

  return (
    <>
      <Button disabled={submit.isPending} onClick={ask}>
        {submit.isPending ? 'Submitting…' : 'Submit for approval'}
      </Button>

      {session.can('crm.discount.approve') ? (
        <Button disabled={approve.isPending} onClick={clear}>
          {approve.isPending ? 'Approving…' : 'Approve the discount'}
        </Button>
      ) : null}
      <Button
        tone="primary"
        disabled={order.isPending}
        title={
          status === 'Draft'
            ? 'The server refuses an order against a Draft quote until the discount is approved.'
            : undefined
        }
        onClick={place}
      >
        {order.isPending ? 'Placing…' : 'Place order'}
      </Button>
    </>
  )
}
