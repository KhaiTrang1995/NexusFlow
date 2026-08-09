import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import { ApiError } from '@/api/client'
import { Button } from './Button'
import styles from './States.module.css'

/**
 * Nothing here yet.
 *
 * **Say what would be here and how to put it there.** "No data" tells the reader the screen
 * works and nothing else; the useful version names the thing and offers the action.
 */
export function EmptyState({
  title,
  detail,
  action,
}: {
  title: string
  detail?: string
  action?: ReactNode
}) {
  return (
    <div className={styles.state}>
      <p className={styles.title}>{title}</p>
      {detail ? <p className={styles.detail}>{detail}</p> : null}
      {action}
    </div>
  )
}

/**
 * It went wrong.
 *
 * **The server's own words are shown.** Every refusal from this backend is a problem document
 * with a code and a sentence written for a person — throwing that away and rendering "Something
 * went wrong" discards the only part of the response that could have helped.
 */
export function ErrorState({
  error,
  onRetry,
}: {
  error: unknown
  /**
   * Retrying is a refetch, and a refetch returns a promise. Typed `() => void`, every caller
   * handing this `query.refetch` was passing a promise where none was expected — seven of them,
   * each one a rejection nothing would catch. The type says what a retry actually is.
   */
  onRetry?: () => void | Promise<unknown>
}) {
  const problem = error instanceof ApiError ? error.problem : null

  return (
    <div className={styles.state} role="alert">
      <p className={styles.title}>{problem?.title ?? 'That request did not go through'}</p>
      <p className={styles.detail}>
        {problem?.detail ?? (error instanceof Error ? error.message : 'The server did not answer.')}
      </p>
      {problem?.code ? <p className={styles.code}>{problem.code}</p> : null}

      {/*
        NO RETRY ON A REFUSAL THE SERVER WILL REPEAT. A 403 is not a failed request: the caller
        does not hold the permission, and pressing "Try again" produces the same 403 for ever.
        The same is true of a validation refusal. Offering the button anyway teaches people that
        this application's buttons do nothing.
      */}
      {onRetry && !isSettled(error) ? (
        <Button tone="secondary" onClick={() => void onRetry()}>
          Try again
        </Button>
      ) : null}
    </div>
  )
}

/**
 * Whether asking again would get the same answer.
 *
 * Authorization and validation are decisions about the request, not accidents of the moment. A
 * not-found is deliberately not in this list: the row may appear.
 */
export function isSettled(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 401 || error.status === 403 || error.status === 422)
}

/**
 * Placeholder bars while a panel loads.
 *
 * **The bars are decoration, but "still loading" is not.** Hiding the whole thing leaves a reader
 * who is not looking at it with an empty panel and no way to tell a slow read from a finished one
 * that found nothing — so the bars stay hidden and one line of text does not. It is deliberately
 * not a live region: the toast strip owns `role="status"` on these screens, and a second one in
 * every loading panel would be read out over it.
 */
export function Skeleton({ rows = 4, className }: { rows?: number; className?: string }) {
  // A taper, not an arithmetic sequence: past fourteen rows `100 - index * 7` goes negative, the
  // declaration is dropped, and the bar draws full width — a widening stack that reads as content.
  const width = (index: number) => Math.max(35, 100 - index * 7)

  return (
    <div className={cx(styles.stack, className)} aria-busy="true">
      <span className="sr-only">Loading…</span>
      {Array.from({ length: Math.max(0, rows) }, (_, index) => (
        <div key={index} className={styles.bar} style={{ width: `${width(index)}%` }} aria-hidden="true" />
      ))}
    </div>
  )
}

export interface AsyncBoundaryProps<T> {
  query: { data: T | undefined; isPending: boolean; isError: boolean; error: unknown; refetch: () => void }
  /** Rows of skeleton while pending. */
  skeletonRows?: number
  /**
   * The caller has already explained why nothing was asked for. A disabled query is pending for
   * ever, so without this a screen that cannot ask — no period is declared, no record is
   * selected — shows a skeleton that never resolves beside the sentence saying why.
   */
  hidden?: boolean
  children: (data: T) => ReactNode
}

/**
 * Pending, failed, or here — decided in one place.
 *
 * Every screen in this application reads its data through this, so a failure looks the same
 * everywhere and no screen can accidentally render a half-loaded page by forgetting a guard.
 */
export function AsyncBoundary<T>({ query, skeletonRows, hidden, children }: AsyncBoundaryProps<T>) {
  if (hidden === true) return null
  if (query.isError) return <ErrorState error={query.error} onRetry={query.refetch} />
  if (query.isPending || query.data === undefined)
    return <Skeleton {...(skeletonRows !== undefined ? { rows: skeletonRows } : {})} />
  return <>{children(query.data)}</>
}
