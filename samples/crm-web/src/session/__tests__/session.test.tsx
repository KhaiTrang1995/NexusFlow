import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { PERSONAS, SessionProvider, useSession } from '../SessionProvider'
import type { Persona } from '../SessionProvider'

/**
 * Who the application thinks is using it, and what it does when the browser says something odd.
 *
 * Nothing had ever been mounted before this file. The session decides the token on every request
 * and the tenant on every cache key, so a fault here is a fault on every screen at once.
 */

const KEY = 'crm-web.persona'

function Who() {
  const session = useSession()

  return (
    <div>
      <p data-testid="who">{`${session.persona} ${session.token} ${session.tenantId}`}</p>
      <p data-testid="admin">{String(session.can('crm.admin'))}</p>
      <button type="button" onClick={() => session.switchTo('director')}>
        become a director
      </button>
    </div>
  )
}

function mount() {
  return render(
    <SessionProvider>
      <Who />
    </SessionProvider>,
  )
}

beforeEach(() => {
  localStorage.clear()
})

describe('the persona the browser remembers', () => {
  it('is used when this build has it', () => {
    localStorage.setItem(KEY, 'director')
    mount()

    expect(screen.getByTestId('who')).toHaveTextContent('director director-northwind-token')
  })

  it('is ignored when this build no longer has it', () => {
    // The state after a persona is dropped from the build, or after somebody edits the value.
    localStorage.setItem(KEY, 'ops-lead')
    mount()

    expect(screen.getByTestId('who')).toHaveTextContent('rep rep-northwind-token crm-northwind')
  })

  it.each(['constructor', 'toString', 'hasOwnProperty', '__proto__'])(
    'is ignored when it names something every object answers to: %s',
    (inherited) => {
      // `'constructor' in PEOPLE` is true. Reading the table with `in` handed the application
      // `Object`'s own constructor as the signed-in person, and the first permission check threw
      // before a single screen rendered — a white page from one string in localStorage.
      localStorage.setItem(KEY, inherited)
      mount()

      expect(screen.getByTestId('who')).toHaveTextContent('rep rep-northwind-token crm-northwind')
      expect(screen.getByTestId('admin')).toHaveTextContent('false')
    },
  )

  it('is written when the persona is switched, so a reload keeps the chair', async () => {
    mount()
    await userEvent.click(screen.getByRole('button', { name: 'become a director' }))

    expect(localStorage.getItem(KEY)).toBe('director')
    expect(screen.getByTestId('who')).toHaveTextContent('director')
  })
})

describe('every persona this build offers', () => {
  /**
   * The token names the tenant it was minted for, so a pairing that disagrees is visible here
   * rather than as one tenant's rows arriving under another's name. Switching is a single piece
   * of state and both values are read off the same row — this guards the row.
   */
  it.each(PERSONAS.map((persona) => persona.id))('carries a token minted for its tenant: %s', (id: Persona) => {
    render(
      <SessionProvider initialPersona={id}>
        <Who />
      </SessionProvider>,
    )

    const shown = screen.getByTestId('who').textContent ?? ''
    const [, token, tenantId] = shown.split(' ')

    expect(token).toBeDefined()
    expect(tenantId).toMatch(/^crm-/)
    expect(token).toContain(tenantId?.replace('crm-', ''))
  })
})
