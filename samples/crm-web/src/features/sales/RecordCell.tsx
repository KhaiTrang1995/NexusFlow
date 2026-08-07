import type { ReactNode } from 'react'
import { Tag } from '@/design/primitives'
import type { TagTone } from '@/design/primitives'
import { date, fullMoney } from '@/lib/format'
import { fieldOf } from '@/fixtures/objects'
import type { ObjectModel, RecordRow } from '@/fixtures/objects'

/**
 * How one field of one record is drawn.
 *
 * ONE FUNCTION, USED BY THE LIST, THE BOARD, THE RECORD PAGE AND EVERY PEEK. The prototype
 * formats a currency in four places and a status in five; four copies is four chances for the
 * board to say $184,000 while the list beside it says $184k. Here the object's own field type
 * decides, so a field added to the model renders correctly everywhere at once.
 */

/**
 * Which words mean trouble.
 *
 * A closed vocabulary rather than a guess: a status this build does not recognise is drawn
 * neutrally rather than coloured by whichever substring happened to match.
 */
const STATUS_TONE: Readonly<Record<string, TagTone>> = {
  // good
  Qualified: 'positive',
  Accepted: 'positive',
  Complete: 'positive',
  Done: 'positive',
  'Closed Won': 'positive',
  Green: 'positive',
  Customer: 'positive',
  // watch
  Working: 'accent',
  'In Progress': 'accent',
  'In Review': 'accent',
  Scheduled: 'accent',
  Sent: 'accent',
  Nurture: 'warning',
  Amber: 'warning',
  High: 'warning',
  // trouble
  Unqualified: 'critical',
  Rejected: 'critical',
  'Closed Lost': 'critical',
  Red: 'critical',
  Critical: 'critical',
  Blocker: 'critical',
}

export function toneFor(value: string): TagTone {
  return STATUS_TONE[value] ?? 'outline'
}

export function renderCell(model: ObjectModel, row: RecordRow, name: string): ReactNode {
  const definition = fieldOf(model, name)
  const value = row[name]

  // The one formula the design carries. Computed rather than stored, so it cannot disagree with
  // the two fields it is made of.
  if (name === 'weighted') {
    return fullMoney(Math.round((Number(row['amount'] ?? 0) * Number(row['probability'] ?? 0)) / 100))
  }

  if (value === undefined || value === null || value === '') return '—'
  if (!definition) return String(value)

  switch (definition.type) {
    case 'currency':
      return fullMoney(Number(value))
    case 'percent':
      return `${value}%`
    case 'number':
      return Number(value).toLocaleString('en-US')
    case 'date':
      // The fixtures carry '2026-08-20' and the server carries
      // '2026-08-28T23:18:06.488811+00:00'. Falling through to String() rendered the first
      // acceptably and the second as a machine's timestamp, so every date column looked correct
      // for as long as the screen it was on was reading fixtures.
      return date(String(value))
    case 'picklist':
      return <Tag tone={toneFor(String(value))}>{String(value)}</Tag>
    case 'email':
      return <a href={`mailto:${String(value)}`}>{String(value)}</a>
    case 'phone':
      return <a href={`tel:${String(value).replace(/\s/g, '')}`}>{String(value)}</a>
    default:
      return String(value)
  }
}

/** Whether a column should be right-aligned and tabular. */
export function isNumeric(model: ObjectModel, name: string): boolean {
  const type = fieldOf(model, name)?.type
  return type === 'currency' || type === 'number' || type === 'percent' || name === 'weighted'
}
