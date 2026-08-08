import { useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  DataTable,
  Drawer,
  DrawerHighlights,
  DrawerSection,
  FieldRow,
  Page,
  PageHeader,
  StatGrid,
  StatTile,
  Tag,
  TextAreaField,
} from '@/design/primitives'
import type { Column } from '@/design/primitives'
import { useCaseWorklist, useCommentOnCase } from '@/api/queries/hooks'
import type { CasePriority, CaseStatus, QueuedCase } from '@/api/contracts'
import { useToast } from '@/app/ToastProvider'
import { dateTime, fromNow } from '@/lib/format'
import { NewCaseDrawer } from './NewCaseDrawer'
import styles from './service.module.css'

/**
 * The service console — the live queue, from the backend.
 *
 * BREACH IS THE SERVER'S ANSWER, NOT THE CLIENT'S. The backend computes it as of the read, over
 * the promise it stamped on the case in the hours the desk was open. Recomputing it here from a
 * due date and `Date.now()` would be a second implementation of business-hours arithmetic living
 * in a browser whose clock nobody controls — and the two would eventually disagree in front of a
 * customer.
 */
export function CaseConsoleScreen() {
  const navigate = useNavigate()
  const toast = useToast()

  const [mineOnly, setMineOnly] = useState(false)
  const [priority, setPriority] = useState<CasePriority | null>(null)
  const [breachedOnly, setBreachedOnly] = useState(false)
  const [opening, setOpening] = useState(false)
  const [peek, setPeek] = useState<QueuedCase | null>(null)

  const worklist = useCaseWorklist({ mineOnly, priority, breachedOnly })
  const comment = useCommentOnCase()

  const columns: readonly Column<QueuedCase>[] = [
    {
      id: 'number',
      header: 'Case',
      width: '110px',
      cell: (row) => <span className={styles.number}>#{row.number}</span>,
      sortValue: (row) => row.number,
    },
    {
      id: 'subject',
      header: 'Subject',
      cell: (row) => (
        <>
          <span className={styles.subject}>{row.subject}</span>
          <div className={styles.sub}>{row.ownerId}</div>
        </>
      ),
      sortValue: (row) => row.subject,
    },
    {
      id: 'priority',
      header: 'Priority',
      cell: (row) => (
        <Tag tone={row.priority === 'Urgent' ? 'critical' : row.priority === 'High' ? 'warning' : 'outline'}>
          {row.priority}
        </Tag>
      ),
      sortValue: (row) => row.priority,
    },
    {
      id: 'status',
      header: 'Status',
      cell: (row) => <Tag tone={row.status === 'Escalated' ? 'warning' : 'neutral'}>{row.status}</Tag>,
      sortValue: (row) => row.status,
    },
    {
      id: 'response',
      header: 'First response',
      cell: (row) =>
        row.firstResponseDueAt === null ? (
          <span className={styles.sub}>no promise</span>
        ) : row.responseBreached ? (
          <Tag tone="critical" dot>
            {row.awaitingFirstResponse ? 'Late, unanswered' : 'Answered late'}
          </Tag>
        ) : row.awaitingFirstResponse ? (
          <span className={styles.due}>{dateTime(row.firstResponseDueAt)}</span>
        ) : (
          <Tag tone="positive">Answered</Tag>
        ),
    },
    {
      id: 'resolution',
      header: 'Resolution due',
      numeric: true,
      cell: (row) =>
        row.minutesToResolutionDue === null ? (
          <span className={styles.sub}>—</span>
        ) : (
          <span className={row.resolutionBreached ? styles.late : styles.due}>
            {fromNow(row.minutesToResolutionDue)}
          </span>
        ),
      sortValue: (row) => row.minutesToResolutionDue ?? Number.MAX_SAFE_INTEGER,
    },
  ]

  return (
    <Page layout="full">
      <PageHeader
        bar
        small
        eyebrow="Service console"
        title="Case queue"
        actions={
          <>
            <Button onClick={() => void navigate({ to: '/service/board' })}>Board</Button>
            <Button onClick={() => void navigate({ to: '/service/sla' })}>SLA setup</Button>
            {/*
              It had no handler at all — the one screen whose purpose is what has come in could
              be read and worked but never added to. `crm.case.open` has been there throughout.
            */}
            <Button tone="primary" onClick={() => setOpening(true)}>
              New case
            </Button>
          </>
        }
      />

      <div className={styles.filters}>
        <ButtonGroup label="Whose cases">
          <Button size="sm" aria-pressed={!mineOnly} onClick={() => setMineOnly(false)}>
            All
          </Button>
          <Button size="sm" aria-pressed={mineOnly} onClick={() => setMineOnly(true)}>
            Mine
          </Button>
        </ButtonGroup>

        <ButtonGroup label="Priority">
          <Button size="sm" aria-pressed={priority === null} onClick={() => setPriority(null)}>
            Any
          </Button>
          {(['Low', 'Normal', 'High', 'Urgent'] as const).map((option) => (
            <Button
              key={option}
              size="sm"
              aria-pressed={priority === option}
              onClick={() => setPriority(option)}
            >
              {option}
            </Button>
          ))}
        </ButtonGroup>

        <Button
          size="sm"
          pill
          aria-pressed={breachedOnly}
          onClick={() => setBreachedOnly((current) => !current)}
        >
          Breached only
        </Button>

        <span className={styles.filterNote}>
          {worklist.data
            ? `${worklist.data.cases.length} open · ${worklist.data.breached} late · ${worklist.data.awaitingFirstResponse} unanswered`
            : 'reading the queue…'}
        </span>
      </div>

      <div className={styles.body}>
        <AsyncBoundary query={worklist} skeletonRows={6}>
          {(data) => (
            <>
              <div style={{ padding: '15px 21px 0' }}>
                <StatGrid columns={3}>
                  <StatTile label="Open" value={data.cases.length} note="in this filter" />
                  <StatTile
                    label="Breached"
                    value={data.breached}
                    direction={data.breached > 0 ? 'down' : 'flat'}
                    delta={data.breached > 0 ? 'needs attention' : 'none'}
                    note="response or resolution"
                  />
                  <StatTile
                    label="Awaiting first response"
                    value={data.awaitingFirstResponse}
                    note="nobody has replied"
                  />
                </StatGrid>
              </div>

              <DataTable
                caption="Open cases"
                columns={columns}
                rows={data.cases}
                rowKey={(row) => row.caseId}
                onRowClick={setPeek}
                isRowSelected={(row) => row.caseId === peek?.caseId}
                empty={
                  breachedOnly
                    ? 'Nothing in this filter is late. That is the answer, not an empty screen.'
                    : 'No open cases match this filter.'
                }
              />
            </>
          )}
        </AsyncBoundary>
      </div>

      {peek ? (
        <Drawer
          eyebrow={`#${peek.number}`}
          title={peek.subject}
          subtitle={`${peek.status} · ${peek.priority} · ${peek.ownerId}`}
          onClose={() => setPeek(null)}
          actions={
            <>
              {/*
                This screen is the console, so "open in" it is where the reader already is. An
                escalation is a capability the server does not carry.
              */}
              <Button tone="primary" onClick={() => setPeek(null)}>
                Close
              </Button>
              <Button disabled title="This build has no escalation capability.">
                Escalate
              </Button>
            </>
          }
        >
          <DrawerHighlights
            items={[
              {
                label: 'First response',
                value: peek.awaitingFirstResponse ? 'Awaiting' : 'Answered',
                tone: peek.responseBreached ? 'critical' : 'positive',
              },
              {
                label: 'Resolution',
                value: fromNow(peek.minutesToResolutionDue),
                tone: peek.resolutionBreached ? 'critical' : 'default',
              },
              { label: 'Opened', value: dateTime(peek.openedAt) },
            ]}
          />

          <DrawerSection label="Details" />
          <FieldRow label="Case number">#{peek.number}</FieldRow>
          <FieldRow label="Status">{peek.status}</FieldRow>
          <FieldRow label="Priority">{peek.priority}</FieldRow>
          <FieldRow label="Owner">{peek.ownerId}</FieldRow>
          <FieldRow label="First response due">{dateTime(peek.firstResponseDueAt)}</FieldRow>
          <FieldRow label="Resolution due">{dateTime(peek.resolutionDueAt)}</FieldRow>

          <DrawerSection label="Reply" note="only a public reply stops the response clock" />
          <ReplyForm
            busy={comment.isPending}
            onSend={(body, isPublic, status) => {
              comment.mutate(
                { caseId: peek.caseId, body, isPublic, status },
                {
                  onSuccess: (result) => {
                    toast.saved(
                      result.stoppedTheResponseClock
                        ? `Reply sent — the response clock stopped${result.breachedFirstResponse ? ', late' : ''}.`
                        : 'Note added. The response clock is still running.',
                    )
                    setPeek(null)
                  },
                  onError: (error) => toast.failed(error),
                },
              )
            }}
          />
        </Drawer>
      ) : null}
      {opening ? <NewCaseDrawer onClose={() => setOpening(false)} /> : null}
    </Page>
  )
}

