import { AsyncBoundary, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
import { schemaEdges, schemaRows } from './schemaModel'
import styles from './setup.module.css'

/**
 * Every entity and the edges between them.
 *
 * THE EDGES ARE DERIVED FROM THE REFERENCES, NOT DRAWN BY HAND. A diagram somebody maintains
 * beside the model is a diagram that is wrong within a month; reading the reference fields means a
 * relationship added in setup appears here without anybody remembering to add it.
 *
 * THE FAR END WAS THE FIELD'S OWN NAME. `schemaEdges` resolves `references` — an object id — back
 * to the object's label, so an edge names something that exists. Before this the arrow pointed at
 * a string like `owner`, which is a field, not an entity.
 *
 * A FAILED READ IS NOT AN EMPTY SCHEMA. This screen had no boundary: a refused or dropped
 * `describe` left "Entities · reading…" above nothing and a relationship panel confidently
 * announcing nought edges, which is a claim about the tenant made from a request that never
 * answered.
 */
export function SchemaScreen() {
  const schema = useSchema()

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Schema" />

      <AsyncBoundary query={schema} skeletonRows={8}>
        {(description) => {
          const objects = schemaRows(description)
          const edges = schemaEdges(description)

          return (
            <>
              <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
                <PanelHeader
                  title="Entities"
                  note={`${objects.length} · schema v${description.version}`}
                />
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
                <PanelHeader
                  title="Relationships"
                  note={`${edges.length} edges, read from the reference fields`}
                />
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

                  {edges.length === 0 ? (
                    <p className={styles.sub} style={{ padding: 17 }}>
                      Nothing points at anything. A reference field on a custom object is what
                      draws an edge here.
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
