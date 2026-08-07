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

// ─────────────────────────────────────────────────────────────── built-in entity pages

/**
 * A page of a built-in entity.
 *
 * NO FILTER IS SENT. The list screen already filters and sorts what it holds, and a screen that
 * pushed its search box to the server would show a spinner on every keystroke over a page it
 * could have filtered in the browser. The server's filter is for the case this does not cover —
 * a list too long for one page — and that is the caller's decision, not this hook's.
 */
export function useEntityPage(
  entity: C.ReadableEntity | null,
  limit = 200,
): UseQueryResult<C.RecordPage> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.entities.page(tenantId, entity ?? 'none', limit),
    queryFn: ({ signal }) =>
      call.read<C.RecordPage, C.ReadEntityPage>(
        '/entities',
        { entity: entity!, filter: null, limit, after: null },
        signal,
      ),
    enabled: entity !== null,
    staleTime: 15_000,
  })
}

/**
 * One record of a built-in entity, by its id.
 *
 * A filter on the key column rather than a route of its own. The page endpoint already checks
 * the field name against the entity's closed column list and already binds the value, so a
 * record read is a page of one — and there is no second surface to keep the masking rules in
 * step with.
 */
export function useEntityRecord(
  entity: C.ReadableEntity | null,
  keyColumn: string | null,
  id: string | null,
): UseQueryResult<C.RecordPage> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.entities.record(tenantId, entity ?? 'none', id ?? 'none'),
    queryFn: ({ signal }) =>
      call.read<C.RecordPage, C.ReadEntityPage>(
        '/entities',
        {
          entity: entity!,
          filter: { match: 'All', criteria: [{ field: keyColumn!, operator: 'Equals', value: id! }] },
          limit: 1,
          after: null,
        },
        signal,
      ),
    enabled: entity !== null && keyColumn !== null && id !== null && id.length > 0,
    staleTime: 15_000,
  })
}

// ─────────────────────────────────────────────────────────────── the schema, described

/**
 * What this tenant's schema looks like to this caller.
 *
 * ONE CALL FOR EVERY SETUP SCREEN. Objects, built-in entities, their columns, the fields an
 * administrator added and the permissions already resolved — a client that asked four endpoints
 * for those would be four descriptions of one schema, and the fourth would disagree with the
 * first the moment somebody declared a field between two of the requests.
 *
 * The permissions arrive resolved rather than as rules to evaluate: `canRead` and `canWrite` are
 * answers about this caller, so a screen hides a control instead of reimplementing the policy.
 */
export function useSchema(): UseQueryResult<C.SchemaDescription> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.schema.described(tenantId),
    queryFn: ({ signal }) =>
      call.read<C.SchemaDescription, C.DescribeSchema>('/describe', { target: null }, signal),
    // A schema changes when an administrator changes it, which is rarely and deliberately.
    staleTime: 60_000,
  })
}

// ─────────────────────────────────────────────────────────────── territory coverage

/**
 * What is covered and, more usefully, what is not.
 *
 * The number this read exists for is `unrouted`: accounts that fall into no territory at all. A
 * list-per-person model cannot ask that question — an account missing from every list looks
 * exactly like an account nobody has got to yet.
 */
export function useCoverage(): UseQueryResult<C.Coverage> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.territories.coverage(tenantId),
    queryFn: ({ signal }) => call.read<C.Coverage, Record<string, never>>('/territories/coverage', {}, signal),
    staleTime: 30_000,
  })
}

// ─────────────────────────────────────────────────────────────── what a tenant has declared

/**
 * What this tenant has declared of one kind, each row with a sentence saying what it does.
 *
 * ONE HOOK FOR TWELVE SETUP SCREENS. The summary is composed by the server — it knows what an
 * operator and a value mean together, and five clients composing five different sentences about
 * one validation rule is five chances to describe it wrongly.
 *
 * `crm.admin`: a caller without it gets a refusal, and the screen shows the server's own words
 * rather than an empty table that reads as "nothing is configured".
 */
export function useConfig(kind: C.ConfigKind, limit = 100): UseQueryResult<C.ConfigList> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.config.kind(tenantId, kind),
    queryFn: ({ signal }) => call.read<C.ConfigList, C.ReadConfig>('/config', { kind, limit }, signal),
    staleTime: 30_000,
  })
}

// ─────────────────────────────────────────────────────────────── one plan, whole

