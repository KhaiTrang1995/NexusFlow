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
import { useEntityPage, useExecutiveBoard, useProcess } from '@/api/queries/hooks'
import { money, pct, percent } from '@/lib/format'
import { byStage, openDeals } from './pipeline'
import type { StageBand } from './pipeline'
import { NoPeriods, PeriodPicker, usePeriod } from '@/period'
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

  // The two reads the funnel is made of: the tenant's open deals, and the stages the
  // administrator published to put them in order.
  const deals = useEntityPage('Opportunity')
  const process = useProcess('Opportunity')

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
                  {/*
                    THREE INVENTED BANDS HOLDING 42, 34 AND 24 PER CENT OF ONE TOTAL. Early, Mid
                    and Late were written in this file and split the open value by fractions that
                    were the same on every tenant in every period — a funnel that could not
                    disagree with the pipeline it claimed to draw. The stages below are the
                    published process's, in its order, and each amount is the sum of the deals
                    actually standing in it.
                  */}
                  <AsyncBoundary query={process} skeletonRows={3}>
                    {(published) => (
                      <AsyncBoundary query={deals} skeletonRows={3}>
                        {(page) => {
                          // An empty stage on the way through is the shape of the pipeline and
                          // is drawn. An empty stage the process calls an end is not on the way
                          // anywhere — it is only worth a column when something is standing in
                          // it, which for an undecided deal is worth seeing.
                          const bands = byStage(openDeals(page.records), published.stages).filter(
                            (band) => band.count > 0 || !band.isTerminal,
                          )

                          return bands.every((band) => band.count === 0) ? (
                            <p className={styles.sub} style={{ paddingTop: 12 }}>
                              No deal in this tenant is still open, so there is no pipeline to
                              place in a stage.
                            </p>
                          ) : (
                            <div style={{ paddingTop: 12 }}>
                              <Funnel
                                caption="Open pipeline by stage"
                                stages={bands.map((band) => ({
                                  name: band.name,
                                  value: band.amount,
                                  amount: money(band.amount),
                                  meta: `${band.count} deal(s)${meta(band)}`,
                                }))}
                              />
                            </div>
                          )
                        }}
                      </AsyncBoundary>
                    )}
                  </AsyncBoundary>
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

/**
 * What is odd about a band, if anything.
 *
 * A DEAL WITH NO OUTCOME IN A TERMINAL STAGE IS STILL OPEN PIPELINE, and it belongs in this total
 * — the funnel and the board's open value have to agree or one of them is wrong. But it is also
 * the anomaly a pipeline review looks for, so the column says which it is instead of leaving a
 * reader to wonder why Closed Lost is in a funnel.
 */
function meta(band: StageBand): string {
  if (!band.isDeclared) {
    return ' · the process no longer declares this stage'
  }

  return band.isTerminal ? ' · undecided, in a stage the process calls an end' : ''
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
