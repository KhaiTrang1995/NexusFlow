import { useNavigate } from '@tanstack/react-router'
import { Page, PageHeader, Panel, StatGrid, StatTile, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import styles from './setup.module.css'

interface SetupCard {
  title: string
  detail: string
  to: string
  tag?: string
}

const CARDS: readonly SetupCard[] = [
  { title: 'Objects', detail: 'Entities this build has never heard of, declared at run time.', to: '/setup/objects' },
  { title: 'Fields', detail: 'A column on a built-in entity or on a custom object.', to: '/setup/fields' },
  { title: 'Page layouts', detail: 'What a record page shows, section by section.', to: '/setup/layout' },
  { title: 'List views', detail: 'A named query, its fields checked when it is saved.', to: '/setup/list-views' },
  { title: 'Stages', detail: 'The path a record walks, and what each step implies.', to: '/setup/stages' },
  { title: 'Schema', detail: 'Every entity and the edges between them.', to: '/setup/schema' },
  { title: 'Permissions', detail: 'Who may read, write and administer what.', to: '/setup/permissions' },
  { title: 'Flows', detail: 'What happens after a record is written.', to: '/setup/flows' },
  { title: 'Approvals', detail: 'Who has to say yes, in what order.', to: '/setup/approvals', tag: 'live' },
  { title: 'Validation rules', detail: 'Refused when it holds, in the administrator’s own words.', to: '/setup/validation' },
  { title: 'Data quality', detail: 'What is missing, stale, or duplicated.', to: '/setup/quality' },
  { title: 'Onboarding', detail: 'Stand a new organisation up in seven steps.', to: '/setup/onboarding' },
]

/**
 * Setup — everything an administrator changes without a deployment.
 *
 * THE CARDS ARE A TABLE. Adding a setup area is a row, not a screen with a hand-placed tile —
 * which is what keeps this page from drifting out of step with the routes behind it.
 */
export function SetupHomeScreen() {
  const navigate = useNavigate()
  const objects = Object.values(OBJECT_MODELS)
  const fields = objects.reduce((sum, object) => sum + object.fields.length, 0)

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Configuration" />

      <StatGrid columns={4}>
        <StatTile label="Objects" value={objects.length} note="built-in and custom" />
        <StatTile label="Fields" value={fields} note="across every object" />
        <StatTile label="Stages" value={objects.filter((object) => object.stages).length} note="objects with a path" />
        <StatTile label="Layouts" value={objects.reduce((sum, object) => sum + object.layout.length, 0)} note="sections" />
      </StatGrid>

      <div className={styles.tiles}>
        {CARDS.map((card) => (
          <Panel key={card.title} onActivate={() => void navigate({ to: card.to })}>
            <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
              <span className={styles.tileTitle}>{card.title}</span>
              {card.tag ? <Tag tone="positive">{card.tag}</Tag> : null}
            </div>
            <p className={styles.sub} style={{ marginTop: 5 }}>
              {card.detail}
            </p>
          </Panel>
        ))}
      </div>
    </Page>
  )
}
