import { isValidElement, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './DataTable.module.css'

export interface Column<Row> {
  /** Stable identity. Also the sort key. */
  id: string
  header: ReactNode
  /** What to draw in the cell. */
  cell: (row: Row) => ReactNode
  /** Right-aligned and tabular. Use for every amount and count. */
  numeric?: boolean
  /**
   * What to sort by. Absent means the column does not sort — which is the honest answer for a
   * column of rendered markup, rather than sorting by whatever `String(cell)` happens to produce.
   */
  sortValue?: (row: Row) => string | number
  width?: string
}

export type SortDirection = 'asc' | 'desc'

export interface DataTableProps<Row> {
  /** What the table is, for anybody not looking at it. */
  caption: string
  columns: readonly Column<Row>[]
  rows: readonly Row[]
  rowKey: (row: Row) => string
  onRowClick?: (row: Row) => void
  isRowSelected?: (row: Row) => boolean
  striped?: boolean
  /** Shown under the header when there are no rows. Say what would be here, not "no data". */
  empty?: ReactNode
  className?: string | undefined
}

/**
 * The one table.
 *
 * **Sorting is opt-in per column and client-side.** These screens page from the server; sorting
 * what is on the page is the honest scope, and a header that offered to sort a column the server
 * ordered would silently reorder a page rather than the result.
 *
 * **A clickable row is still keyboard-reachable, and the table is what makes it so.** A
 * `<tr onClick>` alone is live to a mouse and to nothing else: no tab stop, no role, no Enter.
 * This used to be a sentence here asking callers to put a real link or button in the first cell,
 * and not one of the six tables in this client did — every one of them passed a plain `<span>`
 * styled to look like a link, so every list, related list and case queue was mouse-only. A rule a
 * primitive states and cannot check is a rule that is not kept, so the primitive keeps it
 * instead: given `onRowClick`, the leftmost cell that is not already a control has its content
 * wrapped in a real button carrying the same activation. The row keeps its own handler, because
 * clicking anywhere along a row is the thing a mouse expects, and the button stops the click
 * travelling so a mouse gets one activation rather than two.
 *
 * **An empty table is still a table, and always says that it is empty.** Returning a bare div
 * instead threw away the header — the one thing on the screen that says what would be here — and
 * the caption with it, so a screen reader was left with a floating sentence and no table to
 * attach it to. Returning the table with an empty body and nothing else, which is what happened
 * when the caller passed no `empty`, is worse: a header row over blank space is what a broken
 * render looks like, and no reader can tell it from a panel that failed halfway.
 */
export function DataTable<Row>({
  caption,
  columns,
  rows,
  rowKey,
  onRowClick,
  isRowSelected,
  striped = true,
  empty,
  className,
}: DataTableProps<Row>) {
  const [sort, setSort] = useState<{ id: string; direction: SortDirection } | null>(null)

  const ordered = useMemo(() => {
    if (!sort) return rows
    const column = columns.find((c) => c.id === sort.id)
    if (!column?.sortValue) return rows
    const read = column.sortValue
    const sign = sort.direction === 'asc' ? 1 : -1

    return [...rows].sort((a, b) => compare(read(a), read(b)) * sign)
  }, [rows, sort, columns])

  function toggle(id: string) {
    setSort((current) =>
      current?.id === id
        ? { id, direction: current.direction === 'asc' ? 'desc' : 'asc' }
        : { id, direction: 'asc' },
    )
  }

  return (
    <div className={cx(styles.wrap, className)}>
      <table
        className={cx(styles.table, striped && styles.striped, onRowClick && styles.clickable)}
      >
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.id}
                scope="col"
                style={column.width ? { width: column.width } : undefined}
                className={cx(column.numeric && styles.numeric)}
                aria-sort={
                  sort?.id === column.id
                    ? sort.direction === 'asc'
                      ? 'ascending'
                      : 'descending'
                    : column.sortValue
                      ? 'none'
                      : undefined
                }
              >
                {column.sortValue ? (
                  <button
                    type="button"
                    className={styles.sortButton}
                    onClick={() => toggle(column.id)}
                  >
                    {column.header}
                    <span className={styles.sortMark} aria-hidden="true">
                      {sort?.id === column.id ? (sort.direction === 'asc' ? '▲' : '▼') : '↕'}
                    </span>
                  </button>
                ) : (
                  column.header
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {ordered.map((row, rowIndex) => {
            const cells = columns.map((column) => column.cell(row))

            // The leftmost cell that can hold a control. Normally the first, and on a contact
            // list whose reader has hidden the name, the account, the title and the role it is
            // whichever comes after the mailto and the tel — those already carry an anchor, and
            // nothing interactive may sit inside a button.
            const open = onRowClick ? cells.findIndex((cell) => !carriesControl(cell)) : -1

            if (import.meta.env.DEV && rowIndex === 0 && onRowClick && open === -1 && cells.length > 0) {
              // eslint-disable-next-line no-console
              console.warn(
                `DataTable "${caption}": every column already carries a control, so the row's own action has nowhere to go and is reachable by mouse only.`,
              )
            }

            return (
              <tr
                key={rowKey(row)}
                className={cx(isRowSelected?.(row) && styles.selected)}
                {...(onRowClick ? { onClick: () => onRowClick(row) } : {})}
              >
                {columns.map((column, index) => (
                  <td key={column.id} className={cx(column.numeric && styles.numeric)}>
                    {onRowClick && index === open ? (
                      <button
                        type="button"
                        className={styles.rowOpen}
                        onClick={(event) => {
                          // The row is listening too. Without this a mouse click here opens the
                          // record twice: a duplicated history entry on a router, or a peek that
                          // opens and immediately re-opens.
                          event.stopPropagation()
                          onRowClick(row)
                        }}
                      >
                        {cells[index]}
                      </button>
                    ) : (
                      cells[index]
                    )}
                  </td>
                ))}
              </tr>
            )
          })}
        </tbody>
      </table>

      {/*
        Outside the body rather than as a row spanning every column: a `<tr>` carrying a sentence
        is a row, and every count of rows on the screen and in the suite would include it.
      */}
      {ordered.length === 0 ? <div className={styles.empty}>{empty ?? 'No rows.'}</div> : null}
    </div>
  )
}

/** What may not sit inside the row's own button, because a control cannot contain a control. */
const CONTROLS = new Set(['a', 'button', 'input', 'select', 'textarea', 'label', 'summary'])

/**
 * Whether a cell already holds something clickable.
 *
 * A `mailto:` in a contact list is the case this exists for. Host elements are read by tag; a
 * component is read by the props that make one a control, because what it renders is not visible
 * from here. That leaves one gap — a control passed through a prop other than `children`, as in
 * `<CellStack primary={<Link/>}/>` — and no caller does it, so the gap is documented rather than
 * closed with a walk of every prop of every element.
 */
function carriesControl(node: ReactNode): boolean {
  if (Array.isArray(node)) return node.some((child) => carriesControl(child as ReactNode))
  if (!isValidElement(node)) return false

  if (typeof node.type === 'string' && CONTROLS.has(node.type)) return true

  const props = node.props as { children?: ReactNode; href?: unknown; to?: unknown; onClick?: unknown }
  if (props.href !== undefined || props.to !== undefined || props.onClick !== undefined) return true

  return carriesControl(props.children)
}

/**
 * Orders two sort keys.
 *
 * **Text is compared as text is read.** `<` on strings compares UTF-16 code units, which puts
 * every lower-case name after every upper-case one — "Zenith" before "acme" — and orders
 * "Region 10" before "Region 9". A column that says it is sorted and is not is worse than a
 * column that never offered.
 */
function compare(left: string | number, right: string | number): number {
  if (typeof left === 'string' && typeof right === 'string') {
    return left.localeCompare(right, 'en', { numeric: true, sensitivity: 'base' })
  }

  // A key that is not a number — NaN from a field that failed to parse — goes to one end rather
  // than making the comparator inconsistent, which leaves every row in an order nothing chose.
  const leftKnown = typeof left === 'number' && Number.isFinite(left)
  const rightKnown = typeof right === 'number' && Number.isFinite(right)
  if (!leftKnown || !rightKnown) return leftKnown ? -1 : rightKnown ? 1 : 0

  return left === right ? 0 : left < right ? -1 : 1
}

/** A cell whose first line is the record and whose second is its context. */
export function CellStack({ primary, secondary }: { primary: ReactNode; secondary?: ReactNode }) {
  return (
    <>
      <span className={styles.link}>{primary}</span>
      {secondary ? <div className={styles.sub}>{secondary}</div> : null}
    </>
  )
}
