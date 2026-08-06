import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, newIdempotencyKey, post } from '../client'

/**
 * The one way this application talks to the CRM.
 *
 * THE TEST THIS FILE EXISTS FOR IS THE PROBLEM DOCUMENT. Every refusal from this backend carries a
 * code and a sentence written for a person; a client that threw that away and reported "request
 * failed" would discard the only part of the response that could help. So the parse is asserted,
 * and so is the case where it cannot be parsed at all.
 */
describe('post', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockReset()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  /**
   * A fresh `Response` per call. A `Response` body can be read once, so a mock that resolved the
   * same instance twice fails the second call with "body has already been read" — which looks
   * like a bug in the client and is a bug in the test.
   */
  function replying(body: unknown, init: ResponseInit = {}) {
    return () =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'content-type': 'application/json' },
        ...init,
      })
  }

  it('sends the token and the body to the versioned route', async () => {
    fetchMock.mockImplementation(replying({ ok: true }))

    await post('/service/queue', { mineOnly: true }, { token: 'rep-northwind-token' })

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/v1/crm/service/queue')
    expect(init.method).toBe('POST')
    expect(new Headers(init.headers).get('authorization')).toBe('Bearer rep-northwind-token')
    expect(init.body).toBe('{"mineOnly":true}')
  })

  it('sends an idempotency key only when one is given', async () => {
    fetchMock.mockImplementation(replying({}))

    await post('/service/cases', {}, { token: 't', idempotencyKey: 'abc' })
    expect(new Headers((fetchMock.mock.calls[0] as [string, RequestInit])[1].headers).get('idempotency-key')).toBe('abc')

    await post('/service/queue', {}, { token: 't' })
    expect(new Headers((fetchMock.mock.calls[1] as [string, RequestInit])[1].headers).get('idempotency-key')).toBeNull()
  })

  it('carries the backend’s own words on a refusal', async () => {
    fetchMock.mockImplementation(
      replying(
        {
          code: 'crm.approval_self',
          title: 'Forbidden',
          detail: 'The person who submitted a request cannot be the one who approves it.',
        },
        { status: 403 },
      ),
    )

    const failure = await post('/approvals/decisions', {}, { token: 't' }).catch((error: unknown) => error)

    expect(failure).toBeInstanceOf(ApiError)
    const error = failure as ApiError
    expect(error.status).toBe(403)
    expect(error.is('crm.approval_self')).toBe(true)
    expect(error.message).toContain('cannot be the one who approves it')
  })

  it('still reports the status when the body is not a problem document', async () => {
    // A gateway that answered HTML, or a connection that closed mid-body. Failing to parse the
    // problem must not itself become the error.
    fetchMock.mockImplementation(() => new Response('<html>502</html>', { status: 502 }))

    const failure = (await post('/board', {}, { token: 't' }).catch((error: unknown) => error)) as ApiError

    expect(failure).toBeInstanceOf(ApiError)
    expect(failure.status).toBe(502)
    expect(failure.problem).toBeNull()
    expect(failure.is('anything')).toBe(false)
  })

  it('accepts 204 from a flow that returns nothing', async () => {
    fetchMock.mockImplementation(() => new Response(null, { status: 204 }))

    await expect(post('/labels', {}, { token: 't' })).resolves.toBeUndefined()
  })
})

describe('newIdempotencyKey', () => {
  it('is a fresh key each time', () => {
    // One per attempt the user makes. A retry that minted a new key would be a retry that writes
    // twice, which is the whole thing the header exists to prevent.
    expect(newIdempotencyKey()).not.toBe(newIdempotencyKey())
    expect(newIdempotencyKey()).toMatch(/^[0-9a-f]{32}$/)
  })
})
