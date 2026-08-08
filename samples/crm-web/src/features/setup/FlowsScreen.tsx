import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
  TextField,
} from '@/design/primitives'
import { useManifest } from '@/api/queries/hooks'
import type { ManifestFlow, ManifestTrigger } from '@/api/contracts'
import styles from './setup.module.css'

type Lens = 'all' | 'Durable' | 'Ephemeral'

/**
 * Every flow this application declares, read from the manifest the compiler wrote.
 *
 * THIS SCREEN WAS A LIST SOMEBODY TYPED. Eleven flows, of which five named flows that do not
 * exist — `crm.lead.assign` for `crm.lead.assignment`, `crm.escalation.sweep` for
 * `crm.task.escalation` — against eighty-four that do. A hand-written inventory of a generated
 * thing is wrong from the first commit that adds one, and nothing ever says so.
 *
 * NOT THE OPENAPI DOCUMENT, AND THAT IS WHY THE MANIFEST IS SERVED. OpenAPI describes an HTTP
 * surface: it has nowhere to put a flow's execution profile, and it omits every trigger that is
 * not a path — the subscriptions, the schedules, the agent tools. Half of what runs here has no
 * route at all.
 *
 * DURABLE IS THE PROPERTY WORTH SHOWING. A flow that stages an event and a flow that reads a
 * report look identical until the process dies halfway through, and only one of them is still
 * correct afterwards.
 */
export function FlowsScreen() {
  const manifest = useManifest()

  const [lens, setLens] = useState<Lens>('all')
  const [phrase, setPhrase] = useState('')

  return (
    <Page>
      <PageHeader
        eyebrow="Setup"
        title="Flows"
        actions={
          <ButtonGroup label="Profile">
            {(['all', 'Durable', 'Ephemeral'] as const).map((option) => (
              <Button key={option} aria-pressed={lens === option} onClick={() => setLens(option)}>
                {option === 'all' ? 'All' : option}
              </Button>
            ))}
          </ButtonGroup>
        }
      />

      <AsyncBoundary query={manifest} skeletonRows={8}>
        {(document) => {
          const durable = document.flows.filter((flow) => flow.profile === 'Durable').length

          const shown = document.flows
            .filter((flow) => lens === 'all' || flow.profile === lens)
            .filter((flow) => flow.id.includes(phrase.trim().toLowerCase()))

          return (
            <>
              <StatGrid columns={4}>
                <StatTile label="Flows" value={document.flows.length} note="declared" />
                <StatTile
                  label="Durable"
                  value={durable}
                  note="survive a restart"
                  direction="up"
                />
                <StatTile
                  label="Ephemeral"
                  value={document.flows.length - durable}
                  note="write nothing worth resuming"
                />
                <StatTile
                  label="Manifest"
                  value={document.application.version}
                  note={`${document.application.name} · schema ${document.schemaVersion}`}
                />
              </StatGrid>

              <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
                <div style={{ padding: 12 }}>
                  <TextField
                    label="Find a flow"
                    hideLabel
                    placeholder="Filter by id — crm.lead, crm.approval…"
                    value={phrase}
                    onChange={(event) => setPhrase(event.target.value)}
                  />
                </div>
              </Panel>

              <Panel padding="flush">
                <PanelHeader
                  title="Declared flows"
                  note={`${shown.length} of ${document.flows.length}`}
                />
                <div>
                  {shown.map((flow) => (
                    <div key={flow.id} className={styles.flowCard}>
                      <div className={styles.flowRow} style={{ borderBottom: 0, paddingBottom: 4 }}>
                        <span className={styles.mono}>{flow.id}</span>
                        <Tag tone={flow.profile === 'Durable' ? 'positive' : 'outline'}>
                          {flow.profile}
                        </Tag>
                        {flow.deadline !== undefined ? (
                          <Tag tone="neutral">{flow.deadline}</Tag>
                        ) : null}
                        <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                          {flow.errors?.length ?? 0} declared error(s)
                        </span>
                      </div>
                      <div className={styles.flowRow} style={{ borderBottom: 0, paddingTop: 0 }}>
                        {flow.triggers.map((trigger, index) => (
                          <span key={`${flow.id}-${index}`} className={styles.flowStep}>
                            {describe(trigger)}
                          </span>
                        ))}
                        {flow.triggers.length === 0 ? (
                          <span className={styles.sub}>
                            No trigger — nothing reaches this flow.
                          </span>
                        ) : null}
                      </div>
                    </div>
                  ))}

                  {shown.length === 0 ? (
                    <p className={styles.sub} style={{ padding: 17 }}>
                      Nothing matches. {document.flows.length} flows are declared.
                    </p>
                  ) : null}
                </div>
              </Panel>
            </>
          )
        }}
      </AsyncBoundary>
    </Page>
  )
}

/**
 * One trigger, in the words its kind uses.
 *
 * A subscription has a topic and no route; a schedule has a cron expression and neither. Drawing
 * them all as a method and a path is how a screen ends up showing eight flows that appear to have
 * no way in — this application has three bus subscriptions, four schedules, one change
 * subscription and one agent tool.
 */
function describe(trigger: ManifestTrigger): string {
  switch (trigger.kind) {
    case 'Http':
      return `${trigger.method ?? 'POST'} ${trigger.route ?? '—'}`
    case 'Bus':
      return `on ${trigger.topic ?? '—'}`
    case 'Change':
      // The database emitted it, not a flow. Same shape as a bus subscription and a different
      // fact about where the work comes from.
      return `on change ${trigger.topic ?? '—'}`
    case 'Schedule':
      return `cron ${trigger.cron ?? '—'} ${trigger.timeZone ?? 'UTC'}`
    case 'Agent':
      return 'agent tool'
    default:
      // A kind this build does not know is named rather than hidden: the manifest is generated,
      // so a new kind means the compiler grew one and this screen should say it saw it.
      return trigger.kind
  }
}

/** Exported for the test: the profile filter is the one thing on this screen with a rule in it. */
export function matching(flows: readonly ManifestFlow[], lens: Lens, phrase: string) {
  return flows
    .filter((flow) => lens === 'all' || flow.profile === lens)
    .filter((flow) => flow.id.includes(phrase.trim().toLowerCase()))
}
