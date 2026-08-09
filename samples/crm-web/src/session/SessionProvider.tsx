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

export type Persona = 'rep' | 'manager' | 'director' | 'admin' | 'contoso'

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
  /**
   * The uuid rows are owned by, which is not the same thing as {@link userId}.
   *
   * `owner_id` on an account, an opportunity and an activity is a uuid; a token's subject is a
   * string a directory chose. A real deployment resolves one from the other; this sample states
   * both, because pretending they are the same value is how a write ends up with a uuid parsed
   * out of somebody's login name.
   */
  ownerId: string
  permissions: readonly string[]
}

const READ = 'crm.read'
const WRITE = 'crm.write'
const ADMIN = 'crm.admin'
const APPROVE = 'crm.discount.approve'

/**
 * The sample's three tokens, and the four personas that use them.
 *
 * Admin rides the manager token because that is what the sample mints for it — said
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
    ownerId: '33333333-3333-3333-3333-333333333333',
    permissions: [READ, WRITE],
  },
  manager: {
    persona: 'manager',
    displayName: 'B. Vance',
    initials: 'BV',
    token: 'manager-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'manager-northwind-1',
    ownerId: '33333333-3333-3333-3333-333333333333',
    permissions: [READ, WRITE, ADMIN, APPROVE],
  },
  director: {
    persona: 'director',
    displayName: 'P. Almeida',
    initials: 'PA',
    token: 'director-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'director-northwind-1',
    ownerId: '33333333-3333-3333-3333-333333333333',

    // No APPROVE. A director is senior to the manager and is not in the discount chain — the
    // manager is the control. Giving the director every grant would make the personas
    // indistinguishable, which is the opposite of what they exist to show.
    permissions: [READ, WRITE, ADMIN],
  },
  // A second tenant, and an empty one. Two things nothing else here demonstrates: that a
  // token's `tid` claim decides which rows exist at all — not a filter this client applies —
  // and what every screen looks like before anybody has put anything in it, which is the state
  // a real organisation starts in and the one no seeded demo ever shows.
  contoso: {
    persona: 'contoso',
    displayName: 'R. Adeyemi',
    initials: 'RA',
    token: 'rep-contoso-token',
    tenantId: 'crm-contoso',
    userId: 'rep-contoso-1',

    // Contoso has no org chart and no accounts, so there is no owner uuid to hold. The one
    // below belongs to nobody in this tenant, which is the honest value: "mine" is empty.
    ownerId: '00000000-0000-0000-0000-000000000000',
    permissions: [READ, WRITE],
  },
  admin: {
    persona: 'admin',
    displayName: 'B. Vance',
    initials: 'BV',
    token: 'manager-northwind-token',
    tenantId: 'crm-northwind',
    userId: 'manager-northwind-1',
    ownerId: '33333333-3333-3333-3333-333333333333',
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
  // Remembered across a reload. Switching persona and losing it on the next full page load
  // makes every deep link a representative's — which is how a screen that needs `crm.admin`
  // reads as broken rather than as refused.
  const [persona, remember] = useState<Persona>(() => stored() ?? initialPersona)

  const setPersona = useCallback((next: Persona) => {
    remember(next)

    try {
      globalThis.localStorage?.setItem(PersonaKey, next)
    } catch {
      // A browser with storage disabled still switches persona; it just forgets on reload.
    }
  }, [])

  const user = PEOPLE[persona]

  const can = useCallback(
    (permission: string) => user.permissions.includes(permission),
    [user.permissions],
  )

  const value = useMemo<Session>(
    () => ({ ...user, can, switchTo: setPersona }),
    [user, can, setPersona],
  )

  return <SessionContext value={value}>{children}</SessionContext>
}

/** Where the chosen persona is remembered. */
const PersonaKey = 'crm-web.persona'

function stored(): Persona | null {
  try {
    const value = globalThis.localStorage?.getItem(PersonaKey)

    // `Object.hasOwn`, not `in`. `'constructor' in PEOPLE` is true, as is `toString` and every
    // other name on `Object.prototype` — so a browser holding one of those, from an older build
    // or from anybody who has opened the console, was handed `PEOPLE.constructor` as the signed-in
    // person and the first `can()` threw before anything rendered. A name this build does not
    // have is not a persona, however the object answers about it.
    return value !== null && value !== undefined && Object.hasOwn(PEOPLE, value)
      ? (value as Persona)
      : null
  } catch {
    return null
  }
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

  // Labelled by its tenant rather than by a role, because that is what switching to it changes.
  { id: 'contoso', label: 'Contoso' },
]
