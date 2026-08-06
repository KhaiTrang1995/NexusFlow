import { useEffect, useRef } from 'react'
import type { ReactNode } from 'react'
import { Button } from './Button'
import styles from './Drawer.module.css'

export interface DrawerProps {
  /** The dialog's name, announced when it opens. */
  title: ReactNode
  /** The small line above the title — a record number, a code. */
  eyebrow?: ReactNode
  /** The line under the title — an account, a tier. */
  subtitle?: ReactNode
  /** Buttons under the heading. */
  actions?: ReactNode
  onClose: () => void
  width?: number
  children: ReactNode
}

/**
 * The right-hand peek panel.
 *
 * **Escape closes it and focus goes inside when it opens.** A peek that traps neither is one a
 * keyboard user opens and then cannot leave; both are a handful of lines and both are the first
 * things reported when this pattern ships without them.
 */
export function Drawer({ title, eyebrow, subtitle, actions, onClose, width, children }: DrawerProps) {
  const panel = useRef<HTMLDivElement>(null)
  const opener = useRef<Element | null>(null)

  useEffect(() => {
    opener.current = document.activeElement
    panel.current?.focus()

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose()
    }

    document.addEventListener('keydown', onKeyDown)

    return () => {
      document.removeEventListener('keydown', onKeyDown)
      // Focus goes back where it came from. Without this it lands on <body> and the next Tab
      // starts at the top of the page, which is the whole document away from where they were.
      if (opener.current instanceof HTMLElement) opener.current.focus()
    }
  }, [onClose])

  return (
    <div
      className={styles.scrim}
      onClick={(event) => {
        if (event.target === event.currentTarget) onClose()
      }}
    >
      <div
        ref={panel}
        role="dialog"
        aria-modal="true"
        aria-label={typeof title === 'string' ? title : undefined}
        tabIndex={-1}
        className={styles.panel}
        style={width ? ({ '--drawer-width': `${width}px` } as React.CSSProperties) : undefined}
      >
        <header className={styles.head}>
          <div className={styles.headTop}>
            {eyebrow ? <span className={styles.eyebrow}>{eyebrow}</span> : null}
            <Button
              iconOnly
              size="sm"
              aria-label="Close"
              className={styles.close}
              onClick={onClose}
            >
              ✕
            </Button>
          </div>
          <div className={styles.title}>{title}</div>
          {subtitle ? <div className={styles.subtitle}>{subtitle}</div> : null}
          {actions ? <div className={styles.actions}>{actions}</div> : null}
        </header>
        <div className={styles.body}>{children}</div>
      </div>
    </div>
  )
}

export function DrawerSection({ label, note }: { label: string; note?: string }) {
  return (
    <div className={styles.sectionLabel}>
      <span>{label}</span>
      {note ? <span className={styles.sectionNote}>{note}</span> : null}
    </div>
  )
}

export interface Highlight {
  label: string
  value: ReactNode
  tone?: 'default' | 'positive' | 'warning' | 'critical'
}

const HIGHLIGHT_COLOUR: Record<string, string> = {
  default: 'var(--color-text)',
  positive: 'var(--color-positive)',
  warning: 'var(--color-warning)',
  critical: 'var(--color-critical)',
}

/** The strip of numbers under a peek panel's heading. */
export function DrawerHighlights({ items }: { items: readonly Highlight[] }) {
  return (
    <div className={styles.highlights}>
      {items.map((item) => (
        <div key={item.label} style={{ minWidth: 0 }}>
          <div className={styles.highlightLabel}>{item.label}</div>
          <div
            className={styles.highlightValue}
            style={{ color: HIGHLIGHT_COLOUR[item.tone ?? 'default'] }}
          >
            {item.value}
          </div>
        </div>
      ))}
    </div>
  )
}
