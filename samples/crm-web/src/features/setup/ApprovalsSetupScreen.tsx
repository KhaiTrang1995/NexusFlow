import { useState } from 'react'
import {
  Button,
  Columns,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Tag,
  TextField,
} from '@/design/primitives'
import { useDefineApprovalProcess } from '@/api/queries/hooks'
import type { ApprovalCriterion, ApprovalStepDefinition, ApprovalSubject, ApproverKind, GuardOperator } from '@/api/contracts'
import { useToast } from '@/app/ToastProvider'
import { DeclaredList } from '@/config/DeclaredList'
import styles from './setup.module.css'

const OPERATORS: readonly GuardOperator[] = ['Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet']

/** Which attributes each subject can be tested on. Mirrors `ApprovalAttributes.Of` on the server. */
const ATTRIBUTES: Readonly<Record<ApprovalSubject, readonly string[]>> = {
  Quote: ['discount', 'subtotal', 'total', 'status'],
  Opportunity: ['amount', 'probability', 'currency'],
  Plan: ['target_amount', 'kind'],
}

/**
 * Approval processes — who has to say yes, in what order.
 *
 * A NAMED STEP BREAKS WHEN SOMEBODY LEAVES; A MANAGER STEP DOES NOT. `SubmittersManager` reads the
 * reporting line the organisation already keeps, so one process works for every seller and keeps
 * working through a reorganisation that would strand every named step — including the ones on
 * requests already waiting.
 *
 * A SUBMITTER CAN NEVER APPROVE THEIR OWN REQUEST. Enforced on the server, on the capability, and
 * said here because it is the first thing an auditor asks and the last thing a home-grown approval
 * system implements.
 */
