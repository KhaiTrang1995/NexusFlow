import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  DataTable,
  EmptyState,
  ErrorState,
  Panel,
  PanelHeader,
  Tag,
} from '@/design/primitives'
import { usePlan, usePlanTree } from '@/api/queries/hooks'
import type {
  PlanObjectiveRow,
  PlanQualificationRow,
  PlanRiskRow,
  PlanStakeholderRow,
  PlanStepRow,
} from '@/api/contracts'
import { fullMoney } from '@/lib/format'
import type { PeriodChoice } from '@/features/exec/period'
import { PeriodGate, hasNoPeriod } from './PeriodGate'
import { AnswerQualificationDrawer } from './AnswerQualificationDrawer'
import { qualificationOf } from './qualification'
import type { QualificationLine } from './qualification'
import styles from './planning.module.css'

/**
 * The tenant's own plans of one kind, and everything under whichever is chosen.
 *
 * ONE COMPONENT FOR THE ACCOUNT AND DEAL SCREENS. They differ in which plans they list and in
 * nothing else: the objectives, the mutual action plan, the risks, the qualification and the
 * stakeholders are the same five things about a different subject.
 *
 * THE STEP'S "OVERDUE" IS THE SERVER'S. It arrives decided, against one clock — a client
 * comparing a due date against its own reports a step late in Sydney and not in Lisbon on the
 * same afternoon, and an overdue step is the earliest signal a deal has stopped moving.
 */
/**
 * What a plan kind is called in a sentence.
 *
 * `MarketingLead` lower-cased is "marketinglead", which is what the empty state read. A closed
 * vocabulary of four is a table, not a transformation.
 *
 * AND IT IS THE WORD THE REST OF THE APPLICATION USES, NOT THE SERVER'S ENUM. An
 * `Opportunity` plan is a deal plan everywhere a person reads one — the heading over this panel,
 * the portfolio's readiness column, the button that navigates here. "No opportunity plan is
 * committed" under a heading reading Deal plans is one thing with two names.
 */
function label(kind: string): string {
  switch (kind) {
    case 'MarketingLead':
      return 'demand'
    case 'Opportunity':
      return 'deal'
    case 'Rollup':
      return 'roll-up'
    default:
      return kind.toLowerCase()
  }
}

