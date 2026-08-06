import { Columns, DataTable, Meter, Page, PageHeader, Panel, PanelHeader, StatGrid, StatTile, Tag } from '@/design/primitives'
import { StackedBars } from '@/design/charts'
import { fullMoney, money, percent } from '@/lib/format'
import { DEMAND } from './planFixtures'
import type { DemandRow } from './planFixtures'
import styles from './planning.module.css'

/**
 * The demand plan — what marketing has committed to deliver.
 *
 * COST PER LEAD IS SHOWN BESIDE ATTAINMENT, NOT INSTEAD OF IT. A channel at 110% of target and
 * four times the cost of every other one is not a channel that is winning; a plan that only
 * showed attainment would keep funding it.
 */
export function LeadPlanScreen() {
  const target = DEMAND.reduce((sum, row) => sum + row.target, 0)
  const actual = DEMAND.reduce((sum, row) => sum + row.actual, 0)
  const spend = DEMAND.reduce((sum, row) => sum + row.actual * row.costPerLead, 0)
  const short = DEMAND.filter((row) => row.actual < row.target)

  return (
    <Page>
      <PageHeader eyebrow="Planning · marketing" title="Demand plan" />

      <StatGrid columns={4}>
        <StatTile label="Leads targeted" value={target} note="across every segment" />
        <StatTile
          label="Delivered"
          value={actual}
          delta={percent(actual / target)}
          direction={actual >= target ? 'up' : 'down'}
          note="of the number committed"
        />
        <StatTile label="Spend" value={money(spend)} note="at the recorded cost per lead" />
        <StatTile
          label="Channels short"
          value={short.length}
          direction={short.length > 0 ? 'down' : 'up'}
          note={`of ${DEMAND.length}`}
        />
      </StatGrid>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title="By segment and channel" note="target against delivered" />
          <DataTable
            caption="Demand by segment and channel"
            rows={DEMAND}
            rowKey={(row) => row.segment + row.channel}
            columns={[
              {
                id: 'segment',
                header: 'Segment',
                cell: (row: DemandRow) => (
                  <>
                    <span className={styles.link}>{row.segment}</span>
                    <div className={styles.sub}>{row.channel}</div>
                  </>
                ),
                sortValue: (row: DemandRow) => row.segment,
              },
              { id: 'target', header: 'Target', numeric: true, cell: (row: DemandRow) => row.target, sortValue: (row: DemandRow) => row.target },
              {
                id: 'actual',
                header: 'Delivered',
                numeric: true,
                cell: (row: DemandRow) => (
                  <span className={row.actual >= row.target ? styles.covered : styles.gap}>
                    {row.actual}
                  </span>
                ),
                sortValue: (row: DemandRow) => row.actual,
              },
              {
                id: 'cost',
                header: 'Cost per lead',
                numeric: true,
                cell: (row: DemandRow) => (
                  <span className={row.costPerLead > 300 ? styles.gap : undefined}>
                    {fullMoney(row.costPerLead)}
                  </span>
                ),
                sortValue: (row: DemandRow) => row.costPerLead,
              },
              {
                id: 'meter',
                header: 'Attainment',
                cell: (row: DemandRow) => (
                  <Meter
                    label={`${row.segment} ${row.channel} attainment`}
                    value={row.actual}
                    target={row.target}
                    tone={row.actual >= row.target ? 'positive' : 'accent'}
                  />
                ),
              },
            ]}
          />
        </Panel>

        <Panel>
          <PanelHeader title="Where the leads came from" note="delivered, by channel" />
          <div style={{ paddingTop: 14 }}>
            <StackedBars
              caption="Leads delivered by channel"
              series={[
                { label: 'Enterprise', colour: 'var(--color-accent-800)' },
                { label: 'Mid-market', colour: 'var(--color-accent)' },
                { label: 'Public sector', colour: 'var(--color-accent-300)' },
              ]}
              bars={[...new Set(DEMAND.map((row) => row.channel))].map((channel) => {
                const rows = DEMAND.filter((row) => row.channel === channel)
                const pick = (segment: string) =>
                  rows.filter((row) => row.segment === segment).reduce((sum, row) => sum + row.actual, 0)

                return {
                  label: channel,
                  values: [pick('Enterprise'), pick('Mid-market'), pick('Public sector')],
                  readout: String(rows.reduce((sum, row) => sum + row.actual, 0)),
                }
              })}
              height={200}
            />
          </div>
          <div style={{ marginTop: 14, display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {short.map((row) => (
              <Tag key={row.segment + row.channel} tone="warning">
                {row.segment} {row.channel} short by {row.target - row.actual}
              </Tag>
            ))}
          </div>
        </Panel>
      </Columns>
    </Page>
  )
}
