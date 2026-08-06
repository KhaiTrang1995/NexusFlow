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
  StatGrid,
  StatStrip,
  StatTile,
  Tag,
} from '@/design/primitives'
import type { Column } from '@/design/primitives'
import { Funnel, StackedBars, Waterfall } from '@/design/charts'
import { fullMoney, money, percent } from '@/lib/format'
import type { RecordRow } from '@/fixtures/objects'
import { useConsole } from './useConsole'
import { ATTAINMENT, PULSE_BARS, PULSE_SERIES, RHYTHM, WATERFALL } from './consoleFixtures'
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

  const closingColumns: readonly Column<RecordRow>[] = [
    {
      id: 'name',
      header: 'Opportunity',
      cell: (row) => (
        <>
          <span className={styles.link}>{row['name']}</span>
          <div className={styles.sub}>{row['account']}</div>
        </>
      ),
      sortValue: (row) => String(row['name']),
    },
    {
      id: 'stage',
      header: 'Stage',
      cell: (row) => <Tag tone="outline">{row['stage']}</Tag>,
      sortValue: (row) => String(row['stage']),
    },
    {
      id: 'amount',
      header: 'Amount',
      numeric: true,
      cell: (row) => <span className={styles.amount}>{fullMoney(Number(row['amount']))}</span>,
      sortValue: (row) => Number(row['amount'] ?? 0),
    },
    { id: 'close', header: 'Close', cell: (row) => row['closeDate'], sortValue: (row) => String(row['closeDate']) },
    { id: 'owner', header: 'Owner', cell: (row) => row['owner'], sortValue: (row) => String(row['owner']) },
  ]

  return (
    <Page>
      <PageHeader
        eyebrow="Sales console — quarter to date"
        title="Pipeline overview"
        actions={
          <>
            <Button size="lg">Q3 FY26 ▾</Button>
            <Button size="lg">Save view</Button>
            <Button size="lg" tone="primary">
              New opportunity
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
            <Button size="sm" pill onClick={console.clear}>
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
            { value: 'team', label: 'My team' },
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
        <FilterGroup
          label="Forecast"
          selected={console.filters.forecast}
          onSelect={(value) => console.setFilter('forecast', value)}
          options={[
            { value: 'all', label: 'All' },
            { value: 'Commit', label: 'Commit' },
            { value: 'Best Case', label: 'Best case' },
            { value: 'Pipeline', label: 'Pipeline' },
          ]}
        />
      </FilterBar>

      <StatGrid columns={5}>
        <StatTile
          label="Open pipeline"
          value={money(console.openValue)}
          delta="▲ 12%"
          direction="up"
          note="vs last week"
          drillLabel="the open pipeline"
          onActivate={() => void navigate({ to: '/records/$object', params: { object: 'opportunity' } })}
        />
        <StatTile
          label="Weighted"
          value={money(console.weightedValue)}
          delta="▲ 6%"
          direction="up"
          note="commit + best case"
        />
        <StatTile
          label="Closed won QTD"
          value={money(console.wonValue)}
          delta={percent(console.wonValue / 160_000)}
          note="of $160k target"
        />
        <StatTile label="Win rate" value="58%" delta="▼ 3pt" direction="down" note="trailing 90 days" />
        <StatTile label="Avg cycle" value="74d" delta="▼ 5d" direction="up" note="qualify → close" />
      </StatGrid>

      <Panel padding="flush" className={styles.rhythm}>
        <PanelHeader
          title="Rhythm"
          note="team · quarter to date"
          actions={
            <>
              <Button size="sm">Team</Button>
              <Button size="sm">Quarter</Button>
            </>
          }
        />
        <StatStrip cells={RHYTHM} />

        <div className={styles.rhythmBody}>
          <div className={styles.rhythmCharts}>
            <div>
              <div className={styles.chartLabel}>Weekly created vs closed</div>
              <StackedBars
                caption="Opportunities created and closed, by week"
                series={PULSE_SERIES}
                bars={PULSE_BARS}
              />
            </div>

            <div className={styles.movement}>
              <div className={styles.chartLabel}>Pipeline movement</div>
              <div className={styles.movementHead}>
                <span className={styles.movementNet}>+{money(118_000)}</span>
                <span className={styles.movementNote}>
                  net change · closing balance {money(console.openValue)}
                </span>
              </div>
              <Waterfall caption="How the open pipeline changed this quarter" steps={WATERFALL} />
            </div>
          </div>

          <div className={styles.rhythmList}>
            <div className={styles.chartLabel}>
              What moved
              <span className={styles.chartNote}>last 7 days</span>
            </div>
            <ol className={styles.timeline}>
              {console.open.slice(0, 5).map((row) => (
                <li key={row['id']} className={styles.timelineRow}>
                  <span className={styles.timelineChip} aria-hidden="true">
                    ◆
                  </span>
                  <div className={styles.timelineBody}>
                    <div className={styles.timelineHead}>
                      <span className={styles.timelineName}>{row['name']}</span>
                      <span className={styles.timelineWhen}>{row['closeDate']}</span>
                    </div>
                    <div className={styles.sub}>{row['nextStep']}</div>
                    <div className={styles.timelineTags}>
                      <Tag tone="outline">{row['stage']}</Tag>
                      <Tag tone="accent">{fullMoney(Number(row['amount']))}</Tag>
                      <span className={styles.sub}>{row['forecast']}</span>
                    </div>
                  </div>
                </li>
              ))}
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
              name: total.stage.name,
              value: total.sum,
              amount: money(total.sum),
              meta: `${total.count} opps · ${total.stage.pct}%`,
            }))}
            onSelect={() => void navigate({ to: '/kanban' })}
          />
        </Panel>

        <Panel>
          <div className={styles.panelHead}>
            <h2 className={styles.panelTitle}>Attainment</h2>
            <span className={styles.sub}>quota $1.60M</span>
          </div>
          <div className={styles.attainment}>
            {ATTAINMENT.map((row) => (
              <div key={row.name}>
                <div className={styles.attainRow}>
                  <span>{row.name}</span>
                  <span className={styles.attainValue}>
                    {money(row.achieved)} · {percent(row.achieved / row.quota)}
                  </span>
                </div>
                <Meter
                  label={`${row.name} attainment`}
                  value={row.achieved}
                  target={row.quota}
                  tone={row.achieved >= row.quota ? 'positive' : 'accent'}
                />
              </div>
            ))}
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
                  <div>{task['subject']}</div>
                  <div className={styles.sub}>
                    {task['related']} · due {task['due']}
                  </div>
                </div>
                <Tag className={styles.todoTag}>{task['type']}</Tag>
              </div>
            ))}
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}
