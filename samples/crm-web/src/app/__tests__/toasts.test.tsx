import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { ToastProvider, useToast } from '../ToastProvider'

/**
 * What the strip at the bottom of the screen says when a write does not succeed.
 *
 * Every refusal in this application arrives here, and the sentence it carries is the only thing
 * most people will read about what just happened.
 */

function Thrower({ error }: { error: unknown }) {
  const toast = useToast()

  return (
    <div>
      <button type="button" onClick={() => toast.failed(error, 'That placement was refused.')}>
        fail
      </button>
      <button type="button" onClick={() => toast.saved('The account was saved.')}>
        save
      </button>
    </div>
  )
}

async function press(error: unknown, which: 'fail' | 'save' = 'fail') {
  render(
    <ToastProvider>
      <Thrower error={error} />
    </ToastProvider>,
  )

  await userEvent.click(screen.getByRole('button', { name: which }))
}

describe('a write the server refused', () => {
  it('is reported in the server’s own sentence', async () => {
    await press(
      new ApiError(409, {
        code: 'crm.discount_approval_required',
        title: 'The quote was not issued',
        detail: 'a manager has to approve the discount before this quote can be issued',
      }),
    )

    expect(screen.getByRole('alert')).toHaveTextContent('a manager has to approve the discount')
  })

  it('falls back to the problem’s title, and then to the status, rather than to nothing', async () => {
    await press(new ApiError(500, null))

    expect(screen.getByRole('alert')).toHaveTextContent('The server answered 500.')
  })
})

describe('a write whose answer never arrived', () => {
  /**
   * `fetch` rejects with a `TypeError` when the request did not complete. The strip used to print
   * that object's message, so a dropped connection reached the reader as **Failed to fetch**, in
   * red, in the place the server's own words go — and the caller's fallback sentence, which every
   * call site passes, was never reachable.
   */
  it('does not put the browser’s diagnostic where the server’s sentence goes', async () => {
    await press(new TypeError('Failed to fetch'))

    expect(screen.getByRole('alert')).not.toHaveTextContent('Failed to fetch')
  })

  it('does not claim the write was refused, because nobody knows whether it landed', async () => {
    await press(new TypeError('NetworkError when attempting to fetch resource.'))

    const said = screen.getByRole('alert')

    expect(said).toHaveTextContent('The server did not answer')
    expect(said).toHaveTextContent('not known')
    expect(said).not.toHaveTextContent('That placement was refused.')
  })
})

describe('a refusal the client made without asking', () => {
  /**
   * The board declines a move the published process has no transition for, and says so without a
   * round trip. That sentence is written for the reader and has to survive to the strip intact.
   */
  it('is shown as the caller wrote it', async () => {
    await press(new Error('The published process has no move from Discovery to Closed Won.'))

    expect(screen.getByRole('alert')).toHaveTextContent('no move from Discovery to Closed Won')
  })

  it('uses the caller’s fallback when what was thrown is not an error at all', async () => {
    await press({ oops: true })

    expect(screen.getByRole('alert')).toHaveTextContent('That placement was refused.')
  })
})

describe('the strip itself', () => {
  /**
   * The browser suite finds a toast by `[role=alert], [role=status]` and the screen-reader
   * behaviour is the same distinction: a refusal interrupts, a confirmation waits its turn.
   */
  it('announces a refusal assertively and a confirmation politely', async () => {
    await press(new ApiError(422, { detail: 'That value is not one of the field’s options.' }))

    expect(screen.getByRole('alert')).toHaveAttribute('aria-live', 'assertive')

    await userEvent.click(screen.getByRole('button', { name: 'save' }))

    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
    expect(screen.getByRole('status')).toHaveTextContent('The account was saved.')
  })
})
