import { describe, expect, it } from 'vitest'
import { progress, stateOf } from '../onboardingState'
import type { Evidence } from '../onboardingState'

const NOTHING: Evidence = {
  objects: 0,
  fields: 0,
  hasProcess: false,
  businessHours: 0,
  slaPolicies: 0,
  approvals: 0,
}

const STEPS = ['objects', 'fields', 'stages', 'layout', 'permissions', 'hours', 'approvals']

/**
 * What a new tenant is told about itself.
 *
 * The screen used to seed two steps as already done, so every organisation arrived
 * two-sevenths configured before anybody had touched it — on the one screen a new administrator
 * takes at face value.
 */
describe('stateOf', () => {
  it('marks nothing done for a tenant that has done nothing', () => {
    for (const step of ['objects', 'fields', 'stages', 'hours', 'approvals']) {
      expect(stateOf(step, NOTHING), step).toBe('todo')
    }
  })

  it('marks a step done from the evidence, not from a list', () => {
    expect(stateOf('objects', { ...NOTHING, objects: 1 })).toBe('done')
    expect(stateOf('stages', { ...NOTHING, hasProcess: true })).toBe('done')
    expect(stateOf('approvals', { ...NOTHING, approvals: 2 })).toBe('done')
  })

  it('needs both halves of the SLA step', () => {
    // A policy with no week is measured in calendar time; a week with no policy promises nothing.
    expect(stateOf('hours', { ...NOTHING, businessHours: 1 })).toBe('todo')
    expect(stateOf('hours', { ...NOTHING, slaPolicies: 1 })).toBe('todo')
    expect(stateOf('hours', { ...NOTHING, businessHours: 1, slaPolicies: 1 })).toBe('done')
  })

  it('calls a step with no evidence unknowable rather than todo', () => {
    // Layouts are not stored and permissions are a token's scopes. Neither can be read, and a
    // tick with nothing behind it is the fault this replaced.
    expect(stateOf('layout', NOTHING)).toBe('unknowable')
    expect(stateOf('permissions', { ...NOTHING, objects: 9 })).toBe('unknowable')
  })
})

describe('progress', () => {
  it('counts only what can be known, and says how much cannot', () => {
    expect(progress(STEPS, NOTHING)).toEqual({ done: 0, knowable: 5, unknowable: 2 })
  })

  it('reaches complete without the unknowable ones ever being done', () => {
    const everything: Evidence = {
      objects: 1,
      fields: 3,
      hasProcess: true,
      businessHours: 1,
      slaPolicies: 2,
      approvals: 1,
    }

    const counted = progress(STEPS, everything)

    expect(counted.done).toBe(counted.knowable)
    expect(counted.unknowable).toBe(2)
  })
})
