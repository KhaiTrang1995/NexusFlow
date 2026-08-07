import { EmptyState, PanelBody, Skeleton, Tag } from '@/design/primitives'
import { useRelatedRecords } from '@/api/queries/hooks'
import { dateTime } from '@/lib/format'
import styles from './RecordScreen.module.css'

/** What the four kinds of activity are drawn with. A glyph, not a colour, so it reads in mono. */
const GLYPH: Readonly<Record<string, string>> = {
  Call: '☎',
  Meeting: '⌘',
  Note: '✎',
  Task: '☑',
}

/**
 * What has actually happened against this record.
 *
 * THIS PANEL USED TO BE FOUR SENTENCES WRITTEN BY HAND, ON EVERY RECORD IN THE TENANT. The same
 * redlined MSA was sent to every account, the same discovery call happened against every lead, and
 * the tab counted four whether the record was a day old or a year. Beside a New task button that
 * writes for real, that is not a placeholder — it is a record of things that did not happen.
 *
 * `relates_to_id` IS WHAT MAKES IT THIS RECORD'S. The polymorphic parent is a kind and an id
 * together on the server; filtering on the id alone is enough here because ids do not collide
 * across kinds, and asking for both would be asking the page endpoint for a column it offers
 * anyway.
 */
export function ActivityFeed({ recordId }: { recordId: string }) {
  const page = useRelatedRecords('Activity', 'relates_to_id', recordId)

  if (page.isPending) {
    return (
      <PanelBody>
        <Skeleton rows={3} />
      </PanelBody>
    )
  }

  if (page.isError) {
    return (
      <PanelBody>
        <EmptyState title="The activity could not be read" detail={page.error.message} />
      </PanelBody>
    )
  }

  if (page.data.records.length === 0) {
    return (
      <PanelBody>
        <EmptyState
          title="Nothing has been logged against this record"
          detail="A task, call, meeting or note added here appears in the feed — this is the tenant's own history, not an example of one."
        />
      </PanelBody>
    )
  }

  return (
    <PanelBody>
      <div className={styles.feed}>
        {page.data.records.map((row) => {
          const kind = row.values['kind'] ?? 'Task'
          const due = row.values['due_at']
          const done = row.values['completed_at']

          return (
            <div key={row.recordId} className={styles.feedRow}>
              <span className={styles.feedIcon} aria-hidden="true">
                {GLYPH[kind] ?? '•'}
              </span>
              <div style={{ minWidth: 0 }}>
                <div>{row.values['subject'] ?? '—'}</div>
                <div className={styles.sub}>
                  {kind}
                  {/*
                    The status is the server's word, not a comparison made here. Whether a step is
                    late is decided against one clock; a client comparing a due date against its
                    own calls a task overdue in Sydney and not in Lisbon on the same afternoon.
                  */}
                  {row.values['status'] !== null && row.values['status'] !== undefined ? (
                    <>
                      {' · '}
                      <Tag tone={row.values['status'] === 'Completed' ? 'positive' : 'outline'}>
                        {row.values['status']}
                      </Tag>
                    </>
                  ) : null}
                </div>
              </div>
              <span className={styles.feedWhen}>
                {done !== null && done !== undefined
                  ? dateTime(done)
                  : due !== null && due !== undefined
                    ? `due ${dateTime(due)}`
                    : ''}
              </span>
            </div>
          )
        })}
      </div>
    </PanelBody>
  )
}
