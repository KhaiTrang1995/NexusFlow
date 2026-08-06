import { useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  Button,
  DataTable,
  EmptyState,
  FieldGrid,
  Page,
  Panel,
  PanelBody,
  PanelHeader,
  Tabs,
} from '@/design/primitives'
import { fullMoney } from '@/lib/format'
import { modelFor, OBJECT_MODELS } from '@/fixtures/objects'
import type { RecordRow } from '@/fixtures/objects'
import { renderCell } from './RecordCell'
import styles from './RecordScreen.module.css'

type RecordTab = 'details' | 'related' | 'activity' | 'files'

/**
 * A record page, laid out from the object's own page layout.
 *
 * THE PATH IS THE STAGE FIELD, NOT A SECOND COPY OF IT. The chevrons across the top are drawn
 * from `stages`, and which one is current is read from the record — so a stage added in setup
 * appears here, and a record in a stage the model does not have is visibly in none of them rather
 * than silently drawn as the first.
 */
export function RecordScreen({ objectKey, id }: { objectKey: string; id: string }) {
  const navigate = useNavigate()
  const model = modelFor(objectKey)
  const [tab, setTab] = useState<RecordTab>('details')

  const record = model.records.find((candidate) => candidate.id === id)

  if (!record) {
    return (
      <Page>
        <EmptyState
          title={`No ${model.label.toLowerCase()} with the id ${id}`}
          detail="It may have been deleted, or the link may be from another tenant."
          action={
            <Button
              tone="primary"
              onClick={() => void navigate({ to: '/records/$object', params: { object: model.key } })}
            >
              Back to {model.plural.toLowerCase()}
            </Button>
          }
        />
      </Page>
    )
  }

  const titleField = model.listCols[0] ?? 'name'
  const stageName = model.stageField ? String(record[model.stageField]) : null
  const stageIndex = model.stages?.findIndex((stage) => stage.name === stageName) ?? -1

  const related = relatedRecords(model.key, record)

  return (
    <Page layout="full">
      <header className={styles.header}>
        <div className={styles.identity}>
          <div className={styles.avatar} aria-hidden="true">
            {model.mono}
          </div>
          <div>
            <div className={styles.eyebrow}>
              {model.label} · {record.id}
            </div>
            <h1 className={styles.title}>{String(record[titleField] ?? record.id)}</h1>
          </div>
          <div className={styles.actions}>
            <Button>Edit</Button>
            <Button>Clone</Button>
            {model.key === 'quote' ? (
              <Button tone="primary" onClick={() => void navigate({ to: '/quote/$id', params: { id: record.id } })}>
                Open builder
              </Button>
            ) : (
              <Button tone="primary">New task</Button>
            )}
          </div>
        </div>

        <div className={styles.highlights}>
          {model.listCols.slice(1, 5).map((name) => (
            <div key={name}>
              <div className={styles.highlightLabel}>
                {model.fields.find((field) => field.name === name)?.label ?? name}
              </div>
              <div className={styles.highlightValue}>{renderCell(model, record, name)}</div>
            </div>
          ))}
        </div>

        {model.stages ? (
          <div className={styles.path} role="list" aria-label={`${model.label} path`}>
            {model.stages
              .filter((stage) => !stage.lost)
              .map((stage, index) => (
                <button
                  key={stage.name}
                  type="button"
                  role="listitem"
                  className={`${styles.pathStep} ${
                    index < stageIndex ? styles.pathDone : index === stageIndex ? styles.pathCurrent : ''
                  }`}
                  aria-current={index === stageIndex ? 'step' : undefined}
                >
                  {stage.name}
                </button>
              ))}
          </div>
        ) : null}

        <Tabs
          label="Record sections"
          variant="underlined"
          selected={tab}
          onSelect={setTab}
          items={[
            { id: 'details', label: 'Details' },
            { id: 'related', label: 'Related', badge: related.length },
            { id: 'activity', label: 'Activity', badge: 4 },
            { id: 'files', label: 'Files' },
          ]}
        />
      </header>

      <div className={styles.body}>
        {tab === 'details' ? (
          <>
            {model.layout.map((section) => (
              <Panel key={section.title} className={styles.section}>
                <h2 className={styles.sectionTitle}>{section.title}</h2>
                <FieldGrid columns={section.columns}>
                  {section.fields.map((name) => {
                    const field = model.fields.find((candidate) => candidate.name === name)
                    return (
                      <div key={name}>
                        <div className={styles.highlightLabel}>{field?.label ?? name}</div>
                        <div style={{ fontSize: 15 }}>{renderCell(model, record, name)}</div>
                        {field?.formula ? <div className={styles.sub}>ƒ {field.formula}</div> : null}
                      </div>
                    )
                  })}
                </FieldGrid>
              </Panel>
            ))}
          </>
        ) : null}

        {tab === 'related' ? (
          <div className={styles.related}>
            {related.length === 0 ? (
              <EmptyState
                title="Nothing points at this record yet"
                detail="Related lists appear here once a contact, an opportunity or a quote refers to it."
              />
            ) : (
              related.map((group) => (
                <Panel key={group.title} padding="flush">
                  <PanelHeader title={group.title} note={`${group.rows.length}`} />
                  <DataTable
                    caption={group.title}
                    columns={group.columns.map((name) => ({
                      id: name,
                      header:
                        OBJECT_MODELS[group.objectKey]?.fields.find((field) => field.name === name)
                          ?.label ?? name,
                      cell: (row: RecordRow) =>
                        renderCell(modelFor(group.objectKey), row, name),
                    }))}
                    rows={group.rows}
                    rowKey={(row) => row.id}
                    onRowClick={(row) =>
                      void navigate({
                        to: '/records/$object/$id',
                        params: { object: group.objectKey, id: row.id },
                      })
                    }
                  />
                </Panel>
              ))
            )}
          </div>
        ) : null}

        {tab === 'activity' ? (
          <Panel padding="flush">
            <PanelHeader title="Activity" note="newest first" />
            <PanelBody>
              <div className={styles.feed}>
                {ACTIVITY.map((entry) => (
                  <div key={entry.title} className={styles.feedRow}>
                    <span className={styles.feedIcon} aria-hidden="true">
                      {entry.glyph}
                    </span>
                    <div style={{ minWidth: 0 }}>
                      <div>{entry.title}</div>
                      <div className={styles.sub}>{entry.detail}</div>
                    </div>
                    <span className={styles.feedWhen}>{entry.when}</span>
                  </div>
                ))}
              </div>
            </PanelBody>
          </Panel>
        ) : null}

        {tab === 'files' ? (
          <Panel padding="flush">
            <PanelHeader title="Files" note="3" />
            <PanelBody>
              <div className={styles.feed}>
                {FILES.map((file) => (
                  <div key={file.name} className={styles.feedRow}>
                    <span className={styles.feedIcon} aria-hidden="true">
                      ▤
                    </span>
                    <div style={{ minWidth: 0 }}>
                      <div>{file.name}</div>
                      <div className={styles.sub}>
                        {file.size} · uploaded by {file.by}
                      </div>
                    </div>
                    <span className={styles.feedWhen}>{file.when}</span>
                  </div>
                ))}
              </div>
            </PanelBody>
          </Panel>
        ) : null}
      </div>
    </Page>
  )
}

