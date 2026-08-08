import { describe, expect, it } from 'vitest'
import { offGrid, ownedBy, weekEvents, weekOf } from '../week'

describe('weekOf', () => {
  /**
   * The off-by-one that puts a whole week's work on the wrong screen. `getDay` counts from
   * Sunday and the grid counts from Monday, so Sunday has to go *back* six days rather than
   * forward one — and the naive `date - getDay()` sends it forward into the week that has not
   * started.
   */
  it('starts on Monday, including when today is Sunday', () => {
    // 2026-08-09 is a Sunday; its Monday is the 3rd, not the 10th.
    const { monday } = weekOf(new Date(2026, 7, 9))

    expect(monday.getDate()).toBe(3)
    expect(monday.getDay()).toBe(1)
  })

  it('starts on Monday when today is Monday', () => {
    const { monday, labels } = weekOf(new Date(2026, 7, 3))

    expect(monday.getDate()).toBe(3)
    expect(labels).toHaveLength(5)
  })

  it('is midnight, so an event at 00:30 on Monday is in the week', () => {
    expect(weekOf(new Date(2026, 7, 5, 14, 30)).monday.getHours()).toBe(0)
  })
})

describe('weekEvents', () => {
  const monday = weekOf(new Date(2026, 7, 5)).monday

  it('places an activity on the day its due date falls', () => {
    const [event] = weekEvents(
      [
        {
          recordId: 'a1',
          values: {
            subject: 'Security review',
            kind: 'Meeting',
            due_at: new Date(2026, 7, 5, 10, 15).toISOString(),
            relates_to_kind: 'Opportunity',
            status: 'Open',
          },
        },
      ],
      monday,
    )

    expect(event).toMatchObject({ day: 2, hour: 10, minutes: 15, kind: 'meeting' })
  })

  /**
   * The one behaviour worth arguing about. A calendar that dropped what did not fit is the
   * calendar that let the task go overdue — so it comes back with a negative day and the screen
   * lists it under the grid.
   */
  it('keeps what falls outside the week rather than dropping it', () => {
    const events = weekEvents(
      [
        {
          recordId: 'late',
          values: {
            subject: 'Overdue call',
            kind: 'Call',
            due_at: new Date(2026, 6, 20, 9, 0).toISOString(),
            status: 'Open',
          },
        },
        { recordId: 'undated', values: { subject: 'No date', kind: 'Task', due_at: null } },
      ],
      monday,
    )

    expect(events).toHaveLength(2)
    expect(offGrid(events).map((event) => event.id)).toEqual(['late', 'undated'])
  })

  it('draws a call or a meeting differently from a task', () => {
    const kinds = weekEvents(
      [
        { recordId: '1', values: { subject: 'a', kind: 'Meeting', due_at: null } },
        { recordId: '2', values: { subject: 'b', kind: 'Call', due_at: null } },
        { recordId: '3', values: { subject: 'c', kind: 'Task', due_at: null } },
        { recordId: '4', values: { subject: 'd', kind: 'Note', due_at: null } },
      ],
      monday,
    ).map((event) => event.kind)

    expect(kinds).toEqual(['meeting', 'meeting', 'task', 'task'])
  })

  it('carries the owner, which is what the calendar filters "mine" on', () => {
    const [event] = weekEvents(
      [{ recordId: '1', values: { subject: 'a', kind: 'Task', due_at: null, owner_id: 'u-1' } }],
      monday,
    )

    expect(event?.ownerId).toBe('u-1')
  })
})

/**
 * Whose week it is.
 *
 * THE SWITCH ASKED THE WRONG QUESTION ENTIRELY. "Mine" filtered on `status === 'Open'`, so it hid
 * everybody's finished activities and showed everybody's unfinished ones — under a control
 * labelled "Whose week". Nothing about it was about ownership, and the two settings differed only
 * by whether closed items were included.
 */
describe('ownedBy', () => {
  const events = weekEvents(
    [
      { recordId: 'mine-open', values: { subject: 'a', due_at: null, owner_id: 'u-1', status: 'Open' } },
      { recordId: 'mine-done', values: { subject: 'b', due_at: null, owner_id: 'u-1', status: 'Completed' } },
      { recordId: 'theirs', values: { subject: 'c', due_at: null, owner_id: 'u-2', status: 'Open' } },
      { recordId: 'masked', values: { subject: 'd', due_at: null, owner_id: null, status: 'Open' } },
    ],
    weekOf(new Date(2026, 7, 5)).monday,
  )

  it('keeps every row this person owns, finished or not', () => {
    expect(ownedBy(events, 'u-1').map((event) => event.id)).toEqual(['mine-open', 'mine-done'])
  })

  it('leaves out somebody else"s row even when it is open', () => {
    expect(ownedBy(events, 'u-1').map((event) => event.id)).not.toContain('theirs')
  })

  it('does not claim a row whose owner was masked', () => {
    // Field-level security can redact `owner_id`. Treating that as a match would put another
    // person's week on this one's screen — the exact failure the switch exists to avoid.
    expect(ownedBy(events, 'u-1').map((event) => event.id)).not.toContain('masked')
    expect(ownedBy(events, '')).toEqual([])
  })
})
