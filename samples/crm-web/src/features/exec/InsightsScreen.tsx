import { AsyncBoundary, Columns, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useExecutiveBoard } from '@/api/queries/hooks'
import { money, pct } from '@/lib/format'
import { NoPeriods, PeriodPicker, usePeriod, withPeriod } from '@/period'
import styles from './exec.module.css'

/**
 * What changed, and what it means.
 *
 * EVERY LINE POINTS AT A NUMBER SOMEBODY CAN CHECK. An insight that cannot be traced back to a
 * figure on another screen is an opinion with a chart next to it, and those are the ones that get
 * quoted in a board meeting and cannot be defended.
 */
export function InsightsScreen() {
  const choice = usePeriod()
  const board = useExecutiveBoard(choice.period)

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Executive', choice)}
        title="Insights"
        actions={<PeriodPicker choice={choice} />}
      />

      {choice.isUndeclared ? <NoPeriods what="these insights" /> : null}

      <AsyncBoundary query={board} skeletonRows={5} hidden={choice.isUndeclared}>
        {(data) => (
          <Columns layout="split">
            <Panel padding="flush">
              <PanelHeader title="What the numbers say" note="each traceable to a screen" />
              <div>
                <Insight
                  tone={data.rollUp.gap > 0 ? 'warn' : 'ok'}
                  title={
                    data.rollUp.gap > 0
                      ? `${money(data.rollUp.gap)} of the target is not committed anywhere`
                      : 'The target is fully committed'
                  }
                  detail="From the plan tree: target less the sum of what the levels below have committed. Trace it on the board pack."
                />
                <Insight
                  tone={data.deals.stalled > 0 ? 'warn' : 'ok'}
                  title={`${data.deals.stalled} open deals have not moved stage in sixty days`}
                  detail="A deal nobody has moved is not a deal going slowly. They are worth more attention than the ones losing on price."
                />
                {/*
                  A NULL WIN RATE IS NOT A BAD ONE. `?? 0` made "nothing has been decided" score
                  below every threshold, so a period with no closed deals was flagged as a problem
                  reading "Win rate is —" — an exclamation mark over an em dash.
                */}
                <Insight
                  tone={data.deals.winRate === null ? 'ok' : data.deals.winRate < 50 ? 'warn' : 'ok'}
                  title={
                    data.deals.winRate === null
                      ? 'No deal has been decided this period, so there is no win rate'
                      : `Win rate is ${pct(data.deals.winRate, 1)}`
                  }
                  detail={`${data.deals.won} won against ${data.deals.lost} lost, ${money(data.deals.wonValue)} of value.`}
                />
                <Insight
                  tone="ok"
                  title={`Average won deal is ${money(data.deals.averageWonValue)}`}
                  detail="Of the deals that actually closed this period. Null rather than nought when nothing closed."
                />
                {data.scorecard.kpis
                  .filter((kpi) => kpi.status !== 'OnTrack')
                  .map((kpi) => (
                    <Insight
                      key={kpi.name}
                      tone="warn"
                      title={`${kpi.label} is off track`}
                      detail={kpi.lastCommentary ?? 'Nobody has reviewed it, so nobody has explained it.'}
                    />
                  ))}
              </div>
            </Panel>

            {/*
              FOUR SPARKLINES OF EIGHT WEEKS THAT NEVER HAPPENED. `[820, 861, 902, 878, …]` was
              written in this file: the same rising line on every tenant and in every period,
              under a heading that said "the last eight weeks". A trend is the one chart a reader
              cannot check against anything else on the page, which is exactly why an invented one
              survives. "Weighted" was worse again — the open value times 0.36, a ratio from
              nowhere, drawn beside three figures the server had actually said.

              THIS BUILD SERVES NO HISTORY. Every executive read is "as of now, for a period";
              nothing anywhere returns a series. So the panel says where the numbers stand and
              says why there is no line, rather than drawing one out of nothing.
            */}
            <Panel>
              <PanelHeader title="Where it stands" note={`as of now · ${data.period}`} />
              <div style={{ display: 'grid', gap: 14, paddingTop: 14 }}>
                <Reading label="Open pipeline" value={money(data.deals.openValue)} note={`${data.deals.open} deal(s)`} />
                <Reading label="Won" value={money(data.deals.wonValue)} note={`${data.deals.won} deal(s)`} />
                <Reading label="Lost" value={money(data.deals.lostValue)} note={`${data.deals.lost} deal(s)`} />
                <Reading
                  label="Stalled"
                  value={String(data.deals.stalled)}
                  note="open, no stage change in sixty days"
                />
                <p className={styles.sub}>
                  No line is drawn because there is nothing to draw one from: every figure this
                  backend serves is a reading taken now for a period, and no endpoint returns a
                  series. A sparkline here would be this screen's own invention.
                </p>
              </div>
            </Panel>
          </Columns>
        )}
      </AsyncBoundary>
    </Page>
  )
}

function Insight({ tone, title, detail }: { tone: 'ok' | 'warn'; title: string; detail: string }) {
  return (
    <div className={styles.insight}>
      <span className={`${styles.insightMark} ${tone === 'warn' ? styles.insightMarkWarn : ''}`} aria-hidden="true">
        {tone === 'warn' ? '!' : '✓'}
      </span>
      <div style={{ minWidth: 0 }}>
        <div className={styles.insightTitle}>{title}</div>
        <p className={styles.sub} style={{ marginTop: 3 }}>
          {detail}
        </p>
      </div>
    </div>
  )
}

function Reading({ label, value, note }: { label: string; value: string; note: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 9 }}>
      <span className={styles.sub}>{label}</span>
      <Tag tone="outline" className={styles.numeric}>
        {value}
      </Tag>
      <span className={styles.sub} style={{ marginLeft: 'auto' }}>
        {note}
      </span>
    </div>
  )
}
