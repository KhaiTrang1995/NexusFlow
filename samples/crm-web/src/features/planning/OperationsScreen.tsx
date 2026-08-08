import {
  AsyncBoundary,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
  Meter,
  Tag,
} from '@/design/primitives'
import { fullMoney, money, percent } from '@/lib/format'
import { useCoverage, useOrgChart, useQuotaAttainment } from '@/api/queries/hooks'
import { usePeriod, withPeriod } from '@/features/exec/period'
import { NoPeriods, PeriodPicker } from '@/features/exec/PeriodPicker'
import { capacityOf } from './capacity'
import type { TeamCapacity } from './capacity'
import styles from './planning.module.css'

/**
 * The capacity model.
 *
 * RAMPED HEADCOUNT, NOT HEADCOUNT. A seller who started in June carries a fraction of a quota,
 * and a plan built on bodies rather than on ramped capacity is a plan that is short by exactly the
 * ramp — every year, in the same direction, and nobody can ever say why.
 *
 * IT WAS FOUR TEAMS IN A FILE. 29 people, €9.3M of capacity and 94% coverage were constants in
 * this client, shown to every tenant including an empty one. The numbers are the quotas people
 * actually carry now, and a team is whoever they report to — the only grouping this schema has.
 */
export function OperationsScreen() {
  const choice = usePeriod()
  const quota = useQuotaAttainment(choice.period)
  const org = useOrgChart()
  const coverage = useCoverage()

  const capacity = capacityOf(quota.data?.rows ?? [], org.data?.members ?? [])

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Planning · operations', choice)}
        title="Capacity and coverage"
        actions={<PeriodPicker choice={choice} />}
      />

      {choice.isUndeclared ? <NoPeriods what="capacity" /> : null}

      <AsyncBoundary query={quota} skeletonRows={4} hidden={choice.isUndeclared}>
        {() => (
          <StatGrid columns={4}>
            <StatTile
              label="Headcount"
              value={capacity.people}
              note={`${capacity.rampedPeople.toFixed(1)} ramped · carrying a revenue number`}
            />
            <StatTile
              label="Capacity"
              value={money(capacity.capacity)}
              note="what they can carry, after ramp"
            />
            <StatTile
              label="Target"
              value={money(capacity.target)}
              note="what was assigned, before ramp"
            />
            <StatTile
              label="Coverage"
              value={capacity.target === 0 ? '—' : percent(capacity.capacity / capacity.target)}
              direction={capacity.capacity >= capacity.target ? 'up' : 'down'}
              delta={
                capacity.target === 0
                  ? 'nobody carries a number'
                  : capacity.capacity >= capacity.target
                    ? 'covered'
                    : `${money(capacity.target - capacity.capacity)} short`
              }
              note="capacity against target"
            />
          </StatGrid>
        )}
      </AsyncBoundary>

      <AsyncBoundary query={coverage} skeletonRows={4}>
        {(map) => (
          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader
              title="Territory coverage"
              note={`${map.territories.length} territories · ${map.unrouted} accounts routed nowhere`}
            />

            {/*
              The two numbers a list-per-person model cannot produce. An account in nobody's
              territory looks exactly like an account nobody has got to yet, and a territory with
              rules and no owner routes accounts into a queue nobody reads.
            */}
            <StatGrid columns={2}>
              <StatTile
                label="Accounts routed nowhere"
                value={map.unrouted}
                direction={map.unrouted === 0 ? 'up' : 'down'}
                note="in no territory at all"
              />
              <StatTile
                label="Territories with nobody on them"
                value={map.unowned}
                direction={map.unowned === 0 ? 'up' : 'down'}
                note="rules with no owner"
              />
            </StatGrid>

            <DataTable
              caption="Territory coverage"
              rows={map.territories}
              rowKey={(row) => row.territory}
              columns={[
                {
                  id: 'territory',
                  header: 'Territory',
                  cell: (row: { label: string; territory: string }) => (
                    <>
                      <span className={styles.link}>{row.label}</span>
                      <div className={styles.sub}>{row.territory}</div>
                    </>
                  ),
                  sortValue: (row: { label: string }) => row.label,
                },
                {
                  id: 'owners',
                  header: 'Owners',
                  numeric: true,
                  cell: (row: { owners: number }) =>
                    row.owners === 0 ? <Tag tone="critical">none</Tag> : row.owners,
                  sortValue: (row: { owners: number }) => row.owners,
                },
                {
                  id: 'accounts',
                  header: 'Accounts',
                  numeric: true,
                  cell: (row: { accounts: number }) => row.accounts,
                  sortValue: (row: { accounts: number }) => row.accounts,
                },
              ]}
              empty="No territories are declared."
            />
          </Panel>
        )}
      </AsyncBoundary>

      <Panel padding="flush">
        <PanelHeader
          title="By team"
          note={
            capacity.otherMeasures === 0
              ? 'ramped capacity against the number, by reporting line'
              : `by reporting line · ${capacity.otherMeasures} quota row(s) in another measure, not added`
          }
        />
        <DataTable
          caption="Capacity by team"
          rows={capacity.teams}
          rowKey={(row) => row.team}
          columns={[
            {
              id: 'team',
              header: 'Team',
              cell: (row: TeamCapacity) => (
                <>
                  <span className={styles.link}>{row.team}</span>
                  <div className={styles.sub}>
                    {row.people} people · {row.rampedPeople.toFixed(1)} ramped
                  </div>
                </>
              ),
              sortValue: (row: TeamCapacity) => row.team,
            },
            {
              id: 'capacity',
              header: 'Capacity',
              numeric: true,
              cell: (row: TeamCapacity) => fullMoney(row.capacity),
              sortValue: (row: TeamCapacity) => row.capacity,
            },
            {
              id: 'target',
              header: 'Target',
              numeric: true,
              cell: (row: TeamCapacity) => fullMoney(row.target),
              sortValue: (row: TeamCapacity) => row.target,
            },
            {
              id: 'coverage',
              header: 'Coverage',
              cell: (row: TeamCapacity) => (
                <>
                  <Meter
                    label={`${row.team} capacity against target`}
                    value={row.capacity}
                    target={row.target}
                    tone={row.capacity >= row.target ? 'positive' : 'critical'}
                  />
                  <div className={styles.sub} style={{ marginTop: 3 }}>
                    {row.target === 0 ? '—' : percent(row.capacity / row.target)}
                  </div>
                </>
              ),
            },
            {
              id: 'verdict',
              header: '',
              cell: (row: TeamCapacity) =>
                row.capacity >= row.target ? (
                  <Tag tone="positive">Covered</Tag>
                ) : (
                  <Tag tone="critical" dot>
                    {money(row.target - row.capacity)} short
                  </Tag>
                ),
            },
          ]}
          empty="Nobody carries a revenue number this period, so there is no capacity to compare."
        />
      </Panel>
    </Page>
  )
}
