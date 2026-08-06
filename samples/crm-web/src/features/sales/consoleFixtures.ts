import type { StatStripCell } from '@/design/primitives'
import type { Series, StackedBar, WaterfallStep } from '@/design/charts'

/**
 * The console's secondary numbers.
 *
 * These are the prototype's own figures. They are separated from the screen so the component
 * reads as layout, and so the day one of them is served by an endpoint the change is to an import.
 */

export const RHYTHM: readonly StatStripCell[] = [
  { label: 'Meetings held', value: '38', delta: '▲ 6', direction: 'up', fraction: 0.76, note: '50 target' },
  { label: 'New pipeline', value: '$612k', delta: '▲ 9%', direction: 'up', fraction: 0.68, note: '$900k target' },
  { label: 'Stage moves', value: '27', delta: '▼ 4', direction: 'down', fraction: 0.54, note: 'vs 31 last week' },
  { label: 'Slipped deals', value: '5', delta: '▲ 2', direction: 'down', fraction: 0.31, note: 'pushed a quarter' },
  { label: 'Proposals out', value: '11', delta: '▲ 3', direction: 'up', fraction: 0.61, note: '18 target' },
  { label: 'Aged > 60d', value: '9', delta: '▼ 1', direction: 'up', fraction: 0.22, note: 'of 41 open' },
]

export const PULSE_SERIES: readonly Series[] = [
  { label: 'Created', colour: 'var(--color-accent-800)', share: '42%' },
  { label: 'Advanced', colour: 'var(--color-accent)', share: '38%' },
  { label: 'Closed', colour: 'var(--color-accent-300)', share: '20%' },
]

export const PULSE_BARS: readonly StackedBar[] = [
  { label: 'w26', values: [5, 4, 2], readout: '11' },
  { label: 'w27', values: [7, 5, 3], readout: '15' },
  { label: 'w28', values: [4, 6, 2], readout: '12' },
  { label: 'w29', values: [8, 7, 4], readout: '19' },
  { label: 'w30', values: [6, 5, 5], readout: '16' },
  { label: 'w31', values: [9, 8, 3], readout: '20' },
  { label: 'w32', values: [7, 6, 4], readout: '17', emphasis: true },
]

export const WATERFALL: readonly WaterfallStep[] = [
  { label: 'Opening', delta: 1_012_000, anchor: true, readout: '$1.01M' },
  { label: 'Created', delta: 612_000, readout: '+$612k' },
  { label: 'Expanded', delta: 84_000, readout: '+$84k' },
  { label: 'Slipped', delta: -196_000, readout: '−$196k' },
  { label: 'Won', delta: -76_000, readout: '−$76k' },
  { label: 'Lost', delta: -54_000, readout: '−$54k' },
  { label: 'Closing', delta: 1_130_000, anchor: true, readout: '$1.13M' },
]

export interface AttainmentRow {
  name: string
  achieved: number
  quota: number
}

export const ATTAINMENT: readonly AttainmentRow[] = [
  { name: 'A. Ruiz', achieved: 488_000, quota: 520_000 },
  { name: 'M. Chen', achieved: 180_000, quota: 300_000 },
  { name: 'J. Park', achieved: 415_000, quota: 400_000 },
  { name: 'K. Osei', achieved: 128_000, quota: 240_000 },
  { name: 'L. Novak', achieved: 92_000, quota: 140_000 },
]
