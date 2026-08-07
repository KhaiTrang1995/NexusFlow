import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  DataTable,
  EmptyState,
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
export function PlanDetailPanels({ kind, period }: { kind: string; period: string }) {
  const tree = usePlanTree(period)
  const [chosen, setChosen] = useState<string | null>(null)

  const plans = (tree.data?.nodes ?? []).filter((node) => node.kind === kind)
  const name = chosen ?? plans[0]?.name ?? null
  const plan = usePlan(name)

  if (tree.isSuccess && plans.length === 0) {
    return (
      <Panel>
        <EmptyState
          title={`No ${kind.toLowerCase()} plan is committed for this period.`}
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
                note={
                  detail.targetAmount === null
                    ? `${detail.kind} plan · ${detail.period}`
                    : `${detail.kind} plan · ${detail.period} · ${fullMoney(
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

            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="Qualification"
                note={`${detail.qualification.filter((row) => row.isAnswered).length} of ${
                  detail.qualification.length
                } answered`}
              />
              <DataTable
                caption="Qualification"
                rows={detail.qualification}
                rowKey={(row) => row.element}
                columns={[
                  {
                    id: 'element',
                    header: 'Element',
                    cell: (row: PlanQualificationRow) => row.element,
                  },
                  {
                    id: 'answered',
                    // Answered or not — never a rating. A number a representative chooses is a
                    // number they choose to be comfortable with.
                    header: 'Known',
                    cell: (row: PlanQualificationRow) =>
                      row.isAnswered ? <Tag tone="positive">yes</Tag> : <Tag tone="outline">not yet</Tag>,
                  },
                  {
                    id: 'note',
                    header: 'What is known',
                    cell: (row: PlanQualificationRow) => (
                      <span className={styles.sub}>{row.note}</span>
                    ),
                  },
                ]}
                empty="Nothing has been qualified."
              />
            </Panel>

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