/**
 * One plan and everything hung off it, in one request.
 *
 * By name rather than by id, because that is what {@link usePlanTree} hands back for every node —
 * a screen navigating from the tree to the plan needs nothing the tree did not already give it.
 *
 * `isOverdue` arrives decided. A client comparing a due date against its own clock reports a step
 * overdue in Sydney and not in Lisbon on the same afternoon, and an overdue step is the earliest
 * signal a deal has stopped moving.
 */
export function usePlan(name: string | null): UseQueryResult<C.PlanDetail> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.planning.plan(tenantId, name ?? 'none'),
    queryFn: ({ signal }) =>
      call.read<C.PlanDetail, C.ReadPlan>('/planning/plan', { name: name! }, signal),
    enabled: name !== null && name.length > 0,
    staleTime: 15_000,
  })
}

// ─────────────────────────────────────────────────────────────── the configured process

/**
 * The active process for one entity kind: its stages, and what may follow what.
 *
 * THE CLAIM THIS SAMPLE PROVES, READ BACK. The stages an administrator published, with the
 * guards that have to hold and the actions each transition takes — none of which this client
 * knows anything about, and all of which change without a deployment.
 */
export function useProcess(appliesTo: C.EntityKind): UseQueryResult<C.ProcessView> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.processes.of(tenantId, appliesTo),
    queryFn: ({ signal }) =>
      call.read<C.ProcessView, C.ReadProcess>('/processes', { appliesTo }, signal),
    staleTime: 60_000,
  })
}

// ─────────────────────────────────────────────────────────────── capturing work

/**
 * Captures a lead.
 *
 * INVALIDATES THE LEAD PAGE AND NOTHING ELSE. A mutation that invalidated the world would
 * refetch six panels that could not have changed, and the reader would watch the whole page
 * flicker for one row.
 *
 * The idempotency key is per attempt, from `useCall().write` — so a double click is one lead and
 * a retry after a timeout is the same lead rather than a second one.
 */
export function useCaptureLead(): UseMutationResult<C.LeadCaptured, Error, C.CaptureLead> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.CaptureLead) => call.write<C.LeadCaptured, C.CaptureLead>('/leads', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

/**
 * Creates a task, call, meeting or note against a record.
 *
 * `relatesTo` is a kind and an id together, which is what the server's polymorphic trigger
 * checks — an id without its kind is a reference nothing can verify.
 */
export function useCreateTask(): UseMutationResult<C.TaskCreated, Error, C.CreateTask> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.CreateTask) => call.write<C.TaskCreated, C.CreateTask>('/tasks', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

// ─────────────────────────────────────────────────────────────── editing and quoting

/**
 * Sets custom fields on a built-in record.
 *
 * A MERGE, NOT A REPLACEMENT. Only the fields being changed are sent, so two people editing
 * different fields of one account do not overwrite each other — which is what a form posting the
 * whole record does, and the loser never finds out.
 */
export function useSetCustomFields(): UseMutationResult<
  C.CustomFieldsSet,
  Error,
  C.SetCustomFields
> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.SetCustomFields) =>
      call.write<C.CustomFieldsSet, C.SetCustomFields>('/custom/entity-fields', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

/**
 * Prices and issues a quote against an opportunity.
 *
 * THE PRICE IS THE SERVER'S. Lines and a discount go up; a subtotal, a total and — the part that
 * matters — whether it needs approval come back. A client that computed the total would be a
 * second pricing engine, and the discount threshold is exactly the rule somebody would get wrong.
 */
export function useIssueQuote(): UseMutationResult<C.QuoteIssued, Error, C.IssueQuote> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.IssueQuote) => call.write<C.QuoteIssued, C.IssueQuote>('/quotes', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

/**
 * Converts a lead into an account, a contact and an opportunity.
 *
 * ONE CALL, NOT THREE. The server runs it as a saga: a failure at the opportunity unwinds the
 * contact and then the account, so a conversion either happened or did not. A client that made
 * the three writes itself would leave an account and a contact behind whenever the third failed,
 * and nobody would ever find them.
 *
 * Every list is invalidated because a conversion writes into four of them at once.
 */
export function useConvertLead(): UseMutationResult<C.ConversionResult, Error, C.ConvertLead> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.ConvertLead) =>
      call.write<C.ConversionResult, C.ConvertLead>('/lead-conversions', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

/**
 * Applies a trigger to an opportunity and lets the published process decide where it lands.
 *
 * THE DESTINATION IS NOT SENT. A trigger the process has no transition for is refused, and a
 * guard that does not hold refuses it too — which is the whole point of configuring the process
 * rather than writing the stage from a drop-down.
 */
export function useAdvanceOpportunity(): UseMutationResult<
  C.OpportunityAdvanced,
  Error,
  C.AdvanceOpportunity
> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.AdvanceOpportunity) =>
      call.write<C.OpportunityAdvanced, C.AdvanceOpportunity>('/opportunities/triggers', input),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: keys.entities.all(tenantId) })

      // The stage counts on the process view move with it, and a board still showing the old
      // occupancy is the screen somebody plans the week from.
      client.invalidateQueries({ queryKey: keys.processes.all(tenantId) })
    },
  })
}