export function ApprovalsSetupScreen() {
  const toast = useToast()
  const define = useDefineApprovalProcess()

  const [name, setName] = useState('over_twenty_per_cent')
  const [label, setLabel] = useState('Discount over 20%')
  const [subject, setSubject] = useState<ApprovalSubject>('Quote')
  const [priority, setPriority] = useState('50')
  const [criteria, setCriteria] = useState<readonly ApprovalCriterion[]>([
    { attribute: 'discount', operator: 'GreaterThan', value: '20' },
  ])
  const [steps, setSteps] = useState<readonly ApprovalStepDefinition[]>([
    { label: 'Manager', kind: 'SubmittersManager', approver: null },
  ])

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Approvals" />

      <div style={{ marginBottom: 'var(--section-gap)' }}>
        <DeclaredList
          kind="ApprovalProcess"
          title="Approval processes"
          empty="Nothing needs approving. The form below changes that."
        />
      </div>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title="Declare a process" note="changed on a Tuesday, without a deployment" />
          <PanelBody>
            <form
              style={{ display: 'grid', gap: 12 }}
              onSubmit={(event) => {
                event.preventDefault()
                define.mutate(
                  {
                    name,
                    label,
                    subject,
                    priority: Number(priority),
                    criteria: [...criteria],
                    steps: [...steps],
                  },
                  {
                    onSuccess: (result) =>
                      toast.saved(`${label} declared — it asks for ${result.steps} approvals.`),
                    onError: (error) => toast.failed(error),
                  },
                )
              }}
            >
              <TextField label="Name" required value={name} onChange={(event) => setName(event.target.value)} />
              <TextField label="Label" required value={label} onChange={(event) => setLabel(event.target.value)} />
              <SelectField
                label="What it governs"
                value={subject}
                onChange={(event) => {
                  const next = event.target.value as ApprovalSubject
                  setSubject(next)
                  setCriteria([
                    { attribute: ATTRIBUTES[next][0] as string, operator: 'GreaterThan', value: '' },
                  ])
                }}
                options={[
                  { value: 'Quote', label: 'Quote' },
                  { value: 'Opportunity', label: 'Opportunity' },
                  { value: 'Plan', label: 'Plan' },
                ]}
              />
              <TextField
                label="Priority"
                type="number"
                hint="Lower runs first. The first matching process governs the request."
                value={priority}
                onChange={(event) => setPriority(event.target.value)}
              />

              <div>
                <div className={styles.sub} style={{ marginBottom: 6 }}>
                  Criteria — all of them must hold. Alternatives would be a second process.
                </div>
                {criteria.map((criterion, index) => (
                  <div key={index} style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr auto', gap: 8, marginBottom: 8 }}>
                    <SelectField
                      label="Attribute"
                      value={criterion.attribute}
                      onChange={(event) => patchCriterion(index, { attribute: event.target.value })}
                      options={ATTRIBUTES[subject].map((attribute) => ({ value: attribute, label: attribute }))}
                    />
                    <SelectField
                      label="Operator"
                      value={criterion.operator}
                      onChange={(event) =>
                        patchCriterion(index, { operator: event.target.value as GuardOperator })
                      }
                      options={OPERATORS.map((operator) => ({ value: operator, label: operator }))}
                    />
                    <TextField
                      label="Value"
                      disabled={criterion.operator === 'IsSet'}
                      value={criterion.value}
                      onChange={(event) => patchCriterion(index, { value: event.target.value })}
                    />
                    <Button
                      iconOnly
                      aria-label="Remove this criterion"
                      onClick={() => setCriteria((current) => current.filter((_, i) => i !== index))}
                    >
                      ✕
                    </Button>
                  </div>
                ))}
                <Button
                  size="sm"
                  onClick={() =>
                    setCriteria((current) => [
                      ...current,
                      { attribute: ATTRIBUTES[subject][0] as string, operator: 'Equals', value: '' },
                    ])
                  }
                >
                  Add a criterion
                </Button>
              </div>

              <div>
                <div className={styles.sub} style={{ marginBottom: 6 }}>
                  Steps — asked in this order. A rejection ends the request.
                </div>
                {steps.map((step, index) => (
                  <div key={index} style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr auto', gap: 8, marginBottom: 8 }}>
                    <TextField
                      label="Label"
                      value={step.label}
                      onChange={(event) => patchStep(index, { label: event.target.value })}
                    />
                    <SelectField
                      label="Approver"
                      value={step.kind}
                      onChange={(event) => {
                        const kind = event.target.value as ApproverKind
                        patchStep(index, { kind, approver: kind === 'SubmittersManager' ? null : '' })
                      }}
                      options={[
                        { value: 'SubmittersManager', label: 'Submitter’s manager' },
                        { value: 'Named', label: 'A named person' },
                        { value: 'RoleHolder', label: 'Anybody in a role' },
                      ]}
                    />
                    <TextField
                      label={step.kind === 'RoleHolder' ? 'Role' : 'Who'}
                      disabled={step.kind === 'SubmittersManager'}
                      value={step.approver ?? ''}
                      onChange={(event) => patchStep(index, { approver: event.target.value })}
                    />
                    <Button
                      iconOnly
                      aria-label="Remove this step"
                      disabled={steps.length === 1}
                      onClick={() => setSteps((current) => current.filter((_, i) => i !== index))}
                    >
                      ✕
                    </Button>
                  </div>
                ))}
                <Button
                  size="sm"
                  onClick={() =>
                    setSteps((current) => [
                      ...current,
                      { label: 'Finance', kind: 'RoleHolder', approver: 'finance' },
                    ])
                  }
                >
                  Add a step
                </Button>
              </div>

              <Button type="submit" tone="primary" disabled={define.isPending || steps.length === 0}>
                {define.isPending ? 'Saving…' : 'Declare the process'}
              </Button>
            </form>
          </PanelBody>
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="What this process does" note="read it back before you save it" />
          <PanelBody>
            <p style={{ fontSize: 15, marginBottom: 12 }}>
              When a <strong>{subject.toLowerCase()}</strong> is submitted and{' '}
              {criteria.length === 0 ? (
                <em>nothing is required of it</em>
              ) : (
                criteria.map((criterion, index) => (
                  <span key={index}>
                    {index > 0 ? ' and ' : ''}
                    <Tag tone="outline">
                      {criterion.attribute} {criterion.operator}{' '}
                      {criterion.operator === 'IsSet' ? '' : criterion.value}
                    </Tag>
                  </span>
                ))
              )}
              , it is held until:
            </p>
            <ol style={{ paddingLeft: 20, display: 'grid', gap: 8 }}>
              {steps.map((step, index) => (
                <li key={index}>
                  <strong>{step.label}</strong> —{' '}
                  {step.kind === 'SubmittersManager'
                    ? 'whoever the submitter reports to'
                    : step.kind === 'RoleHolder'
                      ? `anybody in the ${step.approver || '…'} role`
                      : step.approver || 'a named person'}
                </li>
              ))}
            </ol>
            <p className={styles.sub} style={{ marginTop: 14 }}>
              A step that resolves to nobody is refused at submission, not left waiting. A request
              waiting on nobody sits in a queue for a fortnight before anybody works out why it
              never moved.
            </p>
            <p className={styles.sub} style={{ marginTop: 8 }}>
              The person who submits can never be the person who approves — whatever this process
              says.
            </p>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )

  function patchCriterion(index: number, change: Partial<ApprovalCriterion>) {
    setCriteria((current) =>
      current.map((entry, i) => (i === index ? { ...entry, ...change } : entry)),
    )
  }

  function patchStep(index: number, change: Partial<ApprovalStepDefinition>) {
    setSteps((current) => current.map((entry, i) => (i === index ? { ...entry, ...change } : entry)))
  }
}
