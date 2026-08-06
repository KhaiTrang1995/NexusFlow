import { forwardRef } from 'react'
import type { ButtonHTMLAttributes, ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './Button.module.css'

export type ButtonTone = 'primary' | 'secondary' | 'ghost' | 'danger'
export type ButtonSize = 'sm' | 'md' | 'lg'

export interface ButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'type'> {
  /** What kind of action this is. Defaults to the quiet one. */
  tone?: ButtonTone
  /** How tall. Defaults to the density the surrounding screen uses. */
  size?: ButtonSize
  /** Square, for a button whose whole label is a glyph. */
  iconOnly?: boolean
  /** Fully rounded, for the filter chips. */
  pill?: boolean
  /** Fills its container. */
  block?: boolean
  /** Native type. Defaults to `button`, never `submit` by accident. */
  type?: 'button' | 'submit' | 'reset'
  children?: ReactNode
}

/**
 * The one button.
 *
 * **`type` defaults to `button`.** React's default is `submit`, which means a button placed in a
 * form for any other reason silently submits it. That is a bug the reader cannot see at the call
 * site, so the default is inverted here and a caller who wants a submit says so.
 *
 * **An icon-only button needs an accessible name.** It has no text, so `aria-label` is not
 * optional decoration — without it the control is unreachable by anybody not looking at it.
 */
export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  {
    tone = 'secondary',
    size = 'md',
    iconOnly = false,
    pill = false,
    block = false,
    type = 'button',
    className,
    children,
    ...rest
  },
  ref,
) {
  if (import.meta.env.DEV && iconOnly && !rest['aria-label'] && !rest.title) {
    // eslint-disable-next-line no-console
    console.warn('Button: an icon-only button needs an aria-label or a title.')
  }

  return (
    <button
      {...rest}
      ref={ref}
      type={type}
      className={cx(
        styles.btn,
        styles[size],
        styles[tone],
        iconOnly && styles.icon,
        pill && styles.pill,
        block && styles.block,
        className,
      )}
    >
      {children}
    </button>
  )
})

export interface ButtonGroupProps {
  /** What the run of buttons is for, read by anybody not looking at it. */
  label: string
  children: ReactNode
  className?: string | undefined
}

/**
 * A run of buttons that reads as one control — the segmented pickers the prototype draws with
 * collapsed borders. The pressed one is marked with `aria-pressed`, which is both what styles it
 * and what a screen reader announces; those cannot drift apart because there is only one.
 */
export function ButtonGroup({ label, children, className }: ButtonGroupProps) {
  return (
    <div role="group" aria-label={label} className={cx(styles.group, className)}>
      {children}
    </div>
  )
}
