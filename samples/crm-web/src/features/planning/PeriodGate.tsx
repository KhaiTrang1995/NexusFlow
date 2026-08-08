import { ErrorState } from '@/design/primitives'
import { NoPeriods } from '@/features/exec/PeriodPicker'
import type { PeriodChoice } from '@/features/exec/period'

/**
 * What a planning screen shows when there is no period for it to ask about.
 *
 * <strong>Two states, and only one of them was answered.</strong> A tenant that has declared no
 * periods gets a sentence saying so. A tenant whose period list could not be read gets nothing at
 * all: every hook on these screens takes a period and asks nothing for null, so a disabled query
 * is pending for ever, and a skeleton is `aria-hidden` — the whole page was a heading over white
 * space, which reads as a broken build rather than as a refused request.
 *
 * <strong>The refusal is shown before the emptiness.</strong> "This organisation has not declared
 * a period" is a claim about the tenant, and a failed read is not evidence for it.
 */
export function PeriodGate({ choice, what }: { choice: PeriodChoice; what: string }) {
  if (choice.error !== null) {
    return <ErrorState error={choice.error} />
  }

  if (choice.isUndeclared) {
    return <NoPeriods what={what} />
  }

  return null
}

/**
 * Whether this screen can ask the server anything yet.
 *
 * The flag every boundary on these screens is hidden by: no period means no request was made, so
 * there is nothing pending and nothing to draw a placeholder for.
 */
export function hasNoPeriod(choice: PeriodChoice): boolean {
  return choice.error !== null || choice.isUndeclared
}
