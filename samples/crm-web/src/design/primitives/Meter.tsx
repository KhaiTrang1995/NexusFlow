import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { widthOf } from '@/lib/format'
import styles from './Meter.module.css'

export type MeterTone = 'accent' | 'positive' | 'warning' | 'critical'

export interface MeterProps {
  /** Where the number stands. */
  value: number
  /** What it is measured against. */
  target: number
  tone?: MeterTone
  /** The hairline variant used inside stat strips. */
  thin?: boolean
  /** What the bar is about, for anybody not looking at it. */
  label: string
  className?: string | undefined
}

/**
 * A number against a target.
 *
 * **Over target does not overflow the track.** A seller at 140% of quota is drawn full, and the
 * number beside it says 140% — a bar that ran past its own border would be the only thing on the
 * screen that could not be trusted to stay inside its box.
 *
 * **A meter with no target does not draw a proportion.** There is nothing to be a proportion of.
 * An empty track beside a name reads as "achieved nothing", which is a statement about the
 * seller; a quota of zero is a statement about the plan, and the two cannot share a picture. The
 * same goes for a reading that is not a number — `role="meter"` with `aria-valuemax` of 0, or a
 * `width: NaN%` the browser drops and replaces with a full bar, both announce something nobody
 * measured. Those cases get a marked track that says what is missing instead.
 */
export function Meter({ value, target, tone = 'accent', thin = false, label, className }: MeterProps) {
  const targeted = Number.isFinite(target) && target > 0
  const read = Number.isFinite(value)

  if (!targeted || !read) {
    const why = !targeted ? 'no target set' : 'no reading'

    return (
      <div
        className={cx(styles.meter, thin && styles.thin, styles.unmeasured, className)}
        role="img"
        aria-label={`${label}: ${why}`}
        title={why}
      />
    )
  }

  return (
    <div
      className={cx(styles.meter, thin && styles.thin, className)}
      role="meter"
      aria-label={label}
      aria-valuenow={value}
      aria-valuemin={0}
      aria-valuemax={target}
    >
      <div className={cx(styles.fill, tone !== 'accent' && styles[tone])} style={{ width: widthOf(value, target) }} />
    </div>
  )
}

export interface MeterRowProps extends MeterProps {
  /** What is being measured — a person, a region, a stage. */
  name: ReactNode
  /** The figure shown beside the name. */
  readout: ReactNode
}

/** A labelled meter: name on the left, figure on the right, bar underneath. */
export function MeterRow({ name, readout, ...meter }: MeterRowProps) {
  return (
    <div>
      <div className={styles.row}>
        <span>{name}</span>
        <span className={styles.rowValue}>{readout}</span>
      </div>
      <Meter {...meter} />
    </div>
  )
}
