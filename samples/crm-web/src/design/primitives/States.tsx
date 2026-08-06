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
export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const problem = error instanceof ApiError ? error.problem : null

  return (
    <div className={styles.state} role="alert">
      <p className={styles.title}>{problem?.title ?? 'That request did not go through'}</p>
      <p className={styles.detail}>
        {problem?.detail ?? (error instanceof Error ? error.message : 'The server did not answer.')}
      </p>
      {problem?.code ? <p className={styles.code}>{problem.code}</p> : null}
      {onRetry ? (
        <Button tone="secondary" onClick={onRetry}>
          Try again
        </Button>
      ) : null}
    </div>
  )
}

/** Placeholder bars while a panel loads. */
export function Skeleton({ rows = 4, className }: { rows?: number; className?: string }) {
  return (
    <div className={cx(styles.stack, className)} aria-hidden="true">
      {Array.from({ length: rows }, (_, index) => (
        <div key={index} className={styles.bar} style={{ width: `${100 - index * 7}%` }} />
      ))}
    </div>
  )
}

export interface AsyncBoundaryProps<T> {
  query: { data: T | undefined; isPending: boolean; isError: boolean; error: unknown; refetch: () => void }
  /** Rows of skeleton while pending. */
  skeletonRows?: number
  children: (data: T) => ReactNode
}

/**
 * Pending, failed, or here — decided in one place.
 *
 * Every screen in this application reads its data through this, so a failure looks the same
 * everywhere and no screen can accidentally render a half-loaded page by forgetting a guard.
 */
export function AsyncBoundary<T>({ query, skeletonRows, children }: AsyncBoundaryProps<T>) {
  if (query.isError) return <ErrorState error={query.error} onRetry={query.refetch} />
  if (query.isPending || query.data === undefined)
    return <Skeleton {...(skeletonRows !== undefined ? { rows: skeletonRows } : {})} />
  return <>{children(query.data)}</>
}
