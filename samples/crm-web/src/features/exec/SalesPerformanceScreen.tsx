import {
  AsyncBoundary,
  Button,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
} from '@/design/primitives'
import { useState } from 'react'
import { useQuotaAttainment, useSalesPerformance } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { SetQuotaDrawer } from './SetQuotaDrawer'
import type { QuotaAttainment, SellerPerformance } from '@/api/contracts'
import { fullMoney, money, pct, percent } from '@/lib/format'
import { NoPeriods, PeriodPicker, usePeriod } from '@/period'
import styles from './exec.module.css'

/**
 * How the people are doing.
 *
 * COMMITMENT AND QUOTA ARE DIFFERENT NUMBERS AND ARE SHOWN AS SUCH. A quota is assigned
 * downwards; a commitment is offered upwards. The gap between them is what a sales-operations
 * review is actually about — a seller carrying 500 who has committed 380 has a 120 hole that no
 * roll-up of commitments can show, because every commitment in it is real.
 */
export function SalesPerformanceScreen() {
  const choice = usePeriod()
  const sales = useSalesPerformance(choice.period)
  const quota = useQuotaAttainment(choice.period)
  const session = useSession()
  const [assigning, setAssigning] = useState(false)

  // Whoever the period already reports on. Assigning to somebody the server does not know about
  // writes a row no review will ever show, which looks like the write failing rather than the
  // name being wrong.
  // One entry per person, not per quota row: somebody carrying both a revenue and a leads
  // number appears twice in the attainment rows, and a picker listing them twice reads as a
  // duplicate record rather than as two measures.
  const people = [
    ...new Map(
      (quota.data?.rows ?? sales.data?.sellers ?? []).map((row) => [row.userId, row]),
    ).values(),
  ]

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Sales performance"
        actions={
          <>
            <PeriodPicker choice={choice} />
            {session.can('crm.admin') ? (
              <Button
                tone="primary"
                disabled={people.length === 0}
                title={people.length === 0 ? 'Nobody is reported on for this period yet.' : undefined}
                onClick={() => setAssigning(true)}
              >
                Set a quota
              </Button>
            ) : null}
          </>
        }
      />

      {choice.isUndeclared ? <NoPeriods what="sales performance" /> : null}

      <AsyncBoundary query={quota} skeletonRows={4} hidden={choice.isUndeclared}>
        {(data) => {
          // Only the revenue rows. A quota can be carried in leads or in activities, and adding
          // forty leads to three hundred thousand euros produced "€300,040" — a number that is
          // not wrong by a little, it is not a quantity at all. Summing across measures is the
          // arithmetic every currency bug in this repo has looked like.
          const revenue = data.rows.filter((row) => row.measure === 'Revenue')

          const assigned = revenue.reduce((sum, row) => sum + row.quota, 0)
          const achieved = revenue.reduce((sum, row) => sum + row.actual, 0)
          const committed = revenue.reduce((sum, row) => sum + (row.committed ?? 0), 0)

          const others = data.rows.length - revenue.length

          return (
            <>
              <StatGrid columns={4}>
                <StatTile
                  label="Assigned"
                  value={money(assigned)}
                  note={
                    others === 0
                      ? 'after ramp'
                      : `after ramp · ${others} row(s) in another measure, not added`
                  }
                />
                <StatTile
                  label="Committed"
                  value={money(committed)}
                  delta={percent(assigned === 0 ? null : committed / assigned)}
                  direction={committed >= assigned ? 'up' : 'down'}
                  note="of the assigned number"
                />
                <StatTile
                  label="Achieved"
                  value={money(achieved)}
                  delta={percent(assigned === 0 ? null : achieved / assigned)}
                  direction={achieved >= assigned ? 'up' : 'down'}
                  note="read live"
                />
                <StatTile
                  label="Commitment gap"
                  value={money(assigned - committed)}
                  direction={assigned - committed > 0 ? 'down' : 'up'}
                  note="quota less committed"
                />
              </StatGrid>

              <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
                <PanelHeader title="Against the number" note="weakest attainment first" />
                <DataTable
                  caption="Quota attainment"
                  rows={data.rows}
                  // A person carries one quota per measure, so the id alone is not a key —
                  // React silently dropped the second row of anybody holding two.
                  rowKey={(row) => `${row.userId}/${row.measure}`}
                  columns={[
                    {
                      id: 'name',
                      header: 'Person',
                      cell: (row: QuotaAttainment) => (
                        <>
                          <span className={styles.link}>{row.displayName}</span>
                          <div className={styles.sub}>{row.measure}</div>
                        </>
                      ),
                      sortValue: (row: QuotaAttainment) => row.displayName,
                    },
                    { id: 'quota', header: 'Quota', numeric: true, cell: (row: QuotaAttainment) => inMeasure(row, row.quota), sortValue: (row: QuotaAttainment) => row.quota },
                    { id: 'committed', header: 'Committed', numeric: true, cell: (row: QuotaAttainment) => inMeasure(row, row.committed), sortValue: (row: QuotaAttainment) => row.committed ?? -1 },
                    { id: 'actual', header: 'Actual', numeric: true, cell: (row: QuotaAttainment) => inMeasure(row, row.actual), sortValue: (row: QuotaAttainment) => row.actual },
                    {
                      id: 'gap',
                      header: 'Commitment gap',
                      numeric: true,
                      cell: (row: QuotaAttainment) => (
                        <span
                          className={
                            row.commitmentGap === null
                              ? styles.sub
                              : row.commitmentGap > 0
                                ? styles.negative
                                : styles.positive
                          }
                        >
                          {inMeasure(row, row.commitmentGap)}
                        </span>
                      ),
                      sortValue: (row: QuotaAttainment) => row.commitmentGap ?? 0,
                    },
                    {
                      id: 'attainment',
                      header: 'Attainment',
                      numeric: true,
                      cell: (row: QuotaAttainment) =>
                        row.attainment === null ? (
                          <span className={styles.sub}>no number</span>
                        ) : (
                          <span className={row.attainment >= 100 ? styles.positive : undefined}>
                            {pct(row.attainment, 1)}
                          </span>
                        ),
                      sortValue: (row: QuotaAttainment) => row.attainment ?? -1,
                    },
                  ]}
                  empty="Nobody carries a quota this period."
                />
              </Panel>
            </>
          )
        }}
      </AsyncBoundary>

      <AsyncBoundary query={sales} skeletonRows={4} hidden={choice.isUndeclared}>
        {(data) => (
          <Panel padding="flush">
            <PanelHeader title="Pipeline and closed" note={`${data.sellers.length} people`} />
            <DataTable
              caption="Sellers"
              rows={data.sellers}
              rowKey={(row) => row.userId}
              columns={[
                {
                  id: 'name',
                  header: 'Seller',
                  cell: (row: SellerPerformance) => (
                    <>
                      <span className={styles.link}>{row.displayName}</span>
                      <div className={styles.sub}>{row.role}</div>
                    </>
                  ),
                  sortValue: (row: SellerPerformance) => row.displayName,
                },
                { id: 'open', header: 'Open pipeline', numeric: true, cell: (row: SellerPerformance) => fullMoney(row.openPipeline), sortValue: (row: SellerPerformance) => row.openPipeline },
                { id: 'committed', header: 'Committed', numeric: true, cell: (row: SellerPerformance) => fullMoney(row.committed), sortValue: (row: SellerPerformance) => row.committed },
                { id: 'won', header: 'Won', numeric: true, cell: (row: SellerPerformance) => fullMoney(row.won), sortValue: (row: SellerPerformance) => row.won },
              ]}
              empty="No sellers in scope."
            />
          </Panel>
        )}
      </AsyncBoundary>
      {assigning ? (
        <SetQuotaDrawer period={choice.period as string} people={people} onClose={() => setAssigning(false)} />
      ) : null}
    </Page>
  )
}

/**
 * A quota figure in the unit its own row is measured in.
 *
 * A LEADS QUOTA IS A COUNT, NOT AN AMOUNT. Rendering forty leads as "€40.00" is not a formatting
 * slip: it reads as a target somebody would query, and the person who set it has no way to tell
 * from the screen that the number is right and the currency sign is wrong.
 */
function inMeasure(row: QuotaAttainment, value: number | null): string {
  // Null is "the question does not arise here", which an em dash says and a zero does not.
  if (value === null) {
    return '—'
  }

  return row.measure === 'Revenue' ? fullMoney(value) : value.toLocaleString('en-GB')
}
