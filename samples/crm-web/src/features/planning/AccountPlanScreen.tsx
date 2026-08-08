import { Page, PageHeader } from '@/design/primitives'
import { usePeriod, withPeriod } from '@/features/exec/period'
import { PeriodPicker } from '@/features/exec/PeriodPicker'
import { PlanDetailPanels } from './PlanDetailPanels'

/**
 * The tenant's account plans.
 *
 * HALF THIS SCREEN WAS A SECOND COPY OF THE OTHER HALF. The live panel below shows a plan's
 * objectives, its mutual action plan, its risks, its qualification and its stakeholders; beneath
 * it sat a fixture section showing the same five things about an invented account, under a line
 * reading "Everything below is sample data". A reader with two stakeholder maps on one page has
 * to work out which is theirs, and the label only tells them after they have read both.
 *
 * WHAT IT IS FOR, BEYOND A NUMBER. Objectives, the people who have to agree, and the risks that
 * would stop it. The stakeholder map is the part that loses B2B deals: an account where nobody
 * has met the economic buyer is not a covered account, whatever the pipeline says.
 */
export function AccountPlanScreen() {
  const choice = usePeriod()

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Planning', choice)}
        title="Account plans"
        actions={<PeriodPicker choice={choice} />}
      />
      <PlanDetailPanels kind="Account" choice={choice} />
    </Page>
  )
}
