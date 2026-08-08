import { ApiError } from '@/api/client'

/**
 * Which onboarding steps a tenant has actually done.
 *
 * <strong>Derived, never remembered.</strong> The screen used to hold a list in component state,
 * seeded with two steps already ticked — so every tenant arrived two-sevenths configured, and
 * ticking one was forgotten on the next page load. A checklist that says "done" about something
 * nobody did is worse than no checklist: it is the one screen a new administrator trusts.
 *
 * <strong>Each step is a question the server already answers.</strong> Nothing here is a new
 * surface: the schema, the published process and the configuration lists are all read by other
 * screens, and a step is done when its evidence exists.
 *
 * <strong>A refused read is not an answer about the tenant, and that was the second fault.</strong>
 * Three of the seven steps read `/config`, which requires `crm.admin`. Every count came through as
 * `?? 0`, so a representative — and every caller of the empty Contoso tenant, which holds no admin
 * token — was told "0 of 5 done" and shown "Until this is done" beside three steps nobody had
 * looked at. A 403 says nothing about whether an SLA policy exists. `unreadable` is the honest
 * third answer, and it is counted apart from the ones that are simply not done.
 *
 * <strong>Two steps have no evidence anywhere and say so.</strong> Layouts are not stored in this
 * build, and permissions are a token's scopes rather than a tenant's configuration — so neither
 * can be ticked by looking, whoever is asking.
 */
export type StepState = 'done' | 'todo' | 'unreadable' | 'unknowable'

/** A count the server answered, or null when the read did not answer at all. */
export type Counted = number | null

export interface Evidence {
  /** Custom objects the tenant declared. */
  objects: Counted
  /** Custom fields declared on the built-in entities. */
  fields: Counted
  /** Whether a process is published for opportunities, or null when the read did not answer. */
  hasProcess: boolean | null
  /** Declared business-hours weeks. */
  businessHours: Counted
  /** Declared SLA policies. */
  slaPolicies: Counted
  /** Declared approval processes. */
  approvals: Counted
}

export function stateOf(step: string, evidence: Evidence): StepState {
  switch (step) {
    case 'objects':
      return fromCounts(evidence.objects)
    case 'fields':
      return fromCounts(evidence.fields)
    case 'stages':
      return evidence.hasProcess === null ? 'unreadable' : evidence.hasProcess ? 'done' : 'todo'
    case 'hours':
      // Both halves, because either alone leaves the promise unmeasurable: a policy with no week
      // is measured in calendar time, and a week with no policy promises nothing.
      return fromCounts(evidence.businessHours, evidence.slaPolicies)
    case 'approvals':
      return fromCounts(evidence.approvals)
    default:
      // Layouts and permissions. Nothing in this build stores either, so there is no evidence to
      // read — and a tick with nothing behind it is what this file exists to stop.
      return 'unknowable'
  }
}

/**
 * A step whose evidence is one or more counts.
 *
 * A zero the server actually sent settles the question — that step is not done. A null settles
 * nothing: the request was refused or never answered, and reporting it as "not done" is a claim
 * about the tenant made from a claim about the caller.
 */
function fromCounts(...counts: readonly Counted[]): StepState {
  if (counts.every((count) => count !== null && count > 0)) return 'done'
  if (counts.some((count) => count === 0)) return 'todo'

  return 'unreadable'
}

/**
 * What a `/processes` read says about whether a process is published.
 *
 * <strong>The 404 is the answer and the 403 is not.</strong> `crm.process_not_published` is how
 * this backend says "nobody has published one", which is a fact about the tenant; any other
 * failure is a fact about the request. Collapsing the two is how a refused read becomes a step
 * telling somebody to go and publish a process that may already exist.
 */
export function publishedProcess(query: {
  isSuccess: boolean
  isError: boolean
  error: unknown
}): boolean | null {
  if (query.isSuccess) return true
  if (query.isError && query.error instanceof ApiError && query.error.status === 404) return false

  return null
}

/** How many steps are done, out of the ones that can be known. */
export function progress(steps: readonly string[], evidence: Evidence) {
  const states = steps.map((step) => stateOf(step, evidence))

  return {
    done: states.filter((state) => state === 'done').length,
    knowable: states.filter((state) => state === 'done' || state === 'todo').length,
    /** Nothing in this build records the answer, for anybody. */
    unknowable: states.filter((state) => state === 'unknowable').length,
    /** This caller was not allowed to look, or the read failed. */
    unreadable: states.filter((state) => state === 'unreadable').length,
  }
}
