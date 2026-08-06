import { Page, PageHeader, Panel, PanelHeader, Tag } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
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
  const objects = Object.values(OBJECT_MODELS)

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
                    {object.plural}
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
    </Page>
  )
}
