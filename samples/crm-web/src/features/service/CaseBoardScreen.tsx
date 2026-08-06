import { useNavigate } from '@tanstack/react-router'
import { AsyncBoundary, Button, Page, PageHeader, Tag } from '@/design/primitives'
import { useCaseWorklist } from '@/api/queries/hooks'
import type { QueuedCase } from '@/api/contracts'
import { fromNow } from '@/lib/format'
import styles from './service.module.css'

const LANES = ['New', 'Working', 'Waiting', 'Escalated'] as const

/**
 * The same queue, grouped by status.
 *
 * **A read, not a board you can drag on.** Moving a case is a comment with a status on it — the
 * backend has no "set the status" endpoint, because a status change with no note is a change
 * nobody can explain later. So the board sends you to the case rather than pretending a drag
 * would write something.
 */
export function CaseBoardScreen() {
  const navigate = useNavigate()
  const worklist = useCaseWorklist({ mineOnly: false, priority: null, breachedOnly: false })

  return (
    <Page layout="full">
      <PageHeader
        bar
        small
        eyebrow="Service console"
        title="Case board"
        actions={<Button onClick={() => void navigate({ to: '/service/cases' })}>Queue</Button>}
      />

      <AsyncBoundary query={worklist} skeletonRows={4}>
        {(data) => (
          <div className={styles.board}>
            {LANES.map((lane) => {
              const cards = data.cases.filter((row) => row.status === lane)

              return (
                <section key={lane} className={styles.column}>
                  <header className={styles.columnHead}>
                    {lane}
                    <span className={styles.columnCount}>{cards.length}</span>
                  </header>
                  <div className={styles.cards}>
                    {cards.map((row) => (
                      <CaseCard key={row.caseId} row={row} onOpen={() => void navigate({ to: '/service/cases' })} />
                    ))}
                  </div>
                </section>
              )
            })}
          </div>
        )}
      </AsyncBoundary>
    </Page>
  )
}

function CaseCard({ row, onOpen }: { row: QueuedCase; onOpen: () => void }) {
  const late = row.responseBreached || row.resolutionBreached

  return (
    <button
      type="button"
      className={`${styles.card} ${late ? styles.cardBreached : ''}`}
      onClick={onOpen}
    >
      <div className={styles.number}>#{row.number}</div>
      <div>{row.subject}</div>
      <div className={styles.cardMeta}>
        <Tag tone={row.priority === 'Urgent' ? 'critical' : row.priority === 'High' ? 'warning' : 'outline'}>
          {row.priority}
        </Tag>
        {row.awaitingFirstResponse ? <Tag tone="warning">Unanswered</Tag> : null}
        <span style={{ marginLeft: 'auto' }} className={late ? styles.late : undefined}>
          {fromNow(row.minutesToResolutionDue)}
        </span>
      </div>
    </button>
  )
}
