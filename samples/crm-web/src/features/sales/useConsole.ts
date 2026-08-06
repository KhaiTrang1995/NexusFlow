import { useMemo, useState } from 'react'
import { OBJECT_MODELS } from '@/fixtures/objects'
import type { RecordRow, StageDefinition } from '@/fixtures/objects'

/**
 * Everything the sales console needs, derived once.
 *
 * WHY A HOOK AND NOT SIX `useMemo`s IN THE COMPONENT. The console's numbers are all views of the
 * same opportunity set, and the one thing they must do is agree: the pipeline total in the tile,
 * the sum of the funnel and the closing table are the same money read three ways. Deriving them
 * together, from one filtered list, is what makes that true by construction rather than by
 * everybody remembering to apply the same filter.
 */

export type OwnerFilter = 'mine' | 'team' | 'all'
export type HorizonFilter = 'quarter' | 'month' | 'open'
export type ForecastFilter = 'all' | 'Commit' | 'Best Case' | 'Pipeline'

export interface ConsoleFilters {
  owner: OwnerFilter
  horizon: HorizonFilter
  forecast: ForecastFilter
}

const DEFAULTS: ConsoleFilters = { owner: 'mine', horizon: 'quarter', forecast: 'all' }

/** The prototype's own reference point, so the fixtures' dates read as "this quarter". */
const TODAY = '2026-08-06'
const QUARTER_END = '2026-10-01'
const MONTH_END = '2026-09-01'

export interface StageTotal {
  stage: StageDefinition
  count: number
  sum: number
}

export interface ConsoleModel {
  filters: ConsoleFilters
  setFilter: <K extends keyof ConsoleFilters>(key: K, value: ConsoleFilters[K]) => void
  clear: () => void
  /** How many of the filters are not at their default, for the "showing…" line. */
  activeCount: number
  open: readonly RecordRow[]
  wonThisQuarter: readonly RecordRow[]
  totals: readonly StageTotal[]
  openValue: number
  weightedValue: number
  wonValue: number
  closingThisMonth: readonly RecordRow[]
  tasks: readonly RecordRow[]
}

const MINE = 'A. Ruiz'
const TEAM = new Set(['A. Ruiz', 'M. Chen'])

export function useConsole(): ConsoleModel {
  const [filters, setFilters] = useState<ConsoleFilters>(DEFAULTS)

  const model = useMemo(() => {
    const opportunity = OBJECT_MODELS['opportunity']
    if (!opportunity?.stages) {
      return { open: [], won: [], totals: [] as StageTotal[] }
    }

    const inScope = opportunity.records.filter((record) => {
      if (filters.owner === 'mine' && record['owner'] !== MINE) return false
      if (filters.owner === 'team' && !TEAM.has(String(record['owner']))) return false
      if (filters.forecast !== 'all' && record['forecast'] !== filters.forecast) return false

      const close = String(record['closeDate'] ?? '')
      if (filters.horizon === 'quarter' && close >= QUARTER_END) return false
      if (filters.horizon === 'month' && close >= MONTH_END) return false

      return true
    })

    const isClosed = (record: RecordRow) => String(record['stage'] ?? '').startsWith('Closed')
    const open = inScope.filter((record) => !isClosed(record))
    const won = inScope.filter((record) => record['stage'] === 'Closed Won')

    const totals: StageTotal[] = opportunity.stages
      .filter((stage) => !stage.won && !stage.lost)
      .map((stage) => {
        const rows = open.filter((record) => record['stage'] === stage.name)
        return {
          stage,
          count: rows.length,
          sum: rows.reduce((sum, row) => sum + Number(row['amount'] ?? 0), 0),
        }
      })

    return { open, won, totals }
  }, [filters])

  const openValue = model.open.reduce((sum, row) => sum + Number(row['amount'] ?? 0), 0)

  const weightedValue = Math.round(
    model.open.reduce(
      (sum, row) => sum + (Number(row['amount'] ?? 0) * Number(row['probability'] ?? 0)) / 100,
      0,
    ),
  )

  const wonValue = model.won.reduce((sum, row) => sum + Number(row['amount'] ?? 0), 0)

  const closingThisMonth = model.open.filter((row) => {
    const close = String(row['closeDate'] ?? '')
    return close >= TODAY && close < MONTH_END
  })

  const activeCount =
    (filters.owner === DEFAULTS.owner ? 0 : 1) +
    (filters.horizon === DEFAULTS.horizon ? 0 : 1) +
    (filters.forecast === DEFAULTS.forecast ? 0 : 1)

  return {
    filters,
    setFilter: (key, value) => setFilters((current) => ({ ...current, [key]: value })),
    clear: () => setFilters(DEFAULTS),
    activeCount,
    open: model.open,
    wonThisQuarter: model.won,
    totals: model.totals,
    openValue,
    weightedValue,
    wonValue,
    closingThisMonth,
    tasks: OBJECT_MODELS['task']?.records ?? [],
  }
}
