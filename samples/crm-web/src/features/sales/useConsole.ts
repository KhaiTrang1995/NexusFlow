import { useMemo, useState } from 'react'
import { useEntityPage, usePeriods } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import type { PeriodSummary, RecordView } from '@/api/contracts'

/**
 * Everything the sales console needs, from the tenant's own rows.
 *
 * THE LANDING PAGE MADE NO REQUEST AT ALL. Pipeline, weighted value, won this quarter, what is
 * closing and the task list were all derived from the prototype's fixture records, filtered by
 * `owner === 'A. Ruiz'` — a name in a file, not the person signed in. The first screen anybody
 * opens showed a pipeline belonging to nobody.
 *
 * WHY A HOOK AND NOT SIX `useMemo`s IN THE COMPONENT. The console's numbers are all views of the
 * same opportunity set, and the one thing they must do is agree: the pipeline total in the tile,
 * the sum of the funnel and the closing table are the same money read three ways. Deriving them
 * together, from one filtered list, is what makes that true by construction rather than by
 * everybody remembering to apply the same filter.
 *
 * THE STAGES ARE THE PUBLISHED PROCESS'S, WHICH IS WHY THE FUNNEL IS BUILT FROM THE ROWS. A stage
 * list this file held would be the stages it was compiled with; grouping by what the rows actually
 * carry means a stage an administrator added appears the moment a deal enters it.
 *
 * AND THE HORIZON HAS TWO ENDS. It had one: `close < quarterEnd`, which admits every deal the
 * tenant has ever had, so "This quarter" meant "all of history, up to the end of this quarter" and
 * the won tile had to be relabelled because it could never mean quarter-to-date. Every figure on
 * the screen inherited that window. `horizonOf` below is the whole of the fix, and the pure
 * functions beside it are what the console's tests act on.
 */

export type OwnerFilter = 'mine' | 'all'
export type HorizonFilter = 'quarter' | 'month' | 'open'
export type OutcomeFilter = 'all' | 'open' | 'Won' | 'Lost'

export interface ConsoleFilters {
  owner: OwnerFilter
  horizon: HorizonFilter
  outcome: OutcomeFilter
}

const DEFAULTS: ConsoleFilters = { owner: 'mine', horizon: 'quarter', outcome: 'open' }

export interface StageTotal {
  stage: string
  count: number
  sum: number
}

export interface ConsoleModel {
  filters: ConsoleFilters
  setFilter: <K extends keyof ConsoleFilters>(key: K, value: ConsoleFilters[K]) => void
  clear: () => void
  /** What the close filter turned out to mean, so the screen can say it rather than imply it. */
  horizon: Horizon
  /** How many of the filters are not at their default, for the "showing…" line. */
  activeCount: number
  isPending: boolean
  error: Error | null
  /** Asks both pages again, for the one place that reports the failure. */
  refetch: () => void
  open: readonly Deal[]
  won: readonly Deal[]
  totals: readonly StageTotal[]
  openValue: number
  weightedValue: number
  wonValue: number
  closingThisMonth: readonly Deal[]
  tasks: readonly Task[]
  overdueTasks: number
}

/** One opportunity, with the strings a page returns turned into the numbers a total needs. */
export interface Deal {
  id: string
  name: string
  account: string | null
  stage: string
  amount: number
  probability: number
  closeDate: string | null
  outcome: string | null
  ownerId: string | null
}

/** One activity. `overdue` is decided here against one clock, which is the browser's. */
export interface Task {
  id: string
  subject: string
  kind: string
  status: string
  dueAt: string | null
}

/**
 * The close dates a horizon admits — ISO days, both ends inclusive.
 *
 * NULL AT AN END MEANS UNBOUNDED THERE, AND ONLY "ALL OPEN" IS. The bug this type exists to make
 * unstateable was a window with `to` and no `from`.
 */
export interface Horizon {
  from: string | null
  to: string | null
  /** What to call it. The tenant's own name for the period wherever there is one. */
  label: string
}

