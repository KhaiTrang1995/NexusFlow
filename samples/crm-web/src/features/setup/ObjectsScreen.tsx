import {
  AsyncBoundary,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  Tag,
} from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
import { schemaRows } from './schemaModel'
import type { SchemaRow } from './schemaModel'
import styles from './setup.module.css'

/**
 * Every object this tenant has, and what it carries.
 *
 * READ FROM `describe`, NOT FROM A LIST THIS CLIENT KEEPS. The whole claim the dynamic schema
 * makes is that an object declared at run time is indistinguishable from one this build ships —
 * and a setup screen listing seven objects it was compiled with would contradict that on the
 * first screen an administrator opens after declaring the eighth.
 */
export function ObjectsScreen() {
  const schema = useSchema()

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Objects" />

      <AsyncBoundary query={schema} skeletonRows={6}>
        {(description) => {
          const rows = schemaRows(description)

          return (
            <Panel padding="flush">
              <PanelHeader
                title="Declared objects"
                note={`${rows.length} · schema v${description.version}`}
              />
              <DataTable
                caption="Objects"
                rows={rows}
                rowKey={(row) => row.key}
                columns={[
                  {
                    id: 'label',
                    header: 'Object',
                    cell: (row: SchemaRow) => (
                      <>
                        <span className={styles.link}>{row.label}</span>
                        <div className={styles.mono}>{row.key}</div>
                      </>
                    ),
                    sortValue: (row: SchemaRow) => row.label,
                  },
                  {
                    id: 'origin',
                    header: 'Origin',
                    // The distinction that matters: one shipped with the build and the other was
                    // typed into a form. Everything else about them is the same on purpose.
                    cell: (row: SchemaRow) =>
                      row.builtIn ? (
                        <Tag>built in</Tag>
                      ) : (
                        <Tag tone="accent">declared at run time</Tag>
                      ),
                    sortValue: (row: SchemaRow) => String(row.builtIn),
                  },
                  {
                    id: 'fields',
                    header: 'Fields',
                    numeric: true,
                    cell: (row: SchemaRow) => row.fields.length,
                    sortValue: (row: SchemaRow) => row.fields.length,
                  },
                  {
                    id: 'readable',
                    header: 'Readable by you',
                    numeric: true,
                    // A count that is lower than the one beside it is field-level security doing
                    // its job, and an administrator should be able to see that it is.
                    cell: (row: SchemaRow) => row.fields.filter((field) => field.canRead).length,
                    sortValue: (row: SchemaRow) =>
                      row.fields.filter((field) => field.canRead).length,
                  },
                  {
                    id: 'views',
                    header: 'Saved views',
                    cell: (row: SchemaRow) =>
                      row.views > 0 ? (
                        <Tag tone="accent">{row.views}</Tag>
                      ) : (
                        <span className={styles.sub}>none</span>
                      ),
                  },
                ]}
                empty="This tenant has no objects, which should be impossible."
              />
            </Panel>
          )
        }}
      </AsyncBoundary>
    </Page>
  )
}
