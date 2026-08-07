import { describe, expect, it } from 'vitest'
import { modelFor } from '@/fixtures/objects'
import { entityOf, keyColumnOf, toRows } from '../liveRecords'

describe('entityOf', () => {
  it('names the four objects the server can page', () => {
    expect(entityOf('lead')).toBe('Lead')
    expect(entityOf('account')).toBe('Account')
    expect(entityOf('contact')).toBe('Contact')
    expect(entityOf('opportunity')).toBe('Opportunity')
  })

  it('is null for an object the server has no page for', () => {
    expect(entityOf('quote')).toBeNull()
    expect(entityOf('task')).toBeNull()
  })
})

describe('keyColumnOf', () => {
  /**
   * The record screen filters on this to read one row. A wrong name is refused by the server —
   * `crm.entity_field_unknown` — so the failure is loud, but only where somebody looks.
   */
  it('is the entity key each read filters on', () => {
    expect(keyColumnOf('account')).toBe('account_id')
    expect(keyColumnOf('contact')).toBe('contact_id')
    expect(keyColumnOf('lead')).toBe('lead_id')
    expect(keyColumnOf('opportunity')).toBe('opportunity_id')
    expect(keyColumnOf('quote')).toBeNull()
  })
})

describe('toRows', () => {
  /** The stage is what a pipeline board groups by, and it is not a column of `opportunity`. */
  it('keeps the stage the server merged into the projection', () => {
    const rows = toRows('opportunity', modelFor('opportunity'), [
      { recordId: 'o1', values: { opportunity_id: 'o1', stage: 'Negotiation' } },
    ])

    expect(rows[0]!['stage']).toBe('Negotiation')
  })

  it('renames a column to the field this screen draws', () => {
    const rows = toRows('lead', modelFor('lead'), [
      {
        recordId: 'a1',
        values: { lead_id: 'a1', contact_name: 'Elena Vargas', company: 'Northwind', score: '40' },
      },
    ])

    expect(rows[0]).toMatchObject({ id: 'a1', name: 'Elena Vargas', company: 'Northwind' })
  })

  /**
   * A page of amounts arriving as strings sorts 90,000 above 800,000, and the reader sees a
   * broken sort rather than a broken type.
   */
  it('keeps a numeric field numeric', () => {
    const rows = toRows('opportunity', modelFor('opportunity'), [
      { recordId: 'o1', values: { opportunity_id: 'o1', amount: '184000.0000' } },
    ])

    expect(rows[0]!['amount']).toBe(184000)
  })

  it('drops a column this screen has no field for', () => {
    const rows = toRows('account', modelFor('account'), [
      { recordId: 'x', values: { account_id: 'x', name: 'N', owner_id: '3333-3333' } },
    ])

    expect(Object.values(rows[0]!)).not.toContain('3333-3333')
  })

  it('leaves a null out rather than drawing an empty string', () => {
    const rows = toRows('account', modelFor('account'), [
      { recordId: 'x', values: { account_id: 'x', name: 'N', industry: null } },
    ])

    expect(rows[0]).not.toHaveProperty('industry')
  })

  /** The account column reads as a name, or not at all — never as an identifier. */
  it('resolves an account id to its name when one is known', () => {
    const page = [{ recordId: 'c1', values: { contact_id: 'c1', account_id: 'acc-1' } }]

    expect(toRows('contact', modelFor('contact'), page, new Map([['acc-1', 'Northwind']]))[0])
      .toMatchObject({ account: 'Northwind' })

    expect(toRows('contact', modelFor('contact'), page)[0]).not.toHaveProperty('account')
  })

  it('is empty for an object the server does not page', () => {
    expect(toRows('quote', modelFor('quote'), [{ recordId: 'q', values: {} }])).toEqual([])
  })
})
