import { DataTable, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import type { ObjectModel } from '@/fixtures/objects'
import styles from './setup.module.css'

/** Every object, and what it carries. */
export function ObjectsScreen() {
  const objects = Object.values(OBJECT_MODELS)

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Objects" />

      <Panel padding="flush">
        <PanelHeader title="Declared objects" note={`${objects.length}`} />
        <DataTable
          caption="Objects"
          rows={objects}
          rowKey={(row) => row.key}
          columns={[
            {
              id: 'label',
              header: 'Object',
              cell: (row: ObjectModel) => (
                <>
                  <span className={styles.link}>{row.plural}</span>
                  <div className={styles.mono}>{row.key}</div>
                </>
              ),
              sortValue: (row: ObjectModel) => row.plural,
            },
            {
              id: 'fields',
              header: 'Fields',
              numeric: true,
              cell: (row: ObjectModel) => row.fields.length,
              sortValue: (row: ObjectModel) => row.fields.length,
            },
            {
              id: 'records',
              header: 'Records',
              numeric: true,
              cell: (row: ObjectModel) => row.records.length,
              sortValue: (row: ObjectModel) => row.records.length,
            },
            {
              id: 'path',
              header: 'Path',
              cell: (row: ObjectModel) =>
                row.stages ? (
                  <Tag tone="accent">{row.stages.length} stages</Tag>
                ) : (
                  <span className={styles.sub}>none</span>
                ),
            },
            {
              id: 'layout',
              header: 'Layout',
              cell: (row: ObjectModel) => `${row.layout.length} sections`,
            },
          ]}
        />
      </Panel>
    </Page>
  )
}
