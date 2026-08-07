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
} from '@/design/primitives'
import { useReviewKpi, useScorecard } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { kpiValue } from './kpiUnits'
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
  const record = useReviewKpi()

  const [kpi, setKpi] = useState('')
  const [commentary, setCommentary] = useState('')

  return (
    <Page>
      <PageHeader eyebrow={`Executive · ${PERIOD_LABEL[period]}`} title="KPI reviews" />

      <Columns layout="split">
        <Panel padding="flush">
          {/*
            THE TENANT'S OWN COMMENTARY, NOT A SPECIMEN. This panel used to hold four written-out
            minutes beside a form that saved nothing, and once the form started saving the two
            together were worse than either: a reader would take the invented entries for the
            record their own review had just joined.

            The scorecard already carries each KPI's last commentary, so there is no second read
            here — and no dates, because that is what the data has. A column of plausible
            timestamps would be the same mistake in a smaller font.
          */}
          <PanelHeader title="What was last said" note="one per KPI" />
          <PanelBody style={{ padding: 0 }}>
            <AsyncBoundary query={scorecard} skeletonRows={3}>
              {(data) => {
                const said = data.kpis.filter((row) => row.lastCommentary !== null)

                return said.length === 0 ? (
                  <EmptyState
                    title="Nothing has been said about these numbers yet"
                    detail="A review recorded on the right appears here — this is the tenant's register, not an example of one."
                  />
                ) : (
                  <>
                    {said.map((row) => (
                      <div key={row.name} className={styles.reviewRow}>
                        <span className={styles.reviewWhen}>{row.status === 'OnTrack' ? 'on' : 'off'}</span>
                        <div style={{ minWidth: 0 }}>
                          <div style={{ display: 'flex', gap: 8, alignItems: 'baseline' }}>
                            <span className={styles.kpiName} style={{ fontSize: 15 }}>
                              {row.label}
                            </span>
                            <Tag tone={row.status === 'OnTrack' ? 'positive' : 'warning'}>
                              {kpiValue(row.source, Number(row.actual))}
                            </Tag>
                          </div>
                          <p
                            className={styles.commentary}
                            style={{ borderTop: 0, marginTop: 4, paddingTop: 0 }}
                          >
                            {row.lastCommentary}
                          </p>
                        </div>
                      </div>
                    ))}
                  </>
                )
              }}
            </AsyncBoundary>
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

                      record.mutate(
                        {
                          kpi: kpi.length > 0 ? kpi : (data.kpis[0]?.name ?? ''),
                          period,
                          commentary: commentary.trim(),
                        },
                        {
                          onSuccess: (result) => {
                            // The stored figure, said back. It is the server's reading at the
                            // instant of recording, and a toast repeating what the screen already
                            // showed would hide the one number this write exists to fix.
                            toast.saved(
                              `Recorded at ${kpiValue(
                                data.kpis.find((row) => row.name === result.kpi)?.source ?? '',
                                Number(result.actual),
                              )} — ${result.status}.`,
                            )
                            setCommentary('')
                          },
                          onError: (error) => toast.failed(error),
                        },
                      )
                    }}
                  >
                    <SelectField
                      label="Which KPI"
                      value={kpi}
                      onChange={(event) => setKpi(event.target.value)}
                      placeholder="Choose one"
                      options={data.kpis.map((row) => ({ value: row.name, label: row.label }))}
                    />
                    <p className={styles.sub}>
                      The reading is not typed here. The server takes it at the moment the review
                      is recorded and keeps that — which is what makes this a minute rather than a
                      figure somebody remembered.
                    </p>
                    <TextAreaField
                      label="Commentary"
                      required
                      hint="Why it is where it is, and what is being done."
                      value={commentary}
                      onChange={(event) => setCommentary(event.target.value)}
                    />
                    <Button
                      type="submit"
                      tone="primary"
                      disabled={commentary.trim() === '' || record.isPending}
                    >
                      {record.isPending ? 'Recording…' : 'Record the review'}
                    </Button>
                    <p className={styles.sub}>
                      Current readings:{' '}
                      {data.kpis
                        .map((row) => `${row.label} ${kpiValue(row.source, Number(row.actual))}`)
                        .join(' · ')}
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