function ReplyForm({
  busy,
  onSend,
}: {
  busy: boolean
  onSend: (body: string, isPublic: boolean, status: CaseStatus | null) => void
}) {
  const [body, setBody] = useState('')
  const [isPublic, setIsPublic] = useState(true)
  const [status, setStatus] = useState<CaseStatus | null>(null)

  return (
    <form
      className={styles.replyForm}
      onSubmit={(event) => {
        event.preventDefault()
        if (body.trim() === '') return
        onSend(body.trim(), isPublic, status)
        setBody('')
      }}
    >
      <TextAreaField
        label="What to say"
        value={body}
        onChange={(event) => setBody(event.target.value)}
        placeholder="Write the reply…"
      />
      <label className={styles.checkbox}>
        <input
          type="checkbox"
          checked={isPublic}
          onChange={(event) => setIsPublic(event.target.checked)}
        />
        The customer sees this
      </label>
      <div className={styles.replyRow}>
        <ButtonGroup label="Move the case">
          {(['Working', 'Waiting', 'Escalated', 'Closed'] as const).map((option) => (
            <Button
              key={option}
              size="sm"
              aria-pressed={status === option}
              onClick={() => setStatus((current) => (current === option ? null : option))}
            >
              {option}
            </Button>
          ))}
        </ButtonGroup>
        <Button type="submit" tone="primary" disabled={busy || body.trim() === ''}>
          {busy ? 'Sending…' : 'Send'}
        </Button>
      </div>
    </form>
  )
}
