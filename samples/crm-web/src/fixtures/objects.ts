/**
 * The prototype's object model and its records.
 *
 * WHY FIXTURES AT ALL. The .NET sample serves twenty-two of this design's surfaces and not the
 * other twenty — it has no `account` list endpoint, no record page, no kanban. Rather than ship
 * twenty blank screens or invent twenty endpoints, the screens that have no server read from here
 * through the same hook shape the server-backed ones use, so wiring one up later is a change to
 * one hook and nothing else.
 *
 * EVERY VALUE IS THE DESIGN'S OWN. Nothing here is made up to fill a column: the records, the
 * picklists, the stage probabilities and the layouts are transcribed from
 * `CRM Platform.dc.html`, which is what makes the screens look like the thing that was designed.
 */

export type FieldType =
  | 'text'
  | 'email'
  | 'phone'
  | 'number'
  | 'currency'
  | 'percent'
  | 'date'
  | 'picklist'
  | 'lookup'
  | 'formula'

export interface FieldDefinition {
  name: string
  label: string
  type: FieldType
  required?: boolean
  /** For a picklist. */
  options?: readonly string[]
  /** For a lookup — what it points at. */
  to?: string
  /** For a formula — what it computes, in words. */
  formula?: string
}

export interface LayoutSection {
  title: string
  columns: 1 | 2
  fields: readonly string[]
}

export interface StageDefinition {
  name: string
  /** The probability the stage implies. */
  pct: number
  won?: boolean
  lost?: boolean
}

export type RecordRow = Record<string, string | number | undefined> & { id: string }

export interface ObjectModel {
  key: string
  label: string
  plural: string
  /** Two letters, for a record's avatar. */
  mono: string
  fields: readonly FieldDefinition[]
  /** Which fields the list view shows, in order. */
  listCols: readonly string[]
  layout: readonly LayoutSection[]
  /** Which field the kanban groups by. Absent means the object has no board. */
  stageField?: string
  stages?: readonly StageDefinition[]
  records: readonly RecordRow[]
}

const field = (
  name: string,
  label: string,
  type: FieldType,
  extra: Omit<FieldDefinition, 'name' | 'label' | 'type'> = {},
): FieldDefinition => ({ name, label, type, ...extra })

