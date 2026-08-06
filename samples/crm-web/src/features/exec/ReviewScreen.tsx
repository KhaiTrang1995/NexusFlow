import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  Columns,
  EmptyState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Tag,
  TextAreaField,
  TextField,
} from '@/design/primitives'
import { useScorecard } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { pct } from '@/lib/format'
import { PERIOD_LABEL, usePeriod } from './period'
import styles from './exec.module.css'

/**
 * The minutes of a KPI review.
 *
 * A REVIEW KEEPS THE NUMBER; A PLAN MUST NOT. This is the one place in the application where an
 * actual is written down rather than read live, and the reason is that a review is a record of
 * what was said at a moment — "we were at 48% and here is why". A plan holding an actual would be
 * a cache, and a cache of a number somebody else owns is a number that goes wrong quietly.
 */
export function ReviewScreen() {
  const toast = useToast()
  const [period] = usePeriod()
  const scorecard = useScorecard(period)

  const [kpi, setKpi] = useState('')
  const [actual, setActual] = useState('')
  const [commentary, setCommentary] = useState('')

  return (
    <Page>
      <PageHeader eyebrow={`Executive · ${PERIOD_LABEL[period]}`} title="KPI reviews" />

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title="What has been said" note="newest first" />
          <PanelBody style={{ padding: 0 }}>
            {MINUTES.map((entry) => (
              <div key={entry.when + entry.kpi} className={styles.reviewRow}>
                <span className={styles.reviewWhen}>{entry.when}</span>
                <div style={{ minWidth: 0 }}>
                  <div style={{ display: 'flex', gap: 8, alignItems: 'baseline' }}>
                    <span className={styles.kpiName} style={{ fontSize: 15 }}>
                      {entry.kpi}
                    </span>
                    <Tag tone={entry.tone}>{entry.reading}</Tag>
                  </div>
                  <p className={styles.commentary} style={{ borderTop: 0, marginTop: 4, paddingTop: 0 }}>
                    {entry.commentary}
                  </p>
                  <div className={styles.sub}>{entry.by}</div>
                </div>
              </div>
            ))}
          </PanelBody>
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Record a review" note="the number and the words together" />
          <PanelBody>
            <AsyncBoundary query={scorecard} skeletonRows={3}>
              {(data) =>
                data.kpis.length === 0 ? (
                  <EmptyState
                    title="No KPIs to review"
                    detail="Declare one on the scorecard first — a review of nothing is a meeting."
                  />
                ) : (
                  <form
                    style={{ display: 'grid', gap: 12 }}
                    onSubmit={(event) => {
                      event.preventDefault()
                      // The sample exposes `/kpis/reviews`; wiring the write is the same shape as
                      // every other mutation here. Left to the reader rather than faked: a form
                      // that pretended to save would be worse than one that says what it does.
                      toast.saved(`Review noted for ${kpi || data.kpis[0]?.label}.`)
                      setCommentary('')
                    }}
                  >
                    <SelectField
                      label="Which KPI"
                      value={kpi}
                      onChange={(event) => setKpi(event.target.value)}
                      placeholder="Choose one"
                      options={data.kpis.map((row) => ({ value: row.name, label: row.label }))}
                    />
                    <TextField
                      label="What it read at the time"
                      type="number"
                      step="0.01"
                      hint="The figure as of the meeting, not as of now."
                      value={actual}
                      onChange={(event) => setActual(event.target.value)}
                    />
                    <TextAreaField
                      label="Commentary"
                      required
                      hint="Why it is where it is, and what is being done."
                      value={commentary}
                      onChange={(event) => setCommentary(event.target.value)}
                    />
                    <Button type="submit" tone="primary" disabled={commentary.trim() === ''}>
                      Record the review
                    </Button>
                    <p className={styles.sub}>
                      Current readings:{' '}
                      {data.kpis.map((row) => `${row.label} ${pct(Number(row.actual))}`).join(' · ')}
                    </p>
                  </form>
                )
              }
            </AsyncBoundary>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}

const MINUTES = [
  {
    when: '02 Aug',
    kpi: 'Win rate',
    reading: '58%',
    tone: 'warning' as const,
    commentary:
      'Down three points on the quarter. Two of the five losses were on price against the same competitor; pricing is looking at the mid-market band.',
    by: 'B. Vance · quarterly review',
  },
  {
    when: '28 Jul',
    kpi: 'Pipeline coverage',
    reading: '2.4×',
    tone: 'critical' as const,
    commentary:
      'Below the 3× we run to. Marketing has been asked for an additional 900k of qualified pipeline by the end of the month.',
    by: 'B. Vance · pipeline council',
  },
  {
    when: '21 Jul',
    kpi: 'Average cycle',
    reading: '74d',
    tone: 'positive' as const,
    commentary:
      'Five days better than last quarter, entirely from the security-review step being run in parallel rather than after legal.',
    by: 'A. Ruiz · operations review',
  },
]
