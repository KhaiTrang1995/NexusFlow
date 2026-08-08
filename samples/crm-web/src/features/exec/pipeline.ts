import type { ProcessStageView, RecordView } from '@/api/contracts'

/**
 * Open pipeline, grouped the way the published process orders it.
 *
 * WHERE THE PIPELINE SITS IS A QUESTION WITH AN ANSWER, AND THE LANDING SCREEN INVENTED ONE. The
 * executive funnel drew three bands — Early, Mid, Late — holding 42%, 34% and 24% of one total.
 * The fractions were written in the client and were the same three on every tenant, in every
 * period, whatever the deals were doing: a chart that cannot disagree with the business is not a
 * reading of it. The stages are the administrator's, the amounts are the rows', and both are
 * already served.
 *
 * A STAGE THE PROCESS NO LONGER DECLARES STILL HOLDS DEALS. Retiring a stage does not move what is
 * standing in it, so anything the rows name and the process does not is kept and marked rather
 * than dropped — a funnel that silently omitted them would total less than the open pipeline
 * beside it, which is the disagreement nobody can find.
 */
export interface StageBand {
  name: string
  count: number
  amount: number
  /** Whether the published process still declares this stage. */
  isDeclared: boolean
  /**
   * Whether the process calls it an end.
   *
   * A terminal stage can still hold an open deal — `outcome` is written by the capability that
   * decides one, and a row moved into Closed Lost without being decided is exactly the anomaly a
   * pipeline review is looking for. So it is a property of the band and never a reason to drop it.
   */
  isTerminal: boolean
}

/**
 * The deals with no outcome yet.
 *
 * `outcome` is null until a deal is won or lost, which is the server's own answer to "is this
 * still live" — a client deciding it from the stage name would be reading the process's words as
 * if they were a state, and a tenant that renamed "Closed Won" would break it.
 */
export function openDeals(records: readonly RecordView[]): RecordView[] {
  return records.filter((record) => record.values['outcome'] === null)
}

/** What a row is worth, or nothing when the column is absent or unreadable. */
export function amountOf(record: RecordView): number {
  const amount = Number(record.values['amount'] ?? 0)

  return Number.isFinite(amount) ? amount : 0
}

/** A slice of the probability scale, inclusive at both ends. */
export interface ProbabilityBand {
  label: string
  from: number
  to: number
}

export interface BandRoll {
  band: string
  count: number
  amount: number
  /** Amount × probability, deal by deal — never the band's midpoint times the total. */
  weighted: number
}

/**
 * Open pipeline, rolled up by what each deal says its chances are.
 *
 * THE WEIGHT IS PER DEAL. Weighting a band's total by the band's own midpoint would be a different
 * and wronger number: two deals at 51% and 75% are not two deals at 63%, and the gap grows with
 * the spread. The server has no weighted figure to read, so this is the arithmetic, and it is here
 * rather than in a screen so it can be checked.
 */
export function byProbability(
  open: readonly RecordView[],
  bands: readonly ProbabilityBand[],
): BandRoll[] {
  return bands.map((band) => {
    const rows = open.filter((record) => {
      const probability = Number(record.values['probability'] ?? 0)

      return probability >= band.from && probability <= band.to
    })

    return {
      band: band.label,
      count: rows.length,
      amount: rows.reduce((total, record) => total + amountOf(record), 0),
      weighted: Math.round(
        rows.reduce(
          (total, record) =>
            total + (amountOf(record) * Number(record.values['probability'] ?? 0)) / 100,
          0,
        ),
      ),
    }
  })
}

/**
 * One band per stage, in the order the process publishes them.
 *
 * Declared stages are kept even when empty: a proposal stage holding nothing is the shape of the
 * pipeline and not a row to hide.
 */
export function byStage(
  open: readonly RecordView[],
  stages: readonly ProcessStageView[],
): StageBand[] {
  const declared = [...stages].sort((left, right) => left.ordinal - right.ordinal)

  const bands = new Map<string, StageBand>(
    declared.map((stage) => [
      stage.name,
      { name: stage.name, count: 0, amount: 0, isDeclared: true, isTerminal: stage.isTerminal },
    ]),
  )

  for (const record of open) {
    const name = record.values['stage'] ?? '—'
    const band = bands.get(name)
      ?? { name, count: 0, amount: 0, isDeclared: false, isTerminal: false }

    band.count += 1
    band.amount += amountOf(record)
    bands.set(name, band)
  }

  return [...bands.values()]
}
