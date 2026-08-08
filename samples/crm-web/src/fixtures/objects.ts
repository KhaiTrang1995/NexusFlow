/**
 * The prototype’s object model — the fields, the layouts and the picklists.
 *
 * THE ROWS ARE GONE AND WHAT IS LEFT IS PRESENTATION. This file used to carry seven arrays of
 * invented records — Northwind Systems, a €184,000 deal, five named contacts — and the list and
 * record screens fell back to them whenever a page had not arrived or a read had failed. A grey
 * eyebrow reading "sample data — the server did not answer" was the only warning, above a table
 * of clickable rows opening ids that resolve to nothing. Nobody reads an eyebrow.
 *
 * What remains is what no endpoint answers: which fields an object has, what they are called,
 * which are shown in a list and how a record page is laid out. Those are transcribed from
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
