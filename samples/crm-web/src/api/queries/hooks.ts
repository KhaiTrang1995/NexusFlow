import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query'
import { newIdempotencyKey, post } from '../client'
import type * as C from '../contracts'
import { useSession } from '@/session/SessionProvider'
import { keys } from './keys'

/**
 * Every server read and write this application makes.
 *
 * ONE HOOK PER BACKEND SURFACE, AND NOTHING ELSE CALLS `post`. A screen that fetched for itself
 * would have to know the route, the token, the idempotency rule and the cache key, and would get
 * one of them subtly wrong — the fourth screen to be written is where that starts costing.
 *
 * A MUTATION SAYS WHAT IT INVALIDATES. Not "everything": invalidating the world after a comment
 * refetches six panels that could not have changed, and the reader sees the whole page flicker
 * for a one-line write.
 */

// ─────────────────────────────────────────────────────────────── the request seam

function useCall() {
  const { token } = useSession()

  return {
    /** A read. No idempotency key: these endpoints do not take one. */
    read<R, B>(path: string, body: B, signal: AbortSignal) {
      return post<R, B>(path, body, { token, signal })
    },
    /** A write. A key per attempt, so a repeat of the same click is the same write. */
    write<R, B>(path: string, body: B) {
      return post<R, B>(path, body, { token, idempotencyKey: newIdempotencyKey() })
    },
  }
}

// ─────────────────────────────────────────────────────────────── service console

export function useCaseWorklist(filter: C.ReadCaseWorklist): UseQueryResult<C.CaseWorklist> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.cases.worklist(tenantId, filter),
    queryFn: ({ signal }) => call.read<C.CaseWorklist, C.ReadCaseWorklist>('/service/queue', filter, signal),
    // A breach is computed as of the read, so a stale queue is a queue that says a late case is
    // fine. Short and refetched on focus rather than long and correct-looking.
    staleTime: 15_000,
    refetchOnWindowFocus: true,
  })
}

export function useOpenCase(): UseMutationResult<C.CaseOpened, Error, C.OpenCase> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.OpenCase) => call.write<C.CaseOpened, C.OpenCase>('/service/cases', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.cases.all(tenantId) }),
  })
}

export function useCommentOnCase(): UseMutationResult<C.CaseCommented, Error, C.CommentOnCase> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.CommentOnCase) =>
      call.write<C.CaseCommented, C.CommentOnCase>('/service/comments', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.cases.all(tenantId) }),
  })
}

export function useDefineSlaPolicy(): UseMutationResult<C.SlaPolicyDefined, Error, C.DefineSlaPolicy> {
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.DefineSlaPolicy) =>
      call.write<C.SlaPolicyDefined, C.DefineSlaPolicy>('/service/policies', input),
  })
}

export function useSetBusinessHours(): UseMutationResult<C.BusinessHoursSet, Error, C.SetBusinessHours> {
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.SetBusinessHours) =>
      call.write<C.BusinessHoursSet, C.SetBusinessHours>('/service/hours', input),
  })
}

// ─────────────────────────────────────────────────────────────── approvals

export function useApprovalInbox(): UseQueryResult<C.ApprovalInbox> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.approvals.inbox(tenantId),
    queryFn: ({ signal }) => call.read<C.ApprovalInbox, object>('/approvals/inbox', {}, signal),
    staleTime: 20_000,
    refetchOnWindowFocus: true,
  })
}

export function useDecideApproval(): UseMutationResult<C.ApprovalDecided, Error, C.DecideApproval> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.DecideApproval) =>
      call.write<C.ApprovalDecided, C.DecideApproval>('/approvals/decisions', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.approvals.all(tenantId) }),
  })
}

export function useSubmitForApproval(): UseMutationResult<C.ApprovalSubmitted, Error, C.SubmitForApproval> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.SubmitForApproval) =>
      call.write<C.ApprovalSubmitted, C.SubmitForApproval>('/approvals/requests', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.approvals.all(tenantId) }),
  })
}

export function useDefineApprovalProcess(): UseMutationResult<
  C.ApprovalProcessDefined,
  Error,
  C.DefineApprovalProcess
> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.DefineApprovalProcess) =>
      call.write<C.ApprovalProcessDefined, C.DefineApprovalProcess>('/approvals/processes', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.approvals.all(tenantId) }),
  })
}

// ─────────────────────────────────────────────────────────────── campaigns

