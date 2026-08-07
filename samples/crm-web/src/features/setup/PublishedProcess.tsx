import { AsyncBoundary, DataTable, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useProcess } from '@/api/queries/hooks'
import type { ProcessStageView, ProcessTransitionView } from '@/api/contracts'
import styles from './setup.module.css'

/**
 * The process that is actually published, read from the tables it lives in.
 *
 * THIS IS THE SAMPLE'S CLAIM, ON THE SCREEN SOMEBODY WOULD CHECK IT ON. The stages, the guards
 * and the actions are rows an administrator rewrites at run time; a screen listing the ones this
 * client was compiled with would contradict that exactly where it matters.
 *
 * A guard renders as three words because it is three columns — five operators and no expression
 * language, which is what makes it something a screen can draw at all.
 */
export function PublishedProcess() {
  const process = useProcess('Opportunity')

  return (
    <AsyncBoundary query={process} skeletonRows={5}>
      {(published) => (
        <>
          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader
              title="Published stages"
              note={`${published.appliesTo} · version ${published.version} · ${published.stages.length} stages`}
            />
            <DataTable
              caption="Published stages"
              rows={published.stages}
              rowKey={(row) => row.name}
              columns={[
                {
                  id: 'ordinal',
                  header: '#',
                  numeric: true,
                  cell: (row: ProcessStageView) => row.ordinal,
                },
                { id: 'name', header: 'Stage', cell: (row: ProcessStageView) => row.name },
                {
                  id: 'occupants',
                  header: 'Deals here',
                  numeric: true,
                  // A stage nothing has ever entered is either new or a mistake; a stage holding
                  // half the pipeline is where deals go to be forgotten.
                  cell: (row: ProcessStageView) =>
                    row.occupants === 0 ? <span className={styles.sub}>—</span> : row.occupants,
                  sortValue: (row: ProcessStageView) => row.occupants,
                },
                {
                  id: 'terminal',
                  header: 'Terminal',
                  cell: (row: ProcessStageView) =>
                    row.isTerminal ? <Tag tone="neutral">nothing follows</Tag> : '—',
                },
              ]}
              empty="This process has no stages, which is a process nothing can enter."
            />
          </Panel>

          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader
              title="Published transitions"
              note={`${published.transitions.length} · what has to hold, and what it does`}
            />
            <DataTable
              caption="Published transitions"
              rows={published.transitions}
              rowKey={(row) => `${row.from}-${row.to}-${row.trigger}`}
              columns={[
                {
                  id: 'move',
                  header: 'Move',
                  cell: (row: ProcessTransitionView) => (
                    <>
                      <span className={styles.link}>{row.from}</span>
                      <span className={styles.sub}> → {row.to}</span>
                    </>
                  ),
                },
                {
                  id: 'trigger',
                  header: 'Trigger',
                  cell: (row: ProcessTransitionView) => (
                    <span className={styles.mono}>{row.trigger}</span>
                  ),
                },
                {
                  id: 'guards',
                  header: 'Only when',
                  cell: (row: ProcessTransitionView) =>
                    row.guards.length === 0 ? (
                      <span className={styles.sub}>always</span>
                    ) : (
                      <span className={styles.sub}>
                        {row.guards
                          .map((guard) => `${guard.field} ${guard.operator} ${guard.value}`)
                          .join(' and ')}
                      </span>
                    ),
                },
                {
                  id: 'actions',
                  header: 'And then',
                  cell: (row: ProcessTransitionView) =>
                    row.actions.length === 0 ? (
                      <span className={styles.sub}>nothing</span>
                    ) : (
                      row.actions.map((action) => (
                        <Tag key={action} tone="accent">
                          {action}
                        </Tag>
                      ))
                    ),
                },
              ]}
              empty="No transitions are declared, so nothing can move."
            />
          </Panel>
        </>
      )}
    </AsyncBoundary>
  )
}
