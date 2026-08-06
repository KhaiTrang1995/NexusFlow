import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  Columns,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
} from '@/design/primitives'
import { ShareBar, StackedBars } from '@/design/charts'
import { useDealPerformance } from '@/api/queries/hooks'
import { money, pct } from '@/lib/format'
import { PERIODS, PERIOD_LABEL, usePeriod } from './period'
import styles from './exec.module.css'

/**
 * How the deals are doing.
 *
 * STALLED IS THE NUMBER A REVIEW IS FOR. Win rate and average size are what a board asks about;
 * the count of open deals that have not changed stage in sixty days is what a pipeline review can
 * actually act on. It is given its own tile rather than buried in a table.
 */
export function DealPerformanceScreen() {
  const [period, setPeriod] = usePeriod()
  const deals = useDealPerformance(period)

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Deal performance"
        actions={
          <ButtonGroup label="Period">
            {PERIODS.map((option) => (
              <Button key={option} aria-pressed={period === option} onClick={() => setPeriod(option)}>
                {PERIOD_LABEL[option]}
              </Button>
            ))}
          </ButtonGroup>
        }
      />

      <AsyncBoundary query={deals} skeletonRows={4}>
        {(data) => (
          <>
            <StatGrid columns={5}>
              <StatTile label="Open" value={data.open} note={money(data.openValue)} />
              <StatTile
                label="Won"
                value={data.won}
                note={money(data.wonValue)}
                direction="up"
                delta={pct(data.winRate, 1)}
              />
              <StatTile label="Lost" value={data.lost} note={money(data.lostValue)} direction="down" />
              <StatTile
                label="Average won"
                value={money(data.averageWonValue)}
                note="of what actually closed"
              />
              <StatTile
                label="Stalled"
                value={data.stalled}
                direction={data.stalled > 0 ? 'down' : 'flat'}
                delta={data.stalled > 0 ? 'no move in 60 days' : 'none'}
                note="of the open deals"
              />
            </StatGrid>

            <Columns layout="split">
              <Panel>
                <PanelHeader title="Where the value went" note="this period" />
                <div style={{ paddingTop: 14 }}>
                  <StackedBars
                    caption="Open, won and lost value"
                    series={[
                      { label: 'Open', colour: 'var(--color-accent)' },
                      { label: 'Won', colour: 'var(--color-positive)' },
                      { label: 'Lost', colour: 'var(--color-neutral-500)' },
                    ]}
                    bars={[
                      {
                        label: PERIOD_LABEL[period],
                        values: [data.openValue, data.wonValue, data.lostValue],
                        readout: money(data.openValue + data.wonValue + data.lostValue),
                      },
                    ]}
                    height={168}
                  />
                </div>
              </Panel>

              <Panel>
                <PanelHeader title="Won against lost" note="by count" />
                <div style={{ paddingTop: 18, display: 'grid', gap: 14 }}>
                  <div>
                    <div className={styles.sub} style={{ marginBottom: 6 }}>
                      Outcomes
                    </div>
                    <ShareBar
                      caption="Won against lost"
                      height={16}
                      parts={[
                        { label: 'Won', value: data.won, colour: 'var(--color-positive)' },
                        { label: 'Lost', value: data.lost, colour: 'var(--color-neutral-500)' },
                      ]}
                    />
                  </div>
                  <div>
                    <div className={styles.sub} style={{ marginBottom: 6 }}>
                      Open pipeline health
                    </div>
                    <ShareBar
                      caption="Moving against stalled"
                      height={16}
                      parts={[
                        { label: 'Moving', value: Math.max(0, data.open - data.stalled), colour: 'var(--color-accent)' },
                        { label: 'Stalled', value: data.stalled, colour: 'var(--color-critical)' },
                      ]}
                    />
                  </div>
                  <p className={styles.sub}>
                    Win rate is {pct(data.winRate, 1)} and is null rather than nought when nothing
                    has been decided — a quarter with no closed deals has no win rate, and showing
                    0% would make it look like the worst one on record.
                  </p>
                </div>
              </Panel>
            </Columns>
          </>
        )}
      </AsyncBoundary>
    </Page>
  )
}
