import { createContext, use, useCallback, useMemo, useState } from 'react'
import type { ReactNode } from 'react'

/**
 * Who is using the application.
 *
 * FOUR PERSONAS, BECAUSE THE SAMPLE MINTS THREE TOKENS. The prototype switches between rep,
 * manager, director and admin to show how the same screens change; the backend maps a token to a
 * tenant and a set of permissions. Both are modelled here so a screen asks "may this person do
 * that" rather than "which button did somebody click at the top of the page".
 *
 * PERMISSIONS ARE HELD, NOT COMPUTED FROM THE ROLE AT EACH USE. A screen asking
 * `can('crm.admin')` keeps working when a role's grants change; a screen asking
 * `persona === 'admin'` has to be found and edited. The list here mirrors what the sample's
 * `Authentication.cs` puts in each token's claims — it is a convenience for hiding controls the
 * server would refuse anyway, never the enforcement itself. **The server is the enforcement.**
 */

export type Persona = 'rep' | 'manager' | 'director' | 'admin'

export interface SessionUser {
  persona: Persona
  /** What the top bar shows. */
  displayName: string
  initials: string
  /** The bearer token this sample's `Authentication.cs` recognises. */
  token: string
  /** Which tenant the token resolves to. Part of every cache key. */
  tenantId: string
  /** The subject claim, which is what an inbox or a worklist filters by. */
  userId: string
  permissions: readonly string[]
}

const READ = 'crm.read'
const WRITE = 'crm.write'
const ADMIN = 'crm.admin'
const APPROVE = 'crm.discount.approve'

/**
 * The sample's three tokens, and the four personas that use them.
 *
 * Director and admin both ride the manager token because that is what the sample mints — said
 * here rather than hidden, so nobody reads this as four separate identities on the server.
 */
const PEOPLE: Record<Persona, SessionUser> = {
  rep: {
    persona: 'rep',
    displayName: 'A. Ruiz',
    initials: 'AR',
    token: 'rep-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'rep-northwind-1',
    permissions: [READ, WRITE],
  },
  manager: {
    persona: 'manager',
    displayName: 'B. Vance',
    initials: 'BV',
    token: 'manager-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'manager-northwind-1',
    permissions: [READ, WRITE, ADMIN, APPROVE],
  },
  director: {
    persona: 'director',
    displayName: 'B. Vance',
    initials: 'BV',
    token: 'manager-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'manager-northwind-1',
    permissions: [READ, WRITE, ADMIN, APPROVE],
  },
  admin: {
    persona: 'admin',
    displayName: 'B. Vance',
    initials: 'BV',
    token: 'manager-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'manager-northwind-1',
    permissions: [READ, WRITE, ADMIN, APPROVE],
  },
}

export interface Session extends SessionUser {
  /** Whether this person holds a permission. Hides controls; never authorises anything. */
  can: (permission: string) => boolean
  switchTo: (persona: Persona) => void
}

const SessionContext = createContext<Session | null>(null)

export function SessionProvider({
  initialPersona = 'rep',
  children,
}: {
  initialPersona?: Persona
  children: ReactNode
}) {
  const [persona, setPersona] = useState<Persona>(initialPersona)
  const user = PEOPLE[persona]

  const can = useCallback(
    (permission: string) => user.permissions.includes(permission),
    [user.permissions],
  )

  const value = useMemo<Session>(
    () => ({ ...user, can, switchTo: setPersona }),
    [user, can],
  )

  return <SessionContext value={value}>{children}</SessionContext>
}

export function useSession(): Session {
  const session = use(SessionContext)

  if (!session) {
    throw new Error('useSession was called outside a SessionProvider.')
  }

  return session
}

export const PERSONAS: readonly { id: Persona; label: string }[] = [
  { id: 'rep', label: 'Rep' },
  { id: 'manager', label: 'Manager' },
  { id: 'director', label: 'Director' },
  { id: 'admin', label: 'Admin' },
]
