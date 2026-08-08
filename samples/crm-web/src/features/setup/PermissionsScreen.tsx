import { AsyncBoundary, Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { schemaRows } from './schemaModel'
import styles from './setup.module.css'

/**
 * The permissions this build actually checks, and what each one unlocks.
 *
 * <strong>A per-object grid used to sit here</strong> — seven objects × four profiles, every cell
 * saying `read`, `write` or `admin`. It described a model this system does not have: authorization
 * is a scope on the token, checked on the capability, and it is the same scope whatever the object
 * is. A reader who took the grid at face value would conclude a representative could read work
 * orders and not write them, which was never true of anything.
 *
 * What replaced it is the four scope strings the server names in its refusals, said once, with
 * whether this caller holds each.
 */
const PERMISSIONS: readonly { permission: string; unlocks: string }[] = [
  { permission: 'crm.read', unlocks: 'Every read: the record pages, the lists, the boards and the reports.' },
  { permission: 'crm.write', unlocks: 'Capturing, converting, quoting, ordering and commenting.' },
  { permission: 'crm.admin', unlocks: 'Declaring objects, fields, processes, policies and approvals.' },
  { permission: 'crm.discount.approve', unlocks: 'Clearing a quote whose discount crossed the threshold.' },
]

export function PermissionsScreen() {
  const session = useSession()
  const schema = useSchema()

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Permissions" />

      <Panel padding="flush">
        <PanelHeader
          title="What this token holds"
          note={`${session.displayName} · enforced on the server, on every request`}
        />
        <div style={{ overflowX: 'auto' }}>
          <table className={styles.matrix}>
            <caption className="sr-only">Permissions this build checks</caption>
            <thead>
              <tr>
                <th scope="col">Permission</th>
                <th scope="col">What it unlocks</th>
                <th scope="col">Held</th>
              </tr>
            </thead>
            <tbody>
              {PERMISSIONS.map((row) => (
                <tr key={row.permission}>
                  <td className={styles.mono}>{row.permission}</td>
                  <td>{row.unlocks}</td>
                  <td>
                    {session.can(row.permission) ? (
                      <Tag tone="positive">held</Tag>
                    ) : (
                      <Tag tone="neutral">not held</Tag>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Panel>

      {/*
        A FAILED `describe` IS NOT AN UNRESTRICTED TOKEN. This panel read `schemaRows(schema.data)`
        with no boundary, so a refused or dropped read produced "0 · resolved by the server for
        this caller" above the sentence "Every declared field is readable and writable by this
        token" — a statement about field-level security composed from a request that never
        answered, on the screen somebody opens to check exactly that.
      */}
      <div style={{ marginTop: 'var(--section-gap)' }}>
        <AsyncBoundary query={schema} skeletonRows={3}>
          {(description) => {
            // `canRead` and `canWrite` came back from the server already resolved for whoever is
            // holding the token, so a field listed here is field-level security refusing this
            // caller — not a rule this client evaluated and could get wrong.
            const restricted = schemaRows(description).flatMap((object) =>
              object.fields
                .filter((field) => field.declared && (!field.canRead || !field.canWrite))
                .map((field) => ({ object: object.label, ...field })),
            )

            const declared = schemaRows(description).reduce(
              (sum, object) => sum + object.fields.filter((field) => field.declared).length,
              0,
            )

            return (
              <Panel padding="flush">
                <PanelHeader
                  title="Fields this token may not have in full"
                  note={`${restricted.length} of ${declared} declared · resolved by the server for this caller`}
                />
                {restricted.length === 0 ? (
                  <div style={{ padding: 'var(--space-4)' }}>
                    <span className={styles.sub}>
                      {declared === 0
                        ? 'This organisation has declared no fields, so there are none to restrict.'
                        : 'Every declared field is readable and writable by this token. Switch persona to see the difference — the answer comes from the server, not from this screen.'}
                    </span>
                  </div>
                ) : (
                  <div>
                    {restricted.map((field) => (
                      <div key={`${field.object}-${field.name}`} className={styles.flowRow}>
                        <Tag tone="outline">{field.object}</Tag>
                        <span className={styles.link}>{field.label}</span>
                        <span className={styles.mono}>{field.name}</span>
                        <span style={{ marginLeft: 'auto', display: 'flex', gap: 'var(--space-2)' }}>
                          <Tag tone={field.canRead ? 'positive' : 'critical'}>
                            {field.canRead ? 'read' : 'no read'}
                          </Tag>
                          <Tag tone={field.canWrite ? 'positive' : 'critical'}>
                            {field.canWrite ? 'write' : 'no write'}
                          </Tag>
                        </span>
                      </div>
                    ))}
                  </div>
                )}
              </Panel>
            )
          }}
        </AsyncBoundary>
      </div>
    </Page>
  )
}
