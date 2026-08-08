import { describe, expect, it } from 'vitest'
import { ELEMENTS, qualificationOf } from '../qualification'
import type { PlanQualificationRow } from '@/api/contracts'

/**
 * The checklist, and the denominator that was the number of rows the server happened to send.
 *
 * A deal nobody has qualified comes back from `/planning/plan` with an empty list, and the panel
 * counted it: "0 of 0 answered", which is what a finished checklist looks like. The portfolio's
 * deal readiness column divides by the vocabulary and said 0/8 about the same deal.
 */
describe('qualificationOf', () => {
  it('asks all eight of an unqualified deal rather than none', () => {
    const lines = qualificationOf([])

    expect(lines).toHaveLength(8)
    expect(lines.every((line) => !line.isRecorded)).toBe(true)
    expect(lines.every((line) => !line.isAnswered)).toBe(true)
  })

  it('keeps the vocabulary in step with the write it feeds', () => {
    // Every element offered has to be one `AnswerQualification` accepts; an invented ninth would
    // be a row with a control that the server refuses.
    expect(new Set(ELEMENTS.map((item) => item.element)).size).toBe(ELEMENTS.length)
    expect(ELEMENTS.every((item) => item.asks.length > 0)).toBe(true)
  })

  it('carries what was recorded onto the line it belongs to', () => {
    const rows: PlanQualificationRow[] = [
      { element: 'EconomicBuyer', isAnswered: true, note: 'Their CFO signs.' },
      { element: 'Competition', isAnswered: false, note: 'Asking the champion.' },
    ]

    const lines = qualificationOf(rows)
    const buyer = lines.find((line) => line.element === 'EconomicBuyer')
    const competition = lines.find((line) => line.element === 'Competition')

    expect(lines).toHaveLength(8)
    expect(lines.filter((line) => line.isAnswered)).toHaveLength(1)
    expect(buyer).toMatchObject({ isAnswered: true, note: 'Their CFO signs.', isRecorded: true })

    // Recorded and not answered is not the same as unasked, and the screen says so differently.
    expect(competition).toMatchObject({ isAnswered: false, isRecorded: true })
  })

  it('keeps an element from a newer server instead of swallowing it', () => {
    // A row the server sent that the screen drops is this whole sweep's defect, in reverse. It is
    // shown and left alone: writing back a name this build was never told is a guess.
    const lines = qualificationOf([
      { element: 'Sustainability', isAnswered: true, note: 'Their board asked.' },
    ])

    const extra = lines.find((line) => line.element === 'Sustainability')

    expect(lines).toHaveLength(9)
    expect(extra).toMatchObject({ isDeclared: false, isRecorded: true, isAnswered: true })
    expect(lines.filter((line) => line.isDeclared)).toHaveLength(8)
  })
})