export const OBJECT_MODELS: Readonly<Record<string, ObjectModel>> = {
  lead: {
    key: 'lead',
    label: 'Lead',
    plural: 'Leads',
    mono: 'LD',
    fields: [
      field('name', 'Name', 'text', { required: true }),
      field('company', 'Company', 'text', { required: true }),
      field('title', 'Title', 'text'),
      field('email', 'Email', 'email'),
      field('phone', 'Phone', 'phone'),
      field('status', 'Lead Status', 'picklist', {
        options: ['New', 'Working', 'Qualified', 'Nurture', 'Unqualified'],
      }),
      field('source', 'Lead Source', 'picklist', {
        options: ['Web', 'Partner', 'Event', 'Outbound', 'Referral'],
      }),
      field('score', 'Score', 'number'),
      field('need', 'Requirement', 'text'),
      field('industry', 'Industry', 'picklist', {
        options: ['SaaS', 'Fintech', 'Logistics', 'Health'],
      }),
      field('owner', 'Owner', 'lookup', { to: 'User' }),
      field('created', 'Created', 'date'),
    ],
    listCols: ['name', 'company', 'title', 'need', 'status', 'source', 'score', 'owner'],
    layout: [
      { title: 'Identity', columns: 2, fields: ['name', 'title', 'company', 'industry'] },
      { title: 'Contact', columns: 2, fields: ['email', 'phone'] },
      { title: 'Qualification', columns: 1, fields: ['need'] },
      { title: 'Routing', columns: 2, fields: ['status', 'source', 'score', 'owner'] },
    ],
    stageField: 'status',
    stages: [
      { name: 'New', pct: 10 },
      { name: 'Working', pct: 25 },
      { name: 'Qualified', pct: 50 },
      { name: 'Nurture', pct: 15 },
      { name: 'Unqualified', pct: 0 },
    ],
    records: [
      {
        id: 'L-2201',
        need: 'Replace a manual reconciliation process across 14 depots before the peak season',
        name: 'Dana Whitfield',
        company: 'Perimeter Logistics',
        title: 'VP Operations',
        email: 'd.whitfield@perimeter.co',
        phone: '+1 415 220 7781',
        status: 'Qualified',
        source: 'Partner',
        score: 84,
        industry: 'Logistics',
        owner: 'A. Ruiz',
        created: '2026-07-14',
      },
      {
        id: 'L-2202',
        need: 'Data residency in the EU and an audit trail their regulator will accept',
        name: 'Samir Bhatt',
        company: 'Ledgerworks',
        title: 'Head of Platform',
        email: 's.bhatt@ledgerworks.io',
        phone: '+1 206 771 3390',
        status: 'Working',
        source: 'Web',
        score: 61,
        industry: 'Fintech',
        owner: 'M. Chen',
        created: '2026-07-19',
      },
      {
        id: 'L-2203',
        need: 'One reporting layer over three regional systems after the merger',
        name: 'Elena Vargas',
        company: 'Northwind Systems',
        title: 'Director, IT',
        email: 'e.vargas@northwind.com',
        phone: '+1 312 508 1142',
        status: 'New',
        source: 'Event',
        score: 47,
        industry: 'SaaS',
        owner: 'K. Osei',
        created: '2026-07-28',
      },
      {
        id: 'L-2204',
        need: 'Cut invoice disputes; currently 9% of shipments are queried',
        name: 'Tom Ilves',
        company: 'Baltic Freight',
        title: 'COO',
        email: 't.ilves@balticfreight.eu',
        phone: '+372 5510 2288',
        status: 'Nurture',
        source: 'Outbound',
        score: 33,
        industry: 'Logistics',
        owner: 'A. Ruiz',
        created: '2026-06-30',
      },
      {
        id: 'L-2205',
        need: 'Clinical analytics with role-based access for 40 hospitals',
        name: 'Priya Raman',
        company: 'Cardinal Health Group',
        title: 'CIO',
        email: 'p.raman@cardinalhg.com',
        phone: '+1 617 224 9903',
        status: 'Qualified',
        source: 'Referral',
        score: 91,
        industry: 'Health',
        owner: 'J. Park',
        created: '2026-07-02',
      },
      {
        id: 'L-2206',
        need: 'Loyalty data joined to store operations for a Q4 campaign',
        name: 'Marcus Feld',
        company: 'Orbit Retail',
        title: 'VP Digital',
        email: 'm.feld@orbitretail.com',
        phone: '+1 646 118 4420',
        status: 'Working',
        source: 'Web',
        score: 55,
        industry: 'SaaS',
        owner: 'M. Chen',
        created: '2026-07-21',
      },
    ],
  },

  account: {
    key: 'account',
    label: 'Account',
    plural: 'Accounts',
    mono: 'AC',
    fields: [
      field('name', 'Account Name', 'text', { required: true }),
      field('type', 'Type', 'picklist', { options: ['Customer', 'Prospect', 'Partner'] }),
      field('tier', 'Tier', 'picklist', { options: ['Strategic', 'Enterprise', 'Mid-market'] }),
      field('industry', 'Industry', 'picklist', {
        options: ['SaaS', 'Fintech', 'Logistics', 'Health'],
      }),
      field('arr', 'ARR', 'currency'),
      field('employees', 'Employees', 'number'),
      field('owner', 'Owner', 'lookup', { to: 'User' }),
      field('region', 'Region', 'picklist', { options: ['NA', 'EMEA', 'APAC'] }),
      field('health', 'Health', 'picklist', { options: ['Green', 'Amber', 'Red'] }),
      field('renewal', 'Renewal Date', 'date'),
    ],
    listCols: ['name', 'type', 'tier', 'industry', 'arr', 'region', 'health', 'owner'],
    layout: [
      { title: 'Account', columns: 2, fields: ['name', 'type', 'tier', 'industry'] },
      { title: 'Commercial', columns: 2, fields: ['arr', 'renewal', 'employees', 'region'] },
      { title: 'Ownership', columns: 2, fields: ['owner', 'health'] },
    ],
    records: [
      { id: 'A-101', name: 'Northwind Systems', type: 'Customer', tier: 'Strategic', industry: 'SaaS', arr: 940000, employees: 2400, owner: 'A. Ruiz', region: 'NA', health: 'Green', renewal: '2027-01-31' },
      { id: 'A-102', name: 'Perimeter Logistics', type: 'Prospect', tier: 'Enterprise', industry: 'Logistics', arr: 0, employees: 5100, owner: 'A. Ruiz', region: 'NA', health: 'Amber', renewal: '—' },
      { id: 'A-103', name: 'Ledgerworks', type: 'Customer', tier: 'Mid-market', industry: 'Fintech', arr: 210000, employees: 340, owner: 'M. Chen', region: 'NA', health: 'Green', renewal: '2026-11-30' },
      { id: 'A-104', name: 'Cardinal Health Group', type: 'Prospect', tier: 'Strategic', industry: 'Health', arr: 0, employees: 12800, owner: 'J. Park', region: 'NA', health: 'Green', renewal: '—' },
      { id: 'A-105', name: 'Baltic Freight', type: 'Customer', tier: 'Mid-market', industry: 'Logistics', arr: 128000, employees: 760, owner: 'K. Osei', region: 'EMEA', health: 'Red', renewal: '2026-09-15' },
      { id: 'A-106', name: 'Orbit Retail', type: 'Partner', tier: 'Enterprise', industry: 'SaaS', arr: 385000, employees: 1900, owner: 'L. Novak', region: 'APAC', health: 'Green', renewal: '2027-03-01' },
    ],
  },

  contact: {
    key: 'contact',
    label: 'Contact',
    plural: 'Contacts',
    mono: 'CT',
    fields: [
      field('name', 'Name', 'text', { required: true }),
      field('account', 'Account', 'lookup', { to: 'Account', required: true }),
      field('title', 'Title', 'text'),
      field('email', 'Email', 'email'),
      field('phone', 'Phone', 'phone'),
      field('role', 'Buying Role', 'picklist', {
        options: ['Champion', 'Economic Buyer', 'Technical', 'Blocker'],
      }),
      field('owner', 'Owner', 'lookup', { to: 'User' }),
      field('lastActivity', 'Last Activity', 'date'),
    ],
    listCols: ['name', 'account', 'title', 'role', 'email', 'phone', 'lastActivity'],
    layout: [
      { title: 'Contact', columns: 2, fields: ['name', 'title', 'account', 'role'] },
      { title: 'Reach', columns: 2, fields: ['email', 'phone'] },
      { title: 'System', columns: 2, fields: ['owner', 'lastActivity'] },
    ],
    records: [
      { id: 'C-501', name: 'Elena Vargas', account: 'Northwind Systems', title: 'Director, IT', email: 'e.vargas@northwind.com', phone: '+1 312 508 1142', role: 'Champion', owner: 'A. Ruiz', lastActivity: '2026-08-03' },
      { id: 'C-502', name: 'Ron Petrov', account: 'Northwind Systems', title: 'CFO', email: 'r.petrov@northwind.com', phone: '+1 312 508 2210', role: 'Economic Buyer', owner: 'A. Ruiz', lastActivity: '2026-07-29' },
      { id: 'C-503', name: 'Dana Whitfield', account: 'Perimeter Logistics', title: 'VP Operations', email: 'd.whitfield@perimeter.co', phone: '+1 415 220 7781', role: 'Champion', owner: 'A. Ruiz', lastActivity: '2026-08-05' },
      { id: 'C-504', name: 'Samir Bhatt', account: 'Ledgerworks', title: 'Head of Platform', email: 's.bhatt@ledgerworks.io', phone: '+1 206 771 3390', role: 'Technical', owner: 'M. Chen', lastActivity: '2026-08-01' },
      { id: 'C-505', name: 'Priya Raman', account: 'Cardinal Health Group', title: 'CIO', email: 'p.raman@cardinalhg.com', phone: '+1 617 224 9903', role: 'Economic Buyer', owner: 'J. Park', lastActivity: '2026-07-24' },
      { id: 'C-506', name: 'Ana Sousa', account: 'Baltic Freight', title: 'Procurement Lead', email: 'a.sousa@balticfreight.eu', phone: '+372 5510 4471', role: 'Blocker', owner: 'K. Osei', lastActivity: '2026-06-28' },
    ],
  },

  opportunity: {
    key: 'opportunity',
    label: 'Opportunity',
    plural: 'Opportunities',
    mono: 'OP',
    fields: [
      field('name', 'Opportunity Name', 'text', { required: true }),
      field('account', 'Account', 'lookup', { to: 'Account', required: true }),
      field('stage', 'Stage', 'picklist', { required: true }),
      field('amount', 'Amount', 'currency', { required: true }),
      field('closeDate', 'Close Date', 'date', { required: true }),
      field('probability', 'Probability', 'percent'),
      field('forecast', 'Forecast Category', 'picklist', {
        options: ['Pipeline', 'Best Case', 'Commit', 'Closed'],
      }),
      field('type', 'Type', 'picklist', {
        options: ['New Business', 'Existing Business', 'Renewal'],
      }),
      field('source', 'Source', 'picklist', {
        options: ['Web', 'Partner', 'Event', 'Outbound', 'Referral'],
      }),
      field('nextStep', 'Next Step', 'text'),
      field('owner', 'Owner', 'lookup', { to: 'User' }),
      field('weighted', 'Weighted Amount', 'formula', { formula: 'Amount × Probability' }),
      field('created', 'Created', 'date'),
    ],
    listCols: ['name', 'account', 'stage', 'amount', 'probability', 'closeDate', 'owner', 'nextStep'],
    layout: [
      { title: 'Opportunity', columns: 2, fields: ['name', 'account', 'type', 'source'] },
      { title: 'Forecast', columns: 2, fields: ['amount', 'probability', 'closeDate', 'forecast', 'weighted'] },
      { title: 'Execution', columns: 1, fields: ['nextStep', 'owner'] },
    ],
    stageField: 'stage',
    stages: [
      { name: 'Prospecting', pct: 10 },
      { name: 'Discovery', pct: 20 },
      { name: 'Qualify', pct: 30 },
      { name: 'Solution Fit', pct: 45 },
      { name: 'Proposal', pct: 60 },
      { name: 'Negotiation', pct: 75 },
      { name: 'Contracting', pct: 90 },
      { name: 'Closed Won', pct: 100, won: true },
      { name: 'Closed Lost', pct: 0, lost: true },
    ],
    records: [
      { id: 'O-1041', name: 'Northwind — Platform Expansion', account: 'Northwind Systems', stage: 'Negotiation', amount: 184000, probability: 75, closeDate: '2026-08-28', forecast: 'Commit', type: 'Existing Business', source: 'Partner', nextStep: 'Legal review of MSA redlines', owner: 'A. Ruiz', created: '2026-04-11' },
      { id: 'O-1042', name: 'Perimeter — Fleet Rollout', account: 'Perimeter Logistics', stage: 'Proposal', amount: 246000, probability: 60, closeDate: '2026-08-31', forecast: 'Best Case', type: 'New Business', source: 'Partner', nextStep: 'Pricing workshop with ops', owner: 'A. Ruiz', created: '2026-05-02' },
      { id: 'O-1043', name: 'Ledgerworks — Tier 2 Upgrade', account: 'Ledgerworks', stage: 'Discovery', amount: 68000, probability: 20, closeDate: '2026-09-19', forecast: 'Pipeline', type: 'Existing Business', source: 'Web', nextStep: 'Confirm budget owner', owner: 'M. Chen', created: '2026-07-08' },
      { id: 'O-1044', name: 'Cardinal — Enterprise Pilot', account: 'Cardinal Health Group', stage: 'Solution Fit', amount: 415000, probability: 45, closeDate: '2026-08-25', forecast: 'Commit', type: 'New Business', source: 'Referral', nextStep: 'Security questionnaire due', owner: 'J. Park', created: '2026-03-27' },
      { id: 'O-1045', name: 'Baltic Freight — Renewal FY27', account: 'Baltic Freight', stage: 'Contracting', amount: 128000, probability: 90, closeDate: '2026-08-15', forecast: 'Commit', type: 'Renewal', source: 'Outbound', nextStep: 'Escalate churn risk to CS', owner: 'K. Osei', created: '2026-06-01' },
      { id: 'O-1046', name: 'Orbit Retail — APAC Expansion', account: 'Orbit Retail', stage: 'Prospecting', amount: 92000, probability: 10, closeDate: '2026-10-10', forecast: 'Pipeline', type: 'New Business', source: 'Event', nextStep: 'Map APAC stakeholders', owner: 'L. Novak', created: '2026-07-16' },
      { id: 'O-1047', name: 'Northwind — Managed Services', account: 'Northwind Systems', stage: 'Closed Won', amount: 76000, probability: 100, closeDate: '2026-07-31', forecast: 'Closed', type: 'Existing Business', source: 'Partner', nextStep: 'Handoff to delivery', owner: 'A. Ruiz', created: '2026-05-20' },
      { id: 'O-1048', name: 'Ledgerworks — Data Residency', account: 'Ledgerworks', stage: 'Closed Lost', amount: 54000, probability: 0, closeDate: '2026-07-22', forecast: 'Closed', type: 'New Business', source: 'Web', nextStep: 'Revisit in Q1', owner: 'M. Chen', created: '2026-04-30' },
      { id: 'O-1049', name: 'Perimeter — Analytics Add-on', account: 'Perimeter Logistics', stage: 'Qualify', amount: 58000, probability: 30, closeDate: '2026-08-29', forecast: 'Best Case', type: 'New Business', source: 'Outbound', nextStep: 'Share ROI model', owner: 'M. Chen', created: '2026-06-18' },
      { id: 'O-1050', name: 'Cardinal — Clinical Analytics', account: 'Cardinal Health Group', stage: 'Discovery', amount: 132000, probability: 20, closeDate: '2026-10-02', forecast: 'Pipeline', type: 'New Business', source: 'Referral', nextStep: 'Workshop with the clinical data team', owner: 'J. Park', created: '2026-07-22' },
      { id: 'O-1051', name: 'Orbit Retail — Loyalty Integration', account: 'Orbit Retail', stage: 'Proposal', amount: 76000, probability: 60, closeDate: '2026-09-08', forecast: 'Best Case', type: 'Existing Business', source: 'Partner', nextStep: 'Confirm integration scope', owner: 'L. Novak', created: '2026-06-11' },
      { id: 'O-1052', name: 'Baltic Freight — Telemetry Pilot', account: 'Baltic Freight', stage: 'Prospecting', amount: 45000, probability: 10, closeDate: '2026-11-14', forecast: 'Pipeline', type: 'New Business', source: 'Event', nextStep: 'Qualify the operations sponsor', owner: 'K. Osei', created: '2026-08-01' },
    ],
  },

  quote: {
    key: 'quote',
    label: 'Quote',
    plural: 'Quotes',
    mono: 'QT',
    fields: [
      field('number', 'Quote Number', 'text', { required: true }),
      field('opportunity', 'Opportunity', 'lookup', { to: 'Opportunity', required: true }),
      field('status', 'Status', 'picklist', {
        options: ['Draft', 'In Review', 'Sent', 'Accepted', 'Rejected'],
      }),
      field('total', 'Total', 'currency'),
      // An amount off, not a percentage off. `quote.discount` is `numeric(19,4)` in the schema,
      // and typing it as a percent rendered a 16,000-euro discount as "16000%" — which reads as a
      // broken screen rather than as the wrong unit, so nobody looks for the mapping.
      field('discount', 'Discount', 'currency'),
      field('expires', 'Expires', 'date'),
      field('owner', 'Owner', 'lookup', { to: 'User' }),
    ],
    listCols: ['number', 'opportunity', 'status', 'total', 'discount', 'expires', 'owner'],
    layout: [
      { title: 'Quote', columns: 2, fields: ['number', 'opportunity', 'status', 'owner'] },
      { title: 'Terms', columns: 2, fields: ['total', 'discount', 'expires'] },
    ],
    stageField: 'status',
    stages: [
      { name: 'Draft', pct: 0 },
      { name: 'In Review', pct: 0 },
      { name: 'Sent', pct: 0 },
      { name: 'Accepted', pct: 0, won: true },
      { name: 'Rejected', pct: 0, lost: true },
    ],
    records: [
      { id: 'Q-9001', number: 'Q-9001', opportunity: 'Northwind — Platform Expansion', status: 'Sent', total: 184000, discount: 14720, expires: '2026-08-20', owner: 'A. Ruiz' },
      { id: 'Q-9002', number: 'Q-9002', opportunity: 'Cardinal — Enterprise Pilot', status: 'In Review', total: 415000, discount: 49800, expires: '2026-08-30', owner: 'J. Park' },
      { id: 'Q-9003', number: 'Q-9003', opportunity: 'Baltic Freight — Renewal FY27', status: 'Accepted', total: 128000, discount: 5120, expires: '2026-08-10', owner: 'K. Osei' },
      { id: 'Q-9004', number: 'Q-9004', opportunity: 'Perimeter — Fleet Rollout', status: 'Draft', total: 246000, discount: 0, expires: '2026-09-05', owner: 'A. Ruiz' },
    ],
  },

  workorder: {
    key: 'workorder',
    label: 'Work Order',
    plural: 'Work Orders',
    mono: 'WO',
    fields: [
      field('number', 'Work Order', 'text', { required: true }),
      field('account', 'Account', 'lookup', { to: 'Account', required: true }),
      field('subject', 'Subject', 'text'),
      field('status', 'Status', 'picklist', {
        options: ['New', 'Scheduled', 'In Progress', 'Complete'],
      }),
      field('priority', 'Priority', 'picklist', {
        options: ['Low', 'Normal', 'High', 'Critical'],
      }),
      field('assignee', 'Assigned To', 'lookup', { to: 'User' }),
      field('scheduled', 'Scheduled', 'date'),
      field('hours', 'Est. Hours', 'number'),
    ],
    listCols: ['number', 'account', 'subject', 'status', 'priority', 'assignee', 'scheduled'],
    layout: [
      { title: 'Work Order', columns: 2, fields: ['number', 'account', 'subject', 'priority'] },
      { title: 'Scheduling', columns: 2, fields: ['status', 'assignee', 'scheduled', 'hours'] },
    ],
    stageField: 'status',
    stages: [
      { name: 'New', pct: 0 },
      { name: 'Scheduled', pct: 0 },
      { name: 'In Progress', pct: 0 },
      { name: 'Complete', pct: 0, won: true },
    ],
    records: [
      { id: 'W-3301', number: 'W-3301', account: 'Northwind Systems', subject: 'Onboarding — data migration', status: 'In Progress', priority: 'High', assignee: 'S. Adeyemi', scheduled: '2026-08-07', hours: 24 },
      { id: 'W-3302', number: 'W-3302', account: 'Baltic Freight', subject: 'Integration remediation', status: 'Scheduled', priority: 'Critical', assignee: 'T. Brand', scheduled: '2026-08-11', hours: 16 },
      { id: 'W-3303', number: 'W-3303', account: 'Ledgerworks', subject: 'SSO configuration', status: 'Complete', priority: 'Normal', assignee: 'S. Adeyemi', scheduled: '2026-07-30', hours: 6 },
      { id: 'W-3304', number: 'W-3304', account: 'Orbit Retail', subject: 'APAC region provisioning', status: 'New', priority: 'Normal', assignee: '—', scheduled: '2026-08-18', hours: 12 },
    ],
  },

  task: {
    key: 'task',
    label: 'Task',
    plural: 'Tasks',
    mono: 'TK',
    fields: [
      field('subject', 'Subject', 'text', { required: true }),
      field('related', 'Related To', 'lookup', { to: 'Any', required: true }),
      field('type', 'Type', 'picklist', { options: ['Call', 'Email', 'Meeting', 'Follow-up'] }),
      field('status', 'Status', 'picklist', { options: ['Open', 'In Progress', 'Done'] }),
      field('priority', 'Priority', 'picklist', { options: ['Low', 'Normal', 'High'] }),
      field('due', 'Due Date', 'date'),
      field('owner', 'Owner', 'lookup', { to: 'User' }),
    ],
    listCols: ['subject', 'related', 'type', 'status', 'priority', 'due', 'owner'],
    layout: [
      { title: 'Task', columns: 2, fields: ['subject', 'related', 'type', 'priority'] },
      { title: 'Tracking', columns: 2, fields: ['status', 'due', 'owner'] },
    ],
    stageField: 'status',
    stages: [
      { name: 'Open', pct: 0 },
      { name: 'In Progress', pct: 0 },
      { name: 'Done', pct: 0, won: true },
    ],
    records: [
      { id: 'T-7701', subject: 'Send redlined MSA to Ron Petrov', related: 'Northwind — Platform Expansion', type: 'Email', status: 'Open', priority: 'High', due: '2026-08-06', owner: 'A. Ruiz' },
      { id: 'T-7702', subject: 'Pricing workshop with ops team', related: 'Perimeter — Fleet Rollout', type: 'Meeting', status: 'Open', priority: 'High', due: '2026-08-06', owner: 'A. Ruiz' },
      { id: 'T-7703', subject: 'Confirm budget owner', related: 'Ledgerworks — Tier 2 Upgrade', type: 'Call', status: 'In Progress', priority: 'Normal', due: '2026-08-07', owner: 'M. Chen' },
      { id: 'T-7704', subject: 'Security questionnaire — upload answers', related: 'Cardinal — Enterprise Pilot', type: 'Follow-up', status: 'Open', priority: 'High', due: '2026-08-08', owner: 'J. Park' },
      { id: 'T-7705', subject: 'Churn risk escalation to CS', related: 'Baltic Freight — Renewal FY27', type: 'Call', status: 'Open', priority: 'Normal', due: '2026-08-06', owner: 'K. Osei' },
    ],
  },
}

export function modelFor(key: string): ObjectModel {
  return OBJECT_MODELS[key] ?? (OBJECT_MODELS['account'] as ObjectModel)
}

export function fieldOf(model: ObjectModel, name: string): FieldDefinition | undefined {
  return model.fields.find((candidate) => candidate.name === name)
}

/** A stage's picklist options come from its stage list, not from a second copy on the field. */
export function optionsFor(model: ObjectModel, name: string): readonly string[] {
  const definition = fieldOf(model, name)
  if (definition?.options) return definition.options
  if (model.stageField === name && model.stages) return model.stages.map((stage) => stage.name)
  return []
}
