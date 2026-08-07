import type { EntityKind, RecordView } from '@/api/contracts'
import type { ObjectModel, RecordRow } from '@/fixtures/objects'

/**
 * The four objects the server can page, and what its columns are called here.
 *
 * WHY A TABLE AND NOT A CONVENTION. `contact_name` is `name` on this screen and `full_name` on
 * the next one; no snake-to-camel rule turns one into the other, and a rule that nearly works is
 * worse than a table that plainly does not — it fails on one field, on one object, in a way that
 * looks like missing data rather than a missing mapping.
 *
 * A COLUMN WITH NO ENTRY IS A COLUMN THIS SCREEN DOES NOT SHOW. Leads carry a title and a phone
 * in the prototype and not in the schema; those cells are empty against live data, which is the
 * honest answer. Opportunity stage is deliberately absent: the stage lives in the configured
 * process, not in the row, and inventing it from `outcome` would put a number on the screen that
 * the server never said.
 *
 * AND `owner_id` IS NOT ONE OF THEM. It is a lookup to a user, and this surface returns the id.
 * A column headed "Owner" showing `33333333-3333-…` is worse than an empty one: the reader has
 * to work out that it is an identifier rather than a person before they can ignore it.
 */
const MAPPINGS: Readonly<Record<string, { entity: EntityKind; columns: Record<string, string> }>> = {
  lead: {
    entity: 'Lead',
    columns: {
      lead_id: 'id',
      contact_name: 'name',
      company: 'company',
      email: 'email',
      source: 'source',
      status: 'status',
      score: 'score',
      captured_at: 'created',
    },
  },
  account: {
    entity: 'Account',
    columns: {
      account_id: 'id',
      name: 'name',
      industry: 'industry',
      lifecycle: 'type',
      region: 'region',
    },
  },
  contact: {
    entity: 'Contact',
    columns: {
      contact_id: 'id',
      full_name: 'name',
      account_id: 'account',
      email: 'email',
      phone: 'phone',
    },
  },
  opportunity: {
    entity: 'Opportunity',
    columns: {
      opportunity_id: 'id',
      name: 'name',
      account_id: 'account',
      amount: 'amount',
      probability: 'probability',
      expected_close: 'closeDate',
      stage: 'stage',
    },
  },
}

/**
 * The column that identifies a row of this entity, for a filter that asks for exactly one.
 *
 * The first column of each mapping above, and deliberately not a convention: the server's own
 * `EntityColumns.KeyOf` reads the same list, and two places agreeing by coincidence is how they
 * stop agreeing.
 */
export function keyColumnOf(objectKey: string): string | null {
  const columns = MAPPINGS[objectKey]?.columns

  return columns === undefined ? null : (Object.keys(columns)[0] ?? null)
}

/** Which built-in entity this object is, or null when the server has no page for it. */
export function entityOf(objectKey: string): EntityKind | null {
  return MAPPINGS[objectKey]?.entity ?? null
}

/**
 * Turns a page of server rows into the rows this screen already knows how to draw.
 *
 * Numbers stay numbers. `renderCell` formats a currency field by its type and `sortValue` orders
 * numerically only when the value is a number — a page of amounts arriving as strings would sort
 * 90,000 above 800,000 and nobody would see why.
 */
export function toRows(
  objectKey: string,
  model: ObjectModel,
  page: RecordView[],
  accountNames: ReadonlyMap<string, string> = new Map(),
): RecordRow[] {
  const mapping = MAPPINGS[objectKey]

  if (mapping === undefined) {
    return []
  }

  const numeric = new Set(
    model.fields
      .filter((field) => field.type === 'number' || field.type === 'currency' || field.type === 'percent')
      .map((field) => field.name),
  )

  return page.map((record) => {
    const row: RecordRow = { id: record.recordId }

    for (const [column, value] of Object.entries(record.values)) {
      const name = mapping.columns[column]

      if (name === undefined || value === null) {
        continue
      }

      // The one lookup this screen can resolve. A contact's account arrives as an id, and the
      // accounts page is already in the cache — so the column reads as a name or not at all.
      if (name === 'account') {
        const named = accountNames.get(value)

        if (named !== undefined) {
          row[name] = named
        }

        continue
      }

      row[name] = numeric.has(name) && value !== '' && !Number.isNaN(Number(value))
        ? Number(value)
        : value
    }

    return row
  })
}
