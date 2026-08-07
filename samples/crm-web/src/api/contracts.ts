/**
 * The backend's contracts, in TypeScript.
 *
 * READ THE UNIT BEFORE YOU FORMAT A RATE. This API expresses "a rate" two ways, and nothing but
 * the C# doc comment says which is which:
 *
 * - **Percentage, 0–100** — `DealPerformance.winRate`, `SellerPerformance.attainment`,
 *   `QuotaAttainment.attainment`, and every `KpiResult` figure. Format with `pct`.
 * - **Fraction, 0–1** — `CampaignPerformance.responseRate` and `AttributedCredit.share`.
 *   Format with `percent`.
 *
 * Getting it wrong is not a crash: it renders a win rate of 10,000% on a board, which is what it
 * did before this note existed. Each field below carries its unit.
 *
 * MIRRORED BY HAND, AND THAT IS A KNOWN COST. The C# records in `samples/crm` are the source of
 * truth; these are a transcription of them. The right fix is generation from the OpenAPI document
 * the backend already serves at `/openapi.json` — `plugins/FlowX.Http/OpenApi.cs` emits it — and
 * until that is wired into the build, a contract that changes on one side and not the other is
 * caught by the integration tests rather than by the compiler. Nothing here is guessed: every
 * shape below is the C# record, property for property.
 *
 * The JSON is camel-cased by `JsonSerializerDefaults.Web` on the server, which is why these read
 * as ordinary TypeScript rather than as PascalCase.
 */

// ─────────────────────────────────────────────────────────────── shared vocabularies

export type GuardOperator = 'Equals' | 'NotEquals' | 'GreaterThan' | 'LessThan' | 'IsSet'

export type Lifecycle = 'Prospect' | 'Customer' | 'Churned'

// ─────────────────────────────────────────────────────────────── service console

export type CasePriority = 'Low' | 'Normal' | 'High' | 'Urgent'
export type CaseOrigin = 'Email' | 'Phone' | 'Web' | 'Chat'
export type CaseStatus = 'New' | 'Working' | 'Waiting' | 'Escalated' | 'Closed'

export interface OpeningHoursOfDay {
  /** 0 is Sunday, matching both `DayOfWeek` and `extract(dow …)`. */
  day: number
  opens: string
  closes: string
}

export interface SetBusinessHours {
  week: OpeningHoursOfDay[]
}

export interface BusinessHoursSet {
  days: number
  minutesPerWeek: number
}

export interface DefineSlaPolicy {
  name: string
  label: string
  priority: CasePriority
  firstResponseMinutes: number
  resolutionMinutes: number
  businessHoursOnly: boolean
}

export interface SlaPolicyDefined {
  policyId: string
  replaced: boolean
}

export interface OpenCase {
  accountId: string
  contactId: string | null
  subject: string
  description: string
  priority: CasePriority
  origin: CaseOrigin
}

export interface CaseOpened {
  caseId: string
  number: number
  policy: string | null
  firstResponseDueAt: string | null
  resolutionDueAt: string | null
}

export interface CommentOnCase {
  caseId: string
  body: string
  isPublic: boolean
  status: CaseStatus | null
}

export interface CaseCommented {
  caseId: string
  ordinal: number
  status: string
  stoppedTheResponseClock: boolean
  firstResponseMinutes: number | null
  breachedFirstResponse: boolean
}

export interface ReadCaseWorklist {
  mineOnly: boolean
  priority: CasePriority | null
  breachedOnly: boolean
}

export interface QueuedCase {
  caseId: string
  number: number
  subject: string
  status: string
  priority: string
  ownerId: string
  openedAt: string
  firstResponseDueAt: string | null
  resolutionDueAt: string | null
  awaitingFirstResponse: boolean
  responseBreached: boolean
  resolutionBreached: boolean
  minutesToResolutionDue: number | null
}

export interface CaseWorklist {
  cases: QueuedCase[]
  breached: number
  awaitingFirstResponse: number
}

// ─────────────────────────────────────────────────────────────── approvals

export type ApprovalSubject = 'Quote' | 'Opportunity' | 'Plan'
export type ApproverKind = 'Named' | 'SubmittersManager' | 'RoleHolder'
export type ApprovalDecisionValue = 'Approved' | 'Rejected'

export interface ApprovalCriterion {
  attribute: string
  operator: GuardOperator
  value: string
}

