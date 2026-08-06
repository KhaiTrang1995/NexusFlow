import { useRef } from 'react'
import type { KeyboardEvent, ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './Tabs.module.css'

export interface TabItem<Id extends string = string> {
  id: Id
  label: ReactNode
  /** A count beside the label. Omitted when zero rather than shown as a nought. */
  badge?: number
}

export interface TabsProps<Id extends string = string> {
  /** What this set of tabs selects between. */
  label: string
  items: readonly TabItem<Id>[]
  selected: Id
  onSelect: (id: Id) => void
  /** The quieter variant used inside a record page. */
  variant?: 'chrome' | 'underlined'
  /** Pushed to the right of the strip. */
  trailing?: ReactNode
  className?: string | undefined
}

/**
 * A tab strip.
 *
 * **Arrow keys move between tabs.** This is the one interaction a hand-rolled tab strip always
 * omits and the one a keyboard user reaches for first; the roving handler is nine lines and the
 * alternative is thirty tab-stops to reach the last tab on the record page.
 */
export function Tabs<Id extends string = string>({
  label,
  items,
  selected,
  onSelect,
  variant = 'chrome',
  trailing,
  className,
}: TabsProps<Id>) {
  const strip = useRef<HTMLDivElement>(null)

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0
    if (step === 0) return

    event.preventDefault()
    const index = items.findIndex((item) => item.id === selected)
    const next = items[(index + step + items.length) % items.length]
    if (!next) return

    onSelect(next.id)
    strip.current?.querySelector<HTMLButtonElement>(`[data-tab="${next.id}"]`)?.focus()
  }

  return (
    <div
      ref={strip}
      role="tablist"
      aria-label={label}
      onKeyDown={onKeyDown}
      className={cx(styles.strip, variant === 'underlined' && styles.underlined, className)}
    >
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          role="tab"
          data-tab={item.id}
          aria-selected={item.id === selected}
          tabIndex={item.id === selected ? 0 : -1}
          className={styles.tab}
          onClick={() => onSelect(item.id)}
        >
          {item.label}
          {item.badge ? <span className={styles.badge}>{item.badge}</span> : null}
        </button>
      ))}
      {trailing}
    </div>
  )
}
