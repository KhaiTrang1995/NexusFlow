import { describe, expect, it } from 'vitest'
import type { ProcessStageView, RecordView } from '@/api/contracts'
import { byProbability, byStage, openDeals } from '../pipeline'

/**
 * The pipeline the executive screens draw.
 *
 * WHAT THIS EXISTS TO STOP COMING BACK. The landing screen's funnel split one total into three
 * bands at 42%, 34% and 24% — fractions written in the client, identical on every tenant and in
 * every period. The test that matters is the last one in `byStage`: the bands have to sum to the
 * open pipeline, because a funnel that disagrees with the number beside it is the disagreement
 * nobody can trace.
 */
function deal(values: Record<string, string | null>): RecordView {
  return { recordId: values['id'] ?? Math.random().toString(), values }
}

function stage(name: string, ordinal: number, isTerminal = false): ProcessStageView {
  return { stageId: name, name, ordinal, isTerminal, occupants: 0 }
}

describe('openDeals', () => {
  it('keeps the rows with no outcome and drops the decided ones', () => {
    const rows = [
      deal({ id: 'a', outcome: null }),
      deal({ id: 'b', outcome: 'Won' }),
      deal({ id: 'c', outcome: 'Lost' }),
      deal({ id: 'd', outcome: null }),
    ]

    expect(openDeals(rows).map((row) => row.recordId)).toEqual(['a', 'd'])
  })
})

describe('byStage', () => {
  const stages = [
    stage('Prospecting', 0),
    stage('Qualify', 2),
    stage('Discovery', 1),
    stage('Closed Won', 7, true),
  ]

  const open = [
    deal({ id: 'a', stage: 'Prospecting', amount: '50000', outcome: null }),
    deal({ id: 'b', stage: 'Prospecting', amount: '30000', outcome: null }),
    deal({ id: 'c', stage: 'Qualify', amount: '100000', outcome: null }),
  ]

  it('orders the bands by the published ordinal and not by the order they arrived', () => {
    expect(byStage(open, stages).map((band) => band.name)).toEqual([
      'Prospecting',
      'Discovery',
      'Qualify',
      'Closed Won',
    ])
  })

  it('keeps a declared stage that is holding nothing, because that is the shape', () => {
    const empty = byStage(open, stages).find((band) => band.name === 'Discovery')

    expect(empty).toMatchObject({ count: 0, amount: 0, isDeclared: true })
  })

  it('keeps a stage the process no longer declares, and marks it', () => {
    const retired = byStage(
      [...open, deal({ id: 'e', stage: 'Bake-off', amount: '7000', outcome: null })],
      stages,
    ).find((band) => band.name === 'Bake-off')

    expect(retired).toMatchObject({ count: 1, amount: 7000, isDeclared: false })
  })

  it('carries the terminal flag from the process, so an undecided deal in one can be named', () => {
    const bands = byStage(
      [...open, deal({ id: 'f', stage: 'Closed Won', amount: '9000', outcome: null })],
      stages,
    )

    expect(bands.find((band) => band.name === 'Closed Won')).toMatchObject({
      count: 1,
      isTerminal: true,
    })
  })

  it('sums to the open pipeline, whatever the stages are', () => {
    const rows = [
      ...open,
      deal({ id: 'e', stage: 'Bake-off', amount: '7000', outcome: null }),
      deal({ id: 'f', stage: 'Closed Won', amount: '9000', outcome: null }),
    ]

    const total = byStage(rows, stages).reduce((sum, band) => sum + band.amount, 0)

    expect(total).toBe(50_000 + 30_000 + 100_000 + 7000 + 9000)
  })

  it('places a row whose stage is missing rather than losing it', () => {
    const bands = byStage([deal({ id: 'g', amount: '1000', outcome: null })], stages)

    expect(bands.find((band) => band.name === '—')).toMatchObject({ count: 1, amount: 1000 })
  })
})

describe('byProbability', () => {
  const bands = [
    { label: 'low', from: 0, to: 50 },
    { label: 'high', from: 51, to: 100 },
  ]

  it('weights each deal by its own probability, not by the band', () => {
    const rows = [
      deal({ id: 'a', probability: '60', amount: '100000', outcome: null }),
      deal({ id: 'b', probability: '90', amount: '100000', outcome: null }),
    ]

    const high = byProbability(rows, bands).find((row) => row.band === 'high')

    // Per deal: 60,000 + 90,000. The band's own midpoint would have given 75.5% of 200,000.
    expect(high).toMatchObject({ count: 2, amount: 200_000, weighted: 150_000 })
  })

  it('is the sum of amount × probability and not the midpoint of the band', () => {
    const rows = [
      deal({ id: 'a', probability: '51', amount: '10000', outcome: null }),
      deal({ id: 'b', probability: '100', amount: '90000', outcome: null }),
    ]

    // Midpoint arithmetic: 75.5% of 100,000 = 75,500. Per deal: 5,100 + 90,000 = 95,100.
    expect(byProbability(rows, bands).find((row) => row.band === 'high')?.weighted).toBe(95_100)
  })

  it('counts a deal with no probability at the bottom of the scale rather than dropping it', () => {
    const rows = [deal({ id: 'a', amount: '4000', outcome: null })]

    expect(byProbability(rows, bands).find((row) => row.band === 'low')).toMatchObject({
      count: 1,
      amount: 4000,
      weighted: 0,
    })
  })
})
