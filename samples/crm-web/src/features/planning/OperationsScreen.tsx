import { DataTable, Page, PageHeader, Panel, PanelHeader, StatGrid, StatTile, Meter, Tag } from '@/design/primitives'
import { fullMoney, money, percent } from '@/lib/format'
import { CAPACITY } from './planFixtures'
import type { CapacityRow } from './planFixtures'
import styles from './planning.module.css'

/**
 * The capacity model.
 *
 * RAMPED HEADCOUNT, NOT HEADCOUNT. A seller who started in June carries a fraction of a quota,
 * and a plan built on bodies rather than on ramped capacity is a plan that is short by exactly the
 * ramp — every year, in the same direction, and nobody can ever say why.
 */
export function OperationsScreen() {
  const capacity = CAPACITY.reduce((sum, row) => sum + row.capacity, 0)
  const target = CAPACITY.reduce((sum, row) => sum + row.target, 0)
  const people = CAPACITY.reduce((sum, row) => sum + row.people, 0)
  const ramped = CAPACITY.reduce((sum, row) => sum + row.rampedPeople, 0)

  return (
    <Page>
      <PageHeader eyebrow="Planning · operations" title="Capacity and coverage" />

      <StatGrid columns={4}>
        <StatTile label="Headcount" value={people} note={`${ramped.toFixed(1)} ramped`} />
        <StatTile label="Capacity" value={money(capacity)} note="ramped people × quota" />
        <StatTile label="Target" value={money(target)} note="what the business has committed" />
        <StatTile
          label="Coverage"
          value={percent(capacity / target)}
          direction={capacity >= target ? 'up' : 'down'}
          delta={capacity >= target ? 'covered' : `${money(target - capacity)} short`}
          note="capacity against target"
        />
      </StatGrid>

      <Panel padding="flush">
        <PanelHeader title="By team" note="ramped capacity against the number" />
        <DataTable
          caption="Capacity by team"
          rows={CAPACITY}
          rowKey={(row) => row.team}
          columns={[
            {
              id: 'team',
              header: 'Team',
              cell: (row: CapacityRow) => (
                <>
                  <span className={styles.link}>{row.team}</span>
                  <div className={styles.sub}>
                    {row.people} people · {row.rampedPeople.toFixed(1)} ramped
                  </div>
                </>
              ),
              sortValue: (row: CapacityRow) => row.team,
            },
            {
              id: 'quota',
              header: 'Quota each',
              numeric: true,
              cell: (row: CapacityRow) => fullMoney(row.quotaEach),
              sortValue: (row: CapacityRow) => row.quotaEach,
            },
            {
              id: 'capacity',
              header: 'Capacity',
              numeric: true,
              cell: (row: CapacityRow) => fullMoney(row.capacity),
              sortValue: (row: CapacityRow) => row.capacity,
            },
            {
              id: 'target',
              header: 'Target',
              numeric: true,
              cell: (row: CapacityRow) => fullMoney(row.target),
              sortValue: (row: CapacityRow) => row.target,
            },
            {
              id: 'coverage',
              header: 'Coverage',
              cell: (row: CapacityRow) => (
                <>
                  <Meter
                    label={`${row.team} capacity against target`}
                    value={row.capacity}
                    target={row.target}
                    tone={row.capacity >= row.target ? 'positive' : 'critical'}
                  />
                  <div className={styles.sub} style={{ marginTop: 3 }}>
                    {percent(row.capacity / row.target)}
                  </div>
                </>
              ),
            },
            {
              id: 'verdict',
              header: '',
              cell: (row: CapacityRow) =>
                row.capacity >= row.target ? (
                  <Tag tone="positive">Covered</Tag>
                ) : (
                  <Tag tone="critical" dot>
                    {money(row.target - row.capacity)} short
                  </Tag>
                ),
            },
          ]}
        />
      </Panel>
    </Page>
  )
}
