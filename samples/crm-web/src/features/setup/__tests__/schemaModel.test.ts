import { describe, expect, it } from 'vitest'
import type { DescribedField, SchemaDescription } from '@/api/contracts'
import { schemaEdges, schemaRowOf, schemaRows } from '../schemaModel'

function field(over: Partial<DescribedField> & { name: string }): DescribedField {
  return {
    label: over.name,
    type: 'Text',
    isRequired: false,
    isComputed: false,
    canRead: true,
    canWrite: true,
    options: [],
    references: null,
    ...over,
  }
}

const DESCRIPTION: SchemaDescription = {
  version: 22,
  entities: [
    {
      kind: 'Account',
      label: 'Account',
      columns: [{ name: 'name', label: 'name', options: [] }],
      fields: [field({ name: 'segment', label: 'Segment', type: 'Picklist', options: ['gold'] })],
    },
  ],
  objects: [
    {
      id: 'obj-project',
      name: 'project',
      label: 'Delivery Project',
      fields: [
        field({ name: 'code', label: 'Code' }),
        field({ name: 'owner', label: 'Owner', type: 'Reference', references: 'obj-team' }),
        field({ name: 'orphan', label: 'Orphan', type: 'Reference', references: 'obj-gone' }),
      ],
      views: [],
    },
    { id: 'obj-team', name: 'team', label: 'Team', fields: [], views: [] },
  ],
}

/**
 * The rows every setup screen is drawn from.
 *
 * Nothing here is presentation: it decides what an administrator is told this organisation has,
 * and each of the three assertions below replaced a screen stating something that was not so.
 */
describe('schemaRows', () => {
  it('carries the object id, which is what a write has to send', () => {
    // `DefineField` takes an owner that is either an entity kind or an object id. A row holding
    // only the key could not tell a form which of the two it was looking at.
    expect(schemaRowOf(DESCRIPTION, 'Account')?.objectId).toBe(null)
    expect(schemaRowOf(DESCRIPTION, 'project')?.objectId).toBe('obj-project')
  })

  it('is empty when there is no description, rather than inventing one', () => {
    expect(schemaRows(undefined)).toEqual([])
  })
})

describe('schemaEdges', () => {
  it('points an edge at the object the field references, not at the field', () => {
    // The screen used to render `field.name` as the far end, so a reference called `owner` drew
    // an edge into an entity called "owner" — a relationship diagram naming a thing that is not
    // an entity, on the screen an administrator opens to find out what the entities are.
    expect(schemaEdges(DESCRIPTION)).toContainEqual({
      from: 'Delivery Project',
      to: 'Team',
      via: 'Owner',
    })
  })

  it('shows a reference whose target it cannot see rather than dropping it', () => {
    expect(schemaEdges(DESCRIPTION)).toContainEqual({
      from: 'Delivery Project',
      to: 'obj-gone',
      via: 'Orphan',
    })
  })

  it('draws nothing from a field that is not a reference', () => {
    // A picklist is not an edge, and neither is a built-in column.
    expect(schemaEdges(DESCRIPTION).map((edge) => edge.via)).toEqual(['Owner', 'Orphan'])
  })
})
