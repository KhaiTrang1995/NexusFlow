import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  Button,
  EmptyState,
  FieldGrid,
  Page,
  Panel,
  PanelBody,
  PanelHeader,
  Skeleton,
  Tabs,
} from '@/design/primitives'
import { useEntityPage, useEntityRecord, useProcess } from '@/api/queries/hooks'
import { modelFor } from '@/fixtures/objects'
import { renderCell } from './RecordCell'
import { entityOf, keyColumnOf, toRows } from './liveRecords'
import { ActivityFeed } from './ActivityFeed'
import { AdvanceOpportunity } from './AdvanceOpportunity'
import { ConvertLeadDrawer } from './ConvertLeadDrawer'
import { EditFieldsDrawer } from './EditFieldsDrawer'
import { IssueQuoteDrawer } from './IssueQuoteDrawer'
import { NewTaskDrawer } from './NewTaskDrawer'
import { QuoteActions } from './QuoteActions'
import { RelatedList } from './RelatedList'
import { relatedLinksOf } from './related'
import { remember } from '@/features/search/recents'
import { useSession } from '@/session/SessionProvider'
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
/** The kinds a custom field can be declared on, which is what the edit drawer writes. */
const EDITABLE: readonly string[] = ['Lead', 'Account', 'Contact', 'Opportunity']

