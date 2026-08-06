import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './Page.module.css'

export interface PageProps {
  /** `full` for a screen that owns its own scrolling, like a list or a board. */
  layout?: 'scroll' | 'full'
  children: ReactNode
  className?: string | undefined
}

export function Page({ layout = 'scroll', children, className }: PageProps) {
  return (
    <div className={cx(styles.page, layout === 'full' && styles.full, className)}>{children}</div>
  )
}

export interface PageHeaderProps {
  /** The small line above the title — what this screen is of. */
  eyebrow?: ReactNode
  title: ReactNode
  actions?: ReactNode
  /** The variant that sits in a bordered strip, for full-height screens. */
  bar?: boolean
  small?: boolean
}

export function PageHeader({ eyebrow, title, actions, bar = false, small = false }: PageHeaderProps) {
  return (
    <header className={cx(styles.header, bar && styles.headerBar)}>
      <div>
        {eyebrow ? <div className={styles.eyebrow}>{eyebrow}</div> : null}
        <h1 className={cx(styles.title, small && styles.titleSmall)}>{title}</h1>
      </div>
      {actions ? <div className={styles.actions}>{actions}</div> : null}
    </header>
  )
}

export interface FilterOption<T extends string> {
  value: T
  label: string
}

export interface FilterGroupProps<T extends string> {
  label: string
  options: readonly FilterOption<T>[]
  selected: T
  onSelect: (value: T) => void
}

/**
 * One row of the filter bar.
 *
 * The chips are buttons with `aria-pressed`, which is both what colours the chosen one and what a
 * screen reader announces — so the two can never say different things.
 */
export function FilterGroup<T extends string>({
  label,
  options,
  selected,
  onSelect,
}: FilterGroupProps<T>) {
  return (
    <div className={styles.filterGroup} role="group" aria-label={label}>
      <span className={styles.filterLabel}>{label}</span>
      <div className={styles.filterOptions}>
        {options.map((option) => (
          <button
            key={option.value}
            type="button"
            className={styles.chip}
            aria-pressed={option.value === selected}
            onClick={() => onSelect(option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  )
}

export function FilterBar({ children, end }: { children: ReactNode; end?: ReactNode }) {
  return (
    <div className={styles.filterBar}>
      {children}
      {end ? <div className={styles.filterEnd}>{end}</div> : null}
    </div>
  )
}

export type ColumnLayout = 'split' | 'halves' | 'thirds' | 'wide'

export function Columns({ layout, children }: { layout: ColumnLayout; children: ReactNode }) {
  return <div className={cx(styles.columns, styles[layout])}>{children}</div>
}

export function Stack({ children }: { children: ReactNode }) {
  return <div className={styles.stack}>{children}</div>
}
