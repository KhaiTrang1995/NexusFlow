import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './Tag.module.css'

export type TagTone = 'neutral' | 'accent' | 'outline' | 'positive' | 'warning' | 'critical'

export interface TagProps {
  tone?: TagTone
  /** A leading dot, for a status whose colour is the information. */
  dot?: boolean
  children: ReactNode
  className?: string | undefined
  title?: string | undefined
}

/**
 * A small piece of state beside something else.
 *
 * **Colour is never the only signal.** Every tone carries its own words, so a reader who cannot
 * tell the red from the amber still reads "Breached" and "At risk". The `dot` is an addition to
 * the label, never a replacement for it.
 */
export function Tag({ tone = 'neutral', dot = false, children, className, title }: TagProps) {
  return (
    <span className={cx(styles.tag, styles[tone], className)} title={title}>
      {dot ? <span className={styles.dot} aria-hidden="true" /> : null}
      {children}
    </span>
  )
}
