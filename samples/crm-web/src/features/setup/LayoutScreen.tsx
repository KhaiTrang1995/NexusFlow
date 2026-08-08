import { useState } from 'react'
import {
  AsyncBoundary,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  Tag,
} from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
import { schemaRowOf } from './schemaModel'
import type { SchemaFieldRow } from './schemaModel'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

/**
 * What there is to lay out, and why none of it is laid out here.
 *
 * NOTHING IN THIS BUILD STORES A PAGE LAYOUT. There is no flow that accepts one — the manifest
 * has `crm.custom.object`, `crm.custom.field`, `crm.custom.list_view` and no layout of any kind —
 * and `describe` answers with fields, not with sections. So a layout screen has nothing of the
 * tenant's to read and nothing to write.
 *
 * WHAT USED TO BE HERE WAS THE PROTOTYPE'S. Three sections and twelve fields out of
 * `fixtures/objects` — "Forecast Category", "Weighted Amount", "Next Step" — shown as
 * "Opportunity layout · 3 sections" to every organisation including one that had declared
 * nothing, each chip carrying a drag handle and `draggable`. Nothing was ever saved, because
 * there was nowhere to save it and nothing that tried. An administrator who dragged a field and
 * came back the next day would find it where they left it, which reads as the save having failed
 * rather than as never having been offered.
 *
 * The object switcher was broken along with it: it sets a key from `describe` — `Opportunity`,
 * `project` — and this screen looked it up in a table keyed `opportunity`, so every object that
 * was not the default fell through `modelFor`'s `?? OBJECT_MODELS['account']` and was drawn as an
 * account.
 */
export function LayoutScreen() {
  const [objectKey, setObjectKey] = useState('Opportunity')
  const schema = useSchema()

  const owner = schemaRowOf(schema.data, objectKey)

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Page layout" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher value={objectKey} onChange={setObjectKey} />
      </Panel>

      <Panel style={{ marginBottom: 'var(--section-gap)' }}>
        <PanelBody>
          <p className={styles.sub}>
            <strong>This build does not store page layouts.</strong> No endpoint accepts one and{' '}
            <span className={styles.mono}>describe</span> returns none, so there is no arrangement
            of this organisation&rsquo;s to show and nothing on this screen writes anything. What a
            record page draws is decided by the build and is the same for every organisation —
            which is a real limitation of the sample, and better said than dressed up as a
            drag-and-drop editor that quietly kept nothing.
          </p>
        </PanelBody>
      </Panel>

      <AsyncBoundary query={schema} skeletonRows={6}>
        {() => (
          <Panel padding="flush">
            <PanelHeader
              title={`${owner?.label ?? objectKey} — what there is to lay out`}
              note={`${owner?.fields.length ?? 0} field(s), in the order the schema describes them`}
            />
            <DataTable
              caption={`${owner?.label ?? objectKey} fields`}
              rows={owner?.fields ?? []}
              rowKey={(row) => row.name}
              columns={[
                {
                  id: 'label',
                  header: 'Field',
                  cell: (row: SchemaFieldRow) => (
                    <>
                      <span className={styles.link}>{row.label}</span>
                      <div className={styles.mono}>{row.name}</div>
                    </>
                  ),
                },
                {
                  id: 'origin',
                  header: 'Origin',
                  cell: (row: SchemaFieldRow) =>
                    row.declared ? (
                      <Tag tone="accent">declared here</Tag>
                    ) : (
                      <Tag>a column of the table</Tag>
                    ),
                },
                {
                  id: 'required',
                  header: 'Required',
                  cell: (row: SchemaFieldRow) =>
                    row.required ? <Tag tone="accent">yes</Tag> : '—',
                },
                {
                  id: 'visible',
                  // Resolved by the server for this caller. A field this token may not read is a
                  // field no page can put on a layout for them, which is the one thing about
                  // what a person sees that this build genuinely does decide per caller.
                  header: 'Visible to you',
                  cell: (row: SchemaFieldRow) =>
                    row.canRead ? (
                      <span className={styles.sub}>yes</span>
                    ) : (
                      <Tag tone="critical">no read</Tag>
                    ),
                },
              ]}
              empty="Nothing is declared on this object, and its columns are the table's."
            />
          </Panel>
        )}
      </AsyncBoundary>
    </Page>
  )
}
