import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError } from '@/api/client'
import { SessionProvider } from '@/session/SessionProvider'
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
        <ToastProvider>{children}</ToastProvider>
      </SessionProvider>
    </QueryClientProvider>
  )
}