/** "All open" is every date there has ever been, which is what the chip says. */
const UNBOUNDED: Horizon = { from: null, to: null, label: 'any close date' }

/**
 * What a close filter means, in dates.
 *
 * THE TENANT'S OWN BOUNDARIES BEFORE THE BROWSER'S. A fiscal quarter is whatever an administrator
 * declared it to be — FY26 Q4 runs July to September in this seed and elsewhere it does not — so a
 * quarter this file computed would be the calendar's, and would disagree with the target, the
 * quota and the roll-up on every screen the reader opens next. Which period contains today is the
 * server's answer too, taken once rather than in each reader's timezone.
 *
 * THE NARROWEST CURRENT ONE, because a quarter sits inside a year and today is inside both: the
 * chip says "This quarter" and the tightest declared window is the one that can honour it. What it
 * actually resolved to is on the screen beside the chips, so a tenant that declares only years
 * sees the year named rather than a quarter implied.
 *
 * A tenant that has declared nothing — and one whose periods this caller may not read — falls back
 * to the calendar quarter, which is a guess at the boundary and never a guess at the shape.
 */
export function horizonOf(
  filter: HorizonFilter,
  periods: readonly PeriodSummary[],
  now: Date,
): Horizon {
  if (filter === 'open') {
    return UNBOUNDED
  }

  if (filter === 'month') {
    // A month is the calendar's everywhere: what a tenant declares are planning periods, and this
    // schema has no notion of a fiscal month to prefer over this one.
    return {
      from: day(new Date(now.getFullYear(), now.getMonth(), 1)),
      to: day(new Date(now.getFullYear(), now.getMonth() + 1, 0)),
      label: 'this month',
    }
  }

  const declared = currentPeriod(periods)

  if (declared !== undefined) {
    return {
      from: declared.startsOn.slice(0, 10),
      to: declared.endsOn.slice(0, 10),
      label: declared.label,
    }
  }

  const quarter = Math.floor(now.getMonth() / 3) * 3

  return {
    from: day(new Date(now.getFullYear(), quarter, 1)),
    to: day(new Date(now.getFullYear(), quarter + 3, 0)),
    label: 'this quarter',
  }
}

/** Whether a deal's close date falls in the horizon. An undated deal falls in no bounded one. */
export function inHorizon(closeDate: string | null, horizon: Horizon): boolean {
  if (horizon.from === null && horizon.to === null) {
    return true
  }

  // Compared as days, not as whatever the column happens to be serialised as. `expected_close` is
  // a `date` and arrives as one, and the next date column this screen reads may be a timestamptz —
  // '2026-09-30T00:00:00+00:00' sorts after '2026-09-30' and would drop the last day of a quarter.
  const close = (closeDate ?? '').slice(0, 10)

  if (close === '') return false
  if (horizon.from !== null && close < horizon.from) return false
  if (horizon.to !== null && close > horizon.to) return false

  return true
}

/** The one filtered set, read every way the console reads it. */
export interface ConsoleScope {
  open: readonly Deal[]
  won: readonly Deal[]
  totals: readonly StageTotal[]
  closing: readonly Deal[]
  openValue: number
  weightedValue: number
  wonValue: number
}

/**
 * Every figure on the console, from one list and one window.
 *
 * PURE, AND EXPORTED, BECAUSE THE AGREEMENT IS THE THING BEING TESTED. The tiles, the funnel and
 * the closing table are three views of what this returns; a test that can call it can assert that
 * the funnel sums to the tile and that the table is a subset of the funnel's rows, which is the
 * property the hook exists to guarantee.
 */
