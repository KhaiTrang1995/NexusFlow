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
  /** What they were assigned, after ramp — the number they are measured on. */
  quota: number
  /** The same number before ramp: what the business committed, which nobody carries in full. */
  assigned: number
  /** The fraction of the period they carry. Summing these is what ramped headcount means. */
  rampFactor: number
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

export interface ReadPeriods {
  /**
   * Nothing is asked. The request body is an empty object because every endpoint here is a POST
   * carrying its input, and a read with no parameters still has to send one.
   */
  readonly _?: never
}

export interface PeriodSummary {
  name: string
  label: string
  /** ISO date, its first day. */
  startsOn: string
  /** ISO date, its last day. */
  endsOn: string
  parent: string | null
  /**
   * Whether today falls inside it, decided by the server. A browser deciding this does it in
   * whatever timezone the machine is set to, and two offices then open different quarters.
   */
  isCurrent: boolean
}

export interface DeclaredPeriods {
  periods: PeriodSummary[]
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
export type ReadableEntity = EntityKind | 'Quote' | 'QuoteLine' | 'Order' | 'Activity'
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
  /**
   * What the column may hold, when it holds one of a closed set; empty when it is free text, a
   * number or a date.
   *
   * THIS IS WHERE A PICKLIST COMES FROM NOW. A described column used to be a name and a label, so
   * a screen that had to draw a status chip read the values off whatever rows had arrived — a
   * status nobody has yet had no chip, and there was no way to filter to it. The types below are
   * still the enums transcribed by hand, and for a built-in column this list is what supersedes
   * them: the server's vocabulary cannot drift from the server.
   */
  options: string[]
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
  /**
   * Whose step it is. Sent straight back by anything that writes the row: `SetPlanStep` upserts
   * the whole step, so a client that dropped this would reassign it to whoever pressed the button.
   */
  owner: string
  dueOn: string
  isComplete: boolean
  /** Decided by the server, against one clock. */
  isOverdue: boolean
}

/**
 * Writes one step of the mutual action plan, or marks one done.
 *
 * THE WHOLE ROW, WHICH IS WHY THE OWNER IS HERE. The server upserts on (plan, ordinal); every
 * field is written, so every field has to be sent — the ones being changed and the ones being
 * kept. `PlanStepRow.owner` is what a "mark done" control sends back for the owner.
 */
export interface SetPlanStep {
  plan: string
  ordinal: number
  description: string
  owner: string
  dueOn: string
  isComplete: boolean
}

export interface PlanStepSet {
  ordinal: number
  /** How many steps of this plan are still to do, counted by the server. */
  outstanding: number
}

export interface PlanRiskRow {
  ordinal: number
  description: string
  severity: string
  mitigation: string
  isOpen: boolean
}

/**
 * What has to be known about a deal before anybody should believe its date.
 *
 * A CLOSED EIGHT, WHICH IS THE DENOMINATOR. `/planning/plan` returns only the elements somebody
 * has recorded, so a deal nobody has qualified comes back with an empty list — and a screen that
 * counted that list said "0 of 0 answered" while the roll-up beside it, which divides by the
 * vocabulary, said 0 of 8 about the same deal.
 *
 * The eight are the enum the server's `AnswerQualification` accepts, and a ninth is a code change
 * on both sides on purpose: a checklist whose items differ per deal is one no two managers can
 * hold against each other, which is the entire reason for having one.
 */
export type QualificationElement =
  | 'Metrics'
  | 'EconomicBuyer'
  | 'DecisionCriteria'
  | 'DecisionProcess'
  | 'PaperProcess'
  | 'IdentifiedPain'
  | 'Champion'
  | 'Competition'

export interface PlanQualificationRow {
  element: string
  isAnswered: boolean
  note: string
}

/**
 * Records whether one thing about a deal is actually known.
 *
 * Answered or not, with a note — never a rating. A seller asked to score their own deal scores it
 * comfortably; a seller asked whether they know who signs either does or does not.
 */
export interface AnswerQualification {
  plan: string
  element: QualificationElement
  isAnswered: boolean
  note: string
}

