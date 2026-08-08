import { describe, expect, it } from 'vitest'
import { UNMANAGED, capacityOf } from '../capacity'
import type { OrgChartMember, QuotaAttainment } from '@/api/contracts'

function quota(partial: Partial<QuotaAttainment> & { userId: string }): QuotaAttainment {
  return {
    displayName: partial.userId,
    measure: 'Revenue',
    quota: 0,
    assigned: 0,
    rampFactor: 1,
    committed: null,
    actual: 0,
    attainment: null,
    commitmentGap: null,
    ...partial,
  }
}

function member(userId: string, reportsTo: string | null): OrgChartMember {
  return { userId, displayName: userId.toUpperCase(), role: 'Representative', reportsTo, reports: 0 }
}

/**
 * The capacity model.
 *
 * Four teams, 29 people and €9.3M of capacity were constants in this client, shown to every
 * tenant including one with nobody in it.
 */
describe('capacityOf', () => {
  it('has nothing to say about a tenant where nobody carries a number', () => {
    expect(capacityOf([], [])).toMatchObject({
      teams: [],
      people: 0,
      rampedPeople: 0,
      capacity: 0,
      target: 0,
    })
  })

  it('separates what was assigned from what can be carried', () => {
    // The whole model: the business committed 180k, the seller joined three months in and can
    // carry 135k. A build that compared either against itself is short by exactly the ramp.
    const capacity = capacityOf(
      [quota({ userId: 'a', assigned: 180_000, quota: 135_000, rampFactor: 0.75 })],
      [member('a', 'boss')],
    )

    expect(capacity.target).toBe(180_000)
    expect(capacity.capacity).toBe(135_000)
    expect(capacity.rampedPeople).toBe(0.75)
    expect(capacity.people).toBe(1)
  })

  it('leaves quotas that are not money out of every total', () => {
    // Forty leads added to three hundred thousand euros is not a quantity at all.
    const capacity = capacityOf(
      [
        quota({ userId: 'a', assigned: 300_000, quota: 300_000 }),
        quota({ userId: 'a', measure: 'Leads', assigned: 40, quota: 40 }),
      ],
      [member('a', 'boss')],
    )

    expect(capacity.target).toBe(300_000)
    expect(capacity.people).toBe(1)
    expect(capacity.otherMeasures).toBe(1)
  })

  it('groups by the reporting line, because that is the only grouping this schema has', () => {
    const capacity = capacityOf(
      [
        quota({ userId: 'a', assigned: 100, quota: 100 }),
        quota({ userId: 'b', assigned: 200, quota: 200 }),
        quota({ userId: 'c', assigned: 900, quota: 900 }),
      ],
      [
        member('boss', null),
        member('other', null),
        member('a', 'boss'),
        member('b', 'boss'),
        member('c', 'other'),
      ],
    )

    // Biggest commitment first: a capacity review starts with the team carrying the most.
    expect(capacity.teams.map((team) => team.team)).toEqual(['OTHER', 'BOSS'])
    expect(capacity.teams[1]).toMatchObject({ team: 'BOSS', people: 2, target: 300 })
  })

  it('puts somebody who reports to nobody in a team of their own', () => {
    // Not "no team": the person at the top of the line carries a number like everybody else, and
    // dropping them loses their capacity from the total.
    const capacity = capacityOf(
      [quota({ userId: 'a', assigned: 500, quota: 500 })],
      [member('a', null)],
    )

    expect(capacity.teams[0]?.team).toBe(UNMANAGED)
    expect(capacity.target).toBe(500)
  })

  it('still counts somebody the org chart does not know about', () => {
    // A quota row exists for them, so their number is committed whether or not they were placed.
    // Dropping them would report a smaller target than the one that was assigned.
    const capacity = capacityOf([quota({ userId: 'ghost', assigned: 700, quota: 700 })], [])

    expect(capacity.target).toBe(700)
    expect(capacity.teams).toHaveLength(1)
  })
})
