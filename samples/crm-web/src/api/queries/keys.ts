/**
 * Every cache key in the application, in one place.
 *
 * WHY NOT KEYS BESIDE THEIR HOOKS. A key spelled in two files is two caches, and the symptom is
 * a mutation that invalidates one of them: the list refreshes and the tile above it does not,
 * which reads as a bug in the number rather than a bug in the key. Written as a tree so
 * `keys.cases.all` invalidates every case query without anybody having to enumerate them.
 *
 * The tenant is part of every key. Switching persona in this sample switches token and tenant
 * together, and a cache that did not know would serve one tenant's rows to another — the exact
 * thing the backend's row-level security exists to make impossible.
 */

import type { AttributionModel, CasePriority } from '../contracts'

export const keys = {
  cases: {
    all: (tenant: string) => ['cases', tenant] as const,
    worklist: (tenant: string, filter: { mineOnly: boolean; priority: CasePriority | null; breachedOnly: boolean }) =>
      ['cases', tenant, 'worklist', filter] as const,
  },
  approvals: {
    all: (tenant: string) => ['approvals', tenant] as const,
    inbox: (tenant: string) => ['approvals', tenant, 'inbox'] as const,
  },
  campaigns: {
    all: (tenant: string) => ['campaigns', tenant] as const,
    performance: (tenant: string, model: AttributionModel, from: string | null, to: string | null) =>
      ['campaigns', tenant, 'performance', model, from, to] as const,
    attribution: (tenant: string, opportunityId: string, model: AttributionModel) =>
      ['campaigns', tenant, 'attribution', opportunityId, model] as const,
  },
  board: {
    all: (tenant: string) => ['board', tenant] as const,
    period: (tenant: string, period: string) => ['board', tenant, period] as const,
  },
  performance: {
    all: (tenant: string) => ['performance', tenant] as const,
    sales: (tenant: string, period: string) => ['performance', tenant, 'sales', period] as const,
    deals: (tenant: string, period: string) => ['performance', tenant, 'deals', period] as const,
    quota: (tenant: string, period: string) => ['performance', tenant, 'quota', period] as const,
  },
  planning: {
    all: (tenant: string) => ['planning', tenant] as const,
    rollUp: (tenant: string, period: string) => ['planning', tenant, 'roll-up', period] as const,
    tree: (tenant: string, period: string) => ['planning', tenant, 'tree', period] as const,
  },
  scorecard: {
    all: (tenant: string) => ['scorecard', tenant] as const,
    period: (tenant: string, period: string) => ['scorecard', tenant, period] as const,
  },
  search: {
    all: (tenant: string) => ['search', tenant] as const,
    phrase: (tenant: string, phrase: string) => ['search', tenant, phrase] as const,
  },
  territories: {
    all: (tenant: string) => ['territories', tenant] as const,
    coverage: (tenant: string) => ['territories', tenant, 'coverage'] as const,
  },
  schema: {
    all: (tenant: string) => ['schema', tenant] as const,
    described: (tenant: string) => ['schema', tenant, 'described'] as const,
  },
  entities: {
    all: (tenant: string) => ['entities', tenant] as const,
    page: (tenant: string, entity: string, limit: number) =>
      ['entities', tenant, entity, limit] as const,
    record: (tenant: string, entity: string, id: string) =>
      ['entities', tenant, entity, 'record', id] as const,
  },
} as const
