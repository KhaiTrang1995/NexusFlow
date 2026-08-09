import { QueryClient, QueryClientProvider, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError } from '@/api/client'
import { SessionProvider, useSession } from '@/session/SessionProvider'
import { ToastProvider } from './ToastProvider'

/**
 * Builds the query client.
 *
 * **A refusal is not retried.** The default retries three times, which is right for a socket that
 * dropped and wrong for a 403: the server has already decided, and asking again three times
 * turns one clear refusal into four seconds of spinner and then the same refusal. Only a 5xx or a
 * transport failure is worth a second attempt.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        retry: (failureCount, error) => {
          if (error instanceof ApiError && error.status < 500) return false
          return failureCount < 2
        },
        refetchOnWindowFocus: false,
      },
      mutations: {
        // Never. A write that failed is a write the person should be told about, and a silent
        // second attempt of something with an idempotency key is at best pointless.
        retry: false,
      },
    },
  })
}

export function AppProviders({ children }: { children: ReactNode }) {
  // Held in state rather than built at module scope, so a test can mount two independent apps
  // and a fast-refresh does not throw the cache away mid-edit.
  const [client] = useState(createQueryClient)

  return (
    <QueryClientProvider client={client}>
      <SessionProvider>
        <CacheScopedToTheCaller>
          <ToastProvider>{children}</ToastProvider>
        </CacheScopedToTheCaller>
      </SessionProvider>
    </QueryClientProvider>
  )
}

/**
 * Empties the cache when the person changes.
 *
 * **A cache key carries the tenant and not the caller.** That is right for what the keys were
 * written against — row-level security is by tenant — but the server also scopes by the token's
 * subject: an approval inbox is what is waiting on *you*, and a worklist filtered to "mine" is
 * resolved from the claim rather than from anything in the key. So switching rep → manager, which
 * keeps the tenant and changes the token, left every one of those entries in place. For the
 * thirty seconds they stayed fresh the manager was shown the representative's rows, under the
 * manager's name, with no request made — and an inbox that is empty because it was somebody
 * else's reads exactly like an inbox with nothing in it.
 *
 * **Cleared during render, not in an effect.** An effect runs after the children have already
 * committed, so the wrong rows get one painted frame before the refetch. Adjusting state during
 * render is the documented way to react to a changed prop, and `clear()` is idempotent under the
 * double render StrictMode does.
 */
function CacheScopedToTheCaller({ children }: { children: ReactNode }) {
  const { token, tenantId } = useSession()
  const client = useQueryClient()

  const caller = `${tenantId} ${token}`
  const [previous, setPrevious] = useState(caller)

  if (previous !== caller) {
    setPrevious(caller)
    client.clear()
  }

  return <>{children}</>
}
