import type { ReactNode } from 'react'
import { Button, ButtonGroup, EmptyState } from '@/design/primitives'
import type { PeriodChoice } from './period'

/**
 * The period selector, over what the tenant has actually declared.
 *
 * FIVE SCREENS HELD THE SAME THREE HARD-CODED BUTTONS. They are one component now not for the
 * duplication but because the empty case has to be answered identically everywhere: a selector
 * that renders nothing on a tenant with no periods, beside a screen that says why, is a screen
 * that explains itself. Five copies of that reasoning is five chances to get one of them wrong.
 */
export function PeriodPicker({ choice }: { choice: PeriodChoice }) {
  if (choice.periods.length === 0) {
    return null
  }

  return (
    <ButtonGroup label="Period">
      {choice.periods.map((period) => (
        <Button
          key={period.name}
          aria-pressed={choice.period === period.name}
          onClick={() => choice.select(period.name)}
        >
          {period.label}
        </Button>
      ))}
    </ButtonGroup>
  )
}

/**
 * What a screen shows instead of its numbers when the tenant has declared no periods.
 *
 * NOT AN ERROR, AND THAT IS THE WHOLE POINT. Before periods were readable this state arrived as
 * "the resource was not found — no period named 'fy26_q3' has been declared", which reads as a
 * broken screen rather than as a configuration step nobody has taken yet.
 */
export function NoPeriods({ what, action }: { what: string; action?: ReactNode }) {
  return (
    <EmptyState
      title="No periods have been declared"
      detail={`This screen reports on a period and this organisation has not declared one. An administrator declares periods; ${what} appears the moment one exists.`}
      {...(action === undefined ? {} : { action })}
    />
  )
}