export function PlanDetailPanels({ kind, choice }: { kind: string; choice: PeriodChoice }) {
  const tree = usePlanTree(choice.period)
  const [chosen, setChosen] = useState<string | null>(null)

  const plans = (tree.data?.nodes ?? []).filter((node) => node.kind === kind)
  const name = chosen ?? plans[0]?.name ?? null
  const plan = usePlan(name)

  if (hasNoPeriod(choice)) {
    return (
      <Panel>
        <PeriodGate choice={choice} what={`${label(kind)} plans`} />
      </Panel>
    )
  }

  // THE SCREEN USED TO RENDER NOTHING AT ALL HERE. When the tree read failed the component fell
  // through to a plan query that was disabled — pending for ever — and a skeleton is
  // `aria-hidden`, so the whole page was a heading and 19 characters of white space. A failed
  // read that produces a blank screen is indistinguishable from a broken build.
  if (tree.isError) {
    return (
      <Panel>
        <ErrorState error={tree.error} onRetry={tree.refetch} />
      </Panel>
    )
  }

  if (tree.isSuccess && plans.length === 0) {
    return (
      <Panel>
        <EmptyState
          title={`No ${label(kind)} plan is committed for this period.`}
          detail="A plan is committed against a period; nothing here is a prototype, it is an empty tenant."
        />
      </Panel>
    )
  }

  return (
    <>
      {plans.length > 1 ? (
        <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
          <PanelHeader title="Plans" note={`${plans.length} committed this period`} />
          <div className={styles.planStrip}>
            <ButtonGroup label="Which plan">
              {plans.map((node) => (
                <Button
                  key={node.name}
                  size="sm"
                  pill
                  aria-pressed={node.name === name}
                  onClick={() => setChosen(node.name)}
                >
                  {node.label}
                </Button>
              ))}
            </ButtonGroup>
          </div>
        </Panel>
      ) : null}

      <AsyncBoundary query={plan} skeletonRows={8}>
        {(detail) => (
          <>
            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title={detail.label}
                // The same word the empty state uses. `detail.kind` raw reads "MarketingLead
                // plan" on the screen whose own heading says Demand plans, and a reader with two
                // names for one thing has to work out whether they are the same thing.
                note={
                  detail.targetAmount === null
                    ? `${label(detail.kind)} plan · ${detail.period}`
                    : `${label(detail.kind)} plan · ${detail.period} · ${fullMoney(
                        detail.targetAmount,
                      )} ${detail.currency ?? ''} committed`
                }
              />
              <DataTable
                caption="Objectives"
                rows={detail.objectives}
                rowKey={(row) => String(row.ordinal)}
                columns={[
                  {
                    id: 'description',
                    header: 'Objective',
                    cell: (row: PlanObjectiveRow) => row.description,
                  },
                  {
                    id: 'target',
                    header: 'Target',
                    numeric: true,
                    cell: (row: PlanObjectiveRow) => `${row.target} ${row.measure.toLowerCase()}`,
                  },
                  {
                    id: 'status',
                    header: 'Status',
                    cell: (row: PlanObjectiveRow) => <Tag tone="accent">{row.status}</Tag>,
                  },
                ]}
                empty="No objectives are set, so the plan commits a number and nothing else."
              />
            </Panel>

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="Mutual action plan"
                note={`${detail.steps.filter((step) => step.isOverdue).length} overdue`}
              />
              <DataTable
                caption="Steps"
                rows={detail.steps}
                rowKey={(row) => String(row.ordinal)}
                columns={[
                  {
                    id: 'description',
                    header: 'Step',
                    cell: (row: PlanStepRow) => row.description,
                  },
                  { id: 'due', header: 'Due', cell: (row: PlanStepRow) => row.dueOn },
                  {
                    id: 'state',
                    header: 'State',
                    // Three states and not two: done, late, and neither. A screen with a single
                    // "complete" tick makes the late ones look the same as the ones with time.
                    cell: (row: PlanStepRow) =>
                      row.isComplete ? (
                        <Tag tone="positive">done</Tag>
                      ) : row.isOverdue ? (
                        <Tag tone="critical">overdue</Tag>
                      ) : (
                        <Tag tone="outline">open</Tag>
                      ),
                  },
                ]}
                empty="Nothing has been agreed with the customer."
              />
            </Panel>

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="Risks"
                note={`${detail.risks.filter((risk) => risk.isOpen).length} open`}
              />
              <DataTable
                caption="Risks"
                rows={detail.risks}
                rowKey={(row) => String(row.ordinal)}
                columns={[
                  { id: 'description', header: 'Risk', cell: (row: PlanRiskRow) => row.description },
                  {
                    id: 'severity',
                    header: 'Severity',
                    cell: (row: PlanRiskRow) => (
                      <Tag tone={row.severity === 'Low' ? 'neutral' : 'critical'}>{row.severity}</Tag>
                    ),
                  },
                  {
                    id: 'mitigation',
                    header: 'What is being done',
                    cell: (row: PlanRiskRow) => (
                      <span className={styles.sub}>{row.mitigation}</span>
                    ),
                  },
                  {
                    id: 'open',
                    header: 'Open',
                    cell: (row: PlanRiskRow) => (row.isOpen ? <Tag tone="critical">yes</Tag> : '—'),
                  },
                ]}
                empty="No risks are recorded, which is either good news or an unfinished plan."
              />
            </Panel>

            <QualificationPanel plan={detail.name} rows={detail.qualification} />

            <Panel padding="flush">
              <PanelHeader
                title="Stakeholders"
                note={`${detail.stakeholders.length} mapped`}
              />
              <DataTable
                caption="Stakeholders"
                rows={detail.stakeholders}
                rowKey={(row) => row.contactId}
                columns={[
                  {
                    id: 'name',
                    header: 'Person',
                    cell: (row: PlanStakeholderRow) => row.fullName,
                  },
                  { id: 'role', header: 'Role', cell: (row: PlanStakeholderRow) => row.role },
                  {
                    id: 'sentiment',
                    header: 'Sentiment',
                    cell: (row: PlanStakeholderRow) => (
                      <Tag
                        tone={
                          row.sentiment === 'Opposed' || row.sentiment === 'Sceptical'
                            ? 'critical'
                            : 'positive'
                        }
                      >
                        {row.sentiment}
                      </Tag>
                    ),
                  },
                  {
                    id: 'influence',
                    header: 'Influence',
                    numeric: true,
                    cell: (row: PlanStakeholderRow) => row.influence,
                  },
                ]}
                empty="Nobody is mapped, so the plan does not say who decides."
              />
            </Panel>
          </>
        )}
      </AsyncBoundary>
    </>
  )
}

