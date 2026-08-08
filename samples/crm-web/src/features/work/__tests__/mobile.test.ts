import { describe, expect, it } from 'vitest'
import type { RecordView } from '@/api/contracts'
import { recentlyMoved } from '../mobile'

/**
 * The phone preview's "Recent" tab.
 *
 * IT WAS THE FIRST SIX ROWS OF A LIST ORDERED BY PRIMARY KEY. Under a heading saying "Recent"
 * that is invisible: six deals are six deals. The list never changed, whatever anybody did to
 * the pipeline, which is the part nobody would notice until they went looking for a deal they
 * had just moved.
 */
function deal(id: string, movedAt: string | null): RecordView {
  return { recordId: id, values: { name: id, stage_entered_at: movedAt } }
}

describe('recentlyMoved', () => {
  it('puts the most recently moved first, whatever order they arrived in', () => {
    const rows = [
      deal('old', '2026-01-04T09:00:00+00:00'),
      deal('newest', '2026-08-08T13:08:36.2701+00:00'),
      deal('middle', '2026-05-05T09:00:00+00:00'),
    ]

    expect(recentlyMoved(rows, 6).map((row) => row.recordId)).toEqual(['newest', 'middle', 'old'])
  })

  it('sorts a row that has never moved to the bottom rather than the top', () => {
    const rows = [deal('never', null), deal('moved', '2026-02-02T00:00:00+00:00')]

    expect(recentlyMoved(rows, 6).map((row) => row.recordId)).toEqual(['moved', 'never'])
  })

  it('takes the newest n and not the first n', () => {
    const rows = [
      deal('a', '2026-01-01T00:00:00+00:00'),
      deal('b', '2026-02-01T00:00:00+00:00'),
      deal('c', '2026-03-01T00:00:00+00:00'),
    ]

    expect(recentlyMoved(rows, 2).map((row) => row.recordId)).toEqual(['c', 'b'])
  })

  it('leaves the caller"s array alone', () => {
    const rows = [deal('a', '2026-01-01T00:00:00+00:00'), deal('b', '2026-09-01T00:00:00+00:00')]

    recentlyMoved(rows, 2)

    expect(rows.map((row) => row.recordId)).toEqual(['a', 'b'])
  })
})
