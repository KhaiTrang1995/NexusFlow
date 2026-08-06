import { useState } from 'react'

/**
 * Which period every executive screen is looking at.
 *
 * ONE PLACE, BECAUSE THE PERIOD IS THE JOIN. The board, the scorecard, the plan tree and the
 * quota report are all "for a period", and two screens reading different ones is how a director
 * ends up comparing this quarter's commitments against last quarter's target without noticing.
 * The period is in the query key too, so switching it refetches rather than showing the old
 * numbers under the new heading.
 */

export const PERIODS = ['fy26-q3', 'fy26-q2', 'fy26-q1'] as const

export type Period = (typeof PERIODS)[number]

export const PERIOD_LABEL: Readonly<Record<Period, string>> = {
  'fy26-q3': 'Q3 FY26',
  'fy26-q2': 'Q2 FY26',
  'fy26-q1': 'Q1 FY26',
}

export function usePeriod(): [Period, (period: Period) => void] {
  const [period, setPeriod] = useState<Period>('fy26-q3')
  return [period, setPeriod]
}
