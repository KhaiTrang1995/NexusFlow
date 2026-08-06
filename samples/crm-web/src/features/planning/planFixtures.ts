/**
 * The planning screens' own content.
 *
 * These four screens — account, opportunity and lead planning, and operations — have no read
 * endpoint on the .NET sample: it can *write* a plan, an objective, a stakeholder, a risk and a
 * step, and it rolls them up, but there is no "read one plan" surface. So the detail here is the
 * prototype's, behind the same shape a hook would return, and the write forms below post to the
 * endpoints that do exist.
 */

export interface Objective {
  name: string
  measure: string
  target: string
  owner: string
  status: 'On track' | 'At risk' | 'Off track'
}

export interface Stakeholder {
  name: string
  title: string
  role: 'Champion' | 'Economic Buyer' | 'Technical' | 'Blocker'
  stance: 'Advocate' | 'Neutral' | 'Against' | 'Unknown'
  lastSeen: string
}

export interface Risk {
  what: string
  impact: 'High' | 'Medium' | 'Low'
  owner: string
  mitigation: string
  closed: boolean
}

export interface PlanStep {
  what: string
  who: string
  due: string
  overdue: boolean
  done: boolean
}

export interface QualificationElement {
  name: string
  question: string
  answer: string | null
}

export const OBJECTIVES: readonly Objective[] = [
  { name: 'expand_platform', measure: 'Revenue', target: '$1.20M ARR', owner: 'A. Ruiz', status: 'On track' },
  { name: 'displace_incumbent', measure: 'Logos', target: '2 business units', owner: 'A. Ruiz', status: 'At risk' },
  { name: 'exec_sponsor', measure: 'Relationship', target: 'CFO sponsorship signed', owner: 'B. Vance', status: 'On track' },
  { name: 'reference', measure: 'Advocacy', target: 'Public case study', owner: 'M. Chen', status: 'Off track' },
]

export const STAKEHOLDERS: readonly Stakeholder[] = [
  { name: 'Elena Vargas', title: 'Director, IT', role: 'Champion', stance: 'Advocate', lastSeen: '3 Aug' },
  { name: 'Ron Petrov', title: 'CFO', role: 'Economic Buyer', stance: 'Neutral', lastSeen: '29 Jul' },
  { name: 'Priya Raman', title: 'CIO', role: 'Technical', stance: 'Unknown', lastSeen: 'never' },
  { name: 'Ana Sousa', title: 'Procurement Lead', role: 'Blocker', stance: 'Against', lastSeen: '28 Jun' },
]

export const RISKS: readonly Risk[] = [
  { what: 'Procurement has not agreed the security addendum', impact: 'High', owner: 'A. Ruiz', mitigation: 'Legal to send a redlined addendum by Friday', closed: false },
  { what: 'The economic buyer has not been met since the reorganisation', impact: 'High', owner: 'B. Vance', mitigation: 'Executive sponsor to request a meeting', closed: false },
  { what: 'A competitor is running a proof of concept in the same business unit', impact: 'Medium', owner: 'M. Chen', mitigation: 'Bring forward the technical bake-off', closed: false },
  { what: 'Data residency was raised as a blocker', impact: 'Medium', owner: 'A. Ruiz', mitigation: 'Resolved — the EU region went live in July', closed: true },
]

export const STEPS: readonly PlanStep[] = [
  { what: 'Send the redlined MSA', who: 'A. Ruiz', due: '6 Aug', overdue: false, done: false },
  { what: 'Security questionnaire answered', who: 'J. Park', due: '4 Aug', overdue: true, done: false },
  { what: 'CFO briefing booked', who: 'B. Vance', due: '11 Aug', overdue: false, done: false },
  { what: 'Reference call with Ledgerworks', who: 'M. Chen', due: '30 Jul', overdue: false, done: true },
]

/**
 * The eight elements a deal plan is qualified against.
 *
 * ANSWERED OR NOT — NEVER A SELF-SCORED RATING. A seller who rates their own deal seven out of ten
 * on "compelling event" has told you nothing; a seller who cannot write down what the compelling
 * event is has told you everything.
 */
export const QUALIFICATION: readonly QualificationElement[] = [
  { name: 'Metrics', question: 'What number does this move, and by how much?', answer: '9% of shipments are queried; the case is to halve that.' },
  { name: 'Economic buyer', question: 'Who signs, and have we met them?', answer: 'Ron Petrov, CFO. Met once, in April.' },
  { name: 'Decision criteria', question: 'What are we being judged on?', answer: 'Audit trail, EU residency, and time to first value.' },
  { name: 'Decision process', question: 'What happens between now and signature?', answer: null },
  { name: 'Paper process', question: 'Who has to touch the contract?', answer: 'Legal, then procurement, then the CFO.' },
  { name: 'Identified pain', question: 'What happens if they do nothing?', answer: 'Peak season repeats last year: 14 depots reconciling by hand.' },
  { name: 'Champion', question: 'Who sells for us when we are not there?', answer: 'Elena Vargas.' },
  { name: 'Competition', question: 'Who else is in it?', answer: null },
]

export interface DemandRow {
  segment: string
  channel: string
  target: number
  actual: number
  costPerLead: number
}

export const DEMAND: readonly DemandRow[] = [
  { segment: 'Enterprise', channel: 'Event', target: 120, actual: 96, costPerLead: 340 },
  { segment: 'Enterprise', channel: 'Outbound', target: 200, actual: 214, costPerLead: 145 },
  { segment: 'Mid-market', channel: 'Web', target: 480, actual: 512, costPerLead: 62 },
  { segment: 'Mid-market', channel: 'Content', target: 300, actual: 188, costPerLead: 88 },
  { segment: 'Public sector', channel: 'Partner', target: 90, actual: 41, costPerLead: 410 },
]

export interface CapacityRow {
  team: string
  people: number
  rampedPeople: number
  quotaEach: number
  capacity: number
  target: number
}

export const CAPACITY: readonly CapacityRow[] = [
  { team: 'Enterprise NA', people: 8, rampedPeople: 6.5, quotaEach: 520_000, capacity: 3_380_000, target: 3_600_000 },
  { team: 'Enterprise EMEA', people: 5, rampedPeople: 4.0, quotaEach: 480_000, capacity: 1_920_000, target: 1_800_000 },
  { team: 'Mid-market NA', people: 12, rampedPeople: 10.5, quotaEach: 300_000, capacity: 3_150_000, target: 3_400_000 },
  { team: 'APAC', people: 4, rampedPeople: 2.5, quotaEach: 340_000, capacity: 850_000, target: 1_100_000 },
]
