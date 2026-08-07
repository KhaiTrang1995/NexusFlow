import type { RecordView } from '@/api/contracts'

/** One thing on the week grid, or in the list of things that are not on it. */
export interface WeekEvent {
  id: string
  title: string
  /** Monday is nought. Negative when it is not in this week at all. */
  day: number
  hour: number
  minutes: number
  kind: 'meeting' | 'task'
  related: string
  status: string
}

/** The five weekday labels of the week `today` falls in. */
export function weekOf(today: Date): { labels: string[]; monday: Date } {
  const monday = new Date(today)

  // getDay is Sunday-first; the grid is Monday-first, and Sunday has to go back six rather than
  // forward one — the off-by-one that puts a whole week's work on the wrong screen.
  const offset = (monday.getDay() + 6) % 7

  monday.setDate(monday.getDate() - offset)
  monday.setHours(0, 0, 0, 0)

  const labels: string[] = []

  for (let index = 0; index < 5; index++) {
    const day = new Date(monday)

    day.setDate(monday.getDate() + index)
    labels.push(
      day.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' }),
    )
  }

  return { labels, monday }
}

/**
 * Turns the server's activities into things that can be drawn on a week.
 *
 * **A due date is not a start time**, and this does not pretend otherwise: an activity with no
 * hour on it is placed at the hour its timestamp carries, which for a seeded row is the moment
 * the seed ran. What matters on a calendar is the day; the hour is where it sits in the column.
 *
 * Anything outside the week comes back with a negative `day` rather than being dropped. A
 * calendar that silently hides the overdue task is the calendar that let it go overdue.
 */
export function weekEvents(records: readonly RecordView[], monday: Date): WeekEvent[] {
  const friday = new Date(monday)

  friday.setDate(monday.getDate() + 5)

  return records.map((record) => {
    const due = record.values['due_at']
    const when = due === null || due === undefined ? null : new Date(due)
    const inWeek = when !== null && when >= monday && when < friday

    const kind = record.values['kind']

    return {
      id: record.recordId,
      title: record.values['subject'] ?? '(no subject)',
      day: inWeek ? Math.floor((when!.getTime() - monday.getTime()) / 86_400_000) : -1,
      hour: when === null ? 9 : when.getHours(),
      minutes: when === null ? 0 : when.getMinutes(),
      kind: kind === 'Meeting' || kind === 'Call' ? 'meeting' : 'task',
      related: record.values['relates_to_kind'] ?? '',
      status: record.values['status'] ?? 'Open',
    }
  })
}

/** Whatever did not fit on the grid: overdue, later, or with no date at all. */
export function offGrid(events: readonly WeekEvent[]): WeekEvent[] {
  return events.filter((event) => event.day < 0)
}
