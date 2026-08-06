import type { ElementType, HTMLAttributes, ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './Panel.module.css'

export type PanelPadding = 'flush' | 'tight' | 'padded'

export interface PanelProps extends HTMLAttributes<HTMLElement> {
  /** How much room inside. `flush` when the panel owns its own header and rows. */
  padding?: PanelPadding
  /** Renders as a button and gains a hover affordance. */
  onActivate?: () => void
  /** What element to be. `section` by default; `article` for a repeated card in a list. */
  as?: ElementType
  children?: ReactNode
}

/**
 * A framed region.
 *
 * **An activatable panel is a real button, not a div with a click handler.** A whole card that
 * navigates somewhere is one of the most common things on these screens, and the div version is
 * unreachable by keyboard and silent to a screen reader. Passing `onActivate` swaps the element.
 */
export function Panel({
  padding = 'padded',
  onActivate,
  as,
  className,
  children,
  ...rest
}: PanelProps) {
  const Element: ElementType = onActivate ? 'button' : (as ?? 'section')

  return (
    <Element
      {...rest}
      {...(onActivate ? { type: 'button', onClick: onActivate } : {})}
      className={cx(styles.panel, styles[padding], onActivate && styles.interactive, className)}
    >
      {children}
    </Element>
  )
}

export interface PanelHeaderProps {
  title: ReactNode
  /** The quiet line beside the title — a count, a scope, a caveat. */
  note?: ReactNode
  /** Buttons and links, pushed to the right. */
  actions?: ReactNode
  className?: string | undefined
}

export function PanelHeader({ title, note, actions, className }: PanelHeaderProps) {
  return (
    <header className={cx(styles.head, className)}>
      <h2 className={styles.headTitle}>{title}</h2>
      {note ? <span className={styles.headNote}>{note}</span> : null}
      {actions ? <div className={styles.headActions}>{actions}</div> : null}
    </header>
  )
}

export function PanelBody({ className, children, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div {...rest} className={cx(styles.body, className)}>
      {children}
    </div>
  )
}