export interface ApprovalStepDefinition {
  label: string
  kind: ApproverKind
  approver: string | null
}

export interface DefineApprovalProcess {
  name: string
  label: string
  subject: ApprovalSubject
  priority: number
  criteria: ApprovalCriterion[]
  steps: ApprovalStepDefinition[]
}

export interface ApprovalProcessDefined {
  processId: string
  steps: number
}

export interface SubmitForApproval {
  subject: ApprovalSubject
  id: string
}

export interface ApprovalSubmitted {
  requestId: string | null
  process: string | null
  required: boolean
  awaitingStep: number
  awaitingLabel: string | null
}

export interface DecideApproval {
  requestId: string
  decision: ApprovalDecisionValue
  note: string
}

export interface ApprovalDecided {
  requestId: string
  status: string
  awaitingStep: number
  awaitingLabel: string | null
}

export interface WaitingApproval {
  requestId: string
  process: string
  subject: string
  subjectId: string
  submittedBy: string
  submittedAt: string
  step: number
  stepLabel: string
}

export interface ApprovalInbox {
  waiting: WaitingApproval[]
}

// ─────────────────────────────────────────────────────────────── campaigns

export type CampaignChannel =
  | 'Email'
  | 'Event'
  | 'Webinar'
  | 'Paid'
  | 'Content'
  | 'Outbound'
  | 'Partner'

export type TouchKind = 'Sent' | 'Opened' | 'Clicked' | 'Attended' | 'Responded'

export type AttributionModel = 'FirstTouch' | 'LastTouch' | 'Linear' | 'PositionBased'

export interface DefineCampaign {
  name: string
  label: string
  channel: CampaignChannel
  startsOn: string
  endsOn: string
  budget: number
}

export interface CampaignDefined {
  campaignId: string
  days: number
}

export interface RecordTouch {
  campaign: string
  leadId: string | null
  contactId: string | null
  kind: TouchKind
  touchedAt: string
}

export interface TouchRecorded {
  touchId: string
  alreadyKnown: boolean
}

export interface RecordCampaignCost {
  campaign: string
  incurredOn: string
  amount: number
  note: string
}

export interface CampaignCostRecorded {
  campaignId: string
  ordinal: number
  spentToDate: number
  overBudget: boolean
}

export interface ReadCampaignPerformance {
  model: AttributionModel
  from: string | null
  to: string | null
}

export interface CampaignPerformance {
  campaignId: string
  campaign: string
  label: string
  channel: string
  people: number
  responses: number
  /** Responses over people, as a fraction 0–1. Null when it touched nobody. */
  responseRate: number | null
  influencedDeals: number
  attributedAmount: number
  budget: number
  spent: number
  costPerResponse: number | null
  return: number | null
}

export interface CampaignReport {
  model: string
  campaigns: CampaignPerformance[]
  dealsConsidered: number
  amountConsidered: number
  amountAttributed: number
}

export interface ReadDealAttribution {
  opportunityId: string
  model: AttributionModel
}

export interface AttributedCredit {
  campaignId: string
  campaign: string
  amount: number
  /** What fraction of the deal this campaign was given, 0–1. */
  share: number
}

export interface DealAttribution {
  opportunityId: string
  model: string
  amount: number
  decidedAt: string
  credits: AttributedCredit[]
  touchesAfterTheDecision: number
}

// ─────────────────────────────────────────────────────────────── territory and quota

export interface ReadQuotaAttainment {
  period: string
}

export interface QuotaAttainment {
  userId: string
  displayName: string
  measure: string
  quota: number
  /**
   * What they committed in their plans, or null on a quota that is not measured in money — a plan
   * commits an amount, so there is nothing to compare a leads target against.
   */
  committed: number | null
  actual: number
  /** Actual over quota, as a percentage 0–100. Null when they carry no number. */
  attainment: number | null
  /**
   * Quota less committed, and null wherever `committed` is. A different number from the gap on a
   * roll-up: a quota is assigned downwards and a commitment is offered upwards, and the two rarely
   * agree.
   */
  commitmentGap: number | null
}

export interface QuotaAttainmentReport {
  period: string
  rows: QuotaAttainment[]
}

// ─────────────────────────────────────────────────────────────── search

export interface SearchEverything {
  phrase: string
  limit: number
}

