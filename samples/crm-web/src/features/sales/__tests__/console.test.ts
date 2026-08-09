import { describe, expect, it } from 'vitest'
import type { PeriodSummary } from '@/api/contracts'
import { horizonOf, inHorizon, scopeOf } from '../useConsole'
import type { ConsoleFilters, Deal, Horizon } from '../useConsole'

/**
 * The window every figure on the sales console is inside.
 *
 * WHAT THIS EXISTS TO STOP COMING BACK. The horizon had an upper bound and no lower one —
 * `close < quarterEnd` — so "This quarter" admitted every deal the tenant had ever had. The
 * consequence was found on one tile, which had to be relabelled because it could not mean
 * quarter-to-date; the funnel, the strip and the closing table were all reading the same
 * unbounded set and nobody had looked. The first test in `inHorizon` is that bound.
 */
const OWNER = '33333333-3333-3333-3333-333333333333'

function period(
  name: string,
  label: string,
  startsOn: string,
  endsOn: string,
  isCurrent: boolean,
  parent: string | null = null,
): PeriodSummary {
  return { name, label, startsOn, endsOn, parent, isCurrent }
}

function deal(id: string, values: Partial<Deal> = {}): Deal {
  return {
    id,
    name: id,
    account: null,
    stage: 'Qualify',
    amount: 1000,
    probability: 50,
    closeDate: '2026-08-15',
    outcome: null,
    ownerId: OWNER,
    ...values,
  }
}

const FILTERS: ConsoleFilters = { owner: 'mine', horizon: 'quarter', outcome: 'open' }

/** 9 August 2026, which is in the calendar quarter July–September. */
const NOW = new Date(2026, 7, 9)

describe('horizonOf', () => {
  /**
   * The boundary is the tenant's, and it is not the calendar's.
   *
   * A fiscal quarter is whatever an administrator declared. This one runs August to October, so a
   * quarter computed in the browser is wrong at both ends — and wrong in a way that agrees with
   * nothing else the reader opens, because the target and the quota are against the declared one.
   */
  it('takes both ends and the name from the period the server marks current', () => {
    const horizon = horizonOf(
      'quarter',
      [period('fy27_q1', 'FY27 Q1', '2026-08-01', '2026-10-31', true)],
      NOW,
    )

    expect(horizon).toEqual({ from: '2026-08-01', to: '2026-10-31', label: 'FY27 Q1' })
  })

  /** A quarter sits inside a year and today is inside both. The chip says quarter. */
  it('prefers the narrowest current period over the year enclosing it', () => {
    const horizon = horizonOf(
      'quarter',
      [
        period('fy26', 'FY26', '2025-10-01', '2026-09-30', true),
        period('fy26_q4', 'FY26 Q4', '2026-07-01', '2026-09-30', true, 'fy26'),
      ],
      NOW,
    )

    expect(horizon.label).toBe('FY26 Q4')
    expect(horizon.from).toBe('2026-07-01')
  })

  /** Declared and none of them current is the same answer as none declared: the calendar's. */
  it('falls back to the calendar quarter, named as one, when nothing is current', () => {
    const past = [period('fy25_q1', 'FY25 Q1', '2024-10-01', '2024-12-31', false)]

    expect(horizonOf('quarter', past, NOW)).toEqual({
      from: '2026-07-01',
      to: '2026-09-30',
      label: 'this quarter',
    })

    expect(horizonOf('quarter', [], NOW)).toEqual({
      from: '2026-07-01',
      to: '2026-09-30',
      label: 'this quarter',
    })
  })

  /** December is the quarter's last day and not the first of January. */
  it('ends the fourth calendar quarter on the last day of December', () => {
    expect(horizonOf('quarter', [], new Date(2026, 11, 3)).to).toBe('2026-12-31')
  })

  /**
   * A month is the calendar's, and it is a whole month.
   *
   * The declared period is deliberately in the list: what a tenant declares are planning periods,
   * and a quarter is not what "This month" says.
   */
  it('is the whole calendar month, first to last', () => {
    const declared = [period('fy26_q4', 'FY26 Q4', '2026-07-01', '2026-09-30', true)]

    expect(horizonOf('month', declared, NOW)).toEqual({
      from: '2026-08-01',
      to: '2026-08-31',
      label: 'this month',
    })
  })

  /** "All open" is the one horizon with no ends, which is what the chip promises. */
  it('is unbounded at both ends for all open', () => {
    expect(horizonOf('open', [], NOW)).toEqual({ from: null, to: null, label: 'any close date' })
  })
})

