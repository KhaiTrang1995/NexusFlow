import { describe, expect, it } from 'vitest'
import { matching } from '../FlowsScreen'
import type { ManifestFlow } from '@/api/contracts'

const FLOWS: readonly ManifestFlow[] = [
  { id: 'crm.lead.capture', version: '1.0.0', profile: 'Durable', triggers: [] },
  { id: 'crm.lead.assignment', version: '1.0.0', profile: 'Durable', triggers: [] },
  { id: 'crm.board', version: '1.0.0', profile: 'Ephemeral', triggers: [] },
]

/**
 * The one rule on the flows screen.
 *
 * Everything else there is the manifest drawn as-is; this decides what a reader is shown, and a
 * filter that quietly dropped a flow would make the screen wrong in exactly the way the
 * hand-written list it replaced was.
 */
describe('matching', () => {
  it('keeps everything when no lens and no phrase are set', () => {
    expect(matching(FLOWS, 'all', '').length).toBe(3)
  })

  it('keeps only the profile asked for', () => {
    expect(matching(FLOWS, 'Ephemeral', '').map((flow) => flow.id)).toEqual(['crm.board'])
    expect(matching(FLOWS, 'Durable', '').length).toBe(2)
  })

  it('matches an id by any part of it, not only its start', () => {
    expect(matching(FLOWS, 'all', 'lead').length).toBe(2)
    expect(matching(FLOWS, 'all', 'assignment').map((flow) => flow.id))
      .toEqual(['crm.lead.assignment'])
  })

  it('ignores surrounding space, which a paste brings with it', () => {
    expect(matching(FLOWS, 'all', '  board  ').map((flow) => flow.id)).toEqual(['crm.board'])
  })

  it('combines the two rather than letting either win', () => {
    expect(matching(FLOWS, 'Durable', 'board')).toEqual([])
  })
})