export interface SearchHit {
  kind: string
  id: string
  title: string
}

export interface SearchResults {
  hits: SearchHit[]
}

// ─────────────────────────────────────────────────────────────── planning and performance

export type OrgRole = 'Representative' | 'Manager' | 'Director'

export interface ViewerScope {
  userId: string
  role: OrgRole
  scope: string[]
}

export interface AccountCoverage {
  plan: string
  account: string
  target: number
  currency: string
  openPipeline: number
}

export interface OpportunityReadiness {
  plan: string
  target: number
  currency: string
  answered: number
  outOf: number
  steps: number
  overdueSteps: number
}

export interface LeadAttainment {
  plan: string
  segment: string
  channel: string
  targetLeads: number
  actualLeads: number
}

export interface PeriodRollUp {
  period: string
  vision: string
  target: number
  currency: string
  committed: number
  gap: number
  accounts: AccountCoverage[]
  opportunities: OpportunityReadiness[]
  marketing: LeadAttainment[]
}

export interface PlanNode {
  name: string
  label: string
  kind: string
  depth: number
  parent: string | null
  owner: string
  target: number
  committed: number
  gap: number
  children: number
}

export interface PlanTree {
  period: string
  nodes: PlanNode[]
}

export interface SellerPerformance {
  userId: string
  displayName: string
  role: string
  committed: number
  openPipeline: number
  won: number
  /** Won over committed, as a percentage 0–100. Null when they committed nothing. */
  attainment: number | null
}

export interface SalesPerformance {
  period: string
  sellers: SellerPerformance[]
}

export interface DealPerformance {
  period: string
  open: number
  openValue: number
  won: number
  wonValue: number
  lost: number
  lostValue: number
  /** A percentage, 0–100. Null when nothing was decided. Format with `pct`. */
  winRate: number | null
  averageWonValue: number | null
  /** Open deals that have not changed stage in sixty days. */
  stalled: number
}

export interface KpiResult {
  name: string
  label: string
  source: string
  target: number
  actual: number
  direction: string
  status: string
  lastCommentary: string | null
}

export interface Scorecard {
  period: string
  kpis: KpiResult[]
}

export interface ReadBoard {
  period: string
}

/**
 * Everything a board looks at, in one request.
 *
 * One call and not six, so a roll-up from one instant cannot appear beside a scorecard from
 * another — which is how two numbers on one page stop agreeing.
 */
export interface ExecutiveBoard {
  period: string
  viewedAs: ViewerScope
  rollUp: PeriodRollUp
  tree: PlanTree
  sales: SalesPerformance
  deals: DealPerformance
  scorecard: Scorecard
}

// ─────────────────────────────────────────────────────────────── built-in entity pages

/**
 * What may be declared on: a validation rule, a custom field, a field policy.
 *
 * Four, because each of those is a check constraint in a migration on the server.
 */
export type EntityKind = 'Lead' | 'Account' | 'Contact' | 'Opportunity'

/**
 * What may be read.
 *
 * A superset of {@link EntityKind}, and the server says so with a vocabulary of its own —
 * quotes, orders and activities are readable without being declarable.
 */
export type ReadableEntity = EntityKind | 'Quote' | 'Order' | 'Activity'
export type FilterMatch = 'All' | 'Any'

export interface RecordCriterion {
  field: string
  operator: GuardOperator
  value: string
}

export interface RecordFilter {
  match: FilterMatch
  criteria: RecordCriterion[]
}

export interface ReadEntityPage {
  entity: ReadableEntity
  filter: RecordFilter | null
  limit: number
  after: string | null
}

export interface RecordView {
  recordId: string
  values: Record<string, string | null>
}

export interface RecordPage {
  records: RecordView[]
  redacted: string[]
  nextCursor: string | null
}

// ─────────────────────────────────────────────────────────────── the schema, described

export interface DescribedField {
  name: string
  label: string
  type: string
  isRequired: boolean
  isComputed: boolean
  canRead: boolean
  canWrite: boolean
  options: string[]
  references: string | null
}

export interface DescribedView {
  name: string
  label: string
  kind: string
  groupBy: string | null
  lanes: string[]
  wipLimit: number | null
  titleField: string
  subtitleField: string | null
  columns: string[]
}

export interface DescribedObject {
  id: string
  name: string
  label: string
  fields: DescribedField[]
  views: DescribedView[]
}

