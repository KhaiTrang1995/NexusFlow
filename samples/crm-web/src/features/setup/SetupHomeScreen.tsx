import { useNavigate } from '@tanstack/react-router'
import { AsyncBoundary, Page, PageHeader, Panel, StatGrid, StatTile, Tag } from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
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
  const schema = useSchema()

  const described = schema.data

  // WHAT THIS TENANT HAS CONFIGURED, NOT WHAT THE PROTOTYPE HAS. These four tiles counted
  // `OBJECT_MODELS` — a table in this client — so every organisation was told it had 7 objects,
  // 65 fields, 5 stages and 19 layout sections, including one that had declared nothing at all.
  // Two of the old tiles are gone rather than reworded: no endpoint says how many objects have a
  // published path, and page layouts are not stored anywhere in this build, so both were numbers
  // with nothing behind them. The two that replaced them are the schema's own.
  const entities = described?.entities ?? []
  const objects = described?.objects ?? []

  const columns = entities.reduce((sum, entity) => sum + entity.columns.length, 0)

  const declared =
    entities.reduce((sum, entity) => sum + entity.fields.length, 0)
    + objects.reduce((sum, object) => sum + object.fields.length, 0)

  return (
    <Page>
      <PageHeader
        eyebrow="Setup"
        title="Configuration"
        actions={
          described === undefined ? null : (
            <Tag tone="outline">schema v{described.version}</Tag>
          )
        }
      />

      <AsyncBoundary query={schema} skeletonRows={2}>
        {() => (
          <StatGrid columns={4}>
            <StatTile
              label="Objects"
              value={entities.length + objects.length}
              note={`${entities.length} built in · ${objects.length} declared here`}
            />
            <StatTile label="Columns" value={columns} note="of the built-in tables" />
            <StatTile
              label="Custom fields"
              value={declared}
              note="added without a deployment"
            />
            <StatTile
              label="Saved views"
              value={objects.reduce((sum, object) => sum + object.views.length, 0)}
              note="named queries over custom objects"
            />
          </StatGrid>
        )}
      </AsyncBoundary>

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
