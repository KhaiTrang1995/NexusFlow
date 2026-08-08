import { Page, PageHeader } from '@/design/primitives'
import { usePeriod, withPeriod } from '@/features/exec/period'
import { PeriodPicker } from '@/features/exec/PeriodPicker'
import { PlanDetailPanels } from './PlanDetailPanels'

/**
 * The tenant's demand plans.
 *
 * THIS SCREEN MADE NO REQUEST AT ALL. A demand table by channel — targets, actuals, cost per lead,
 * six channels — was written into the file, on the one plan screen of three that never called the
 * server. `MarketingLead` has been a plan kind throughout, and the roll-up above counts them: the
 * strategy screen said "3 demand plans" while this one showed six invented channels.
 *
 * COST PER LEAD IS NOT HERE, AND THAT IS WHY. A plan commits a target amount; what a channel
 * actually spent is the campaign surface's answer, not the planning one's. The fixture merged the
 * two into a single table that neither read could produce.
 */
export function LeadPlanScreen() {
  const choice = usePeriod()

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Planning', choice)}
        title="Demand plans"
        actions={<PeriodPicker choice={choice} />}
      />
      <PlanDetailPanels kind="MarketingLead" choice={choice} />
    </Page>
  )
}
