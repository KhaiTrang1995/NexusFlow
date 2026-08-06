import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  Meter,
  Page,
  PageHeader,
  Panel,
  Tag,
} from '@/design/primitives'
import { useScorecard } from '@/api/queries/hooks'
import { pct } from '@/lib/format'
import { PERIODS, PERIOD_LABEL, usePeriod } from './period'
import styles from './exec.module.css'

/**
 * The scorecard.
 *
 * COMPUTED LIVE, NEVER STORED. A KPI here carries a source, a target and a direction — the actual
 * is read at the moment somebody asks. A stored figure is a figure that was true once, and the
 * only way to find out when is to ask the person who last pressed refresh.
 */
export function KpiScreen() {
  const navigate = useNavigate()
  const [period, setPeriod] = usePeriod()
  const scorecard = useScorecard(period)

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Scorecard"
        actions={
          <>
            <ButtonGroup label="Period">
              {PERIODS.map((option) => (
                <Button key={option} aria-pressed={period === option} onClick={() => setPeriod(option)}>
                  {PERIOD_LABEL[option]}
                </Button>
              ))}
            </ButtonGroup>
            <Button onClick={() => void navigate({ to: '/exec/reviews' })}>Reviews</Button>
          </>
        }
      />

      <AsyncBoundary query={scorecard} skeletonRows={5}>
        {(data) => (
          <div className={styles.kpiGrid}>
            {data.kpis.map((kpi) => {
              const good = kpi.status === 'OnTrack'

              return (
                <Panel key={kpi.name}>
                  <div className={styles.kpiHead}>
                    <span className={styles.kpiName}>{kpi.label}</span>
                    <Tag tone={good ? 'positive' : 'critical'} dot>
                      {good ? 'On track' : 'Off track'}
                    </Tag>
                  </div>
                  <div className={styles.kpiValue}>{pct(Number(kpi.actual))}</div>
                  <div className={styles.sub}>
                    target {pct(Number(kpi.target))} ·{' '}
                    {kpi.direction === 'Up' ? 'higher is better' : 'lower is better'}
                  </div>
                  <div style={{ marginTop: 9 }}>
                    <Meter
                      label={`${kpi.label} against target`}
                      value={Number(kpi.actual)}
                      target={Number(kpi.target)}
                      tone={good ? 'positive' : 'critical'}
                    />
                  </div>
                  <div className={styles.sub} style={{ marginTop: 7 }}>
                    read from {kpi.source}
                  </div>
                  {kpi.lastCommentary ? (
                    <p className={styles.commentary}>{kpi.lastCommentary}</p>
                  ) : (
                    <p className={styles.commentary}>
                      No review yet. A number with nobody's words beside it is a number nobody has
                      taken responsibility for.
                    </p>
                  )}
                </Panel>
              )
            })}
            {data.kpis.length === 0 ? (
              <Panel>
                <p className={styles.sub}>No KPIs are declared for {PERIOD_LABEL[period]}.</p>
              </Panel>
            ) : null}
          </div>
        )}
      </AsyncBoundary>
    </Page>
  )
}