export function scopeOf(
  deals: readonly Deal[],
  filters: ConsoleFilters,
  ownerId: string,
  horizon: Horizon,
  month: Horizon,
): ConsoleScope {
  /** Whose deals, closing when — the two filters that are about the deal rather than its end. */
  const inRange = deals.filter((deal) => {
    // "Mine" is answerable and "my team's" is not: an opportunity carries an owner uuid and the
    // reporting line is keyed by the subject a token carries. They are different identity
    // spaces, so a team filter here would be a guess. The executive board is scoped by the line
    // on the server, which is where that question belongs.
    if (filters.owner === 'mine' && deal.ownerId !== ownerId) return false

    return inHorizon(deal.closeDate, horizon)
  })

  const inScope = inRange.filter((deal) => {
    if (filters.outcome === 'open' && deal.outcome !== null) return false
    if (filters.outcome === 'Won' && deal.outcome !== 'Won') return false
    if (filters.outcome === 'Lost' && deal.outcome !== 'Lost') return false

    return true
  })

  const open = inScope.filter((deal) => deal.outcome === null)

  /*
    WON IS COUNTED OUTSIDE THE OUTCOME FILTER, BECAUSE THE OUTCOME FILTER EXCLUDES IT. This read
    `inScope.filter(outcome === 'Won')`, and the console opens with Outcome set to Open — which
    drops every won deal before the count runs. The "Closed won" tile could therefore read
    anything at all as long as it read $0, on every tenant, for ever, and the note under it said
    "won, in this filter", which was true and was the reason nobody looked. A tile that names its
    own outcome is not the outcome chips' to empty; what those scope is the funnel and the table.
  */
  const won = inRange.filter((deal) => deal.outcome === 'Won')

  // Grouped by the stage each row is actually in, in the order they first appear — which is the
  // order the page returned them in, which is the process's ordinal.
  const byStage = new Map<string, StageTotal>()

  for (const deal of open) {
    const total = byStage.get(deal.stage) ?? { stage: deal.stage, count: 0, sum: 0 }

    total.count += 1
    total.sum += deal.amount
    byStage.set(deal.stage, total)
  }

  return {
    open,
    won,
    totals: [...byStage.values()],

    /*
      THE MONTH INSIDE THE WINDOW, AND NOT A SECOND WINDOW OF ITS OWN. This table read
      `[today, monthEnd)` whatever the chips said, so it was the one figure on the screen with a
      near end — and an open deal whose close date had already passed was counted by every tile
      and shown by no table, which is exactly the deal somebody opens this screen to chase. It is
      a slice of the open set now: same rows, narrower dates.
    */
    closing: open.filter((deal) => inHorizon(deal.closeDate, month)),
    openValue: open.reduce((sum, deal) => sum + deal.amount, 0),
    weightedValue: Math.round(
      open.reduce((sum, deal) => sum + (deal.amount * deal.probability) / 100, 0),
    ),
    wonValue: won.reduce((sum, deal) => sum + deal.amount, 0),
  }
}

/**
 * The tightest period the server says today is inside, or none.
 *
 * Span rather than position in the list: the list is ordered by start date and a year declared
 * after its own quarters would otherwise win.
 */
function currentPeriod(periods: readonly PeriodSummary[]): PeriodSummary | undefined {
  let narrowest: PeriodSummary | undefined

  for (const period of periods) {
    if (!period.isCurrent) continue
    if (narrowest === undefined || spanOf(period) < spanOf(narrowest)) narrowest = period
  }

  return narrowest
}

function spanOf(period: PeriodSummary): number {
  return Date.parse(period.endsOn) - Date.parse(period.startsOn)
}

/**
 * A local calendar day, as the column is written.
 *
 * NOT `toISOString().slice(0, 10)`, which answers in UTC: west of Greenwich the first of the month
 * is the last of the one before, so a month boundary computed that way is a day out for half the
 * world and correct in the office it was written in.
 */
function day(value: Date): string {
  const month = String(value.getMonth() + 1).padStart(2, '0')
  const date = String(value.getDate()).padStart(2, '0')

  return `${value.getFullYear()}-${month}-${date}`
}

