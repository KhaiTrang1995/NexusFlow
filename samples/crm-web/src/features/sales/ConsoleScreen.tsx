import { Link, useNavigate } from '@tanstack/react-router'
import {
  Button,
  Columns,
  DataTable,
  FilterBar,
  FilterGroup,
  Meter,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  Skeleton,
  StatGrid,
  StatStrip,
  StatTile,
  Tag,
} from '@/design/primitives'
import type { Column } from '@/design/primitives'
import { Funnel, StackedBars } from '@/design/charts'
import { date, dateTime, fullMoney, money, percent } from '@/lib/format'
import { useQuotaAttainment } from '@/api/queries/hooks'
import { usePeriod } from '@/features/exec/period'
import { useConsole } from './useConsole'
import type { Deal, Task } from './useConsole'

import styles from './ConsoleScreen.module.css'

/**
 * The sales console — the screen the design opens on.
 *
 * FIVE TILES, ONE FUNNEL, ONE TABLE, AND THEY ALL AGREE. Every number here comes from
 * `useConsole`, over one filtered set of opportunities. A tile computing its own total would be a
 * tile that quietly disagrees with the funnel beneath it the first time a filter is added.
 */
export function ConsoleScreen() {
  const navigate = useNavigate()
  const console = useConsole()

  /** Where every deal figure on this screen drills to. One function, so they cannot diverge. */
  const deals = () =>
    void navigate({ to: '/records/$object', params: { object: 'opportunity' } })

  // The reporting line scopes this on the server: a manager sees their people, a seller sees
  // themselves. The period is the one every executive screen shares.
  const choice = usePeriod()
  const attainment = useQuotaAttainment(choice.period)

  const closingColumns: readonly Column<Deal>[] = [
    {
      id: 'name',
      header: 'Opportunity',
      cell: (deal) => <span className={styles.link}>{deal.name}</span>,
      sortValue: (deal) => deal.name,
    },
    {
      id: 'stage',
      header: 'Stage',
      cell: (deal) => <Tag tone="outline">{deal.stage}</Tag>,
      sortValue: (deal) => deal.stage,
    },
    {
      id: 'amount',
      header: 'Amount',
      numeric: true,
      cell: (deal) => <span className={styles.amount}>{fullMoney(deal.amount)}</span>,
      sortValue: (deal) => deal.amount,
    },
    {
      id: 'close',
      header: 'Close',
      cell: (deal) => date(deal.closeDate),
      sortValue: (deal) => deal.closeDate ?? '',
    },
    {
      // Not an owner column. An opportunity carries an owner uuid and this sample has no user
      // directory to resolve it against; a column headed "Owner" showing 33333333-… is worse
      // than no column, because the reader has to work out it is an id before ignoring it.
      id: 'probability',
      header: 'Likely',
      numeric: true,
      cell: (deal) => `${deal.probability}%`,
      sortValue: (deal) => deal.probability,
    },
  ]

  return (
    <Page>
      <PageHeader
        eyebrow="Sales console — quarter to date"
        title="Pipeline overview"
        actions={
          <>
            {/*
              Three buttons with no handler at all sat here: a period picker, Save view and New
              opportunity. This build has no create-opportunity capability — an opportunity comes
              from converting a lead — and no saved view over a built-in entity, so two of them
              had nothing to call and the third's period is fixed for every executive screen.
            */}
            {/*
              A verb beside two destinations, and it is the only one of the three that does not
              name where it goes. Kept as the verb because it is what a seller starts the day
              wanting; the title says what pressing it actually does, which is the convention
              every indirect control in this client follows.
            */}
            <Button
              size="lg"
              title="Opens the lead list — a conversion starts from a lead."
              onClick={() => void navigate({ to: '/records/$object', params: { object: 'lead' } })}
            >
              Convert a lead
            </Button>
            <Button size="lg" onClick={() => void navigate({ to: '/kanban' })}>
              Kanban
            </Button>
            <Button size="lg" tone="primary" onClick={() => void navigate({ to: '/exec/board' })}>
              Executive board
            </Button>
          </>
        }
      />

      <FilterBar
        end={
          <>
            <span>
              {console.activeCount === 0
                ? 'Showing the default view'
                : `${console.activeCount} filter${console.activeCount === 1 ? '' : 's'} applied · ${console.open.length} open`}
            </span>
            {/*
              Disabled when there is nothing to clear, rather than live and inert. Pressing it on
              the default view did nothing at all, which reads as a broken button — and it is the
              button somebody presses first when a list looks wrong.
            */}
            <Button
              size="sm"
              pill
              disabled={console.activeCount === 0}
              title={console.activeCount === 0 ? 'No filters are applied.' : undefined}
              onClick={console.clear}
            >
              Clear
            </Button>
          </>
        }
      >
        <FilterGroup
          label="Owner"
          selected={console.filters.owner}
          onSelect={(value) => console.setFilter('owner', value)}
          options={[
            { value: 'mine', label: 'Mine' },
            { value: 'all', label: 'Everyone' },
          ]}
        />
        <FilterGroup
          label="Close"
          selected={console.filters.horizon}
          onSelect={(value) => console.setFilter('horizon', value)}
          options={[
            { value: 'quarter', label: 'This quarter' },
            { value: 'month', label: 'This month' },
            { value: 'open', label: 'All open' },
          ]}
        />
        {/*
          Outcome, not forecast. A forecast category is not a column on `opportunity`; the four
          this offered were a fixture's, and every one of them filtered to nothing against live
          rows. What a deal carries is an outcome, which is null while it is open.
        */}
        <FilterGroup
          label="Outcome"
          selected={console.filters.outcome}
          onSelect={(value) => console.setFilter('outcome', value)}
          options={[
            { value: 'open', label: 'Open' },
            { value: 'Won', label: 'Won' },
            { value: 'Lost', label: 'Lost' },
            { value: 'all', label: 'All' },
          ]}
        />
      </FilterBar>

      {/*
        A CHEVRON MEANS THE TILE OPENS SOMETHING, AND FOUR OF THESE FIVE HAD NONE. One tile was a
        button and the rest were not, which reads as four broken tiles rather than as one that
        happens to link: a reader who finds a number clickable tries the number beside it. Each
        one now opens the list its own figure is a count of — the two money tiles and the deal
        count are all views of the same filtered opportunities, and tasks are their own list.
      */}
      <StatGrid columns={5}>
        <StatTile
          label="Open pipeline"
          value={money(console.openValue)}
          note="open deals in this filter"
          drillLabel="the open pipeline"
          onActivate={deals}
        />
        <StatTile
          label="Weighted"
          value={money(console.weightedValue)}
          note="amount × probability, deal by deal"
          drillLabel="the deals behind it"
          onActivate={deals}
        />
        <StatTile
          label="Closed won QTD"
          value={money(console.wonValue)}
          note="won, in this filter"
          drillLabel="the deals behind it"
          onActivate={deals}
        />
        {/*
          Two tiles said 58% and 74 days on every tenant, with a trend arrow. Win rate and cycle
          time are real questions with a real surface — `/performance/deals` answers both — and
          the executive board is where they are asked. What belongs here is what this screen's
          own rows can answer.
        */}
        <StatTile
          label="Open deals"
          value={console.open.length}
          note="in this filter"
          drillLabel="the deals behind it"
          onActivate={deals}
        />
        <StatTile
          label="Tasks open"
          value={console.tasks.length}
          delta={console.overdueTasks > 0 ? `${console.overdueTasks} overdue` : undefined}
          direction={console.overdueTasks > 0 ? 'down' : 'flat'}
          note="assigned in this tenant"
          drillLabel="the task list"
          onActivate={() => void navigate({ to: '/records/$object', params: { object: 'task' } })}
        />
      </StatGrid>

      <Panel padding="flush" className={styles.rhythm}>
        <PanelHeader
          title="Rhythm"
          note="what this tenant's rows say"
          actions={
            <Button size="sm" onClick={() => void navigate({ to: '/exec/deal-performance' })}>
              Deal performance
            </Button>
          }
        />

        {/*
          SIX INVENTED CELLS, A WEEKLY BAR CHART AND A WATERFALL USED TO SIT HERE. "Meetings held
          38, ▲ 6", "Stage moves 27, ▼ 4 vs last week", an opening pipeline balance of $1.01M —
          all of them the same on every tenant, all of them trends. Every one needs history the
          server does not keep: there is no weekly snapshot of a pipeline anywhere in this schema,
          so a bar per week and a delta against last week are not figures anybody can source.

          What is here instead is what the rows on this screen can answer without a second read.
          Win rate and cycle time — the two the trend arrows were really claiming — have a real
          surface, and the button above goes to it.
        */}
        <StatStrip
          cells={[
            {
              label: 'Open',
              value: String(console.open.length),
              fraction: fraction(console.open.length, console.open.length + console.won.length),
              note: 'in this filter',
            },
            {
              label: 'Won',
              value: String(console.won.length),
              fraction: fraction(console.won.length, console.open.length + console.won.length),
              note: 'in this filter',
            },
            {
              label: 'Closing this month',
              value: String(console.closingThisMonth.length),
              fraction: fraction(console.closingThisMonth.length, console.open.length),
              note: 'of the open ones',
            },
            {
              label: 'Weighted',
              value: money(console.weightedValue),
              fraction: fraction(console.weightedValue, console.openValue),
              note: 'of the open pipeline',
            },
            {
              label: 'Tasks open',
              value: String(console.tasks.length),
              fraction: fraction(console.tasks.length - console.overdueTasks, console.tasks.length),
              note: `${console.overdueTasks} overdue`,
              direction: console.overdueTasks > 0 ? ('down' as const) : ('flat' as const),
            },
            {
              label: 'Largest open',
              value: money(Math.max(0, ...console.open.map((deal) => deal.amount))),
              fraction: 1,
              note: 'single deal',
            },
          ]}
        />

        <div className={styles.rhythmBody}>
          <div className={styles.rhythmCharts}>
            <div>
              <div className={styles.chartLabel}>Open work by kind</div>
              <StackedBars
                caption="Open activities by kind"
                series={[{ label: 'Open', colour: 'var(--color-accent)' }]}
                bars={byKind(console.tasks)}
              />
            </div>

            <div className={styles.movement}>
              <div className={styles.chartLabel}>Open pipeline by likelihood</div>
              <div className={styles.movementHead}>
                <span className={styles.movementNet}>{money(console.weightedValue)}</span>
                <span className={styles.movementNote}>
                  weighted · of {money(console.openValue)} open
                </span>
              </div>
              <StackedBars
                caption="Open pipeline by probability band"
                series={[{ label: 'Amount', colour: 'var(--color-accent-800)' }]}
                bars={byLikelihood(console.open)}
              />
            </div>
          </div>

          <div className={styles.rhythmList}>
            <div className={styles.chartLabel}>
              Largest open
              <span className={styles.chartNote}>in this filter</span>
            </div>
            <ol className={styles.timeline}>
              {[...console.open]
                .sort((a, b) => b.amount - a.amount)
                .slice(0, 5)
                .map((deal) => (
                  <li key={deal.id} className={styles.timelineRow}>
                    <span className={styles.timelineChip} aria-hidden="true">
                      ◆
                    </span>
                    <div className={styles.timelineBody}>
                      <div className={styles.timelineHead}>
                        <span className={styles.timelineName}>{deal.name}</span>
                        <span className={styles.timelineWhen}>{date(deal.closeDate)}</span>
                      </div>
                      <div className={styles.timelineTags}>
                        <Tag tone="outline">{deal.stage}</Tag>
                        <Tag tone="accent">{fullMoney(deal.amount)}</Tag>
                        <span className={styles.sub}>{deal.probability}% likely</span>
                      </div>
                    </div>
                  </li>
                ))}
              {console.open.length === 0 ? (
                <li className={styles.sub}>Nothing is open in this filter.</li>
              ) : null}
            </ol>
          </div>
        </div>
      </Panel>

      <Columns layout="split">
        <Panel>
          <div className={styles.panelHead}>
            <h2 className={styles.panelTitle}>Pipeline by stage</h2>
            <span className={styles.sub}>open opportunities · weighted</span>
            <Link to="/kanban" className={styles.panelLink}>
              Open kanban →
            </Link>
          </div>
          <Funnel
            caption="Open pipeline by stage"
            stages={console.totals.map((total) => ({
              name: total.stage,
              value: total.sum,
              amount: money(total.sum),
              meta: `${total.count} open`,
            }))}
            onSelect={() => void navigate({ to: '/kanban' })}
          />
        </Panel>

        <Panel>
          <div className={styles.panelHead}>
            <h2 className={styles.panelTitle}>Attainment</h2>
            <span className={styles.sub}>against the assigned number</span>
            <Link to="/exec/sales-performance" className={styles.panelLink}>
              Sales performance →
            </Link>
          </div>

          {/*
            FIVE NAMED SELLERS WITH QUOTAS USED TO BE WRITTEN HERE — A. Ruiz at 488 of 520, and
            four more, identical on every tenant. Quota attainment has had a surface throughout;
            it is scoped by the reporting line, so what a manager sees here is their people and
            what a seller sees is themselves.

            Revenue rows only: a quota can be carried in leads or activities, and drawing forty
            leads on a money meter beside three hundred thousand euros is the same arithmetic
            between different things that this application has now removed twice.
          */}
          <div className={styles.attainment}>
            {attainment.isPending ? <Skeleton rows={4} /> : null}

            {attainment.isSuccess
              ? attainment.data.rows
                  .filter((row) => row.measure === 'Revenue')
                  .map((row) => (
                    <div key={`${row.userId}/${row.measure}`}>
                      <div className={styles.attainRow}>
                        <span>{row.displayName}</span>
                        <span className={styles.attainValue}>
                          {money(row.actual)} · {percent(row.quota === 0 ? null : row.actual / row.quota)}
                        </span>
                      </div>
                      <Meter
                        label={`${row.displayName} attainment`}
                        value={row.actual}
                        target={row.quota}
                        tone={row.actual >= row.quota ? 'positive' : 'accent'}
                      />
                    </div>
                  ))
              : null}

            {choice.isUndeclared ? (
              <p className={styles.sub}>
                No periods have been declared, so nobody carries a number yet. An administrator
                declares them; this panel fills in the moment one exists.
              </p>
            ) : null}

            {attainment.isSuccess
            && attainment.data.rows.filter((row) => row.measure === 'Revenue').length === 0 ? (
              <p className={styles.sub}>
                Nobody carries a revenue number this period. Assign one on sales performance.
              </p>
            ) : null}
          </div>
        </Panel>
      </Columns>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="Closing this month"
            note={`${console.closingThisMonth.length} record${console.closingThisMonth.length === 1 ? '' : 's'}`}
            actions={
              <Link to="/records/$object" params={{ object: 'opportunity' }} className={styles.panelLink}>
                All opportunities →
              </Link>
            }
          />
          <DataTable
            caption="Opportunities closing this month"
            columns={closingColumns}
            rows={console.closingThisMonth}
            rowKey={(row) => row.id}
            onRowClick={(row) =>
              void navigate({
                to: '/records/$object/$id',
                params: { object: 'opportunity', id: row.id },
              })
            }
            empty="Nothing in this filter closes before the end of the month."
          />
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Today" />
          <PanelBody className={styles.todoBody}>
            {console.tasks.map((task) => (
              <div key={task.id} className={styles.todo}>
                <span className={styles.todoBox} aria-hidden="true" />
                <div style={{ minWidth: 0 }}>
                  <div>{task.subject}</div>
                  <div className={styles.sub}>
                    {task.status}
                    {task.dueAt === null ? '' : ` · due ${dateTime(task.dueAt)}`}
                  </div>
                </div>
                <Tag className={styles.todoTag}>{task.kind}</Tag>
              </div>
            ))}
            {console.tasks.length === 0 ? (
              <p className={styles.sub}>Nothing is open against this tenant.</p>
            ) : null}
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}

/**
 * A share, for a strip cell's bar.
 *
 * ZERO OVER ZERO IS ZERO HERE, NOT NaN. An empty tenant renders every cell, and `NaN` in a width
 * makes the bar disappear rather than sit at nothing — which reads as a broken chart instead of
 * an empty one.
 */
function fraction(part: number, whole: number): number {
  return whole <= 0 ? 0 : Math.min(1, part / whole)
}

/** Open activities grouped by what kind of work they are. */
function byKind(tasks: readonly Task[]) {
  const counts = new Map<string, number>()

  for (const task of tasks) {
    counts.set(task.kind, (counts.get(task.kind) ?? 0) + 1)
  }

  return [...counts.entries()].map(([kind, count]) => ({
    label: kind,
    values: [count],
    readout: String(count),
  }))
}

/**
 * Open pipeline in probability bands.
 *
 * BANDS RATHER THAN EVERY VALUE, because a bar per distinct probability is forty bars of one deal
 * each on a real tenant. The four are the ones a review talks in.
 */
function byLikelihood(deals: readonly Deal[]) {
  const bands: readonly { label: string; from: number; to: number }[] = [
    { label: '0–25%', from: 0, to: 25 },
    { label: '26–50%', from: 26, to: 50 },
    { label: '51–75%', from: 51, to: 75 },
    { label: '76–100%', from: 76, to: 100 },
  ]

  return bands.map((band) => {
    const sum = deals
      .filter((deal) => deal.probability >= band.from && deal.probability <= band.to)
      .reduce((total, deal) => total + deal.amount, 0)

    return { label: band.label, values: [sum], readout: money(sum) }
  })
}
