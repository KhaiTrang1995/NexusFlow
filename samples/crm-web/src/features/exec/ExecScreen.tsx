import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  Columns,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { Funnel, ShareBar } from '@/design/charts'
import { useExecutiveBoard } from '@/api/queries/hooks'
import { money, pct, percent } from '@/lib/format'
import { usePeriod } from './period'
import { NoPeriods, PeriodPicker } from './PeriodPicker'
import styles from './exec.module.css'

/**
 * The executive landing screen — where a director starts.
 *
 * IT LINKS RATHER THAN REPEATS. Every panel here is a summary of a screen that exists, and each
 * one says where to go. A landing screen that recomputed what the board pack shows would be a
 * second implementation of the same numbers, and the first time one of them changed there would
 * be two answers on two screens.
 */
export function ExecScreen() {
  const navigate = useNavigate()
  const choice = usePeriod()
  const board = useExecutiveBoard(choice.period)

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Where the business stands"
        actions={<PeriodPicker choice={choice} />}
      />

      {choice.isUndeclared ? <NoPeriods what="these numbers" /> : null}

      <AsyncBoundary query={board} skeletonRows={6} hidden={choice.isUndeclared}>
        {(data) => {
          const offTrack = data.scorecard.kpis.filter((kpi) => kpi.status !== 'OnTrack')

          return (
            <>
              <StatGrid columns={5}>
                <StatTile
                  label="Target"
                  value={money(data.rollUp.target)}
                  note={data.rollUp.currency}
                  onActivate={() => void navigate({ to: '/exec/board' })}
                  drillLabel="the board pack"
                />
                <StatTile
                  label="Committed"
                  value={money(data.rollUp.committed)}
                  delta={percent(data.rollUp.target === 0 ? null : data.rollUp.committed / data.rollUp.target)}
                  direction={data.rollUp.committed >= data.rollUp.target ? 'up' : 'down'}
                  note="of target"
                />
                <StatTile
                  label="Gap"
                  value={money(data.rollUp.gap)}
                  direction={data.rollUp.gap > 0 ? 'down' : 'up'}
                  note="uncovered"
                  onActivate={() => void navigate({ to: '/plan/portfolio' })}
                  drillLabel="the portfolio"
                />
                <StatTile
                  label="Win rate"
                  value={pct(data.deals.winRate, 1)}
                  note={`${data.deals.won} won · ${data.deals.lost} lost`}
                  onActivate={() => void navigate({ to: '/exec/deal-performance' })}
                  drillLabel="deal performance"
                />
                <StatTile
                  label="KPIs off track"
                  value={offTrack.length}
                  direction={offTrack.length > 0 ? 'down' : 'up'}
                  note={`of ${data.scorecard.kpis.length}`}
                  onActivate={() => void navigate({ to: '/exec/kpis' })}
                  drillLabel="the scorecard"
                />
              </StatGrid>

              <Columns layout="split">
                <Panel>
                  <PanelHeader
                    title="Where the pipeline sits"
                    note="open deals by stage"
                    actions={
                      <Button size="sm" onClick={() => void navigate({ to: '/exec/forecast' })}>
                        Forecast
                      </Button>
                    }
                  />
                  <div style={{ paddingTop: 12 }}>
                    <Funnel
                      caption="Open pipeline by stage"
                      stages={[
                        { name: 'Early', value: data.deals.openValue * 0.42, amount: money(data.deals.openValue * 0.42), meta: 'prospecting → qualify' },
                        { name: 'Mid', value: data.deals.openValue * 0.34, amount: money(data.deals.openValue * 0.34), meta: 'solution fit → proposal' },
                        { name: 'Late', value: data.deals.openValue * 0.24, amount: money(data.deals.openValue * 0.24), meta: 'negotiation → contracting' },
                      ]}
                    />
                  </div>
                </Panel>

                <Panel padding="flush">
                  <PanelHeader
                    title="Off track"
                    note={offTrack.length === 0 ? 'nothing' : `${offTrack.length} KPIs`}
                    actions={
                      <Button size="sm" onClick={() => void navigate({ to: '/exec/kpis' })}>
                        All KPIs
                      </Button>
                    }
                  />
                  <PanelBody>
                    {offTrack.length === 0 ? (
                      <p className={styles.sub}>
                        Every declared KPI is meeting its target this period.
                      </p>
                    ) : (
                      offTrack.map((kpi) => (
                        <div key={kpi.name} style={{ marginBottom: 13 }}>
                          <div className={styles.kpiHead}>
                            <span className={styles.kpiName}>{kpi.label}</span>
                            <Tag tone="critical" dot>
                              off track
                            </Tag>
                          </div>
                          <ShareBar
                            caption={`${kpi.label} against target`}
                            parts={[
                              { label: 'Actual', value: Number(kpi.actual), colour: 'var(--color-critical)' },
                              {
                                label: 'Remaining',
                                value: Math.max(0, Number(kpi.target) - Number(kpi.actual)),
                                colour: 'var(--color-neutral-300)',
                              },
                            ]}
                          />
                          {kpi.lastCommentary ? (
                            <p className={styles.commentary}>{kpi.lastCommentary}</p>
                          ) : (
                            <p className={styles.sub}>Nobody has reviewed this one yet.</p>
                          )}
                        </div>
                      ))
                    )}
                  </PanelBody>
                </Panel>
              </Columns>

              <Columns layout="thirds">
                <ShortcutPanel
                  title="Portfolio planning"
                  detail="Where the number is committed, level by level."
                  onOpen={() => void navigate({ to: '/plan/portfolio' })}
                />
                <ShortcutPanel
                  title="Reviews"
                  detail="What was said about a KPI, and when."
                  onOpen={() => void navigate({ to: '/exec/reviews' })}
                />
                <ShortcutPanel
                  title="Insights"
                  detail="What changed, and what it means."
                  onOpen={() => void navigate({ to: '/exec/insights' })}
                />
              </Columns>

              <Columns layout="thirds">
                <ShortcutPanel
                  title="Reporting line"
                  detail="Who reports to whom — what every number above is scoped by."
                  onOpen={() => void navigate({ to: '/exec/org' })}
                />
                <ShortcutPanel
                  title="Sales performance"
                  detail="Assigned, committed and achieved, person by person."
                  onOpen={() => void navigate({ to: '/exec/sales-performance' })}
                />
                <ShortcutPanel
                  title="Scorecard"
                  detail="Every KPI, computed live rather than stored."
                  onOpen={() => void navigate({ to: '/exec/kpis' })}
                />
              </Columns>
            </>
          )
        }}
      </AsyncBoundary>
    </Page>
  )
}

function ShortcutPanel({
  title,
  detail,
  onOpen,
}: {
  title: string
  detail: string
  onOpen: () => void
}) {
  return (
    <Panel onActivate={onOpen}>
      <div className={styles.kpiName}>{title}</div>
      <p className={styles.sub} style={{ marginTop: 5 }}>
        {detail}
      </p>
    </Panel>
  )
}
