import { describe, expect, it } from 'vitest'
import { dueAtFromDays, emailOrNull } from '../newWork'

describe('emailOrNull', () => {
  it('is the address when one was typed', () => {
    expect(emailOrNull('  a@b.example ')).toBe('a@b.example')
  })

  /**
   * An empty string in the column would mean "their address is nothing", and every count of
   * leads-without-an-address would then be nought while the screen shows blanks.
   */
  it('is null when the box is empty or only spaces', () => {
    expect(emailOrNull('')).toBeNull()
    expect(emailOrNull('   ')).toBeNull()
  })
})

describe('dueAtFromDays', () => {
  const now = new Date('2026-08-07T09:00:00.000Z')

  it('counts forward from now', () => {
    expect(dueAtFromDays('7', now)).toBe('2026-08-14T09:00:00.000Z')
  })

  it('counts backward, for something already late', () => {
    expect(dueAtFromDays('-2', now)).toBe('2026-08-05T09:00:00.000Z')
  })

  /**
   * Null and not nought. Nought days is due today, which makes a task nobody dated overdue
   * before the form has closed — and the sweep then escalates it.
   */
  it('is null for an empty box, not today', () => {
    expect(dueAtFromDays('', now)).toBeNull()
    expect(dueAtFromDays('   ', now)).toBeNull()
  })

  it('is null for something that is not a number', () => {
    expect(dueAtFromDays('soon', now)).toBeNull()
  })
})
