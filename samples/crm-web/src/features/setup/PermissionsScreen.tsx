import { Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
import { schemaRows } from './schemaModel'
import styles from './setup.module.css'

type Grant = 'read' | 'write' | 'admin' | 'none'

interface ProfileRow {
  profile: string
  permission: string
  grants: Readonly<Record<string, Grant>>
}

const PROFILES: readonly ProfileRow[] = [
  {
    profile: 'Representative',
    permission: 'crm.read, crm.write',
    grants: { account: 'write', contact: 'write', lead: 'write', opportunity: 'write', quote: 'write', workorder: 'read', task: 'write' },
  },
  {
    profile: 'Manager',
    permission: 'crm.read, crm.write, crm.discount.approve',
    grants: { account: 'write', contact: 'write', lead: 'write', opportunity: 'write', quote: 'admin', workorder: 'write', task: 'write' },
  },
  {
    profile: 'Administrator',
    permission: 'crm.read, crm.write, crm.admin',
    grants: { account: 'admin', contact: 'admin', lead: 'admin', opportunity: 'admin', quote: 'admin', workorder: 'admin', task: 'admin' },
  },
  {
    profile: 'Read only',
    permission: 'crm.read',
    grants: { account: 'read', contact: 'read', lead: 'read', opportunity: 'read', quote: 'read', workorder: 'read', task: 'read' },
  },
]

const TONE: Readonly<Record<Grant, 'positive' | 'accent' | 'outline' | 'neutral'>> = {
  admin: 'positive',
  write: 'accent',
  read: 'outline',
  none: 'neutral',
}

/**
 * Who may do what.
 *
 * THIS IS A PICTURE, NOT THE ENFORCEMENT. Every grant below is checked on the server, on the
 * capability, on every request. A permission matrix that a client could edit into meaning
 * something else would be a permission matrix worth nothing — this screen shows what the server
 * is doing, and hiding a button is a courtesy rather than a control.
 */
export function PermissionsScreen() {
  const schema = useSchema()
  const objects = schemaRows(schema.data)

  // The half of this screen that is not a picture. `canRead` and `canWrite` came back from the
  // server already resolved for whoever is holding the token, so a field missing here is
  // field-level security refusing this caller — not a rule this client evaluated and could get
  // wrong.
  const restricted = objects.flatMap((object) =>
    object.fields
      .filter((field) => field.declared && (!field.canRead || !field.canWrite))
      .map((field) => ({ object: object.label, ...field })),
  )

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Permissions" />

      <Panel padding="flush">
        <PanelHeader title="Profiles" note="enforced on the server, on every request" />
        <div style={{ overflowX: 'auto' }}>
          <table className={styles.matrix}>
            <caption className="sr-only">Object permissions by profile</caption>
            <thead>
              <tr>
                <th scope="col">Profile</th>
                {objects.map((object) => (
                  <th key={object.key} scope="col">
                    {object.label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {PROFILES.map((row) => (
                <tr key={row.profile}>
                  <td>
                    <div>{row.profile}</div>
                    <div className={styles.mono}>{row.permission}</div>
                  </td>
                  {objects.map((object) => {
                    const grant = row.grants[object.key] ?? 'none'
                    return (
                      <td key={object.key}>
                        <Tag tone={TONE[grant]}>{grant}</Tag>
                      </td>
                    )
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Panel>

      <Panel padding="flush" style={{ marginTop: 'var(--section-gap)' }}>
        <PanelHeader
          title="Fields this token may not have in full"
          note={
            schema.isPending
              ? 'reading…'
              : `${restricted.length} · resolved by the server for this caller`
          }
        />
        {restricted.length === 0 ? (
          <div style={{ padding: 'var(--space-4)' }}>
            <span className={styles.sub}>
              Every declared field is readable and writable by this token. Switch persona to see
              the difference — the answer comes from the server, not from this screen.
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
    </Page>
  )
}