export interface QualificationRecorded {
  answered: number
  /** The size of the vocabulary, from the server. */
  outOf: number
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

export type QuoteStatus =
  | 'Draft'
  | 'Issued'
  | 'Accepted'
  | 'Rejected'
  | 'Expired'
  /** Replaced by a re-priced quote. Terminal: no order can be taken against it. */
  | 'Superseded'

export interface QuoteIssued {
  quoteId: string
  total: Money
  status: QuoteStatus
  /** True when the discount crossed the threshold, so it is Draft until a manager approves. */
  needsApproval: boolean
}

/**
 * Re-prices a quote by replacing it with a revised one.
 *
 * A RE-PRICE SUPERSEDES; IT DOES NOT EDIT. The quote the customer holds keeps its lines and its
 * total — that is the record of what was offered — and moves to `Superseded`, which is terminal.
 * The revision is a new quote against the same opportunity, carrying `supersedes` back to it.
 *
 * The lines are the whole revised set and not a patch: the server has no rule for what an omitted
 * line would mean, and every such rule is wrong for somebody.
 */
export interface RepriceQuote {
  quoteId: string
  lines: QuoteRequestLine[]
  discount: number
  validForDays: number
}

export interface QuoteSuperseded {
  /** The revision, which carries the new lines. */
  quoteId: string
  /** The quote it replaced, now `Superseded`. */
  supersedes: string
  total: Money
  status: QuoteStatus
  /**
   * The revision is priced from scratch. A discount a manager signed off on the old lines is not
   * one they signed off on these, so a revision past the threshold is a Draft either way.
   */
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
  /**
   * The handle for this application, and the only thing in this answer worth acting on.
   *
   * NOTHING HERE SAYS WHERE THE DEAL WENT, because when the server writes it nobody knows. The
   * transition is decided afterwards, off the change feed. This id is what `ReadTriggerOutcome`
   * takes, and reading it is how a client learns which of the three things happened.
   */
  applicationId: string
  opportunityId: string
  trigger: string
}

/** Asks what the published process made of one application of a trigger. */
export interface ReadTriggerOutcome {
  applicationId: string
}

/**
 * The three things that can have become of an applied trigger.
 *
 * TWO OF THESE USED TO BE ONE. A client could see that the deal had not moved and had no way to
 * tell a feed that has not arrived from an engine that ran and declined the move — no transition
 * carries the trigger out of that stage, or one does and a guard did not hold. Neither is an
 * error, so there was nothing to catch and this client guessed.
 */
export type TriggerOutcome = 'Pending' | 'Moved' | 'Declined'

/** What the process did with one application of a trigger. */
export interface TriggerOutcomeView {
  applicationId: string
  opportunityId: string
  trigger: string
  outcome: TriggerOutcome
  /** Where this application left the deal: the stage entered when it moved, else where it stayed. */
  stage: string
  /** How many configured actions ran. Zero unless it moved. */
  actionsRun: number
  appliedAt: string
  /** When the engine answered, or null while it has not. */
  decidedAt: string | null
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

// ─────────────────────────────────────────────────────────────── reports

export type ReportSource = 'Opportunity' | 'Lead' | 'Activity' | 'CustomObject'

export type ReportMeasure = 'Count' | 'Sum' | 'Average' | 'Min' | 'Max'

/**
 * Saves a report.
 *
 * `dimension` and `measureOf` are names from the source's own closed vocabulary — never a free
 * expression. That is what lets the server bind them rather than assemble a statement, and it is
 * why a client must read the vocabulary instead of offering every column it can think of.
 */
export interface DefineReport {
  name: string
  label: string
  source: ReportSource
  /** The custom object, for a `CustomObject` report. Null otherwise. */
  target: string | null
  dimension: string
  measure: ReportMeasure
  /** What to aggregate. Null for `Count`, which takes no field. */
  measureOf: string | null
}

export interface ReportDefined {
  reportId: string
}

export interface RunReport {
  name: string
}

export interface ReportGroup {
  /** `"(none)"` where the underlying value is null, so a bar always has a label. */
  dimension: string
  /** The measure, formatted invariantly — parse before you do arithmetic on it. */
  value: string
  /** How many rows are in the group, whatever the measure was. */
  rows: number
}

export interface ReportResult {
  name: string
  label: string
  /** How the groups were reduced. Not what was reduced — that is `measureOf`. */
  measure: string
  /** What was reduced, or null for `Count`. Without it a reader cannot know the unit. */
  measureOf: string | null
  /** Largest first, which is the order the server sorted them in. */
  groups: ReportGroup[]
}

/**
 * Declares a rule that refuses a write when it holds.
 *
 * READS BACKWARDS UNTIL SAID ALOUD: the rule describes the condition that is *refused*, not the
 * one that is required. `discount GreaterThan 35` refuses discounts over thirty-five.
 *
 * Exactly one of `appliesTo` and `target` — a rule on a built-in entity or a rule on a custom
 * object, never both and never neither.
 */
export interface DefineValidationRule {
  appliesTo: EntityKind | null
  target: string | null
  name: string
  field: string
  operator: GuardOperator
  value: string
  /** What the caller is told. The administrator's own words, never "validation failed". */
  message: string
}

export interface ValidationRuleDefined {
  ruleId: string
  name: string
}

/** How to order a saved view's rows. `numeric` picks the comparison, not the display. */
export interface RecordOrder {
  field: string
  descending: boolean
  numeric: boolean
}

/**
 * Saves a named query over a custom object.
 *
 * `target` is the object's id — a view belongs to something the tenant declared, which is why
 * there is no built-in-entity form of this.
 */
export interface DefineListView {
  target: string
  name: string
  label: string
  filter: RecordFilter | null
  order: RecordOrder | null
  limit: number
}

export interface ListViewDefined {
  viewId: string
  name: string
}

/**
 * What a declared field may hold.
 *
 * SEVEN, AND THE SET IS THE SERVER'S `CustomFieldType`. A type is a parse and a comparison, not a
 * label — so a type the backend cannot parse is a value nothing can validate or guard on, and the
 * enum is closed for that reason. The setup form used to offer `email`, `phone`, `currency`,
 * `percent`, `lookup` and `formula`: six words this backend has never heard of, on a form that
 * would have been refused by the first of them had it ever posted anything.
 */
export type CustomFieldType =
  | 'Text'
  | 'Number'
  | 'Boolean'
  | 'Date'
  | 'Picklist'
  | 'Reference'
  | 'MultiPicklist'

/**
 * One allowed value of a picklist.
 *
 * `value` is what is stored and compared, so it is named like a field; `label` is what a person
 * sees and nothing compares it.
 */
export interface CustomFieldOption {
  value: string
  label: string
}

/**
 * Declares a field on a built-in entity kind or on a custom object.
 *
 * Exactly one of `appliesTo` and `target`, the same rule {@link DefineValidationRule} follows —
 * migration 0005's `CHECK ((applies_to IS NULL) <> (object_id IS NULL))` is what makes a field
 * with two owners or none impossible, and the capability turns that into a named refusal.
 *
 * `options` is required for `Picklist` and `MultiPicklist` and empty for everything else: a closed
 * set of nothing accepts nothing. `references` is the object a `Reference` points at, and is null
 * for every other type.
 */
export interface DefineField {
  appliesTo: EntityKind | null
  target: string | null
  name: string
  label: string
  type: CustomFieldType
  isRequired: boolean
  options: CustomFieldOption[]
  references: string | null
  /** The scope a caller must hold to write it, or null when `crm.write` is enough. */
  requiredPermission: string | null
  isUnique: boolean
  /** The scope a caller must hold to see it, or null when `crm.read` is enough. */
  readPermission: string | null
}

export interface FieldDefined {
  fieldId: string
  name: string
}

/**
 * Declares an entity this build has never heard of.
 *
 * The whole claim the dynamic schema makes, and until now there was no way to make it from the
 * client — the setup screen listed objects and offered no control that declared one.
 */
export interface DefineObject {
  name: string
  label: string
}

export interface ObjectDefined {
  objectId: string
  name: string
}

/**
 * Sets a period's number and what it is for.
 *
 * ONE PER PERIOD; A SECOND REPLACES IT. The vision is required because a target with no statement
 * of intent is a number every level below interprets differently.
 */
export interface SetStrategy {
  period: string
  vision: string
  target: number
  currency: string
}

export interface StrategySet {
  strategyId: string
}

// ─────────────────────────────────────────────────────────────── the application's own manifest

/**
 * How a flow is reached.
 *
 * `Http` carries a method and a route; `Bus` and `Change` a topic and a consumer group;
 * `Schedule` a cron expression and a zone; `Agent` a description and nothing else. Which fields
 * are present depends on the kind, which is why they are all optional here.
 *
 * `Bus` and `Change` are the same shape and different things: a bus subscription consumes what
 * some flow published, and a change subscription consumes what the database itself emitted. A
 * screen that drew them alike would hide which of the two a flow is downstream of.
 */
export interface ManifestTrigger {
  kind: string
  method?: string
  route?: string
  topic?: string
  group?: string
  cron?: string
  timeZone?: string
  idempotent?: boolean
  description?: string
}

export interface ManifestFlow {
  id: string
  version: string
  /** `Durable` survives a restart; `Ephemeral` does not. The one property worth showing. */
  profile: string
  /** ISO 8601, e.g. `PT15S`. Absent where a flow declares none. */
  deadline?: string
  triggers: ManifestTrigger[]
  errors?: string[]
}

/**
 * What the compiler wrote about this application.
 *
 * NOT THE OPENAPI DOCUMENT. That describes an HTTP surface; this describes flows — including the
 * ones with no route at all, and the execution profile OpenAPI has nowhere to put.
 */
export interface Manifest {
  schemaVersion: string
  application: { name: string; version: string }
  flows: ManifestFlow[]
}