/**
 * The eight questions, whether or not anybody has answered them.
 *
 * THE DENOMINATOR WAS THE NUMBER OF ROWS THE SERVER HAPPENED TO SEND. `/planning/plan` returns
 * only what has been recorded, so an unqualified deal arrived as an empty list and this panel
 * read "0 of 0 answered" — the sentence a finished checklist produces — while the portfolio's
 * deal readiness column, which divides by the vocabulary, read 0/8 about the same deal.
 *
 * AND THERE WAS NO WAY TO ANSWER ONE. `/planning/qualifications` has been there throughout and
 * needs only `crm.write`, which every persona in this sample holds; this screen read the answers
 * and offered no control that recorded one, so the gap it reports could only ever grow.
 *
 * THE MUTUAL ACTION PLAN ABOVE STILL HAS NO CONTROL, AND THAT IS DELIBERATE. `SetPlanStep` is an
 * upsert of the whole row including its owner, and `PlanDetail` does not return the step's owner
 * — so a "mark done" tick would have to invent one, and would quietly reassign the step to
 * whoever pressed it. A control that writes the wrong thing is worse than no control.
 */
function QualificationPanel({ plan, rows }: { plan: string; rows: readonly PlanQualificationRow[] }) {
  const [answering, setAnswering] = useState<QualificationLine | null>(null)
  const lines = qualificationOf(rows)
  const answered = lines.filter((line) => line.isAnswered).length

  return (
    <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
      <PanelHeader
        title="Qualification"
        note={`${answered} of ${lines.length} answered`}
      />
      <DataTable
        caption="Qualification"
        rows={lines}
        rowKey={(row) => row.element}
        columns={[
          {
            id: 'element',
            header: 'Element',
            cell: (row: QualificationLine) => (
              <>
                <span>{row.label}</span>
                {row.asks === '' ? null : <div className={styles.sub}>{row.asks}</div>}
              </>
            ),
          },
          {
            id: 'answered',
            // Answered or not — never a rating. A number a representative chooses is a number
            // they choose to be comfortable with.
            header: 'Known',
            cell: (row: QualificationLine) =>
              row.isAnswered ? <Tag tone="positive">yes</Tag> : <Tag tone="outline">not yet</Tag>,
          },
          {
            id: 'note',
            header: 'What is known',
            // Three states, not two. Nothing recorded is a question nobody has been asked;
            // recorded with an empty note is somebody having looked and written nothing down.
            cell: (row: QualificationLine) => (
              <span className={styles.sub}>
                {row.isRecorded ? row.note : 'Nobody has recorded an answer.'}
              </span>
            ),
          },
          {
            id: 'record',
            header: '',
            cell: (row: QualificationLine) =>
              row.isDeclared ? (
                <Button size="sm" onClick={() => setAnswering(row)}>
                  {row.isRecorded ? 'Change' : 'Answer'}
                </Button>
              ) : (
                <span className={styles.sub}>from a newer server</span>
              ),
          },
        ]}
        empty="This build knows of no qualification elements, which cannot happen."
      />

      {answering === null ? null : (
        <AnswerQualificationDrawer
          plan={plan}
          line={answering}
          onClose={() => setAnswering(null)}
        />
      )}
    </Panel>
  )
}
