import {
  EmptyState,
  Meter,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  Skeleton,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { useEntityPage, useSchema } from '@/api/queries/hooks'
import type { DescribedField, ReadableEntity, RecordView } from '@/api/contracts'
import { percent } from '@/lib/format'
import styles from './setup.module.css'

/** What each entity is called on this screen, and what a record of it is called. */
const MEASURED: readonly { entity: ReadableEntity; plural: string }[] = [
  { entity: 'Lead', plural: 'Leads' },
  { entity: 'Account', plural: 'Accounts' },
  { entity: 'Contact', plural: 'Contacts' },
  { entity: 'Opportunity', plural: 'Opportunities' },
]

interface Measured {
  entity: ReadableEntity
  plural: string
  records: number
  watched: readonly DescribedField[]
  completeness: number
  gaps: readonly { field: DescribedField; missing: number }[]
  pending: boolean
}

/**
 * What is missing, and where — in the tenant's own rows.
 *
 * THIS SCREEN SCORED THE PROTOTYPE. Every percentage on it was computed over the fixture records
 * this client ships with, on a page headed "Data quality" that an administrator opens to find out
 * about theirs. A completeness figure is the most quotable number in setup and this one was about
 * nobody's data.
 *
 * COMPLETENESS IS MEASURED AGAINST THE FIELDS THAT MATTER, NOT ALL OF THEM. Every object has
 * fields nobody fills in on purpose; scoring against all of them produces a number that is always
 * about sixty per cent and never moves. The denominator is what `describe` marks required, plus
 * the picklists and references — the fields whose absence actually costs something.
 *
 * ONLY THE DECLARED FIELDS, AND THE SCREEN SAYS SO. `describe` returns the custom fields a tenant
 * declared; the built-in columns are `NOT NULL` in the schema and cannot be missing, so scoring
 * them would add a denominator that is always complete and drag every figure towards a hundred.
 *
 * A DECLARED VALUE IS PREFIXED IN A PAGE, because a field may legitimately be called `name` and
 * an account already has a column of that name. Reading the unprefixed key is how this screen
 * reported nought per cent against a tenant whose accounts were fully populated.
 */
export function DataQualityScreen() {
  const schema = useSchema()

  // One call per entity rather than a loop: hooks are not conditional, and four is the whole
  // list of kinds a custom field can be declared on.
  const pages = {
    Lead: useEntityPage('Lead'),
    Account: useEntityPage('Account'),
    Contact: useEntityPage('Contact'),
    Opportunity: useEntityPage('Opportunity'),
  }

  const report: Measured[] = MEASURED.map(({ entity, plural }) => {
    const page = pages[entity as keyof typeof pages]
    const described = schema.data?.entities.find((one) => one.kind === entity)?.fields ?? []

    const watched = described.filter(
      (field) => field.isRequired || field.options.length > 0 || field.references !== null,
    )

    const records = page.data?.records ?? []
    const cells = records.length * watched.length

    const filled = records.reduce(
      (sum, record) => sum + watched.filter((field) => isFilled(record, field)).length,
      0,
    )

    return {
      entity,
      plural,
      records: records.length,
      watched,
      completeness: cells === 0 ? 1 : filled / cells,
      gaps: watched
        .map((field) => ({
          field,
          missing: records.filter((record) => !isFilled(record, field)).length,
        }))
        .filter((entry) => entry.missing > 0)
        .sort((a, b) => b.missing - a.missing),
      pending: page.isPending || schema.isPending,
    }
  })

  if (report.some((entry) => entry.pending)) {
    return (
      <Page>
        <PageHeader eyebrow="Setup" title="Data quality" />
        <Skeleton rows={8} />
      </Page>
    )
  }

  // Only the entities that have something to score. An entity with no declared fields has a
  // completeness of one out of nothing, and averaging that in moves the headline towards a
  // hundred for a reason nobody can see on the screen.
  const scored = report.filter((entry) => entry.watched.length > 0 && entry.records > 0)

  const overall =
    scored.length === 0
      ? null
      : scored.reduce((sum, entry) => sum + entry.completeness, 0) / scored.length

  const worst = [...scored].sort((a, b) => a.completeness - b.completeness)[0]

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Data quality" />

      {scored.length === 0 ? (
        <EmptyState
          title="Nothing here is measurable yet"
          detail="Completeness is scored against declared fields. Declare one in setup, and rows that carry it, and this screen has something to say."
        />
      ) : (
        <>
          <StatGrid columns={4}>
            <StatTile
              label="Completeness"
              value={percent(overall)}
              direction={(overall ?? 0) >= 0.9 ? 'up' : 'down'}
              note="across the fields that matter"
            />
            <StatTile label="Entities" value={scored.length} note="with something to score" />
            <StatTile
              label="Fields watched"
              value={scored.reduce((sum, entry) => sum + entry.watched.length, 0)}
              note="required, picklist and reference"
            />
            <StatTile
              label="Worst"
              value={worst?.plural ?? '—'}
              direction="down"
              delta={percent(worst?.completeness ?? null)}
              note="lowest completeness"
            />
          </StatGrid>

          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader
              title="By entity"
              note="completeness against the fields that cost something"
            />
            <div>
              {scored.map((entry) => (
                <div key={entry.entity} className={styles.qualityBar}>
                  <div className={styles.qualityName}>
                    <div>{entry.plural}</div>
                    <div className={styles.sub}>
                      {entry.records} record(s) · {entry.watched.length} field(s) watched
                    </div>
                  </div>
                  <div className={styles.qualityMeter}>
                    <Meter
                      label={`${entry.plural} completeness`}
                      value={Math.round(entry.completeness * 100)}
                      target={100}
                      tone={
                        entry.completeness >= 0.9
                          ? 'positive'
                          : entry.completeness >= 0.75
                            ? 'warning'
                            : 'critical'
                      }
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
              {scored.flatMap((entry) =>
                entry.gaps.map((gap) => (
                  <div key={`${entry.entity}-${gap.field.name}`} className={styles.flowRow}>
                    <Tag tone="outline">{entry.entity}</Tag>
                    <span>{gap.field.label}</span>
                    {gap.field.isRequired ? <Tag tone="critical">required</Tag> : null}
                    <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                      missing on {gap.missing} of {entry.records}
                    </span>
                  </div>
                )),
              )}
              {scored.every((entry) => entry.gaps.length === 0) ? (
                <p className={styles.sub} style={{ padding: 17 }}>
                  Nothing watched is missing. That is the answer, not an empty screen.
                </p>
              ) : null}
            </div>
          </Panel>
        </>
      )}
    </Page>
  )
}

/**
 * Whether a record carries a value for a field.
 *
 * NULL AND ABSENT ARE THE SAME HOLE HERE, and an empty string is one too — a field cleared to ""
 * reports as present on every count that only checks for null, which is how a completeness figure
 * climbs while the data gets worse.
 */
function isFilled(record: RecordView, field: DescribedField): boolean {
  const value = record.values[`custom.${field.name}`]

  return value !== undefined && value !== null && value !== ''
}
