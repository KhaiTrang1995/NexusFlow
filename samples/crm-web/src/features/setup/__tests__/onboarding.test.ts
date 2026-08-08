import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { progress, publishedProcess, stateOf } from '../onboardingState'
import type { Evidence } from '../onboardingState'

/** A tenant that has done nothing, and whose reads all answered. */
const NOTHING: Evidence = {
  objects: 0,
  fields: 0,
  hasProcess: false,
  businessHours: 0,
  slaPolicies: 0,
  approvals: 0,
}

/** The same tenant, seen by a token that may not read `/config` — three of the five refused. */
const REFUSED: Evidence = {
  ...NOTHING,
  businessHours: null,
  slaPolicies: null,
  approvals: null,
}

const STEPS = ['objects', 'fields', 'stages', 'layout', 'permissions', 'hours', 'approvals']

/**
 * What a new tenant is told about itself.
 *
 * The screen used to seed two steps as already done, so every organisation arrived
 * two-sevenths configured before anybody had touched it — on the one screen a new administrator
 * takes at face value. Then it read three refusals as three zeroes, which is the same fault from
 * the other end: a claim about the tenant assembled out of a claim about the caller.
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

  it('does not report a refused read as a step nobody did', () => {
    // `/config` needs `crm.admin`. A 403 says nothing about whether an SLA policy exists, and
    // "Until this is done" beside it is this client inventing an answer it was denied.
    expect(stateOf('approvals', REFUSED)).toBe('unreadable')
    expect(stateOf('hours', REFUSED)).toBe('unreadable')
    expect(stateOf('stages', { ...NOTHING, hasProcess: null })).toBe('unreadable')
  })

  it('lets a count the server did send settle a step the other half could not', () => {
    // A week that is declared to be nothing is not done, whatever the policy read answered —
    // a zero is an answer and a null is not.
    expect(stateOf('hours', { ...REFUSED, businessHours: 0 })).toBe('todo')
    expect(stateOf('hours', { ...REFUSED, businessHours: 3 })).toBe('unreadable')
  })
})

describe('publishedProcess', () => {
  it('reads the not-found as the answer, because that is what it is', () => {
    const missing = new ApiError(404, { code: 'crm.process_not_published' })

    expect(publishedProcess({ isSuccess: false, isError: true, error: missing })).toBe(false)
  })

  it('does not read a refusal as no process being published', () => {
    const denied = new ApiError(403, { code: 'authorization.permission_denied' })

    expect(publishedProcess({ isSuccess: false, isError: true, error: denied })).toBe(null)
    expect(publishedProcess({ isSuccess: false, isError: false, error: null })).toBe(null)
  })

  it('reads a successful read as published', () => {
    expect(publishedProcess({ isSuccess: true, isError: false, error: null })).toBe(true)
  })
})

describe('progress', () => {
  it('counts only what can be known, and says how much cannot', () => {
    expect(progress(STEPS, NOTHING)).toEqual({
      done: 0,
      knowable: 5,
      unknowable: 2,
      unreadable: 0,
    })
  })

  it('keeps a refused step out of the denominator rather than counting it as not done', () => {
    // The Contoso seller saw "0 of 5 done". Two of those five steps — the SLA pair and the
    // approvals — are `/config` reads the server refused, so the honest headline is nought of
    // three with two the caller may not look at.
    expect(progress(STEPS, REFUSED)).toEqual({
      done: 0,
      knowable: 3,
      unknowable: 2,
      unreadable: 2,
    })
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
    expect(counted.unreadable).toBe(0)
  })
})
