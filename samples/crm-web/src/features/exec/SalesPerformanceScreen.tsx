import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
} from '@/design/primitives'
import { useQuotaAttainment, useSalesPerformance } from '@/api/queries/hooks'
import type { QuotaAttainment, SellerPerformance } from '@/api/contracts'
import { fullMoney, money, percent } from '@/lib/format'
import { PERIODS, PERIOD_LABEL, usePeriod } from './period'
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
  const [period, setPeriod] = usePeriod()
  const sales = useSalesPerformance(period)
  const quota = useQuotaAttainment(period)

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Sales performance"
        actions={
          <ButtonGroup label="Period">
            {PERIODS.map((option) => (
              <Button key={option} aria-pressed={period === option} onClick={() => setPeriod(option)}>
                {PERIOD_LABEL[option]}
              </Button>
            ))}
          </ButtonGroup>
        }
      />

      <AsyncBoundary query={quota} skeletonRows={4}>
        {(data) => {
          const assigned = data.rows.reduce((sum, row) => sum + row.quota, 0)
          const achieved = data.rows.reduce((sum, row) => sum + row.actual, 0)
          const committed = data.rows.reduce((sum, row) => sum + row.committed, 0)

          return (
            <>
              <StatGrid columns={4}>
                <StatTile label="Assigned" value={money(assigned)} note="after ramp" />
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
                  rowKey={(row) => row.userId}
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
                    { id: 'quota', header: 'Quota', numeric: true, cell: (row: QuotaAttainment) => fullMoney(row.quota), sortValue: (row: QuotaAttainment) => row.quota },
                    { id: 'committed', header: 'Committed', numeric: true, cell: (row: QuotaAttainment) => fullMoney(row.committed), sortValue: (row: QuotaAttainment) => row.committed },
                    { id: 'actual', header: 'Actual', numeric: true, cell: (row: QuotaAttainment) => fullMoney(row.actual), sortValue: (row: QuotaAttainment) => row.actual },
                    {
                      id: 'gap',
                      header: 'Commitment gap',
                      numeric: true,
                      cell: (row: QuotaAttainment) => (
                        <span className={row.commitmentGap > 0 ? styles.negative : styles.positive}>
                          {fullMoney(row.commitmentGap)}
                        </span>
                      ),
                      sortValue: (row: QuotaAttainment) => row.commitmentGap,
                    },
                    {
                      id: 'attainment',
                      header: 'Attainment',
                      numeric: true,
                      cell: (row: QuotaAttainment) =>
                        row.attainment === null ? (
                          <span className={styles.sub}>no number</span>
                        ) : (
                          <span className={row.attainment >= 1 ? styles.positive : undefined}>
                            {percent(row.attainment)}
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

      <AsyncBoundary query={sales} skeletonRows={4}>
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
    </Page>
  )
}