export function useCampaignPerformance(
  model: C.AttributionModel,
  from: string | null = null,
  to: string | null = null,
): UseQueryResult<C.CampaignReport> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.campaigns.performance(tenantId, model, from, to),
    queryFn: ({ signal }) =>
      call.read<C.CampaignReport, C.ReadCampaignPerformance>(
        '/campaigns/performance',
        { model, from, to },
        signal,
      ),
    staleTime: 60_000,
  })
}

export function useDealAttribution(
  opportunityId: string | null,
  model: C.AttributionModel,
): UseQueryResult<C.DealAttribution> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.campaigns.attribution(tenantId, opportunityId ?? '', model),
    queryFn: ({ signal }) =>
      call.read<C.DealAttribution, C.ReadDealAttribution>(
        '/campaigns/attribution',
        { opportunityId: opportunityId as string, model },
        signal,
      ),
    enabled: opportunityId !== null,
    staleTime: 60_000,
  })
}

export function useRecordTouch(): UseMutationResult<C.TouchRecorded, Error, C.RecordTouch> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.RecordTouch) =>
      call.write<C.TouchRecorded, C.RecordTouch>('/campaigns/touches', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.campaigns.all(tenantId) }),
  })
}

export function useDefineCampaign(): UseMutationResult<C.CampaignDefined, Error, C.DefineCampaign> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.DefineCampaign) =>
      call.write<C.CampaignDefined, C.DefineCampaign>('/campaigns', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.campaigns.all(tenantId) }),
  })
}

export function useRecordCampaignCost(): UseMutationResult<
  C.CampaignCostRecorded,
  Error,
  C.RecordCampaignCost
> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.RecordCampaignCost) =>
      call.write<C.CampaignCostRecorded, C.RecordCampaignCost>('/campaigns/costs', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.campaigns.all(tenantId) }),
  })
}

// ─────────────────────────────────────────────────────────── board and performance

export function useExecutiveBoard(period: string): UseQueryResult<C.ExecutiveBoard> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.board.period(tenantId, period),
    queryFn: ({ signal }) => call.read<C.ExecutiveBoard, C.ReadBoard>('/board', { period }, signal),
    staleTime: 60_000,
  })
}

export function useSalesPerformance(period: string): UseQueryResult<C.SalesPerformance> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.performance.sales(tenantId, period),
    queryFn: ({ signal }) =>
      call.read<C.SalesPerformance, { period: string }>('/performance/sales', { period }, signal),
    staleTime: 60_000,
  })
}

export function useDealPerformance(period: string): UseQueryResult<C.DealPerformance> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.performance.deals(tenantId, period),
    queryFn: ({ signal }) =>
      call.read<C.DealPerformance, { period: string }>('/performance/deals', { period }, signal),
    staleTime: 60_000,
  })
}

export function useQuotaAttainment(period: string): UseQueryResult<C.QuotaAttainmentReport> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.performance.quota(tenantId, period),
    queryFn: ({ signal }) =>
      call.read<C.QuotaAttainmentReport, C.ReadQuotaAttainment>(
        '/quotas/attainment',
        { period },
        signal,
      ),
    staleTime: 60_000,
  })
}

export function usePlanTree(period: string): UseQueryResult<C.PlanTree> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.planning.tree(tenantId, period),
    queryFn: ({ signal }) =>
      call.read<C.PlanTree, { period: string; root: string | null }>(
        '/planning/tree',
        { period, root: null },
        signal,
      ),
    staleTime: 60_000,
  })
}

export function usePeriodRollUp(period: string): UseQueryResult<C.PeriodRollUp> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.planning.rollUp(tenantId, period),
    queryFn: ({ signal }) =>
      call.read<C.PeriodRollUp, { period: string }>('/planning/roll-ups', { period }, signal),
    staleTime: 60_000,
  })
}

export function useScorecard(period: string): UseQueryResult<C.Scorecard> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.scorecard.period(tenantId, period),
    queryFn: ({ signal }) =>
      call.read<C.Scorecard, { period: string }>('/kpis/scorecards', { period }, signal),
    staleTime: 60_000,
  })
}

// ─────────────────────────────────────────────────────────────── search

export function useSearch(phrase: string, limit = 25): UseQueryResult<C.SearchResults> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.search.phrase(tenantId, phrase),
    queryFn: ({ signal }) =>
      call.read<C.SearchResults, C.SearchEverything>('/search', { phrase, limit }, signal),
    // A two-character phrase matches most of the database and helps nobody.
    enabled: phrase.trim().length >= 2,
    staleTime: 30_000,
  })
}
