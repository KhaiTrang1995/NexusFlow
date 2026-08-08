import type { OrgChartMember, QuotaAttainment } from '@/api/contracts'

/**
 * The capacity model, from the numbers people actually carry.
 *
 * <strong>Four teams were written into this client.</strong> Enterprise NA at 8 people and
 * €3.38M of capacity, three more like it, and four hero tiles adding them up: 29 headcount,
 * €9.3M capacity, 94% coverage. Identical on every tenant, including one with no people and no
 * quotas at all, which is where it was finally noticed — an organisation with nothing in it was
 * shown a fully staffed sales force.
 *
 * <strong>Capacity is ramped and target is not, and that is the whole model.</strong> A seller who
 * started in the second month of a quarter carries a fraction of a number; the business still
 * committed the whole one. Comparing either against itself gives a plan that is short by exactly
 * the ramp, every period, in the same direction, with nothing on the screen to say why. The server
 * returns both — `quota` after ramp, `assigned` before it — so neither is reconstructed here.
 *
 * <strong>A team is a manager, because that is the only grouping this data has.</strong> There is
 * no team column anywhere in the schema; there is a reporting line. Whoever a person reports to is
 * their team, and the people at the top of the line are their own.
 */
export interface TeamCapacity {
  /** The manager's name, or what to call the people who report to nobody. */
  team: string
  people: number
  rampedPeople: number
  /** Ramped money: what this team can actually carry. */
  capacity: number
  /** Unramped: what was assigned to them. */
  target: number
}

/** What the top of the line is called. They are a team of their own, not a team of nobody. */
export const UNMANAGED = 'Top of the line'

export interface Capacity {
  teams: readonly TeamCapacity[]
  people: number
  rampedPeople: number
  capacity: number
  target: number
  /**
   * Quota rows in a measure that is not money, left out of every total above. Leads and
   * activities are real numbers and adding them to euros produces a quantity that is not wrong by
   * a little — it is not a quantity at all.
   */
  otherMeasures: number
}

export function capacityOf(
  rows: readonly QuotaAttainment[],
  members: readonly OrgChartMember[],
): Capacity {
  const name = new Map(members.map((member) => [member.userId, member.displayName]))
  const manager = new Map(members.map((member) => [member.userId, member.reportsTo]))

  const revenue = rows.filter((row) => row.measure === 'Revenue')
  const teams = new Map<string, TeamCapacity>()

  for (const row of revenue) {
    const above = manager.get(row.userId) ?? null
    const team = above === null ? UNMANAGED : (name.get(above) ?? above)

    const found = teams.get(team)
      ?? { team, people: 0, rampedPeople: 0, capacity: 0, target: 0 }

    found.people += 1
    found.rampedPeople += row.rampFactor
    found.capacity += row.quota
    found.target += row.assigned

    teams.set(team, found)
  }

  return {
    // Biggest commitment first: a capacity review starts at the team carrying the most.
    teams: [...teams.values()].sort((left, right) => right.target - left.target),
    people: revenue.length,
    rampedPeople: revenue.reduce((sum, row) => sum + row.rampFactor, 0),
    capacity: revenue.reduce((sum, row) => sum + row.quota, 0),
    target: revenue.reduce((sum, row) => sum + row.assigned, 0),
    otherMeasures: rows.length - revenue.length,
  }
}