export function RecordScreen({ objectKey, id }: { objectKey: string; id: string }) {
  const navigate = useNavigate()
  const model = modelFor(objectKey)
  const { tenantId } = useSession()
  const [tab, setTab] = useState<RecordTab>('details')
  const [editing, setEditing] = useState(false)
  const [quoting, setQuoting] = useState(false)
  const [addingTask, setAddingTask] = useState(false)
  const [converting, setConverting] = useState(false)

  // The record comes from the server for the four entities it can page, and from the
  // prototype's fixtures for the other three. Before this, it came from the fixtures always —
  // so a row opened from a list of live accounts reported that no such account existed, which
  // was true only of the fixtures it was looking in.
  const entity = entityOf(objectKey)
  const live = useEntityRecord(entity, keyColumnOf(objectKey), id)
  const accounts = useEntityPage(entity === 'Contact' || entity === 'Opportunity' ? 'Account' : null)
  const process = useProcess(entity === 'Opportunity' ? 'Opportunity' : null)

  const accountNames = useMemo(() => {
    const names = new Map<string, string>()

    for (const row of accounts.data?.records ?? []) {
      const name = row.values['name']

      if (name !== null && name !== undefined) {
        names.set(row.recordId, name)
      }
    }

    return names
  }, [accounts.data])

  const record = useMemo(
    () => toRows(objectKey, model, live.data?.records ?? [], accountNames)[0],
    [model, objectKey, live.data, accountNames],
  )

  // What search's "recent" panel is. Written from here rather than tracked on the server: nothing
  // in this application records that somebody looked at a row, and a write on every record open to
  // fill one panel would be a poor trade. That panel used to hold four invented records.
  useEffect(() => {
    if (record !== undefined) {
      remember(tenantId, {
        objectKey: model.key,
        id,
        title: String(record[model.listCols[0] ?? 'name'] ?? id),
        subtitle: model.stageField ? String(record[model.stageField]) : model.label,
      })
    }
  }, [record, tenantId, model, id])

  if (entity !== null && live.isPending) {
    return (
      <Page>
        <Skeleton rows={8} />
      </Page>
    )
  }

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
  // The published process where there is one, and the build's own closed vocabulary where there
  // is not — a lead's status and a quote's are column constraints, not tenant configuration.
  const path =
    process.data !== undefined
      ? process.data.stages.filter((stage) => !stage.name.toLowerCase().includes('lost'))
          .map((stage) => stage.name)
      : (model.stages ?? []).filter((stage) => !stage.lost).map((stage) => stage.name)

  const stageIndex = path.indexOf(stageName ?? '')

  // What points at this record, by foreign key. The counts are not known until each list has
  // been read, so the tab carries no badge rather than one this screen guessed.
  const related = relatedLinksOf(model.key)

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
            {/*
              Only the four kinds a custom field can be declared on, and only for a live record.
              Everything else has nothing this build can write.
            */}
            <Button
              disabled={!EDITABLE.includes(String(entity))}
              title={
                EDITABLE.includes(String(entity))
                  ? undefined
                  : 'Nothing on this record is editable by this build.'
              }
              onClick={() => setEditing(true)}
            >
              Edit
            </Button>
            {/*
              Disabled, not removed. The design has a Clone and this build has no capability
              behind it; a button that silently does nothing is read as a broken button, and the
              reader retries it. Saying why is the whole difference.
            */}
            <Button disabled title="This build has no duplicate-record capability.">
              Clone
            </Button>

            {/*
              The seller's loop, one entity at a time: a lead converts, an opportunity moves
              through the published process and takes a quote, and a quote is submitted and
              ordered. Everything else gets the one action that always applies.
            */}
            {entity === 'Opportunity' ? (
              <AdvanceOpportunity opportunityId={id} stage={stageName} />
            ) : null}

            {model.key === 'quote' ? (
              <>
                <QuoteActions quoteId={record.id} status={stageName ?? String(record['status'] ?? '')} />
                <Button onClick={() => void navigate({ to: '/quote/$id', params: { id: record.id } })}>
                  Open builder
                </Button>
              </>
            ) : entity === 'Opportunity' ? (
              <Button tone="primary" onClick={() => setQuoting(true)}>
                New quote
              </Button>
            ) : entity === 'Lead' ? (
              <Button
                tone="primary"
                disabled={String(record['status'] ?? '') === 'Converted'}
                title={
                  String(record['status'] ?? '') === 'Converted'
                    ? 'This lead has already been converted.'
                    : undefined
                }
                onClick={() => setConverting(true)}
              >
                Convert
              </Button>
            ) : (
              <Button tone="primary" onClick={() => setAddingTask(true)}>
                New task
              </Button>
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

        {/*
          THE PATH IS A DISPLAY, AND IT USED TO BE MADE OF BUTTONS. Nine `<button>` elements with
          no handler at all: clicking a stage did nothing, which is the one failure a reader
          cannot diagnose — they conclude the record is stuck rather than that the control was
          never wired. Nothing here should be clickable either, because a stage is not somewhere
          you put a deal: you apply a trigger and the process decides. The moves that do exist are
          the buttons in the header above, which are the published transitions from where it is.

          FOR AN OPPORTUNITY THE STEPS ARE THE PROCESS'S. They were the prototype's nine, matching
          the seeded definition by coincidence of naming; a tenant that renamed a stage saw its
          deal in none of them.
        */}
        {path.length > 0 ? (
          <ol className={styles.path} aria-label={`${model.label} path`}>
            {path.map((step, index) => (
              <li
                key={step}
                className={`${styles.pathStep} ${
                  index < stageIndex ? styles.pathDone : index === stageIndex ? styles.pathCurrent : ''
                }`}
                aria-current={index === stageIndex ? 'step' : undefined}
              >
                {step}
              </li>
            ))}
          </ol>
        ) : null}

        <Tabs
          label="Record sections"
          variant="underlined"
          selected={tab}
          onSelect={setTab}
          items={[
            { id: 'details', label: 'Details' },
            { id: 'related', label: 'Related' },

            // No badge. It was hard-coded to four on every record in the tenant, which is a
            // count of nothing dressed as a count of something.
            { id: 'activity', label: 'Activity' },
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
                title="Nothing points at this kind of record"
                detail="A lead has no children until it is converted, and then it becomes an account, a contact and an opportunity that point at each other."
              />
            ) : (
              related.map((link) => (
                <RelatedList key={link.title} link={link} parentId={record.id} />
              ))
            )}
          </div>
        ) : null}

        {tab === 'activity' ? (
          <Panel padding="flush">
            <PanelHeader title="Activity" note="against this record" />
            <ActivityFeed recordId={record.id} />
          </Panel>
        ) : null}

        {tab === 'files' ? (
          <Panel padding="flush">
            <PanelHeader title="Files" />
            <PanelBody>
              {/*
                THREE INVENTED DOCUMENTS USED TO LIVE HERE — the same redlined MSA and the same
                security questionnaire on every record in the tenant, with sizes and uploaders.
                This build has no file storage at all, and a tab listing files that cannot be
                opened is worse than one that says there are none: somebody goes looking for the
                download that never appears, and concludes the link is broken rather than that the
                feature is absent.
              */}
              <EmptyState
                title="This build stores no files"
                detail="There is no attachment surface on the server, so there is nothing to list. The tab is here because the design has it, not because it is waiting for data."
              />
            </PanelBody>
          </Panel>
        ) : null}
      </div>

      {quoting ? (
        <IssueQuoteDrawer
          opportunityId={id}
          opportunityName={String(record[titleField] ?? id)}
          currency={String(record['currency'] ?? 'EUR')}
          onClose={() => setQuoting(false)}
        />
      ) : null}

      {addingTask ? <NewTaskDrawer onClose={() => setAddingTask(false)} /> : null}

      {converting ? (
        <ConvertLeadDrawer
          leadId={id}
          company={String(record['company'] ?? record[titleField] ?? id)}
          onClose={() => setConverting(false)}
        />
      ) : null}

      {editing && entity !== null ? (
        <EditFieldsDrawer
          kind={entity as 'Lead' | 'Account' | 'Contact' | 'Opportunity'}
          id={id}
          title={String(record[titleField] ?? id)}
          onClose={() => setEditing(false)}
        />
      ) : null}
    </Page>
  )
}