export function useConsole(): ConsoleModel {
  const { ownerId } = useSession()

  const deals = useEntityPage('Opportunity')
  const activities = useEntityPage('Activity')

  // The same cached read the executive period picker makes — one request for the whole client, and
  // already in flight on this screen for the attainment panel.
  const declared = usePeriods()

  const [filters, setFilters] = useState<ConsoleFilters>(DEFAULTS)

  const all = useMemo(() => (deals.data?.records ?? []).map(toDeal), [deals.data])

  // Memoised on the query's own data, so the empty case is one array rather than a new one per
  // render — everything below is keyed on it.
  const periods = useMemo(() => declared.data?.periods ?? [], [declared.data])

  // Today, and not a date this file was written on. The fixtures had a hard-coded "today" so their
  // dates read as this quarter; live rows are dated whenever the tenant made them.
  const horizon = useMemo(
    () => horizonOf(filters.horizon, periods, new Date()),
    // The periods are a stable cached array, and the horizon has to be recomputed when they land.
    [filters.horizon, periods],
  )

  const month = useMemo(() => horizonOf('month', periods, new Date()), [periods])

  const model = useMemo(
    () => scopeOf(all, filters, ownerId, horizon, month),
    [all, filters, ownerId, horizon, month],
  )

  const tasks = useMemo(
    () => (activities.data?.records ?? []).map(toTask).filter((task) => task.status !== 'Completed'),
    [activities.data],
  )

  const activeCount =
    (filters.owner === DEFAULTS.owner ? 0 : 1) +
    (filters.horizon === DEFAULTS.horizon ? 0 : 1) +
    (filters.outcome === DEFAULTS.outcome ? 0 : 1)

  const today = new Date().toISOString()

  return {
    filters,
    setFilter: (key, value) => setFilters((current) => ({ ...current, [key]: value })),
    clear: () => setFilters(DEFAULTS),
    horizon,
    activeCount,
    /*
      THE PERIODS ARE WAITED FOR, AND THEIR REFUSAL IS NOT AN ERROR HERE. Waited for, because the
      window would otherwise be the calendar's for one render and the tenant's for the next, and
      every figure on the screen would move under the reader without a filter having been touched.
      Not an error, because a caller who may not read the planning surface can still be shown their
      own pipeline: the fallback is a calendar quarter, which `horizonOf` names as one.
    */
    isPending: deals.isPending || activities.isPending || declared.isPending,
    error: deals.error ?? activities.error,
    refetch: () => {
      void deals.refetch()
      void activities.refetch()
    },
    open: model.open,
    won: model.won,
    totals: model.totals,
    openValue: model.openValue,
    weightedValue: model.weightedValue,
    wonValue: model.wonValue,
    closingThisMonth: model.closing,
    tasks,
    overdueTasks: tasks.filter((task) => task.dueAt !== null && task.dueAt < today).length,
  }
}

/**
 * One page row as a deal.
 *
 * EVERY VALUE IN A PAGE IS TEXT, including the numbers: the projection returns strings so a filter
 * can compare them. Summing them as strings concatenates, which is the one arithmetic mistake that
 * produces a plausible-looking total.
 */
function toDeal(record: RecordView): Deal {
  return {
    id: record.recordId,
    name: record.values['name'] ?? '—',
    account: record.values['account_id'] ?? null,
    stage: record.values['stage'] ?? '—',
    amount: Number(record.values['amount'] ?? 0),
    probability: Number(record.values['probability'] ?? 0),
    closeDate: record.values['expected_close'] ?? null,
    outcome: record.values['outcome'] ?? null,
    ownerId: record.values['owner_id'] ?? null,
  }
}

function toTask(record: RecordView): Task {
  return {
    id: record.recordId,
    subject: record.values['subject'] ?? '—',
    kind: record.values['kind'] ?? 'Task',
    status: record.values['status'] ?? 'Open',
    dueAt: record.values['due_at'] ?? null,
  }
}
