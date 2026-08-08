import { describe, expect, it, vi } from 'vitest'
import { together } from '../together'
import type { Readable } from '../together'

function pending<T>(): Readable<T> {
  return { data: undefined, isPending: true, isError: false, error: null, refetch: vi.fn() }
}

function ready<T>(data: T): Readable<T> {
  return { data, isPending: false, isError: false, error: null, refetch: vi.fn() }
}

function failed<T>(error: unknown): Readable<T> {
  return { data: undefined, isPending: false, isError: true, error, refetch: vi.fn() }
}

/**
 * Two reads guarded as one.
 *
 * The capacity screen was guarded by the quotas alone. A refused org chart is not an error to the
 * model that consumes it — every seller's manager is simply unknown — so the screen drew one team
 * that does not exist with the right money in it, and said nothing.
 */
describe('together', () => {
  it('hands both answers over only when both have arrived', () => {
    expect(together(ready(1), pending<string>()).data).toBeUndefined()
    expect(together(pending<number>(), ready('a')).data).toBeUndefined()
    expect(together(ready(1), ready('a')).data).toEqual([1, 'a'])
  })

  it('fails when either fails, whichever it is', () => {
    const boom = new Error('refused')

    expect(together(failed<number>(boom), ready('a'))).toMatchObject({
      isError: true,
      error: boom,
      data: undefined,
    })
    expect(together(ready(1), failed<string>(boom))).toMatchObject({ isError: true, error: boom })
  })

  it('reports a refusal rather than a skeleton while the other read is still in flight', () => {
    // A pair with one refusal is already decided. Reporting it as pending hides the server's own
    // sentence behind a placeholder until the other request happens to come back.
    const pair = together(failed<number>(new Error('refused')), pending<string>())

    expect(pair.isPending).toBe(false)
    expect(pair.isError).toBe(true)
  })

  it('retries both, because the reader pressed one button', () => {
    const left = failed<number>(new Error('refused'))
    const right = ready('a')

    together(left, right).refetch()

    expect(left.refetch).toHaveBeenCalledOnce()
    expect(right.refetch).toHaveBeenCalledOnce()
  })
})
