/**
 * Joins class names, dropping anything falsy.
 *
 * Small enough that a dependency would cost more than it saves, and typed so a `0` — the classic
 * way `cx(count && styles.badge)` renders a literal zero into the DOM — cannot be passed.
 */
export type ClassValue = string | false | null | undefined

export function cx(...values: ClassValue[]): string {
  let out = ''

  for (const value of values) {
    if (value) {
      out = out === '' ? value : out + ' ' + value
    }
  }

  return out
}
