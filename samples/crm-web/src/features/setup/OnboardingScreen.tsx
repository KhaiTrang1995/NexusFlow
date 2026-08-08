import { useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import { Button, Columns, Page, PageHeader, Panel, PanelBody, PanelHeader, Skeleton, Tag } from '@/design/primitives'
import { useConfig, useProcess, useSchema } from '@/api/queries/hooks'
import { progress, stateOf } from './onboardingState'
import type { Evidence } from './onboardingState'
import styles from './setup.module.css'

interface Step {
  id: string
  title: string
  detail: string
  to: string
  /** What is not true until this step is done. */
  blocks: string
}

const STEPS: readonly Step[] = [
  { id: 'objects', title: 'Objects', detail: 'Decide what this organisation records. The seven built-in objects are already here; add the ones it has that nobody else does.', to: '/setup/objects', blocks: 'Nothing can be written until there is somewhere to write it.' },
  { id: 'fields', title: 'Fields', detail: 'Add the columns the built-in objects are missing.', to: '/setup/fields', blocks: 'A report can only group by a field that exists.' },
  { id: 'stages', title: 'Stages', detail: 'The path a deal walks, and the probability each step implies.', to: '/setup/stages', blocks: 'A weighted forecast needs a probability per stage.' },
  { id: 'layout', title: 'Layouts', detail: 'What a record page shows, section by section.', to: '/setup/layout', blocks: 'A field nobody can see is a field nobody fills in.' },
  { id: 'permissions', title: 'Permissions', detail: 'Who may read, write and administer what.', to: '/setup/permissions', blocks: 'Everybody is an administrator until this is set.' },
  { id: 'hours', title: 'Business hours and SLA', detail: 'The week the desk is open, and what it promises.', to: '/service/sla', blocks: 'Every promise is measured in calendar time until the week is declared.' },
  { id: 'approvals', title: 'Approvals', detail: 'Who has to say yes, in what order.', to: '/setup/approvals', blocks: 'Any discount can be sent by anybody.' },
]

/**
 * Standing a new organisation up.
 *
 * EACH STEP SAYS WHAT IS NOT TRUE UNTIL IT IS DONE. A wizard that just lists tasks gets abandoned
 * at step three; one that says "every promise is measured in calendar time until you do this" gets
 * finished, because the cost of skipping is on the screen rather than discovered in a report six
 * weeks later.
 *
 * WHAT IS DONE IS READ, NOT REMEMBERED. This held a list in component state seeded with two steps
 * already ticked, so every tenant arrived two-sevenths configured and ticking a third was
 * forgotten on the next page load. Each step is now a question the server already answers, and
 * the two with no evidence anywhere say so instead of being tickable.
 */
export function OnboardingScreen() {
  const navigate = useNavigate()
  const schema = useSchema()
  const process = useProcess('Opportunity')
  const hours = useConfig('BusinessHours')
  const policies = useConfig('SlaPolicy')
  const approvals = useConfig('ApprovalProcess')

  const [current, setCurrent] = useState(0)

  const step = STEPS[current]

  const evidence: Evidence = {
    objects: schema.data?.objects.length ?? 0,
    fields: (schema.data?.entities ?? []).reduce((sum, entity) => sum + entity.fields.length, 0),

    // The read 404s when nothing is published, which is the answer rather than a failure.
    hasProcess: process.isSuccess,
    businessHours: hours.data?.items.length ?? 0,
    slaPolicies: policies.data?.items.length ?? 0,
    approvals: approvals.data?.items.length ?? 0,
  }

  const counted = progress(STEPS.map((entry) => entry.id), evidence)
  const complete = counted.done === counted.knowable

  const loading =
    schema.isPending || hours.isPending || policies.isPending || approvals.isPending

  return (
    <Page>
      <PageHeader
        eyebrow="Setup"
        title="Onboarding"
        actions={
          <Tag tone={complete ? 'positive' : 'accent'}>
            {counted.done} of {counted.knowable} done
            {counted.unknowable > 0 ? ` · ${counted.unknowable} unknowable` : ''}
          </Tag>
        }
      />

      {loading ? <Skeleton rows={3} /> : null}

      <Panel padding="flush">
        <div className={styles.wizard}>
          {STEPS.map((entry, index) => (
            <button
              key={entry.id}
              type="button"
              className={styles.wizardStep}
              aria-current={index === current ? 'step' : undefined}
              onClick={() => setCurrent(index)}
            >
              <div
                className={stateOf(entry.id, evidence) === 'done' ? styles.wizardDone : undefined}
              >
                {stateOf(entry.id, evidence) === 'done'
                  ? '✓ '
                  : stateOf(entry.id, evidence) === 'unknowable'
                    ? '· '
                    : `${index + 1}. `}
                {entry.title}
              </div>
            </button>
          ))}
        </div>

        {step ? (
          <Columns layout="split">
            <PanelBody>
              <h2 className={styles.tileTitle}>{step.title}</h2>
              <p style={{ marginTop: 8, fontSize: 15 }}>{step.detail}</p>
              <div style={{ marginTop: 14, padding: 11, border: '1px solid var(--color-warning)', borderRadius: 9, background: 'var(--color-warning-bg)', color: 'var(--color-warning)', fontSize: 14 }}>
                <strong>
                  {stateOf(step.id, evidence) === 'done'
                    ? 'Done. It was blocking:'
                    : stateOf(step.id, evidence) === 'unknowable'
                      ? 'Nothing in this build records whether this is done. It blocks:'
                      : 'Until this is done:'}
                </strong>{' '}
                {step.blocks}
              </div>
              <div style={{ display: 'flex', gap: 9, marginTop: 16 }}>
                <Button tone="primary" onClick={() => void navigate({ to: step.to })}>
                  Open {step.title.toLowerCase()}
                </Button>
                {/*
                  No "mark as done". A tick was a note to itself that survived nothing and meant
                  nothing; doing the step is what marks it, because the step is a question about
                  the tenant rather than about this browser.
                */}
                <Button onClick={() => setCurrent((index) => Math.min(index + 1, STEPS.length - 1))}>
                  Next step
                </Button>
              </div>
            </PanelBody>

            <PanelBody>
              <PanelHeader title="The rest" note="in the order they unblock each other" />
              <ol style={{ paddingLeft: 20, display: 'grid', gap: 9, marginTop: 12 }}>
                {STEPS.map((entry) => (
                  <li
                    key={entry.id}
                    className={
                      stateOf(entry.id, evidence) === 'done' ? styles.wizardDone : undefined
                    }
                  >
                    <strong>{entry.title}</strong>
                    <div className={styles.sub}>{entry.blocks}</div>
                  </li>
                ))}
              </ol>
            </PanelBody>
          </Columns>
        ) : null}
      </Panel>
    </Page>
  )
}