describe('inHorizon', () => {
  const quarter: Horizon = { from: '2026-07-01', to: '2026-09-30', label: 'FY26 Q4' }

  /**
   * THE DEFECT, IN ONE ASSERTION. A deal that closed in another year is not in this quarter, and
   * with only an upper bound every one of them was.
   */
  it('excludes a close date before the window opens', () => {
    expect(inHorizon('2024-02-11', quarter)).toBe(false)
    expect(inHorizon('2026-06-30', quarter)).toBe(false)
  })

  it('excludes a close date after it shuts', () => {
    expect(inHorizon('2026-10-01', quarter)).toBe(false)
  })

  it('includes both of its own days', () => {
    expect(inHorizon('2026-07-01', quarter)).toBe(true)
    expect(inHorizon('2026-09-30', quarter)).toBe(true)
  })

  /**
   * A timestamp is compared as the day it falls on. '2026-09-30T00:00:00+00:00' sorts after
   * '2026-09-30' as text, which drops the last day of every window the moment a date column on
   * this screen turns out to be a `timestamptz` — as `valid_until` already did.
   */
  it('compares a full timestamp by its day', () => {
    expect(inHorizon('2026-09-30T23:18:06.488811+00:00', quarter)).toBe(true)
  })

  /** An undated deal is in no window, and is in "All open" like everything else. */
  it('puts an undated deal outside a bounded window and inside an unbounded one', () => {
    expect(inHorizon(null, quarter)).toBe(false)
    expect(inHorizon(null, { from: null, to: null, label: 'any close date' })).toBe(true)
  })
})

describe('scopeOf', () => {
  const quarter = horizonOf('quarter', [], NOW)
  const month = horizonOf('month', [], NOW)

  const rows = [
    deal('in-quarter', { amount: 100_000, closeDate: '2026-09-20', stage: 'Proposal' }),
    deal('in-month', { amount: 40_000, closeDate: '2026-08-28' }),
    // Overdue and open: its close date has passed and it is still in the month.
    deal('overdue', { amount: 10_000, closeDate: '2026-08-03' }),
    // Before the window. Every figure below used to count it.
    deal('last-year', { amount: 900_000, closeDate: '2025-11-04' }),
    deal('won-last-year', { amount: 500_000, closeDate: '2025-12-31', outcome: 'Won' }),
    deal('won-this-quarter', { amount: 70_000, closeDate: '2026-07-14', outcome: 'Won' }),
    deal('somebody-else', { amount: 250_000, ownerId: 'bfb3f0f6-0000-0000-0000-000000000000' }),
  ]

  const scope = scopeOf(rows, FILTERS, OWNER, quarter, month)

  /** The figure the tile draws, and the reason the whole file exists. */
  it('leaves a deal that closed before the quarter out of the open pipeline', () => {
    expect(scope.open.map((row) => row.id)).not.toContain('last-year')
    expect(scope.openValue).toBe(150_000)
  })

  /**
   * "Closed won" can mean quarter-to-date again.
   *
   * With no lower bound this was every won deal in the tenant's history, which is why the tile
   * carried a note saying "in this horizon" instead of the period it was named for.
   */
  it('counts what was won in the window and nothing won before it', () => {
    expect(scope.won.map((row) => row.id)).toEqual(['won-this-quarter'])
    expect(scope.wonValue).toBe(70_000)
  })

  /** A won deal is still counted when the outcome chips are on Open, which is the default. */
  it('counts the won deals the outcome filter excludes', () => {
    expect(FILTERS.outcome).toBe('open')
    expect(scope.open.map((row) => row.id)).not.toContain('won-this-quarter')
    expect(scope.wonValue).toBe(70_000)
  })

  /**
   * THE THREE VIEWS AGREE, WHICH IS WHAT DERIVING THEM TOGETHER IS FOR. A funnel that disagrees
   * with the tile above it is the disagreement nobody can trace.
   */
  it('sums the funnel to the pipeline tile and keeps the table inside both', () => {
    expect(scope.totals.reduce((sum, total) => sum + total.sum, 0)).toBe(scope.openValue)
    expect(scope.totals.reduce((sum, total) => sum + total.count, 0)).toBe(scope.open.length)

    for (const row of scope.closing) {
      expect(scope.open, `${row.id} is in the table and not in the funnel`).toContain(row)
    }
  })

  /** The month is a slice of the window, and it starts on the first rather than on today. */
  it('shows the whole month, including the part of it that has passed', () => {
    expect(scope.closing.map((row) => row.id)).toEqual(['in-month', 'overdue'])
  })

  /** Everyone's pipeline is a superset of one seller's — the filter is still the filter. */
  it('still scopes to the owner', () => {
    const everyone = scopeOf(rows, { ...FILTERS, owner: 'all' }, OWNER, quarter, month)

    expect(everyone.open.map((row) => row.id)).toContain('somebody-else')
    expect(everyone.openValue).toBeGreaterThan(scope.openValue)
  })

  /** And "All open" is the one horizon that keeps the history. */
  it('keeps every date when the horizon is unbounded', () => {
    const all = scopeOf(rows, { ...FILTERS, horizon: 'open' }, OWNER, horizonOf('open', [], NOW), month)

    expect(all.open.map((row) => row.id)).toContain('last-year')
    expect(all.wonValue).toBe(570_000)

    // The closing table is still the month, whatever the window around it is.
    expect(all.closing.map((row) => row.id)).toEqual(['in-month', 'overdue'])
  })
})
