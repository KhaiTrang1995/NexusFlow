/**
 * What this person actually opened, most recent first.
 *
 * <strong>The search screen used to show four records belonging to somebody else.</strong> A
 * panel headed "Recent — what you looked at last" listed Northwind Systems, Ron Petrov and two
 * more, hard-coded, on every tenant. None of them had been looked at; on any tenant but the
 * seeded one none of them existed, and clicking one navigated to an id that resolves to nothing.
 *
 * <strong>Kept in the browser, because that is where the fact is.</strong> Nothing on the server
 * records a view — there is no read log, and inventing one to fill a panel would mean a write on
 * every record open. What "you looked at last" means is what this browser opened, and that is
 * exactly what is stored.
 *
 * <strong>Keyed by tenant, and that is not cosmetic.</strong> One browser switches between
 * organisations in this sample. A single list would show one tenant's record titles while signed
 * in to another — the leak this file replaced, reintroduced by the fix.
 */
export interface Recent {
  /** The object key, as the record route takes it. */
  objectKey: string
  id: string
  title: string
  subtitle: string
}

/** Beyond this a "recent" list is a history, which is a different feature. */
const KEEP = 8

function storageKey(tenantId: string): string {
  return `crm-web.recents.${tenantId}`
}

export function readRecents(tenantId: string): readonly Recent[] {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(storageKey(tenantId)) ?? '[]')

    return Array.isArray(stored) ? (stored as Recent[]).filter(isRecent).slice(0, KEEP) : []
  } catch {
    // A corrupted entry is not worth a broken screen; an empty list is honest and self-healing —
    // the next record opened rewrites it.
    return []
  }
}

export function remember(tenantId: string, entry: Recent): void {
  const kept = [entry, ...readRecents(tenantId).filter((seen) => keyOf(seen) !== keyOf(entry))]
    .slice(0, KEEP)

  try {
    localStorage.setItem(storageKey(tenantId), JSON.stringify(kept))
  } catch {
    // Storage full or blocked. Losing the list is not worth failing the page the reader asked for.
  }
}

function keyOf(entry: Recent): string {
  return `${entry.objectKey}/${entry.id}`
}

function isRecent(value: unknown): value is Recent {
  const candidate = value as Partial<Recent> | null

  return (
    candidate !== null
    && typeof candidate === 'object'
    && typeof candidate.objectKey === 'string'
    && typeof candidate.id === 'string'
    && typeof candidate.title === 'string'
  )
}
