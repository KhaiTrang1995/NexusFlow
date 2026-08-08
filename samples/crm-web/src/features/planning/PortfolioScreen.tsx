import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  Columns,
  DataTable,
  Meter,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { usePeriodRollUp, usePlanTree } from '@/api/queries/hooks'
import type { AccountCoverage, LeadAttainment, OpportunityReadiness, PlanNode } from '@/api/contracts'
import { fullMoney, money, percent } from '@/lib/format'
import { usePeriod } from '@/features/exec/period'
import { NoPeriods, PeriodPicker } from '@/features/exec/PeriodPicker'
import styles from './planning.module.css'

/**
 * The portfolio — where the number is committed, level by level.
 *
 * THE GAP IS REPORTED IN BOTH DIRECTIONS AND NOTHING CLOSES IT. A level whose children have
 * committed more than its target is over-covered, and one whose children have committed less has
 * a hole. Both are shown, because a build that clamped the negative would report a company that
 * had double-counted a deal as perfectly on plan.
 */
export function PortfolioScreen() {
  const navigate = useNavigate()
  const choice = usePeriod()
  const rollUp = usePeriodRollUp(choice.period)
  const tree = usePlanTree(choice.period)

  return (
    <Page>
      <PageHeader
        eyebrow="Planning"
        title="Portfolio"
        actions={
          <>
            <PeriodPicker choice={choice} />
            <Button onClick={() => void navigate({ to: '/plan/strategy' })}>Strategy</Button>
          </>
        }
      />

      {choice.isUndeclared ? <NoPeriods what="the portfolio" /> : null}

      <AsyncBoundary query={rollUp} skeletonRows={5} hidden={choice.isUndeclared}>
        {(data) => (
          <>
            <StatGrid columns={4}>
              <StatTile label="Target" value={money(data.target)} note={data.currency} />
              <StatTile
                label="Committed"
                value={money(data.committed)}
                delta={percent(data.target === 0 ? null : data.committed / data.target)}
                direction={data.committed >= data.target ? 'up' : 'down'}
                note="from the levels below"
              />
              <StatTile
                label="Gap"
                value={money(data.gap)}
                direction={data.gap > 0 ? 'down' : 'up'}
                delta={data.gap > 0 ? 'uncovered' : 'over-covered'}
                note="target less committed"
              />
              <StatTile
                label="Accounts planned"
                value={data.accounts.length}
                note={`${data.opportunities.length} deal plans · ${data.marketing.length} demand plans`}
              />
            </StatGrid>

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader title="The vision this sits under" note={choice.label} />
              <p className={styles.vision}>{data.vision}</p>
            </Panel>

            <Columns layout="split">
              <Panel padding="flush">
                <PanelHeader
                  title="Account coverage"
                  note="what is planned against what is in the pipeline"
                  actions={
                    <Button size="sm" onClick={() => void navigate({ to: '/plan/accounts' })}>
                      Account plans
                    </Button>
                  }
                />
                <DataTable
                  caption="Account coverage"
                  rows={data.accounts}
                  rowKey={(row) => row.plan}
                  columns={[
                    {
                      id: 'account',
                      header: 'Account',
                      cell: (row: AccountCoverage) => (
                        <>
                          <span className={styles.link}>{row.account}</span>
                          <div className={styles.sub}>{row.plan}</div>
                        </>
                      ),
                      sortValue: (row: AccountCoverage) => row.account,
                    },
                    {
                      id: 'target',
                      header: 'Target',
                      numeric: true,
                      cell: (row: AccountCoverage) => fullMoney(row.target),
                      sortValue: (row: AccountCoverage) => row.target,
                    },
                    {
                      id: 'pipeline',
                      header: 'Open pipeline',
                      numeric: true,
                      cell: (row: AccountCoverage) => (
                        <span className={row.openPipeline >= row.target ? styles.covered : styles.gap}>
                          {fullMoney(row.openPipeline)}
                        </span>
                      ),
                      sortValue: (row: AccountCoverage) => row.openPipeline,
                    },
                  ]}
                  empty="No account plans for this period."
                />
              </Panel>

              <Panel padding="flush">
                <PanelHeader
                  title="Deal readiness"
                  note="answered, and what is overdue"
                  actions={
                    <Button size="sm" onClick={() => void navigate({ to: '/plan/opportunities' })}>
                      Deal plans
                    </Button>
                  }
                />
                <DataTable
                  caption="Opportunity readiness"
                  rows={data.opportunities}
                  rowKey={(row) => row.plan}
                  columns={[
                    {
                      id: 'plan',
                      header: 'Plan',
                      cell: (row: OpportunityReadiness) => (
                        <>
                          <span className={styles.link}>{row.plan}</span>
                          <div className={styles.sub}>{fullMoney(row.target)}</div>
                        </>
                      ),
                    },
                    {
                      id: 'answered',
                      header: 'Answered',
                      numeric: true,
                      cell: (row: OpportunityReadiness) => `${row.answered}/${row.outOf}`,
                      sortValue: (row: OpportunityReadiness) => row.answered / (row.outOf || 1),
                    },
                    {
                      id: 'steps',
                      header: 'Steps',
                      numeric: true,
                      cell: (row: OpportunityReadiness) =>
                        row.overdueSteps > 0 ? (
                          <Tag tone="critical">{row.overdueSteps} overdue</Tag>
                        ) : (
                          <span>{row.steps}</span>
                        ),
                      sortValue: (row: OpportunityReadiness) => row.overdueSteps,
                    },
                  ]}
                  empty="No deal plans for this period."
                />
              </Panel>
            </Columns>

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="Demand"
                note="leads targeted against leads delivered"
                actions={
                  <Button size="sm" onClick={() => void navigate({ to: '/plan/leads' })}>
                    Lead plans
                  </Button>
                }
              />
              <DataTable
                caption="Lead attainment"
                rows={data.marketing}
                rowKey={(row) => row.plan + row.segment + row.channel}
                columns={[
                  {
                    id: 'plan',
                    header: 'Plan',
                    cell: (row: LeadAttainment) => (
                      <>
                        <span className={styles.link}>{row.plan}</span>
                        <div className={styles.sub}>
                          {row.segment} · {row.channel}
                        </div>
                      </>
                    ),
                  },
                  {
                    id: 'target',
                    header: 'Target',
                    numeric: true,
                    cell: (row: LeadAttainment) => row.targetLeads,
                    sortValue: (row: LeadAttainment) => row.targetLeads,
                  },
                  {
                    id: 'actual',
                    header: 'Delivered',
                    numeric: true,
                    cell: (row: LeadAttainment) => (
                      <span className={row.actualLeads >= row.targetLeads ? styles.covered : styles.gap}>
                        {row.actualLeads}
                      </span>
                    ),
                    sortValue: (row: LeadAttainment) => row.actualLeads,
                  },
                  {
                    id: 'meter',
                    header: 'Against target',
                    cell: (row: LeadAttainment) => (
                      <Meter
                        label={`${row.plan} lead attainment`}
                        value={row.actualLeads}
                        target={row.targetLeads}
                        tone={row.actualLeads >= row.targetLeads ? 'positive' : 'accent'}
                      />
                    ),
                  },
                ]}
                empty="No demand plans for this period."
              />
            </Panel>
          </>
        )}
      </AsyncBoundary>

      <AsyncBoundary query={tree} skeletonRows={5} hidden={choice.isUndeclared}>
        {(data) => (
          <Panel padding="flush">
            <PanelHeader
              title="Plan tree"
              note="a gap traced to the level that owns it"
              actions={<span className={styles.sub}>{data.nodes.length} plans</span>}
            />
            <div className={styles.tree}>
              {data.nodes.map((node) => (
                <TreeRow key={node.name} node={node} />
              ))}
              {data.nodes.length === 0 ? (
                <div style={{ padding: 17 }} className={styles.sub}>
                  No plans for this period yet.
                </div>
              ) : null}
            </div>
          </Panel>
        )}
      </AsyncBoundary>
    </Page>
  )
}

function TreeRow({ node }: { node: PlanNode }) {
  const covered = node.gap <= 0

  return (
    <div className={styles.treeRow} style={{ paddingLeft: 17 + node.depth * 22 }}>
      <div className={styles.treeName}>
        <div>{node.label}</div>
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
