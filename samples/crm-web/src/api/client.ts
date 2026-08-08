/**
 * The one way this application talks to the CRM.
 *
 * EVERY ENDPOINT IS A POST. That is not an oversight in the backend — a FlowX flow is triggered,
 * not fetched, and its input is a contract rather than a query string. It means the usual
 * REST-shaped client is the wrong shape here, and a thin one written for this API is smaller than
 * the configuration a generic one would need.
 *
 * A REFUSAL IS A DOCUMENT, NOT A STATUS. The backend answers every refusal with
 * `application/problem+json` carrying a code and a sentence written for a person. Throwing that
 * away and reporting "request failed" discards the only part of the response that could help,
 * so it is parsed here and carried on the error.
 */

/** What the backend sends when it refuses. */
export interface Problem {
  /** The machine-readable code, e.g. `crm.approval_self`. */
  code?: string
  title?: string
  detail?: string
  status?: number
  type?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: Problem | null

  constructor(status: number, problem: Problem | null) {
    super(problem?.detail ?? problem?.title ?? `The server answered ${status}.`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** Whether this refusal is the one named. Saves every caller writing the string twice. */
  is(code: string): boolean {
    return this.problem?.code === code
  }
}

export interface RequestOptions {
  /**
   * The caller's token. The sample maps three constants to claims; a real deployment puts an
   * access token here and nothing else about this module changes.
   */
  token: string
  /**
   * Makes the write safe to repeat. Required by the backend on every idempotent endpoint, and
   * generated per attempt rather than per retry — a retry that mints a new key is a retry that
   * writes twice.
   */
  idempotencyKey?: string
  signal?: AbortSignal
}

const BASE = '/api/v1/crm'

/**
 * Sends one request.
 *
 * The response type is the caller's claim about what comes back. It is checked by the tests that
 * exercise each surface against the real backend, not at runtime — a validator over every
 * response would double the size of this file and catch what the compiler already knows from the
 * contracts in `contracts.ts`.
 */
export async function post<Response, Body = unknown>(
  path: string,
  body: Body,
  options: RequestOptions,
): Promise<Response> {
  const headers: Record<string, string> = {
    'content-type': 'application/json',
    accept: 'application/json',
    authorization: `Bearer ${options.token}`,
  }

  if (options.idempotencyKey) headers['idempotency-key'] = options.idempotencyKey

  const response = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
    ...(options.signal ? { signal: options.signal } : {}),
  })

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response))
  }

  // 204 is a legitimate answer from a flow that returns nothing.
  if (response.status === 204) return undefined as Response

  return (await response.json()) as Response
}

async function readProblem(response: Response): Promise<Problem | null> {
  try {
    const body: unknown = await response.json()
    return body !== null && typeof body === 'object' ? (body as Problem) : null
  } catch {
    // A gateway that answered HTML, or a connection that closed mid-body. The status is still
    // worth reporting, so a failure to parse the problem is not itself an error.
    return null
  }
}

/** A fresh idempotency key. One per attempt the user makes, not one per network retry. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID().replace(/-/g, '')
}

/**
 * Reads a JSON document served beside the API rather than by it.
 *
 * NO TOKEN AND NO TENANT. The manifest names flow ids, profiles and routes and names no data; the
 * server serves it anonymously for the same reason it serves the OpenAPI document that way. A
 * helper that sent credentials would imply this is scoped to somebody, and it is not.
 *
 * @param path The absolute path, which is not under the CRM prefix.
 * @param signal Cancels the fetch.
 */
export async function getDocument<Response>(path: string, signal?: AbortSignal): Promise<Response> {
  const response = await fetch(path, {
    headers: { accept: 'application/json' },
    ...(signal ? { signal } : {}),
  })

  if (!response.ok) {
    throw new ApiError(response.status, null)
  }

  return (await response.json()) as Response
}
