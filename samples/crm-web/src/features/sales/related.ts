import type { ReadableEntity } from '@/api/contracts'

/** One related list: an object, the column it points back by, and what to show of it. */
export interface RelatedLink {
  /** The object key, so the rows render through the same model the list screen uses. */
  objectKey: string
  entity: ReadableEntity
  /** The column on the child that holds the parent's id. */
  field: string
  title: string
  columns: readonly string[]
}

/**
 * What points at a record of each kind, by foreign key.
 *
 * <strong>Matched on the id, not on the display name.</strong> The prototype's related lists
 * compared a child's `account` string against the parent's `name`, which works exactly as long as
 * every name is unique and nobody renames anything — and fails silently rather than loudly when
 * either stops being true. Every column below is one the server already offers on that entity's
 * closed readable list, so a typo here is a refusal rather than an empty panel.
 *
 * <strong>Nothing points at a lead.</strong> A lead has no children until it is converted, and at
 * that moment it becomes an account, a contact and an opportunity that point at each other rather
 * than back at it. An empty entry would suggest the list exists and is empty.
 */
const LINKS: Readonly<Record<string, readonly RelatedLink[]>> = {
  account: [
    {
      objectKey: 'contact',
      entity: 'Contact',
      field: 'account_id',
      title: 'Contacts',
      columns: ['name', 'email', 'phone'],
    },
    {
      objectKey: 'opportunity',
      entity: 'Opportunity',
      field: 'account_id',
      title: 'Opportunities',
      columns: ['name', 'stage', 'amount', 'closeDate'],
    },
  ],
  opportunity: [
    {
      objectKey: 'quote',
      entity: 'Quote',
      field: 'opportunity_id',
      title: 'Quotes',
      columns: ['status', 'total', 'discount', 'expires'],
    },
  ],
  quote: [
    {
      // Status alone, because status is all this object model can say about a sales order without
      // lying about it. `Est. Hours` was the second column and it drew the order's money: the
      // model is the prototype's work order and has no currency field, so €184,000 rendered as
      // 184,000 hours. A column with nothing honest to put in it is not a column.
      objectKey: 'workorder',
      entity: 'Order',
      field: 'quote_id',
      title: 'Orders',
      columns: ['status'],
    },
  ],
}

export function relatedLinksOf(objectKey: string): readonly RelatedLink[] {
  return LINKS[objectKey] ?? []
}
