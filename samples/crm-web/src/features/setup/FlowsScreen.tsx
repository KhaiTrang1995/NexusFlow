import { Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import styles from './setup.module.css'

interface FlowRow {
  name: string
  trigger: string
  profile: 'Durable' | 'Ephemeral'
  steps: readonly string[]
  note: string
}

/**
 * What happens after a record is written.
 *
 * DURABLE MEANS THE STEP SURVIVES A RESTART. It is the one property worth showing on this screen:
 * a flow that stages an event and a flow that reads a report look identical until the process
 * dies halfway through, and only one of them is still correct afterwards.
 */
const FLOWS: readonly FlowRow[] = [
  { name: 'crm.lead.capture', trigger: 'POST /leads', profile: 'Durable', steps: ['Write the lead', 'Stage lead.created'], note: 'One write and one event, in the same transaction.' },
  { name: 'crm.lead.score', trigger: 'lead.created', profile: 'Durable', steps: ['Score', 'Write back'], note: 'One of three subscriptions on a single publish.' },
  { name: 'crm.lead.assign', trigger: 'lead.created', profile: 'Durable', steps: ['Route by territory', 'Assign owner'], note: 'Independent redelivery from the other two.' },
  { name: 'crm.lead.enrich', trigger: 'lead.created', profile: 'Durable', steps: ['Call the connector', 'Merge the answer'], note: 'Queued, not sent inline.' },
  { name: 'crm.lead.convert', trigger: 'POST /lead-conversions', profile: 'Durable', steps: ['Account', 'Contact', 'Opportunity'], note: 'A saga whose compensations take the step’s input.' },
  { name: 'crm.quote.issue', trigger: 'POST /quotes', profile: 'Durable', steps: ['Price', 'Decide Draft or Issued'], note: 'The discount threshold decides which.' },
  { name: 'crm.approval.submit', trigger: 'POST /approvals/requests', profile: 'Durable', steps: ['Match a process', 'Resolve every approver', 'Open the request'], note: 'Approvers are resolved at submission, not at decision.' },
  { name: 'crm.service.comment', trigger: 'POST /service/comments', profile: 'Durable', steps: ['Write the comment', 'Stop the clock', 'Move the case'], note: 'Only a public reply from somebody else stops the clock.' },
  { name: 'crm.escalation.sweep', trigger: 'cron 0 * * * *', profile: 'Durable', steps: ['Find overdue', 'Escalate once'], note: 'One statement; escalates once per window.' },
  { name: 'crm.delivery.sweep', trigger: 'cron * * * * *', profile: 'Durable', steps: ['Claim a batch', 'Send', 'Record'], note: 'FOR UPDATE SKIP LOCKED, so two replicas do not double-send.' },
  { name: 'crm.board', trigger: 'POST /board', profile: 'Ephemeral', steps: ['Read everything at once'], note: 'One request so two numbers cannot disagree.' },
]

export function FlowsScreen() {
  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Flows" />

      <Panel padding="flush">
        <PanelHeader title="Declared flows" note={`${FLOWS.length}`} />
        <div>
          {FLOWS.map((flow) => (
            <div key={flow.name} style={{ borderBottom: '1px solid var(--color-divider)' }}>
              <div className={styles.flowRow} style={{ borderBottom: 0, paddingBottom: 4 }}>
                <span className={styles.mono}>{flow.name}</span>
                <Tag tone={flow.profile === 'Durable' ? 'positive' : 'outline'}>{flow.profile}</Tag>
                <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                  {flow.trigger}
                </span>
              </div>
              <div className={styles.flowRow} style={{ borderBottom: 0, paddingTop: 0 }}>
                {flow.steps.map((step, index) => (
                  <span key={step} style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
                    {index > 0 ? (
                      <span className={styles.arrow} aria-hidden="true">
                        →
                      </span>
                    ) : null}
                    <span className={styles.flowStep}>{step}</span>
                  </span>
                ))}
              </div>
              <p className={styles.sub} style={{ padding: '0 17px 10px' }}>
                {flow.note}
              </p>
            </div>
          ))}
        </div>
      </Panel>
    </Page>
  )
}
