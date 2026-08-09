import { RouterProvider } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AppProviders } from '../providers'
import { router } from '../router'

/**
 * What the application does with an address it has no screen for.
 */

beforeEach(() => {
  localStorage.clear()

  // jsdom has no scrolling, and the router restores scroll on every navigation.
  vi.stubGlobal('scrollTo', () => undefined)
})

async function open(path: string) {
  window.history.pushState({}, '', path)

  render(
    <AppProviders>
      <RouterProvider router={router} />
    </AppProviders>,
  )

  await screen.findByRole('navigation', { name: 'Applications' })
}

describe('an address this build has no screen for', () => {
  it('says the address is unknown rather than the router’s bare “Not Found”', async () => {
    await open('/exec/forcast')

    expect(await screen.findByText(/no screen at that address/i)).toBeInTheDocument()

    // The router's own default. Inside a CRM it reads as the record being missing, which is a
    // claim about the tenant's data — and nothing had been asked of the tenant at all.
    expect(screen.queryByText('Not Found')).not.toBeInTheDocument()
  })

  it('names the address it did not match, and says nothing was asked of the server', async () => {
    await open('/exec/forcast')

    const said = await screen.findByText(/Nothing was asked of the server/i)

    expect(said).toHaveTextContent('/exec/forcast')
  })

  it('is not dressed as a refusal, because no server refused anything', async () => {
    await open('/exec/forcast')

    await screen.findByText(/no screen at that address/i)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('keeps the shell, so there is a way back', async () => {
    await open('/exec/forcast')

    await screen.findByText(/no screen at that address/i)

    expect(screen.getByRole('navigation', { name: 'Applications' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to the console' })).toHaveAttribute('href', '/')
  })
})
