import { useMemo, useState } from 'react'
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
  /** Shown instead of an empty body. Say what would be here, not "no data". */
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
 * **A clickable row is still keyboard-reachable.** The row carries the click, and the first cell
 * carries a real link or button — a `<tr onClick>` alone is invisible to everything but a mouse.
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

    return [...rows].sort((a, b) => {
      const left = read(a)
      const right = read(b)
      if (left === right) return 0
      return (left < right ? -1 : 1) * sign
    })
  }, [rows, sort, columns])

  function toggle(id: string) {
    setSort((current) =>
      current?.id === id
        ? { id, direction: current.direction === 'asc' ? 'desc' : 'asc' }
        : { id, direction: 'asc' },
    )
  }

  if (rows.length === 0 && empty) {
    return <div className={styles.empty}>{empty}</div>
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
          {ordered.map((row) => (
            <tr
              key={rowKey(row)}
              className={cx(isRowSelected?.(row) && styles.selected)}
              {...(onRowClick ? { onClick: () => onRowClick(row) } : {})}
            >
              {columns.map((column) => (
                <td key={column.id} className={cx(column.numeric && styles.numeric)}>
                  {column.cell(row)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
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