export interface DescribedColumn {
  name: string
  label: string
}

export interface DescribedEntity {
  kind: string
  label: string
  columns: DescribedColumn[]
  fields: DescribedField[]
}

export interface DescribeSchema {
  target: string | null
}

export interface SchemaDescription {
  objects: DescribedObject[]
  entities: DescribedEntity[]
  version: number
}

// ─────────────────────────────────────────────────────────────── territory coverage

export interface ReadCoverage {
  [key: string]: never
}

export interface TerritoryCoverage {
  territory: string
  label: string
  owners: number
  accounts: number
}

export interface Coverage {
  territories: TerritoryCoverage[]
  /** Accounts falling into no territory at all — the ones nobody owns. */
  unrouted: number
  /** Territories with nobody on them. */
  unowned: number
}

// ─────────────────────────────────────────────────────────────── what a tenant has declared

export type ConfigKind =
  | 'ListView'
  | 'ValidationRule'
  | 'RollUp'
  | 'Formula'
  | 'Report'
  | 'Dashboard'
  | 'Connector'
  | 'Label'
  | 'ApprovalProcess'
  | 'SlaPolicy'
  | 'BusinessHours'
  | 'Territory'

export interface ReadConfig {
  kind: ConfigKind
  limit: number
}

export interface ConfigItem {
  /** Null for the two kinds keyed by what they describe rather than by an id. */
  id: string | null
  name: string
  label: string
  /** What it does, in a sentence the server composed. */
  summary: string
  isActive: boolean
}

export interface ConfigList {
  kind: ConfigKind
  items: ConfigItem[]
}

// ─────────────────────────────────────────────────────────────── one plan, whole

export interface ReadPlan {
  name: string
}

export interface PlanObjectiveRow {
  ordinal: number
  description: string
  measure: string
  target: number
  status: string
}

export interface PlanStepRow {
  ordinal: number
  description: string
  dueOn: string
  isComplete: boolean
  /** Decided by the server, against one clock. */
  isOverdue: boolean
}

export interface PlanRiskRow {
  ordinal: number
  description: string
  severity: string
  mitigation: string
  isOpen: boolean
}

export interface PlanQualificationRow {
  element: string
  isAnswered: boolean
  note: string
}

export interface PlanStakeholderRow {
  contactId: string
  fullName: string
  role: string
  sentiment: string
  influence: number
}

export interface PlanDetail {
  name: string
  label: string
  kind: string
  period: string
  owner: string
  targetAmount: number | null
  currency: string | null
  objectives: PlanObjectiveRow[]
  steps: PlanStepRow[]
  risks: PlanRiskRow[]
  qualification: PlanQualificationRow[]
  stakeholders: PlanStakeholderRow[]
}

// ─────────────────────────────────────────────────────────────── the configured process

export interface ReadProcess {
  appliesTo: EntityKind
}

export interface ProcessStageView {
  /** The id a conversion starts an opportunity in. There is nowhere else to read it. */
  stageId: string
  name: string
  ordinal: number
  isTerminal: boolean
  /** How many opportunities are sitting in it. */
  occupants: number
}

export interface ProcessGuardView {
  field: string
  operator: GuardOperator
  value: string
}

export interface ProcessTransitionView {
  from: string
  to: string
  trigger: string
  guards: ProcessGuardView[]
  actions: string[]
}

export interface ProcessView {
  appliesTo: string
  version: number
  stages: ProcessStageView[]
  transitions: ProcessTransitionView[]
}

// ─────────────────────────────────────────────────────────────── capturing work

export type LeadSource = 'Web' | 'Referral' | 'Event' | 'Outbound' | 'Partner'

export interface CaptureLead {
  company: string
  contactName: string
  email: string | null
  source: LeadSource
}

export interface LeadCaptured {
  leadId: string
}

export type ActivityKind = 'Task' | 'Call' | 'Meeting' | 'Note'

/** What an activity hangs off: a kind and an id, never one without the other. */
export interface RelatedRef {
  kind: EntityKind
  id: string
}

export interface CreateTask {
  kind: ActivityKind
  subject: string
  relatesTo: RelatedRef
  owner: string
  dueAt: string | null
}

export interface TaskCreated {
  activityId: string
  dueAt: string | null
}

