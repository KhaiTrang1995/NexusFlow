import { useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { useSession } from '@/session/SessionProvider'
import type { Persona } from '@/session/SessionProvider'
import { AppProviders, createQueryClient } from '../providers'

/**
 * The cache, and who it belongs to.
 */

/** The shape of the retry predicate the query client is built with. */
type Retry = (failureCount: number, error: Error) => boolean

function retryOf(client: QueryClient): Retry {
  const retry = client.getDefaultOptions().queries?.retry

  expect(typeof retry).toBe('function')

  return retry as Retry
}

let client: QueryClient | null = null

function Harness() {
  const session = useSession()

  client = useQueryClient()

  return (
    <div>
      <p data-testid="who">{session.persona}</p>
      {(['rep', 'manager', 'contoso'] as Persona[]).map((persona) => (
        <button key={persona} type="button" onClick={() => session.switchTo(persona)}>
          {persona}
        </button>
      ))}
    </div>
  )
}

const INBOX = ['approvals', 'crm-northwind', 'inbox']

beforeEach(() => {
  localStorage.clear()
  client = null
})

describe('the cache, when the person using the application changes', () => {
  it('is emptied, because a key carries the tenant and the server scopes by the token', async () => {
    render(
      <AppProviders>
        <Harness />
      </AppProviders>,
    )

    // What is waiting on the representative. Both personas are in the same tenant, so this key is
    // the same string for either of them.
    client?.setQueryData(INBOX, { items: [] })
    expect(client?.getQueryData(INBOX)).toEqual({ items: [] })

    await userEvent.click(screen.getByRole('button', { name: 'manager' }))

    expect(screen.getByTestId('who')).toHaveTextContent('manager')
    expect(client?.getQueryData(INBOX)).toBeUndefined()
  })

  it('is kept when the same persona is chosen again, so a stray click costs nothing', async () => {
    localStorage.setItem('crm-web.persona', 'manager')

    render(
      <AppProviders>
        <Harness />
      </AppProviders>,
    )

    client?.setQueryData(INBOX, { items: ['one'] })

    await userEvent.click(screen.getByRole('button', { name: 'manager' }))

    expect(client?.getQueryData(INBOX)).toEqual({ items: ['one'] })
  })
})

describe('the query defaults', () => {
  it('does not ask again after a refusal the server has already made', () => {
    const retry = retryOf(createQueryClient())

    expect(retry(0, new ApiError(403, { code: 'authorization.permission_denied' }))).toBe(false)
    expect(retry(0, new ApiError(404, null))).toBe(false)
  })

  it('asks again after a fault, and gives up rather than hammering', () => {
    const retry = retryOf(createQueryClient())

    expect(retry(0, new ApiError(500, null))).toBe(true)
    expect(retry(0, new TypeError('Failed to fetch'))).toBe(true)
    expect(retry(2, new ApiError(500, null))).toBe(false)
  })

  it('never repeats a write', () => {
    expect(createQueryClient().getDefaultOptions().mutations?.retry).toBe(false)
  })
})
