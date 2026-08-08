import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  Columns,
  DataTable,
  EmptyState,
  Meter,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { useExecutiveBoard } from '@/api/queries/hooks'
import type { KpiResult, PlanNode, SellerPerformance } from '@/api/contracts'
import { fullMoney, money, pct, percent } from '@/lib/format'
import { kpiDirection, kpiValue } from './kpiUnits'
import { usePeriod } from './period'
import { NoPeriods, PeriodPicker } from './PeriodPicker'
import styles from './exec.module.css'

/**
 * The board pack — everything in one request.
 *
 * ONE CALL AND NOT SIX. The backend serves the roll-up, the plan tree, the sales and deal
 * performance and the scorecard together, precisely so this screen cannot show a roll-up from one
 * instant beside a scorecard from another. Fetching them separately here would throw that away
 * and reintroduce the exact disagreement the endpoint exists to prevent.
 */
export function BoardScreen() {
  const navigate = useNavigate()
  const choice = usePeriod()
  const board = useExecutiveBoard(choice.period)

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Board pack"
        actions={
          <>
            <PeriodPicker choice={choice} />
            <Button onClick={() => void navigate({ to: '/exec/kpis' })}>Scorecard</Button>
          </>
        }
      />

      {choice.isUndeclared ? <NoPeriods what="the board pack" /> : null}

      <AsyncBoundary query={board} skeletonRows={8} hidden={choice.isUndeclared}>
        {(data) => (
          <>
            <StatGrid columns={4}>
              <StatTile
                label="Target"
                value={money(data.rollUp.target)}
                note={`${data.rollUp.currency} · ${data.period}`}
              />
              <StatTile
                label="Committed"
                value={money(data.rollUp.committed)}
                delta={percent(
                  data.rollUp.target === 0 ? null : data.rollUp.committed / data.rollUp.target,
                )}
                direction={data.rollUp.committed >= data.rollUp.target ? 'up' : 'down'}
                note="of target"
              />
              <StatTile
                label="Gap"
                value={money(data.rollUp.gap)}
                direction={data.rollUp.gap > 0 ? 'down' : 'up'}
                delta={data.rollUp.gap > 0 ? 'uncovered' : 'covered'}
                note="target less committed"
              />
              <StatTile
                label="Won"
                value={money(data.deals.wonValue)}
                delta={`${data.deals.won} deals`}
                note={`win rate ${pct(data.deals.winRate, 1)}`}
              />
            </StatGrid>

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="The vision this period is under"
                note={data.viewedAs.role}
                actions={<Tag tone="accent">{data.viewedAs.userId}</Tag>}
              />
              <div style={{ padding: '14px 17px 16px', fontSize: 16 }}>{data.rollUp.vision}</div>
            </Panel>

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="Plan tree"
                note="the same gap subtraction, asked at every level"
                actions={
                  <span className={styles.sub}>{data.tree.nodes.length} plans</span>
                }
              />
              {/*
                A HEADING OVER NOTHING IS NOT A TREE. With no plans the panel drew its title, the
                words "0 plans" and an empty box — which reads as a panel that failed rather than
                as a period nobody has planned against yet. The target above it is real and
                uncommitted, and that is the sentence worth saying.
              */}
              {data.tree.nodes.length === 0 ? (
                <EmptyState
                  title="No plans have been made for this period"
                  detail={`Nothing is committed against the ${money(data.rollUp.target)} target, because no level below has a plan yet. A plan made on the portfolio screen appears here.`}
                />
              ) : (
                <div className={styles.tree}>
                  {data.tree.nodes.map((node) => (
                    <PlanRow key={node.name} node={node} />
                  ))}
                </div>
              )}
            </Panel>

            <Columns layout="split">
              <Panel padding="flush">
                <PanelHeader
                  title="Sales performance"
                  note="attainment; null, not nought, for no number"
                  actions={
                    <Button size="sm" onClick={() => void navigate({ to: '/exec/sales-performance' })}>
                      Open
                    </Button>
                  }
                />
                <DataTable
                  caption="Sellers this period"
                  rows={data.sales.sellers}
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
                    {
                      id: 'committed',
                      header: 'Committed',
                      numeric: true,
                      cell: (row: SellerPerformance) => fullMoney(row.committed),
                      sortValue: (row: SellerPerformance) => row.committed,
                    },
                    {
                      id: 'won',
                      header: 'Won',
                      numeric: true,
                      cell: (row: SellerPerformance) => fullMoney(row.won),
                      sortValue: (row: SellerPerformance) => row.won,
                    },
                    {
                      id: 'attainment',
                      header: 'Attainment',
                      numeric: true,
                      cell: (row: SellerPerformance) =>
                        row.attainment === null ? (
                          <span className={styles.sub}>no number</span>
                        ) : (
                          <span className={row.attainment >= 100 ? styles.positive : undefined}>
                            {pct(row.attainment, 1)}
                          </span>
                        ),
                      sortValue: (row: SellerPerformance) => row.attainment ?? -1,
                    },
                  ]}
                  empty="Nobody carries a number this period."
                />
              </Panel>

              <Panel padding="flush">
                <PanelHeader title="Scorecard" note="off-track first" />
                <div>
                  {data.scorecard.kpis.map((kpi) => (
                    <KpiRow key={kpi.name} kpi={kpi} />
                  ))}
                  {data.scorecard.kpis.length === 0 ? (
                    <div style={{ padding: 17 }} className={styles.sub}>
                      No KPIs are declared for this period.
                    </div>
                  ) : null}
                </div>
              </Panel>
            </Columns>

            <Columns layout="thirds">
              <StatTile label="Open deals" value={data.deals.open} note={money(data.deals.openValue)} />
              <StatTile
                label="Average won"
                value={money(data.deals.averageWonValue)}
                note="of the deals that closed"
              />
              <StatTile
                label="Stalled"
                value={data.deals.stalled}
                direction={data.deals.stalled > 0 ? 'down' : 'flat'}
                delta={data.deals.stalled > 0 ? 'no stage change in 60 days' : 'none'}
                note="a deal nobody has moved is not a deal going slowly"
              />
            </Columns>
          </>
        )}
      </AsyncBoundary>
    </Page>
  )
}