// ─────────────────────────────────────────────────────────────── editing and quoting

export interface SetCustomFields {
  kind: EntityKind
  id: string
  /** Only the fields being changed. Absent is "leave it"; null is "clear it". */
  values: Record<string, string | null>
}

export interface CustomFieldsSet {
  id: string
  values: Record<string, string | null>
}

export interface Money {
  amount: number
  currency: string
}

export interface QuoteRequestLine {
  sku: string
  quantity: number
  unitPrice: Money
}

export interface IssueQuote {
  opportunityId: string
  lines: QuoteRequestLine[]
  discount: number
  validForDays: number
}

export type QuoteStatus = 'Draft' | 'Issued' | 'Accepted' | 'Rejected' | 'Expired'

export interface QuoteIssued {
  quoteId: string
  total: Money
  status: QuoteStatus
  /** True when the discount crossed the threshold, so it is Draft until a manager approves. */
  needsApproval: boolean
}

/**
 * Turns a qualified lead into an account, a contact and an opportunity.
 *
 * `stage` is an id the caller reads from the published process — the server refuses a stage
 * belonging to no active definition, and a client that named a stage by string would be a second
 * place the process is written.
 */
export interface ConvertLead {
  leadId: string
  industry: string
  region: string
  owner: string
  opportunityName: string
  amount: number
  currency: string
  stage: string
  expectedClose: string
}

/** The three rows a conversion left behind, or none of them. */
export interface ConversionResult {
  account: string
  contact: string
  opportunity: string
}

/**
 * Applies a trigger to an opportunity.
 *
 * The trigger is the administrator's word, not a stage. Which stage it lands in is the published
 * process's answer, and a client that sent a destination would be deciding it.
 */
export interface AdvanceOpportunity {
  opportunityId: string
  trigger: string
}

export interface OpportunityAdvanced {
  opportunityId: string
  trigger: string
}

/** Accepts a quote and commits the money. Refused while the quote is still a Draft. */
export interface PlaceOrder {
  quoteId: string
}

export interface OrderPlaced {
  orderId: string
  quoteId: string
  total: Money
}

/**
 * Clears a discounted quote so it can be ordered against.
 *
 * A DIFFERENT THING FROM DECIDING AN APPROVAL REQUEST. The configured process records who said
 * yes; this is the grant-holding act that moves the quote out of Draft. The approver is taken from
 * the caller's claims and never from this body, so nobody can approve as somebody else.
 */
export interface ApproveDiscount {
  quoteId: string
}

export interface DiscountApproved {
  quoteId: string
  approvedBy: string
  at: string
}

export type QuotaMeasure = 'Revenue' | 'Leads' | 'Activities'

/**
 * Assigns somebody a number for a period.
 *
 * `rampFactor` is a fraction of the target, for somebody who joined part-way through — the server
 * multiplies, so what comes back is what they actually carry rather than what was typed.
 */
export interface SetQuota {
  period: string
  userId: string
  measure: QuotaMeasure
  target: number
  rampFactor: number
}

export interface QuotaSet {
  userId: string
  /** What was assigned, after ramp. */
  target: number
}

export interface OrgChartMember {
  userId: string
  displayName: string
  role: OrgRole
  /** Their manager's subject, or null at the top. */
  reportsTo: string | null
  /** How many report to them directly — not at any depth, so it matches what is drawn below. */
  reports: number
}

/**
 * The reporting line.
 *
 * `members` arrives managers-first, so a tree can be built in one pass without holding rows aside
 * for parents that have not been seen yet.
 */
export interface OrgChart {
  members: OrgChartMember[]
}

export interface SetOrgMember {
  userId: string
  displayName: string
  role: OrgRole
  reportsTo: string | null
}

export interface OrgMemberSet {
  userId: string
  /** How many are below them in the line, at any depth. */
  reports: number
}

/**
 * Records what was said about a number.
 *
 * THE ACTUAL IS NOT SENT. The server reads the KPI at the moment the review is recorded and
 * stores that — which is what makes a minute a minute. A form that collected the figure would be
 * collecting one the server discards.
 */
export interface ReviewKpi {
  kpi: string
  period: string
  commentary: string
}

export interface KpiReviewed {
  kpi: string
  /** What the number was when the commentary was written. Stored, unlike a plan's actual. */
  actual: number
  status: string
}
