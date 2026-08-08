/**
 * Which onboarding steps a tenant has actually done.
 *
 * <strong>Derived, never remembered.</strong> The screen used to hold a list in component state,
 * seeded with two steps already ticked — so every tenant arrived two-sevenths configured, and
 * ticking one was forgotten on the next page load. A checklist that says "done" about something
 * nobody did is worse than no checklist: it is the one screen a new administrator trusts.
 *
 * <strong>Each step is a question the server already answers.</strong> Nothing here is a new
 * surface: the schema, the published process, the configuration lists and the org chart are all
 * read by other screens, and a step is done when its evidence exists.
 *
 * <strong>Two steps have no evidence and say so.</strong> Layouts are not stored anywhere in this
 * build, and permissions are a token's scopes rather than a tenant's configuration — so neither
 * can be ticked by looking, and pretending otherwise is exactly the fault this replaced.
 */
export type StepState = 'done' | 'todo' | 'unknowable'

export interface Evidence {
  /** Custom objects the tenant declared. */
  objects: number
  /** Custom fields declared on the built-in entities. */
  fields: number
  /** Whether a process is published for opportunities. */
  hasProcess: boolean
  /** Declared business-hours weeks. */
  businessHours: number
  /** Declared SLA policies. */
  slaPolicies: number
  /** Declared approval processes. */
  approvals: number
}

export function stateOf(step: string, evidence: Evidence): StepState {
  switch (step) {
    case 'objects':
      return evidence.objects > 0 ? 'done' : 'todo'
    case 'fields':
      return evidence.fields > 0 ? 'done' : 'todo'
    case 'stages':
      return evidence.hasProcess ? 'done' : 'todo'
    case 'hours':
      // Both halves, because either alone leaves the promise unmeasurable: a policy with no week
      // is measured in calendar time, and a week with no policy promises nothing.
      return evidence.businessHours > 0 && evidence.slaPolicies > 0 ? 'done' : 'todo'
    case 'approvals':
      return evidence.approvals > 0 ? 'done' : 'todo'
    default:
      // Layouts and permissions. Nothing in this build stores either, so there is no evidence to
      // read — and a tick with nothing behind it is what this file exists to stop.
      return 'unknowable'
  }
}

/** How many steps are done, out of the ones that can be known. */
export function progress(steps: readonly string[], evidence: Evidence) {
  const knowable = steps.filter((step) => stateOf(step, evidence) !== 'unknowable')

  return {
    done: knowable.filter((step) => stateOf(step, evidence) === 'done').length,
    knowable: knowable.length,
    unknowable: steps.length - knowable.length,
  }
}
