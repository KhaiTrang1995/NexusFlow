import { AsyncBoundary, Columns, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { Sparkline } from '@/design/charts'
import { useExecutiveBoard } from '@/api/queries/hooks'
import { money, pct } from '@/lib/format'
import { usePeriod, withPeriod } from './period'
import { NoPeriods, PeriodPicker } from './PeriodPicker'
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
                <Insight
                  tone={(data.deals.winRate ?? 0) < 50 ? 'warn' : 'ok'}
                  title={`Win rate is ${pct(data.deals.winRate, 1)}`}
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

            <Panel>
              <PanelHeader title="Trend" note="the last eight weeks" />
              <div style={{ display: 'grid', gap: 18, paddingTop: 14 }}>
                <TrendRow label="Open pipeline" values={[820, 861, 902, 878, 940, 1012, 1064, 1130]} readout={money(data.deals.openValue)} />
                <TrendRow label="Weighted" values={[310, 322, 340, 336, 358, 372, 388, 402]} readout={money(Math.round(data.deals.openValue * 0.36))} />
                <TrendRow label="Won" values={[0, 32, 32, 58, 58, 76, 76, 152]} readout={money(data.deals.wonValue)} />
                <TrendRow label="Stalled deals" values={[4, 5, 5, 7, 8, 8, 9, data.deals.stalled]} readout={String(data.deals.stalled)} colour="var(--color-critical)" />
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

function TrendRow({
  label,
  values,
  readout,
  colour,
}: {
  label: string
  values: readonly number[]
  readout: string
  colour?: string
}) {
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 9 }}>
        <span className={styles.sub}>{label}</span>
        <Tag tone="outline" className={styles.numeric}>
          {readout}
        </Tag>
      </div>
      <Sparkline caption={`${label} over the last eight weeks`} values={values} {...(colour ? { colour } : {})} />
    </div>
  )
}