/**
 * Accepts a quote and commits the money.
 *
 * REFUSED WHILE THE QUOTE IS A DRAFT, and that refusal is the control. A discount past the
 * threshold leaves the quote in Draft until somebody senior agrees; ordering against it anyway
 * would make the approval decorative.
 */
export function usePlaceOrder(): UseMutationResult<C.OrderPlaced, Error, C.PlaceOrder> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.PlaceOrder) => call.write<C.OrderPlaced, C.PlaceOrder>('/orders', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

/**
 * Clears a discounted quote, so an order can be taken against it.
 *
 * WHO APPROVED IS NOT SENT. The server reads it from the caller's claims, which is what makes the
 * register worth reading — a body carrying an approver id is a body somebody can write anybody's
 * name into.
 *
 * This is not the same act as deciding an approval request. The configured process says which
 * named people agreed; this is the grant that moves the quote out of Draft, and a client that
 * treated one as the other would let a recorded decision stand in for the control.
 */
export function useApproveDiscount(): UseMutationResult<
  C.DiscountApproved,
  Error,
  C.ApproveDiscount
> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.ApproveDiscount) =>
      call.write<C.DiscountApproved, C.ApproveDiscount>('/quotes/approvals', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.entities.all(tenantId) }),
  })
}

/**
 * Assigns somebody a quota for a period.
 *
 * THE RAMP IS APPLIED BY THE SERVER AND THE ANSWER SAYS SO. A client that multiplied the target
 * itself would put a number on the screen that the register does not hold, and the two would only
 * be found to disagree at the review the number exists for.
 */
export function useSetQuota(): UseMutationResult<C.QuotaSet, Error, C.SetQuota> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.SetQuota) => call.write<C.QuotaSet, C.SetQuota>('/quotas', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.performance.all(tenantId) }),
  })
}

/**
 * The reporting line, whole.
 *
 * WHAT EVERY OTHER READ IN THIS CLIENT IS SCOPED BY. A manager sees their reports' rows and a
 * director sees everybody's, and which is which is decided here — so somebody placed under the
 * wrong manager sees the wrong pipeline, and until this screen existed nothing showed it.
 */
export function useOrgChart(): UseQueryResult<C.OrgChart> {
  const { tenantId } = useSession()
  const call = useCall()

  return useQuery({
    queryKey: keys.org.chart(tenantId),
    queryFn: ({ signal }) => call.read<C.OrgChart, object>('/org/chart', {}, signal),
  })
}

/**
 * Places a person in the line, or moves them.
 *
 * A LOOP IS REFUSED AT THE WRITE, and the settings screen is exactly where one gets made by
 * accident — A reports to B on Monday, B is moved under A on Thursday by somebody else. The
 * refusal comes back in the server's words rather than being pre-empted here.
 */
export function useSetOrgMember(): UseMutationResult<C.OrgMemberSet, Error, C.SetOrgMember> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.SetOrgMember) =>
      call.write<C.OrgMemberSet, C.SetOrgMember>('/org/members', input),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: keys.org.all(tenantId) })

      // Moving somebody changes whose rows everybody below them sees, so every scoped read is
      // now answering from a line that no longer exists.
      client.invalidateQueries({ queryKey: keys.performance.all(tenantId) })
      client.invalidateQueries({ queryKey: keys.board.all(tenantId) })
      client.invalidateQueries({ queryKey: keys.planning.all(tenantId) })
    },
  })
}

/**
 * Records what was said about a KPI.
 *
 * THE NUMBER IS THE SERVER'S, READ AT THE MOMENT OF RECORDING. This is the one place in the
 * application where an actual is written down rather than read live, because a minute of a meeting
 * that silently updated itself would not be one — and a client that sent its own figure would be
 * recording what its cache held rather than what was true when somebody said it.
 */
export function useReviewKpi(): UseMutationResult<C.KpiReviewed, Error, C.ReviewKpi> {
  const client = useQueryClient()
  const { tenantId } = useSession()
  const call = useCall()

  return useMutation({
    mutationFn: (input: C.ReviewKpi) => call.write<C.KpiReviewed, C.ReviewKpi>('/kpis/reviews', input),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.scorecard.all(tenantId) }),
  })
}
