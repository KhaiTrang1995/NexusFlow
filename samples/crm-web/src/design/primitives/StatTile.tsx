import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { Panel } from './Panel'
import styles from './StatTile.module.css'

export type Direction = 'up' | 'down' | 'flat'

export interface StatTileProps {
  label: string
  value: ReactNode
  /** The change beside the value. */
  delta?: ReactNode
  /**
   * Whether the change is good news. Separate from its sign, because a falling sales cycle is up
   * and a falling win rate is down — the number alone cannot say which.
   */
  direction?: Direction
  /** What the number is against. */
  note?: ReactNode
  /** Makes the whole tile a button. */
  onActivate?: () => void
  /** What opens if it is activated, said out loud. */
  drillLabel?: string
}

/**
 * One headline number.
 *
 * **The direction is given, not inferred from the delta's sign.** A cycle time falling by five
 * days is good and a win rate falling by three points is not; a tile that coloured both by the
 * sign would be confidently wrong on half the screens here.
 */
export function StatTile({
  label,
  value,
  delta,
  direction = 'flat',
  note,
  onActivate,
  drillLabel,
}: StatTileProps) {
  const body = (
    <>
      <div className={styles.label}>
        <span className={styles.labelText}>{label}</span>
        {onActivate ? (
          <span className={styles.chevron} aria-hidden="true">
            ›
          </span>
        ) : null}
      </div>
      <div className={styles.value}>{value}</div>
      {delta || note ? (
        <div className={styles.foot}>
          {delta ? <span className={styles[direction]}>{delta}</span> : null}
          {note ? <span>{note}</span> : null}
        </div>
      ) : null}
    </>
  )

  return (
    <Panel
      padding="tight"
      {...(onActivate ? { onActivate } : {})}
      {...(drillLabel ? { title: `Open ${drillLabel}` } : {})}
    >
      {body}
    </Panel>
  )
}

export interface StatGridProps {
  /** How many across. The prototype uses five on the console and four elsewhere. */
  columns?: number
  children: ReactNode
  className?: string
}

export function StatGrid({ columns = 5, children, className }: StatGridProps) {
  return (
    <div
      className={cx(styles.grid, className)}
      style={{ '--tile-columns': columns } as React.CSSProperties}
    >
      {children}
    </div>
  )
}

export interface StatStripCell {
  label: string
  value: ReactNode
  delta?: ReactNode
  direction?: Direction
  note?: ReactNode
  /**
   * 0–1. Draws the hairline bar under the value when present.
   *
   * A fraction that is not a number draws no bar. `width: NaN%` is a declaration the browser
   * drops, and a fill with no width of its own fills its track — so a ratio over a denominator of
   * zero came out as a full bar, which is the one reading it certainly did not mean.
   */
  fraction?: number
}

/** The six-across strip of secondary numbers under a panel header. */
export function StatStrip({ cells }: { cells: readonly StatStripCell[] }) {
  return (
    <div className={styles.strip}>
      {cells.map((cell) => (
        <div key={cell.label} className={styles.stripCell}>
          <div className={styles.stripLabel}>{cell.label}</div>
          <div className={styles.stripValue}>
            <span className={cx(styles.value, styles.compact)}>{cell.value}</span>
            {cell.delta ? (
              <span className={cx(styles[cell.direction ?? 'flat'])} style={{ fontSize: 13 }}>
                {cell.delta}
              </span>
            ) : null}
          </div>
          {cell.fraction !== undefined && Number.isFinite(cell.fraction) ? (
            <div
              style={{
                height: 5,
                borderRadius: 3,
                background: 'var(--color-neutral-300)',
                marginTop: 7,
              }}
            >
              <div
                style={{
                  height: '100%',
                  borderRadius: 3,
                  background: 'var(--color-accent)',
                  width: `${Math.max(0, Math.min(100, Math.round((cell.fraction ?? 0) * 100)))}%`,
                }}
              />
            </div>
          ) : null}
          {cell.note ? <div className={styles.stripNote}>{cell.note}</div> : null}
        </div>
      ))}
    </div>
  )
}
