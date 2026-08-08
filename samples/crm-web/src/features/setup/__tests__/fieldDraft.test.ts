import { describe, expect, it } from 'vitest'
import { FIELD_TYPES, isUsableName, optionsOf, refusalOf, requestOf } from '../fieldDraft'
import type { FieldDraft } from '../fieldDraft'
import type { SchemaRow } from '../schemaModel'

const ACCOUNT: SchemaRow = {
  key: 'Account',
  label: 'Account',
  builtIn: true,
  views: 0,
  objectId: null,
  fields: [],
}

const PROJECT: SchemaRow = {
  key: 'project',
  label: 'Delivery Project',
  builtIn: false,
  views: 0,
  objectId: 'obj-project',
  fields: [],
}

const DRAFT: FieldDraft = {
  owner: ACCOUNT,
  name: 'renewal_risk',
  label: 'Renewal risk',
  type: 'Text',
  required: false,
  options: '',
  references: '',
}

/**
 * The form that declared nothing.
 *
 * "Declare the field" toasted `${label} declared on ${object}` and never called the API, so every
 * rule below was untested by anything — including the ten types offered, six of which this
 * backend has never heard of.
 */
describe('the type list', () => {
  it('is the server’s closed set and nothing the prototype invented', () => {
    expect([...FIELD_TYPES].sort()).toEqual([
      'Boolean',
      'Date',
      'MultiPicklist',
      'Number',
      'Picklist',
      'Reference',
      'Text',
    ])
  })
})

describe('requestOf', () => {
  it('names a built-in entity by its kind and a custom object by its id, never both', () => {
    // The server's `CHECK ((applies_to IS NULL) <> (object_id IS NULL))`. A body carrying both is
    // refused, and so is one carrying neither.
    expect(requestOf(DRAFT)).toMatchObject({ appliesTo: 'Account', target: null })
    expect(requestOf({ ...DRAFT, owner: PROJECT })).toMatchObject({
      appliesTo: null,
      target: 'obj-project',
    })
  })

  it('sends a picklist’s options and no others', () => {
    expect(requestOf({ ...DRAFT, type: 'Picklist', options: 'gold, silver ,' })?.options).toEqual([
      { value: 'gold', label: 'gold' },
      { value: 'silver', label: 'silver' },
    ])

    // Left over from a type the administrator changed their mind about. A text field with a set
    // of allowed values is a shape the server has no column for.
    expect(requestOf({ ...DRAFT, type: 'Text', options: 'gold' })?.options).toEqual([])
  })

  it('sends a reference’s target and nulls it for every other type', () => {
    expect(
      requestOf({ ...DRAFT, type: 'Reference', references: 'obj-team' })?.references,
    ).toBe('obj-team')
    expect(requestOf({ ...DRAFT, type: 'Number', references: 'obj-team' })?.references).toBe(null)
  })

  it('returns nothing at all when the draft would be refused', () => {
    expect(requestOf({ ...DRAFT, owner: undefined })).toBe(null)
    expect(requestOf({ ...DRAFT, type: 'Picklist', options: '' })).toBe(null)
  })
})

describe('refusalOf', () => {
  it('refuses a closed set with no values, for both of the closed types', () => {
    // The field exists, the control renders, and nothing can ever be chosen. The server names
    // this one; saying it here means the administrator finds out while they are still typing.
    expect(refusalOf({ ...DRAFT, type: 'Picklist', options: ' , ' })).toMatch(/no options/)
    expect(refusalOf({ ...DRAFT, type: 'MultiPicklist', options: '' })).toMatch(/no options/)
  })

  it('refuses a reference that points at nothing', () => {
    expect(refusalOf({ ...DRAFT, type: 'Reference', references: '' })).toMatch(/point at/)
    expect(refusalOf({ ...DRAFT, type: 'Reference', references: 'obj-team' })).toBe(null)
  })

  it('refuses a name the server’s identifier rule would refuse', () => {
    expect(refusalOf({ ...DRAFT, name: 'Renewal Risk' })).toMatch(/lower-case/)
    expect(refusalOf({ ...DRAFT, name: '9lives' })).toMatch(/lower-case/)
    expect(refusalOf({ ...DRAFT, name: 'renewal_risk_2' })).toBe(null)
  })

  it('refuses an option that is not storable as a name', () => {
    expect(refusalOf({ ...DRAFT, type: 'Picklist', options: 'Gold' })).toMatch(/same rule/)
  })

  it('says nothing when the draft is sendable', () => {
    expect(refusalOf(DRAFT)).toBe(null)
  })
})

describe('optionsOf and isUsableName', () => {
  it('drops the empty entries a trailing comma leaves behind', () => {
    expect(optionsOf(' a , , b ,')).toEqual([
      { value: 'a', label: 'a' },
      { value: 'b', label: 'b' },
    ])
  })

  it('mirrors CustomValues.IsUsableName', () => {
    expect(isUsableName('a')).toBe(true)
    expect(isUsableName('a_1')).toBe(true)
    expect(isUsableName('')).toBe(false)
    expect(isUsableName('_a')).toBe(false)
    expect(isUsableName('a-b')).toBe(false)
    expect(isUsableName('a'.repeat(64))).toBe(false)
    expect(isUsableName('a'.repeat(63))).toBe(true)
  })
})
