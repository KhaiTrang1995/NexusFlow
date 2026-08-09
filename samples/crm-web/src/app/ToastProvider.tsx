import { createContext, use, useCallback, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError } from '@/api/client'
import { cx } from '@/lib/cx'
import styles from './ToastProvider.module.css'

/**
 * The confirmations and refusals that appear at the bottom of the screen.
 *
 * **A refusal is announced, not just shown.** The deck is a live region, so a screen reader says
 * what happened rather than leaving the person to wonder whether their click landed. Failures are
 * `assertive` and successes are `polite`, because interrupting somebody to tell them a thing
 * worked is rude and interrupting them to say it did not is the whole point.
 */

export type ToastKind = 'saved' | 'failed'

interface Toast {
  id: number
  kind: ToastKind
  message: string
}

interface ToastApi {
  /** A confirmation. */
  saved: (message: string) => void
  /** A refusal. Accepts an error so the backend's own sentence is what gets shown. */
  failed: (error: unknown, fallback?: string) => void
}

const ToastContext = createContext<ToastApi | null>(null)

const LIFETIME_MS = 2600

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<readonly Toast[]>([])
  const nextId = useRef(1)

  const push = useCallback((kind: ToastKind, message: string) => {
    const id = nextId.current++
    setToasts((current) => [...current, { id, kind, message }])
    setTimeout(() => setToasts((current) => current.filter((toast) => toast.id !== id)), LIFETIME_MS)
  }, [])

  const api = useMemo<ToastApi>(
    () => ({
      saved: (message) => push('saved', message),
      failed: (error, fallback) => push('failed', sentenceFor(error, fallback)),
    }),
    [push],
  )

  return (
    <ToastContext value={api}>
      {children}
      <div className={styles.deck}>
        {toasts.map((toast) => (
          <div
            key={toast.id}
            className={cx(styles.toast, toast.kind === 'failed' && styles.failure)}
            role={toast.kind === 'failed' ? 'alert' : 'status'}
            aria-live={toast.kind === 'failed' ? 'assertive' : 'polite'}
          >
            <span className={styles.kind}>{toast.kind}</span>
            {toast.message}
          </div>
        ))}
      </div>
    </ToastContext>
  )
}

/**
 * What to put in the strip for a write that did not succeed.
 *
 * THREE THINGS CAN GO WRONG AND ONLY ONE OF THEM IS A REFUSAL. An `ApiError` is the server having
 * decided, and its problem document is a sentence written for a person — that one is quoted.
 * `fetch` rejects with a `TypeError` when the request never completed: offline, the host down, the
 * connection cut mid-flight. That is not a refusal and the write's outcome is not known — it may
 * well have been applied — so it does not get "That did not go through", and it certainly does not
 * get the browser's own "Failed to fetch" printed where the server's words go. Anything else
 * thrown by a caller is a sentence that caller wrote (the board refuses a move the published
 * process does not contain, without a round trip), so it is shown as written.
 */
function sentenceFor(error: unknown, fallback = 'That did not go through.'): string {
  if (error instanceof ApiError) {
    return error.problem?.detail ?? error.problem?.title ?? error.message
  }

  if (error instanceof TypeError) {
    return 'The server did not answer, so whether this was applied is not known. Check before trying again.'
  }

  return error instanceof Error ? error.message : fallback
}

export function useToast(): ToastApi {
  const api = use(ToastContext)

  if (!api) {
    throw new Error('useToast was called outside a ToastProvider.')
  }

  return api
}
