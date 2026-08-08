import { describe, expect, it } from 'vitest'
import { kpiDirection, kpiValue } from '../kpiUnits'

/**
 * The two things a KPI figure needs before it can be drawn: its unit, and which way is good.
 *
 * BOTH WERE WRONG ON THE BOARD PACK, AND ONE OF THEM ON THE SCORECARD TOO. Open pipeline was
 * rendered `pct(1626000)` — "1626000%" — and every KPI was captioned "lower is better" because
 * the direction was compared against `'Up'`, a value the server's `KpiDirection` enum has never
 * had. Neither is a crash and neither shows up in a type: they are two strings that read as
 * deliberate statements about the number.
 */
describe('kpiValue', () => {
  it('draws the two money sources as money', () => {
    // The amount alone is not the assertion: a plain count renders the same digits, and it was a
    // bare `toLocaleString` that let a pipeline figure read as a tally.
    for (const source of ['OpenPipeline', 'WonRevenue']) {
      const drawn = kpiValue(source, 1_626_000)

      expect(drawn).toContain('1,626,000')
      expect(drawn).toMatch(/[$€£]/)
    }
  })

  it('draws the three counting sources as counts, with no currency and no per cent', () => {
    for (const source of ['LeadsCaptured', 'OpenTasks', 'OverduePlanSteps']) {
      const drawn = kpiValue(source, 1_626_000)

      expect(drawn).toBe('1,626,000')
      expect(drawn).not.toContain('%')
      expect(drawn).not.toMatch(/[$€£]/)
    }
  })

  it('falls back to a plain number for a source this build does not know', () => {
    expect(kpiValue('SomethingDeclaredLater', 42)).toBe('42')
  })
})

describe('kpiDirection', () => {
  it('reads the server vocabulary rather than a word the enum never had', () => {
    expect(kpiDirection('HigherIsBetter')).toBe('higher is better')
    expect(kpiDirection('LowerIsBetter')).toBe('lower is better')
  })

  it('says nothing about a direction it does not recognise', () => {
    // The failure this replaced: anything that was not the expected string silently became the
    // other half of the pair, so all five KPIs claimed lower was better.
    expect(kpiDirection('Up')).toBeNull()
    expect(kpiDirection('')).toBeNull()
  })
})
