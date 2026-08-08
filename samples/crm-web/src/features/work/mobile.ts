import type { RecordView } from '@/api/contracts'

/**
 * The deals that moved most recently.
 *
 * "RECENT" WAS THE FIRST SIX ROWS IN WHATEVER ORDER THEY ARRIVED. The entity page comes back
 * ordered by its key column, so the tab headed "Recent" showed the six opportunities with the
 * lowest ids — a stable list that never changed and had nothing to do with recency. Nobody would
 * catch it: six deals under that heading look exactly like six recent deals.
 *
 * `stage_entered_at` IS THE ONLY CLOCK ON THE ROW. An opportunity carries no created or modified
 * timestamp in its readable columns; what it does carry is when it last entered its stage, which
 * is when somebody last moved it. That is a defensible reading of "recent" and it is the one this
 * names — a row that has never moved sorts last rather than being dropped.
 */
export function recentlyMoved(records: readonly RecordView[], limit: number): RecordView[] {
  return [...records].sort((left, right) => movedAt(right) - movedAt(left)).slice(0, limit)
}

/**
 * When a row last moved, as an instant.
 *
 * Parsed rather than compared as text. Text works only while every stamp carries the same UTC
 * offset, which is true of what this server writes and is not a property worth depending on. What
 * the parse also buys is the answer for a row with no stamp or an unreadable one: the bottom of
 * the list rather than the top, because an absent date is not news.
 */
function movedAt(record: RecordView): number {
  const when = Date.parse(record.values['stage_entered_at'] ?? '')

  return Number.isNaN(when) ? Number.NEGATIVE_INFINITY : when
}
