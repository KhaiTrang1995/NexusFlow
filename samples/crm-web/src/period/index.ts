/**
 * The period, which belongs to no one feature.
 *
 * <strong>It lived inside `exec` and three features used it.</strong> Planning imported the hook
 * and the picker on six screens, the sales console imported the hook, and deleting `exec` would
 * have broken both — which is the test of whether a concept is a feature's. It is not: the board,
 * the scorecard, the plan tree, the quota report and the console are all "for a period", and the
 * period is the join between them.
 *
 * <strong>Outside `features/` rather than in `design`, `api` or `lib`</strong>, because it is none
 * of those three things. `design` draws and does not fetch, and this reads what the tenant has
 * declared; `api` is the transport and the contracts, and this holds React state and renders a
 * selector; `lib` is the leaf that knows nothing of ours. What it is, is a concept the product has
 * and the screens share — the same shape as `session`, which sits beside it for the same reason.
 */
export { NoPeriods, PeriodPicker } from './PeriodPicker'
export { usePeriod, withPeriod } from './period'
export type { PeriodChoice } from './period'
