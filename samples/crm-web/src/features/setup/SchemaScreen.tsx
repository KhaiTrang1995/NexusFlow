import { Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import styles from './setup.module.css'

/**
 * Every entity and the edges between them.
 *
 * THE EDGES ARE DERIVED FROM THE LOOKUPS, NOT DRAWN BY HAND. A diagram somebody maintains beside
 * the model is a diagram that is wrong within a month; reading the lookup fields means a
 * relationship added in setup appears here without anybody remembering to add it.
 */
export function SchemaScreen() {
  const objects = Object.values(OBJECT_MODELS)

  const edges = objects.flatMap((object) =>
    object.fields
      .filter((field) => field.type === 'lookup' && field.to && field.to !== 'User' && field.to !== 'Any')
      .map((field) => ({ from: object.label, to: field.to as string, via: field.label })),
  )

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Schema" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <PanelHeader title="Entities" note={`${objects.length}`} />
        <div className={styles.schema}>
          {objects.map((object) => (
            <div key={object.key} className={styles.entity}>
              <div className={styles.entityHead}>{object.label}</div>
              {object.fields.slice(0, 8).map((field) => (
                <div key={field.name} className={styles.entityField}>
                  <span>{field.label}</span>
                  <span className={styles.entityType}>{field.type}</span>
                </div>
              ))}
              {object.fields.length > 8 ? (
                <div className={styles.entityField}>
                  <span className={styles.sub}>+{object.fields.length - 8} more</span>
                </div>
              ) : null}
            </div>
          ))}
        </div>
      </Panel>

      <Panel padding="flush">
        <PanelHeader title="Relationships" note={`${edges.length} edges, read from the lookups`} />
        <div>
          {edges.map((edge) => (
            <div key={`${edge.from}-${edge.via}-${edge.to}`} className={styles.flowRow}>
              <Tag tone="accent">{edge.from}</Tag>
              <span className={styles.arrow} aria-hidden="true">
                →
              </span>
              <Tag tone="outline">{edge.to}</Tag>
              <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                via {edge.via}
              </span>
            </div>
          ))}
        </div>
      </Panel>
    </Page>
  )
}
