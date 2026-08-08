import { useState } from 'react'
import { usePeriods } from '@/api/queries/hooks'
import type { PeriodSummary } from '@/api/contracts'

/**
 * Which period every executive and planning screen is looking at.
 *
 * THE NAMES WERE THIS FILE'S, AND THEY BELONGED TO ONE TENANT. `fy26_q3`, `fy26_q2` and `fy26_q1`
 * were written here as constants; they matched the seeded fixtures by construction and matched
 * nothing else. On any other organisation twelve screens asked the server for a quarter nobody had
 * declared, and each showed "no period named 'fy26_q3' has been declared for this tenant" —
 * underneath a selector that printed Q3 FY26 as though it existed.
 *
 * ONE PLACE, BECAUSE THE PERIOD IS THE JOIN. The board, the scorecard, the plan tree and the quota
 * report are all "for a period", and two screens reading different ones is how a director ends up
 * comparing this quarter's commitments against last quarter's target without noticing. The period
 * is in the query key too, so switching it refetches rather than showing the old numbers under the
 * new heading.
 *
 * WHICH ONE IS CURRENT IS THE SERVER'S ANSWER. A browser deciding it does so in whatever timezone
 * the machine is set to; two offices would open the same screen on different quarters on the last
 * day of one.
 */
export interface PeriodChoice {
  /** Every period this tenant has declared, most recent first. */
  periods: readonly PeriodSummary[]
  /**
   * The one being looked at. Null while the list is loading and when the tenant has declared
   * none — which is why every hook that takes a period takes null and asks nothing for it.
   */
  period: string | null
  /** What to show a person, or an empty string when there is nothing to show. */
  label: string
  select: (name: string) => void
  isPending: boolean
  /** The answer is in and it is empty. A screen says so once instead of showing a not-found. */
  isUndeclared: boolean
  error: Error | null
}

/**
 * An eyebrow with the period on it, or without one when there is none.
 *
 * `Executive · ${label}` reads as "EXECUTIVE ·" on a tenant with no periods — a separator
 * pointing at nothing, which looks like a value that failed to load.
 */
export function withPeriod(prefix: string, choice: PeriodChoice): string {
  return choice.label === '' ? prefix : `${prefix} · ${choice.label}`
}

export function usePeriod(): PeriodChoice {
  const declared = usePeriods()
  const [chosen, setChosen] = useState<string | null>(null)

  const periods = declared.data?.periods ?? []

  // The chosen one only while it is still declared — an administrator can retire a period, and a
  // selection held in state would then ask for one that no longer exists.
  const current =
    periods.find((period) => period.name === chosen)
    ?? periods.find((period) => period.isCurrent)
    ?? periods[0]

  return {
    periods,
    period: current?.name ?? null,
    label: current?.label ?? '',
    select: setChosen,
    isPending: declared.isPending,
    isUndeclared: !declared.isPending && declared.error === null && periods.length === 0,
    error: declared.error,
  }
}
