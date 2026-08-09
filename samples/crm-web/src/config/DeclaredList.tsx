import { AsyncBoundary, DataTable, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useConfig } from '@/api/queries/hooks'
import type { ConfigItem, ConfigKind } from '@/api/contracts'
import styles from './DeclaredList.module.css'

/**
 * What this tenant has declared of one kind, and what each one does.
 *
 * ONE COMPONENT FOR TWELVE SCREENS, because the server answers the same four columns for all of
 * them. A table per kind would be twelve tables differing in nothing but a heading, and the
 * twelfth would be written by somebody copying the eleventh.
 *
 * NOT `setup`'S, THOUGH IT WAS WRITTEN THERE. Service declares business hours and SLA policies,
 * analytics declares dashboards, and `ConfigKind` is the server's vocabulary rather than one
 * screen's — so three features imported it and two of them reached across a feature boundary to
 * do it.
 *
 * THE SENTENCE IS THE SERVER'S. `summary` says what a rule refuses or what a policy promises,
 * composed where the columns are — so this component never has to know that an SLA policy has a
 * first-response clock or that a list view has an operator.
 */
export function DeclaredList({
  kind,
  title,
  note,
  empty,
}: {
  kind: ConfigKind
  title: string
  note?: string | undefined
  empty: string
}) {
  const config = useConfig(kind)

  return (
    <AsyncBoundary query={config} skeletonRows={4}>
      {(list) => (
        <Panel padding="flush">
          <PanelHeader
            title={title}
            note={note ?? `${list.items.length} declared`}
          />
          <DataTable
            caption={title}
            rows={list.items}
            rowKey={(row) => row.name}
            columns={[
              {
                id: 'label',
                header: 'Name',
                cell: (row: ConfigItem) => (
                  <>
                    <span className={styles.link}>{row.label}</span>
                    <div className={styles.mono}>{row.name}</div>
                  </>
                ),
                sortValue: (row: ConfigItem) => row.label,
              },
              {
                id: 'summary',
                header: 'What it does',
                cell: (row: ConfigItem) => <span className={styles.sub}>{row.summary}</span>,
                sortValue: (row: ConfigItem) => row.summary,
              },
              {
                id: 'active',
                header: 'In force',
                // A declaration that is switched off is not the same as one that is missing, and
                // a screen that drew them the same way is one an administrator debugs twice.
                cell: (row: ConfigItem) =>
                  row.isActive ? <Tag tone="positive">yes</Tag> : <Tag tone="critical">off</Tag>,
                sortValue: (row: ConfigItem) => String(row.isActive),
              },
            ]}
            empty={empty}
          />
        </Panel>
      )}
    </AsyncBoundary>
  )
}
