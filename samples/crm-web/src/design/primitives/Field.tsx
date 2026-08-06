import { useId } from 'react'
import type {
  InputHTMLAttributes,
  ReactNode,
  SelectHTMLAttributes,
  TextareaHTMLAttributes,
} from 'react'
import { cx } from '@/lib/cx'
import styles from './Field.module.css'

interface FieldShellProps {
  label: string
  /**
   * Hides the label visually and keeps it for a screen reader.
   *
   * For a control in a table cell, where the column header already names it on screen. Removing
   * the label outright would leave the input nameless to anybody not looking at the header.
   */
  hideLabel?: boolean | undefined
  /** Marked with an asterisk and passed to the control. */
  required?: boolean | undefined
  /** What the value is for. */
  hint?: string | undefined
  /** Why it was refused. Replaces the hint while present. */
  error?: string | undefined
  className?: string | undefined
  children: (props: {
    id: string
    'aria-describedby': string | undefined
    'aria-invalid': boolean | undefined
    required: boolean
    className: string
  }) => ReactNode
}

/**
 * The label, hint and error around a control.
 *
 * **The description is wired, not just placed nearby.** A hint rendered under an input is
 * invisible to a screen reader unless `aria-describedby` points at it, and an error message a
 * user cannot hear is an error message that stops them dead.
 */
function FieldShell({ label, hideLabel = false, required = false, hint, error, className, children }: FieldShellProps) {
  const id = useId()
  const describedBy = error ? `${id}-error` : hint ? `${id}-hint` : undefined

  return (
    <div className={cx(styles.field, className)}>
      <label
        htmlFor={id}
        className={cx(hideLabel ? 'sr-only' : styles.label, required && styles.required)}
      >
        {label}
      </label>
      {children({
        id,
        'aria-describedby': describedBy,
        'aria-invalid': error ? true : undefined,
        required,
        className: styles['input'] ?? '',
      })}
      {error ? (
        <p id={`${id}-error`} className={styles.error}>
          {error}
        </p>
      ) : hint ? (
        <p id={`${id}-hint`} className={styles.hint}>
          {hint}
        </p>
      ) : null}
    </div>
  )
}

export interface TextFieldProps
  extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id' | 'className' | 'required'> {
  label: string
  hideLabel?: boolean | undefined
  required?: boolean | undefined
  hint?: string | undefined
  error?: string | undefined
  className?: string | undefined
}

export function TextField({ label, hideLabel, required, hint, error, className, ...rest }: TextFieldProps) {
  return (
    <FieldShell
      label={label}
      {...(hideLabel !== undefined ? { hideLabel } : {})}
      {...(required !== undefined ? { required } : {})}
      {...(hint !== undefined ? { hint } : {})}
      {...(error !== undefined ? { error } : {})}
      {...(className !== undefined ? { className } : {})}
    >
      {(bound) => <input {...rest} {...bound} />}
    </FieldShell>
  )
}

export interface TextAreaFieldProps
  extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id' | 'className' | 'required'> {
  label: string
  hideLabel?: boolean | undefined
  required?: boolean | undefined
  hint?: string | undefined
  error?: string | undefined
  className?: string | undefined
}

export function TextAreaField({
  label,
  hideLabel,
  required,
  hint,
  error,
  className,
  ...rest
}: TextAreaFieldProps) {
  return (
    <FieldShell
      label={label}
      {...(hideLabel !== undefined ? { hideLabel } : {})}
      {...(required !== undefined ? { required } : {})}
      {...(hint !== undefined ? { hint } : {})}
      {...(error !== undefined ? { error } : {})}
      {...(className !== undefined ? { className } : {})}
    >
      {(bound) => <textarea {...rest} {...bound} />}
    </FieldShell>
  )
}

export interface SelectFieldProps
  extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id' | 'className' | 'required'> {
  label: string
  hideLabel?: boolean | undefined
  required?: boolean | undefined
  hint?: string | undefined
  error?: string | undefined
  className?: string | undefined
  options: readonly { value: string; label: string }[]
  /** Shown first and selected when the value is empty. Omit to require a choice. */
  placeholder?: string | undefined
}

export function SelectField({
  label,
  hideLabel,
  required,
  hint,
  error,
  className,
  options,
  placeholder,
  ...rest
}: SelectFieldProps) {
  return (
    <FieldShell
      label={label}
      {...(hideLabel !== undefined ? { hideLabel } : {})}
      {...(required !== undefined ? { required } : {})}
      {...(hint !== undefined ? { hint } : {})}
      {...(error !== undefined ? { error } : {})}
      {...(className !== undefined ? { className } : {})}
    >
      {(bound) => (
        <select {...rest} {...bound}>
          {placeholder ? <option value="">{placeholder}</option> : null}
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      )}
    </FieldShell>
  )
}

/** A read-only label/value pair, as the record page and every peek panel draw them. */
export function FieldRow({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className={styles.row}>
      <div className={styles.rowLabel}>{label}</div>
      <div className={styles.rowValue}>{children}</div>
    </div>
  )
}

/** A form section's column grid. */
export function FieldGrid({ columns = 2, children }: { columns?: number; children: ReactNode }) {
  return (
    <div className={styles.grid} style={{ '--field-columns': columns } as React.CSSProperties}>
      {children}
    </div>
  )
}
