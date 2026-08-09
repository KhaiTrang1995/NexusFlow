import { Page, PageHeader } from '@/design/primitives'
import { PeriodPicker, usePeriod, withPeriod } from '@/period'
import { PlanDetailPanels } from './PlanDetailPanels'

/**
 * The tenant's deal plans.
 *
 * THE SAME FIVE SECTIONS AS AN ACCOUNT PLAN, ABOUT A DEAL. One component draws both because they
 * differ in which plans they list and in nothing else — and the fixture half that used to sit
 * under this one was a second, invented copy of exactly that.
 *
 * QUALIFICATION IS THE ONE WORTH OPENING. A deal nobody can name the economic buyer of is not a
 * qualified deal, and the answers are stored rather than remembered so the gap is visible before
 * the quarter ends rather than after.
 */
export function OpportunityPlanScreen() {
  const choice = usePeriod()

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Planning', choice)}
        title="Deal plans"
        actions={<PeriodPicker choice={choice} />}
      />
      <PlanDetailPanels kind="Opportunity" choice={choice} />
    </Page>
  )
}
