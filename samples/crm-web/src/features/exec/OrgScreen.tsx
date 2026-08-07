import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Tag,
  TextField,
} from '@/design/primitives'
import { useOrgChart, useSetOrgMember } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { useToast } from '@/app/ToastProvider'
import type { OrgChartMember, OrgRole } from '@/api/contracts'
import styles from './exec.module.css'

const ROLES: readonly OrgRole[] = ['Representative', 'Manager', 'Director']

/**
 * The reporting line, and who may change it.
 *
 * THE SCREEN EVERY OTHER NUMBER IN THIS APPLICATION DEPENDS ON. A seller sees their own rows, a
 * manager sees their reports', a director sees everybody's — and which is which is decided by this
 * tree, not by any permission. Somebody placed under the wrong manager reads a pipeline that is
 * quietly not theirs, and the answer looks like a slow quarter rather than a misfiled person.
 *
 * THE ROWS COME BACK MANAGERS-FIRST, so the tree is drawn in one pass. A client that sorted them
 * itself would be a second implementation of the same walk, and the one that goes wrong is
 * whichever ships first.
 *
 * "YOU ARE NOT IN THIS LINE" IS SAID OUT LOUD. A caller with no place in the chart has no scope,
 * so every scoped read answers them emptily — which is indistinguishable from an empty tenant
 * unless somebody says which it is.
 */
export function OrgScreen() {
  const chart = useOrgChart()
  const session = useSession()
  const [editing, setEditing] = useState<OrgChartMember | 'new' | null>(null)

  return (
    <Page>
      <PageHeader
        eyebrow="Executive"
        title="Reporting line"
        actions={
          session.can('crm.admin') ? (
            <Button tone="primary" onClick={() => setEditing('new')}>
              Place somebody
            </Button>
          ) : null
        }
      />

      <AsyncBoundary query={chart} skeletonRows={5}>
        {(data) => {
          const me = data.members.find((member) => member.userId === session.userId)

          return (
            <Panel padding="flush">
              <PanelHeader
                title="Who reports to whom"
                note={`${data.members.length} placed · top of the line first`}
              />
              <PanelBody>
                {me === undefined ? (
                  <p className={styles.sub} role="status">
                    You ({session.userId}) are not in this line. Every scoped read will answer you
                    emptily until somebody places you — which is not the same thing as there being
                    nothing to see.
                  </p>
                ) : null}

                <ul className={styles.orgList}>
                  {data.members.map((member) => (
                    <li
                      key={member.userId}
                      className={styles.orgRow}
                      style={{ marginLeft: depthOf(member, data.members) * 24 }}
                    >
                      <span className={styles.orgName}>
                        {member.displayName}
                        {member.userId === session.userId ? ' — you' : ''}
                      </span>
                      <Tag tone="outline">{member.role}</Tag>
                      <span className={styles.sub}>
                        {member.userId}
                        {member.reports > 0 ? ` · ${member.reports} direct` : ''}
                      </span>
                      {session.can('crm.admin') ? (
                        <Button size="sm" onClick={() => setEditing(member)}>
                          Move
                        </Button>
                      ) : null}
                    </li>
                  ))}
                </ul>
              </PanelBody>
            </Panel>
          )
        }}
      </AsyncBoundary>

      {editing !== null ? (
        <PlaceDrawer
          member={editing === 'new' ? null : editing}
          managers={chart.data?.members ?? []}
          onClose={() => setEditing(null)}
        />
      ) : null}
    </Page>
  )
}

/**
 * How far down the line somebody is, walked from the row itself.
 *
 * The server orders managers before their reports, so every parent is already in the list by the
 * time its child is reached — which is what makes counting hops here safe rather than a second
 * traversal that could disagree with the order it was given.
 */
function depthOf(member: OrgChartMember, all: readonly OrgChartMember[]): number {
  let depth = 0
  let parent = member.reportsTo

  // Bounded by the list itself: a cycle is refused at the write, and a bound here means a chart
  // that somehow contained one still renders instead of hanging the tab.
  while (parent !== null && depth < all.length) {
    const above = all.find((candidate) => candidate.userId === parent)

    if (above === undefined) {
      break
    }

    parent = above.reportsTo
    depth += 1
  }

  return depth
}

/**
 * Places somebody, or moves them under a different manager.
 *
 * THE LOOP IS THE SERVER'S TO REFUSE. A two-person cycle makes every roll-up below it wrong or
 * non-terminating, and this is exactly the screen where one gets made by accident — so the check
 * lives at the write, and this form shows the refusal rather than pre-empting it with a copy of
 * the rule that will go stale.
 */
function PlaceDrawer({
  member,
  managers,
  onClose,
}: {
  member: OrgChartMember | null
  managers: readonly OrgChartMember[]
  onClose: () => void
}) {
  const place = useSetOrgMember()
  const toast = useToast()

  const [userId, setUserId] = useState(member?.userId ?? '')
  const [displayName, setDisplayName] = useState(member?.displayName ?? '')
  const [role, setRole] = useState<OrgRole>(member?.role ?? 'Representative')
  const [reportsTo, setReportsTo] = useState(member?.reportsTo ?? '')

  const ready = userId.trim().length > 0 && displayName.trim().length > 0

  function submit() {
    place.mutate(
      {
        userId: userId.trim(),
        displayName: displayName.trim(),
        role,
        // Empty is the top of the line, which is a real answer and not a missing one.
        reportsTo: reportsTo.length > 0 ? reportsTo : null,
      },
      {
        onSuccess: (result) => {
          toast.saved(
            result.reports === 0
              ? `${displayName.trim()} placed, with nobody below them.`
              : `${displayName.trim()} placed, with ${result.reports} below them.`,
          )
          onClose()
        },
        onError: (error) => toast.failed(error, 'That placement was refused.'),
      },
    )
  }

  return (
    <Drawer
      eyebrow={member === null ? 'Place' : 'Move'}
      title={member?.displayName ?? 'Somebody new'}
      subtitle="Whose rows they see follows from where they sit"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || place.isPending} onClick={submit}>
            {place.isPending ? 'Saving…' : 'Save'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Who" />
      <TextField
        label="Subject"
        value={userId}
        required
        disabled={member !== null}
        hint="As their token carries it. This is what every scoped read matches on."
        onChange={(event) => setUserId(event.target.value)}
      />
      <TextField
        label="Display name"
        value={displayName}
        required
        onChange={(event) => setDisplayName(event.target.value)}
      />

      <DrawerSection label="Where" />
      <SelectField
        label="Role"
        value={role}
        hint="A director's scope is everybody; a representative's is themselves."
        options={ROLES.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setRole(event.target.value as OrgRole)}
      />
      <SelectField
        label="Reports to"
        value={reportsTo}
        placeholder="Nobody — the top of the line"
        options={managers
          .filter((candidate) => candidate.userId !== member?.userId)
          .map((candidate) => ({
            value: candidate.userId,
            label: `${candidate.displayName} · ${candidate.role}`,
          }))}
        onChange={(event) => setReportsTo(event.target.value)}
      />

      {place.isError ? <ErrorState error={place.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