interface RelatedGroup {
  title: string
  objectKey: string
  columns: readonly string[]
  rows: readonly RecordRow[]
}

/**
 * What points at this record.
 *
 * Matched on the display value because that is what the design's fixtures carry — a real client
 * follows an id. Written as one table rather than as a branch per object so an object added to
 * the model gets its related lists without a code change here.
 */
function relatedRecords(objectKey: string, record: RecordRow): readonly RelatedGroup[] {
  const links: Readonly<Record<string, readonly { child: string; via: string; columns: string[] }[]>> = {
    account: [
      { child: 'contact', via: 'account', columns: ['name', 'title', 'role', 'email'] },
      { child: 'opportunity', via: 'account', columns: ['name', 'stage', 'amount', 'closeDate'] },
      { child: 'workorder', via: 'account', columns: ['number', 'subject', 'status', 'scheduled'] },
    ],
    opportunity: [
      { child: 'quote', via: 'opportunity', columns: ['number', 'status', 'total', 'discount'] },
      { child: 'task', via: 'related', columns: ['subject', 'type', 'status', 'due'] },
    ],
  }

  const name = String(record['name'] ?? record['number'] ?? '')

  return (links[objectKey] ?? [])
    .map((link) => {
      const model = modelFor(link.child)
      const rows = model.records.filter((row) => row[link.via] === name)
      return { title: model.plural, objectKey: link.child, columns: link.columns, rows }
    })
    .filter((group) => group.rows.length > 0)
}

const ACTIVITY = [
  { glyph: '✉', title: 'Sent the redlined MSA', detail: 'To Ron Petrov, cc legal', when: '2 days ago' },
  { glyph: '☎', title: 'Discovery call', detail: '38 minutes · Elena Vargas, Ron Petrov', when: '5 days ago' },
  { glyph: '◆', title: 'Stage moved to Negotiation', detail: 'From Proposal, by A. Ruiz', when: '8 days ago' },
  { glyph: '✎', title: 'Amount raised to ' + fullMoney(184_000), detail: 'Was $164,000', when: '12 days ago' },
]

const FILES = [
  { name: 'MSA — redlined v4.pdf', size: '412 KB', by: 'A. Ruiz', when: '2 days ago' },
  { name: 'Security questionnaire.xlsx', size: '88 KB', by: 'J. Park', when: '9 days ago' },
  { name: 'Solution overview.pdf', size: '1.2 MB', by: 'M. Chen', when: '3 weeks ago' },
]
