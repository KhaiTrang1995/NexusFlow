import type { PlanQualificationRow, QualificationElement } from '@/api/contracts'

/**
 * The qualification checklist, whole, over the answers a plan happens to carry.
 *
 * <strong>The denominator was the number of rows the server sent.</strong> `/planning/plan`
 * returns only the elements somebody has recorded, so a deal nobody has qualified came back with
 * an empty list and the panel header read "0 of 0 answered" — which is the sentence a finished
 * checklist produces. The roll-up on the portfolio screen divides by the vocabulary and said
 * 0 of 8 about the same deal on the same afternoon.
 *
 * <strong>The eight are a closed list, so they are drawn whether or not they are answered.</strong>
 * An unasked question and an answered one are the same absence in a table of recorded rows, and
 * the unasked ones are the whole value of a checklist: a deal nobody can name the economic buyer
 * of is not a qualified deal, and that only shows if the line is on the screen.
 *
 * <strong>A ninth is kept, not dropped.</strong> If the server grows an element this build has
 * never heard of it is appended rather than discarded — a row the server sent that the screen
 * silently swallows is the failure this whole pass exists for, in the other direction.
 */
export interface QualificationLine {
  element: string
  /** What to call it in a sentence. The enum name for anything this build does not know. */
  label: string
  /** What the question actually asks. Empty for an element this build does not know. */
  asks: string
  isAnswered: boolean
  note: string
  /** Whether anything at all has been recorded against it. */
  isRecorded: boolean
  /**
   * Whether this build knows the element well enough to write it back.
   *
   * False only for one that arrived from a newer server. Posting a name this client has not been
   * told is a guess at somebody else's vocabulary, and the refusal would land on a reader who did
   * nothing wrong — so the row is shown and left alone.
   */
  isDeclared: boolean
}

/**
 * The vocabulary, in the order a deal is worked rather than alphabetically.
 *
 * The wording is the server's own documentation of each element, which is where the definition
 * lives; a client that invented its own phrasing would be two definitions of "qualified".
 */
export const ELEMENTS: readonly { element: QualificationElement; label: string; asks: string }[] = [
  { element: 'IdentifiedPain', label: 'Identified pain', asks: 'The problem that makes doing nothing worse than buying.' },
  { element: 'Metrics', label: 'Metrics', asks: 'What the customer will measure, in their numbers.' },
  { element: 'EconomicBuyer', label: 'Economic buyer', asks: 'Who can spend the money without asking anybody.' },
  { element: 'Champion', label: 'Champion', asks: 'Somebody inside who wants this to happen and can act.' },
  { element: 'DecisionCriteria', label: 'Decision criteria', asks: 'What has to be true for them to sign.' },
  { element: 'DecisionProcess', label: 'Decision process', asks: 'The steps their side takes to get to a signature, and who takes them.' },
  { element: 'PaperProcess', label: 'Paper process', asks: 'What their legal, procurement and security teams do, and how long it takes.' },
  { element: 'Competition', label: 'Competition', asks: 'Who else they are talking to, and why they would win.' },
]

export function qualificationOf(rows: readonly PlanQualificationRow[]): QualificationLine[] {
  const recorded = new Map(rows.map((row) => [row.element, row]))

  const known = ELEMENTS.map((item) => {
    const answer = recorded.get(item.element)

    return {
      element: item.element,
      label: item.label,
      asks: item.asks,
      isAnswered: answer?.isAnswered ?? false,
      note: answer?.note ?? '',
      isRecorded: answer !== undefined,
      isDeclared: true,
    }
  })

  const unknown = rows
    .filter((row) => !ELEMENTS.some((item) => item.element === row.element))
    .map((row) => ({
      element: row.element,
      label: row.element,
      asks: '',
      isAnswered: row.isAnswered,
      note: row.note,
      isRecorded: true,
      isDeclared: false,
    }))

  return [...known, ...unknown]
}
