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
