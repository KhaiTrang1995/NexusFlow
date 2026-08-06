import { useMemo } from 'react'
import { Meter, Page, PageHeader, Panel, PanelHeader, StatGrid, StatTile, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import { percent } from '@/lib/format'
import styles from './setup.module.css'

/**
 * What is missing, and where.
 *
 * COMPLETENESS IS MEASURED AGAINST THE FIELDS THAT MATTER, NOT ALL OF THEM. Every object has
 * fields nobody fills in on purpose; scoring against all of them produces a number that is always
 * about sixty per cent and never moves. Here the denominator is the required fields plus the ones
 * a report groups by — the fields whose absence actually costs something.
 */
export function DataQualityScreen() {
  const report = useMemo(() => {
    return Object.values(OBJECT_MODELS).map((model) => {
      const watched = model.fields.filter(
        (field) => field.required || field.type === 'picklist' || field.type === 'lookup',
      )

      const cells = model.records.length * watched.length
      const filled = model.records.reduce(
        (sum, record) =>
          sum +
          watched.filter((field) => {
            const value = record[field.name]
            return value !== undefined && value !== '' && value !== '—'
          }).length,
        0,
      )

      const gaps = watched
        .map((field) => ({
          field,
          missing: model.records.filter((record) => {
            const value = record[field.name]
            return value === undefined || value === '' || value === '—'
          }).length,
        }))
        .filter((entry) => entry.missing > 0)
        .sort((a, b) => b.missing - a.missing)

      return {
        model,
        watched: watched.length,
        completeness: cells === 0 ? 1 : filled / cells,
        gaps,
      }
    })
  }, [])

  const worst = [...report].sort((a, b) => a.completeness - b.completeness)[0]
  const overall =
    report.reduce((sum, entry) => sum + entry.completeness, 0) / (report.length || 1)

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Data quality" />

      <StatGrid columns={4}>
        <StatTile
          label="Completeness"
          value={percent(overall)}
          direction={overall >= 0.9 ? 'up' : 'down'}
          note="across the fields that matter"
        />
        <StatTile label="Objects" value={report.length} note="measured" />
        <StatTile
          label="Fields watched"
          value={report.reduce((sum, entry) => sum + entry.watched, 0)}
          note="required, picklist and lookup"
        />
        <StatTile
          label="Worst"
          value={worst?.model.plural ?? '—'}
          direction="down"
          delta={percent(worst?.completeness ?? null)}
          note="lowest completeness"
        />
      </StatGrid>

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <PanelHeader title="By object" note="completeness against the fields that cost something" />
        <div>
          {report.map((entry) => (
            <div key={entry.model.key} className={styles.qualityBar}>
              <div className={styles.qualityName}>
                <div>{entry.model.plural}</div>
                <div className={styles.sub}>
                  {entry.model.records.length} records · {entry.watched} fields watched
                </div>
              </div>
              <div className={styles.qualityMeter}>
                <Meter
                  label={`${entry.model.plural} completeness`}
                  value={Math.round(entry.completeness * 100)}
                  target={100}
                  tone={entry.completeness >= 0.9 ? 'positive' : entry.completeness >= 0.75 ? 'warning' : 'critical'}
                />
              </div>
              <span className={styles.numeric} style={{ width: 56, textAlign: 'right' }}>
                {percent(entry.completeness)}
              </span>
            </div>
          ))}
        </div>
      </Panel>

      <Panel padding="flush">
        <PanelHeader title="Where the holes are" note="the fields with the most missing values" />
        <div>
          {report.flatMap((entry) =>
            entry.gaps.map((gap) => (
              <div key={`${entry.model.key}-${gap.field.name}`} className={styles.flowRow}>
                <Tag tone="outline">{entry.model.label}</Tag>
                <span>{gap.field.label}</span>
                {gap.field.required ? <Tag tone="critical">required</Tag> : null}
                <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                  missing on {gap.missing} of {entry.model.records.length}
                </span>
              </div>
            )),
          )}
          {report.every((entry) => entry.gaps.length === 0) ? (
            <p className={styles.sub} style={{ padding: 17 }}>
              Nothing watched is missing. That is the answer, not an empty screen.
            </p>
          ) : null}
        </div>
      </Panel>
    </Page>
  )
}