function PlanRow({ node }: { node: PlanNode }) {
  const covered = node.gap <= 0

  return (
    <div className={styles.treeRow} style={{ paddingLeft: 17 + node.depth * 22 }}>
      <div className={styles.treeName}>
        <div className={styles.treeLabel}>{node.label}</div>
        <div className={styles.sub}>
          {node.kind} · {node.owner}
          {node.children > 0 ? ` · ${node.children} below` : ''}
        </div>
      </div>
      <div className={styles.treeMeter}>
        <Meter
          label={`${node.label} committed against target`}
          value={node.committed}
          target={node.target}
          tone={covered ? 'positive' : 'accent'}
        />
      </div>
      <div className={styles.treeNumbers}>
        <span>{fullMoney(node.target)}</span>
        <span>{fullMoney(node.committed)}</span>
        <span className={covered ? styles.covered : styles.gap}>
          {covered ? `+${fullMoney(-node.gap)}` : fullMoney(node.gap)}
        </span>
      </div>
    </div>
  )
}

/**
 * One KPI, in the unit its source produces.
 *
 * IT WAS DRAWN AS A PERCENTAGE, AND NONE OF THE FIVE SOURCES IS ONE. Open pipeline of 1,626,000
 * rendered here as "1626000%" beside a target of "2000000%" — the same defect the scorecard screen
 * fixed with {@link kpiValue}, left behind on the one page a board reads. The direction was
 * compared against `'Up'`, which `KpiDirection` has never been, so all five said "lower is better".
 */
function KpiRow({ kpi }: { kpi: KpiResult }) {
  const good = kpi.status === 'OnTrack'
  const way = kpiDirection(kpi.direction)

  return (
    <div className={styles.treeRow}>
      <div className={styles.treeName}>
        <div className={styles.treeLabel}>{kpi.label}</div>
        <div className={styles.sub}>
          {kpi.source}
          {way === null ? '' : ` · ${way}`}
        </div>
      </div>
      <Tag tone={good ? 'positive' : 'critical'} dot>
        {good ? 'On track' : 'Off track'}
      </Tag>
      <div className={styles.treeNumbers} style={{ width: 190 }}>
        <span>{kpiValue(kpi.source, Number(kpi.actual))}</span>
        <span className={styles.sub}>target {kpiValue(kpi.source, Number(kpi.target))}</span>
      </div>
    </div>
  )
}
