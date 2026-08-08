import { useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  Columns,
  DataTable,
  EmptyState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
  TextAreaField,
} from '@/design/primitives'
import type { Column } from '@/design/primitives'
import { useApprovalInbox, useCaseWorklist, useDecideApproval, useEntityPage } from '@/api/queries/hooks'
import type { WaitingApproval } from '@/api/contracts'
import { useToast } from '@/app/ToastProvider'
import { dateTime, fromNow } from '@/lib/format'
import styles from './work.module.css'

type Lane = 'approvals' | 'cases' | 'tasks'

/**
 * One inbox for everything waiting on this person.
 *
 * TWO OF THE THREE LANES ARE THE SERVER'S OWN INBOXES. Approvals and cases are filtered by who is
 * asking — from the token's subject claim, never from a user id in the body — which is exactly
 * what makes them an inbox rather than a query anybody could run as anybody.
 *
 * A SUBMITTER NEVER SEES THEIR OWN REQUEST HERE. The backend leaves it out, because they could
 * not act on it anyway and an inbox full of things you are forbidden to decide is an inbox people
 * stop opening.
 */
export function InboxScreen() {
  const navigate = useNavigate()
  const toast = useToast()

  const [lane, setLane] = useState<Lane>('approvals')
  const [selected, setSelected] = useState<WaitingApproval | null>(null)
  const [note, setNote] = useState('')

  const approvals = useApprovalInbox()
  const cases = useCaseWorklist({ mineOnly: true, priority: null, breachedOnly: false })
  const decide = useDecideApproval()

  // The tenant's own open activities. This lane showed the prototype's four tasks — the same
  // four on every tenant, in an inbox headed "waiting on you".
  const activities = useEntityPage('Activity')

  const tasks = (activities.data?.records ?? []).filter(
    (row) => row.values['status'] !== 'Completed',
  )

  const approvalColumns: readonly Column<WaitingApproval>[] = [
    {
      id: 'subject',
      header: 'Waiting on you',
      cell: (row) => (
        <>
          <span className={styles.link}>
            {row.subject} · {row.stepLabel}
          </span>
          <div className={styles.sub}>
            {row.process} · submitted by {row.submittedBy}
          </div>
        </>
      ),
      sortValue: (row) => row.submittedAt,
    },
    { id: 'step', header: 'Step', numeric: true, cell: (row) => row.step + 1 },
    {
      id: 'when',
      header: 'Submitted',
      cell: (row) => dateTime(row.submittedAt),
      sortValue: (row) => row.submittedAt,
    },
  ]

  return (
    <Page>
      <PageHeader
        eyebrow="My work"
        title="Inbox"
        actions={
          <>
            <Button onClick={() => void navigate({ to: '/work/calendar' })}>Calendar</Button>
            <Button onClick={() => void navigate({ to: '/work/mobile' })}>Mobile preview</Button>
          </>
        }
      />

      <StatGrid columns={3}>
        <StatTile
          label="Approvals"
          value={approvals.data?.waiting.length ?? '—'}
          note="waiting on you"
          onActivate={() => setLane('approvals')}
          drillLabel="the approvals lane"
        />
        <StatTile
          label="My cases"
          value={cases.data?.cases.length ?? '—'}
          delta={cases.data?.breached ? `${cases.data.breached} late` : undefined}
          direction={cases.data?.breached ? 'down' : 'flat'}
          note="open and assigned to you"
          onActivate={() => setLane('cases')}
          drillLabel="the cases lane"
        />
        <StatTile
          label="Tasks"
          value={activities.data === undefined ? '—' : tasks.length}
          note={activities.isError ? 'the read was refused' : 'open against this tenant'}
          onActivate={() => setLane('tasks')}
          drillLabel="the tasks lane"
        />
      </StatGrid>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader
            title="What is waiting"
            actions={
              <ButtonGroup label="Inbox lane">
                <Button size="sm" aria-pressed={lane === 'approvals'} onClick={() => setLane('approvals')}>
                  Approvals
                </Button>
                <Button size="sm" aria-pressed={lane === 'cases'} onClick={() => setLane('cases')}>
                  Cases
                </Button>
                <Button size="sm" aria-pressed={lane === 'tasks'} onClick={() => setLane('tasks')}>
                  Tasks
                </Button>
              </ButtonGroup>
            }
          />

          {lane === 'approvals' ? (
            <AsyncBoundary query={approvals} skeletonRows={3}>
              {(data) => (
                <DataTable
                  caption="Approvals waiting on you"
                  columns={approvalColumns}
                  rows={data.waiting}
                  rowKey={(row) => row.requestId}
                  onRowClick={setSelected}
                  isRowSelected={(row) => row.requestId === selected?.requestId}
                  empty="Nothing is waiting on you. Your own submissions are not shown — you could not decide them anyway."
                />
              )}
            </AsyncBoundary>
          ) : null}

          {lane === 'cases' ? (
            <AsyncBoundary query={cases} skeletonRows={3}>
              {(data) => (
                <DataTable
                  caption="My open cases"
                  columns={[
                    {
                      id: 'subject',
                      header: 'Case',
                      cell: (row) => (
                        <>
                          <span className={styles.link}>
                            #{row.number} {row.subject}
                          </span>
                          <div className={styles.sub}>{row.status}</div>
                        </>
                      ),
                    },
                    {
                      id: 'priority',
                      header: 'Priority',
                      cell: (row) => <Tag tone="outline">{row.priority}</Tag>,
                    },
                    {
                      id: 'due',
                      header: 'Resolution',
                      numeric: true,
                      cell: (row) => (
                        <span className={row.resolutionBreached ? styles.late : undefined}>
                          {fromNow(row.minutesToResolutionDue)}
                        </span>
                      ),
                    },
                  ]}
                  rows={data.cases}
                  rowKey={(row) => row.caseId}
                  onRowClick={() => void navigate({ to: '/service/cases' })}
                  empty="No cases are assigned to you."
                />
              )}
            </AsyncBoundary>
          ) : null}

          {/*
            THE OTHER TWO LANES WERE GUARDED AND THIS ONE WAS NOT. A refused or in-flight activity
            read left the table rendering its own empty message — "Nothing due." — which is a
            claim about the tenant made from a request that never answered.
          */}
          {lane === 'tasks' ? (
            <AsyncBoundary query={activities} skeletonRows={3}>
              {() => (
            <DataTable
              caption="My tasks"
              columns={[
                {
                  id: 'subject',
                  header: 'Task',
                  cell: (row) => (
                    <>
                      <span className={styles.link}>{row.values['subject'] ?? '—'}</span>
                      <div className={styles.sub}>{row.values['status'] ?? ''}</div>
                    </>
                  ),
                },
                { id: 'type', header: 'Type', cell: (row) => <Tag>{row.values['kind'] ?? '—'}</Tag> },
                {
                  id: 'due',
                  header: 'Due',
                  // The server's instant, formatted here. A raw timestamptz in a column headed
                  // "Due" is the same defect this application fixed on the record page.
                  cell: (row) => dateTime(row.values['due_at']),
                },
              ]}
              rows={tasks}
              rowKey={(row) => row.recordId}
              empty="Nothing is open against this tenant."
            />
              )}
            </AsyncBoundary>
          ) : null}
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Decide" note={selected ? selected.process : 'nothing selected'} />
          <PanelBody>
            {selected ? (
              <form
                className={styles.decide}
                onSubmit={(event) => {
                  event.preventDefault()
                }}
              >
                <div className={styles.decideHead}>
                  <div className={styles.decideSubject}>
                    {selected.subject} · {selected.stepLabel}
                  </div>
                  <div className={styles.sub}>
                    Submitted by {selected.submittedBy}, {dateTime(selected.submittedAt)}
                  </div>
                </div>

                <TextAreaField
                  label="Why"
                  required
                  hint="Kept on the register — this is what an audit reads."
                  value={note}
                  onChange={(event) => setNote(event.target.value)}
                />

                <div className={styles.decideActions}>
                  <Button
                    tone="primary"
                    disabled={decide.isPending || note.trim() === ''}
                    onClick={() => send('Approved')}
                  >
                    Approve
                  </Button>
                  <Button
                    tone="danger"
                    disabled={decide.isPending || note.trim() === ''}
                    onClick={() => send('Rejected')}
                  >
                    Reject
                  </Button>
                </div>

                <p className={styles.sub}>
                  A rejection ends the request; no later step is asked. A later approver being
                  asked anyway would turn “no” into “not yet”.
                </p>
              </form>
            ) : (
              <EmptyState
                title="Pick something from the list"
                detail="A decision needs a note, because the register is what an audit reads."
              />
            )}
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )

  function send(decision: 'Approved' | 'Rejected') {
    if (!selected) return

    decide.mutate(
      { requestId: selected.requestId, decision, note: note.trim() },
      {
        onSuccess: (result) => {
          toast.saved(
            result.status === 'Pending'
              ? `Recorded. Now waiting on ${result.awaitingLabel}.`
              : `The request is ${result.status.toLowerCase()}.`,
          )
          setSelected(null)
          setNote('')
        },
        onError: (error) => toast.failed(error),
      },
    )
  }
}
