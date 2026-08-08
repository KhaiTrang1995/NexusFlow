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
    plan: (tenant: string, name: string) => ['planning', tenant, 'plan', name] as const,
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
  // No tenant: the manifest is a compile-time constant and is the same for every caller.
  manifest: {
    all: () => ['manifest'] as const,
  },
  reports: {
    all: (tenant: string) => ['reports', tenant] as const,
    run: (tenant: string, name: string) => ['reports', tenant, 'run', name] as const,
  },
  org: {
    all: (tenant: string) => ['org', tenant] as const,
    chart: (tenant: string) => ['org', tenant, 'chart'] as const,
  },
  processes: {
    all: (tenant: string) => ['processes', tenant] as const,
    of: (tenant: string, kind: string) => ['processes', tenant, kind] as const,
  },
  config: {
    all: (tenant: string) => ['config', tenant] as const,
    kind: (tenant: string, kind: string) => ['config', tenant, kind] as const,
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
    related: (tenant: string, entity: string, field: string, value: string) =>
      ['entities', tenant, entity, 'related', field, value] as const,
  },
} as const
