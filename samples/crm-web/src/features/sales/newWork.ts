/**
 * The two decisions the create forms make about what "nothing" means.
 *
 * Both are the same decision twice: an empty box is *absent*, not *empty*. A column that reads
 * "" when nobody typed an address, or a due date of today when nobody promised a day, is a
 * column that has quietly answered a question nobody asked — and every count over it is then
 * wrong in a way nothing reports.
 */

/** An address, or null when nobody gave one. */
export function emailOrNull(typed: string): string | null {
  const trimmed = typed.trim()

  return trimmed.length > 0 ? trimmed : null
}

/**
 * A due date counted from now, or null.
 *
 * Null for an empty box and null for anything that is not a number: a task with no date is one
 * the overdue sweep leaves alone, which is the right answer for a task nobody dated. Treating
 * "soon" as nought days would make it overdue before the form closed.
 */
export function dueAtFromDays(typed: string, now: Date): string | null {
  const trimmed = typed.trim()

  if (trimmed.length === 0) {
    return null
  }

  const days = Number(trimmed)

  return Number.isFinite(days) ? new Date(now.getTime() + days * 86_400_000).toISOString() : null
}
