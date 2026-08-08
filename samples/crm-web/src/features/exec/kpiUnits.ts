import { fullMoney } from '@/lib/format'

/**
 * A KPI's number in the unit its source actually produces.
 *
 * NONE OF THE FIVE SOURCES IS A PERCENTAGE, AND EVERY ONE OF THEM WAS DRAWN AS ONE. Open pipeline
 * of 226,000 rendered as "226000%" — which does not read as the wrong unit, it reads as a broken
 * screen, so nobody goes looking for the formatter. Two of the sources are money and three are
 * counts, and the server names which is which.
 *
 * A TABLE OVER A CLOSED VOCABULARY, NOT A CONVENTION. `KpiSource` is five values on the server and
 * adding a sixth is a deliberate act; a rule guessing from the label would fail on the first KPI
 * somebody named "Pipeline coverage ratio". An unknown source falls back to a plain number, which
 * is wrong in scale at worst and never wrong in kind.
 */
const MONEY: readonly string[] = ['OpenPipeline', 'WonRevenue']

export function kpiValue(source: string, value: number): string {
  if (MONEY.includes(source)) {
    return fullMoney(value)
  }

  return value.toLocaleString('en-GB')
}

/**
 * Which way is good, in the server's own vocabulary.
 *
 * `KpiDirection` is `HigherIsBetter` or `LowerIsBetter`, and two screens compared it against
 * `'Up'` — a value the enum has never had. The comparison is false for every KPI, so both said
 * "lower is better" under all five, including open pipeline and won revenue. It reads as a
 * deliberate statement about the number rather than as a string that never matched.
 *
 * An unrecognised direction says nothing rather than guessing one of the two: a sixth value added
 * on the server would otherwise be silently reported as the wrong half of a pair.
 */
export function kpiDirection(direction: string): string | null {
  if (direction === 'HigherIsBetter') {
    return 'higher is better'
  }

  return direction === 'LowerIsBetter' ? 'lower is better' : null
}
