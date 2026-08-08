import type { CustomFieldOption, CustomFieldType, DefineField, EntityKind } from '@/api/contracts'
import type { SchemaRow } from './schemaModel'

/**
 * What the fields form is holding, and what it becomes.
 *
 * <strong>The types are the server's seven and not the prototype's ten.</strong> The form offered
 * `email`, `phone`, `currency`, `percent`, `lookup` and `formula` — six words this backend has
 * never heard of. It got away with it because the form posted nothing at all: "Declare the field"
 * toasted `${label} declared on ${object}` and never touched the network, so nobody ever saw the
 * refusal that every one of those six would have earned.
 *
 * <strong>The rules restated here are the ones a person can be told about while typing.</strong>
 * Each mirrors a refusal `crm.custom.define_field` returns — a picklist with no options, a name
 * that is not lower snake case, a reference pointing at nothing, a field with no owner. The
 * server still decides; this exists so the administrator is not told "refused" for something the
 * form could have said before they pressed the button.
 */
export interface FieldDraft {
  /** Which object or entity it is being declared on. Undefined before the schema has arrived. */
  owner: SchemaRow | undefined
  name: string
  label: string
  type: CustomFieldType
  required: boolean
  /** The picklist's values, comma separated as typed. */
  options: string
  /** The object id a `Reference` points at. */
  references: string
}

/** Every type the backend can parse. Adding an eighth is a change on both sides. */
export const FIELD_TYPES: readonly CustomFieldType[] = [
  'Text',
  'Number',
  'Boolean',
  'Date',
  'Picklist',
  'MultiPicklist',
  'Reference',
]

/** Whether a type is a closed set, and so needs its values. */
export function isClosedSet(type: CustomFieldType): boolean {
  return type === 'Picklist' || type === 'MultiPicklist'
}

/**
 * The typed values of a picklist.
 *
 * One entry per comma, stored and shown as the same string. The server compares `value` as text
 * and shows `label` to a person; offering two boxes for what an administrator types as one list
 * is a form nobody finishes, and a value that differs from its label is a rename away from a
 * guard that stops matching.
 */
export function optionsOf(text: string): CustomFieldOption[] {
  return text
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
    .map((entry) => ({ value: entry, label: entry }))
}

/**
 * The server's `CustomValues.IsUsableName`, restated.
 *
 * Sixty-three characters, starting with a lower-case letter, then lower-case letters, digits and
 * underscores. It is the identifier a guard, a rule and a report all refer to the field by, which
 * is why it is closed and why it never changes again.
 */
export function isUsableName(name: string): boolean {
  return /^[a-z][a-z0-9_]{0,62}$/.test(name)
}

/**
 * Why this draft cannot be sent, in words, or null when it can.
 *
 * A sentence rather than a boolean because it is also the `title` on the disabled button — a
 * control that is off and does not say why is the one a reader cannot diagnose.
 */
export function refusalOf(draft: FieldDraft): string | null {
  if (draft.owner === undefined) {
    return 'Choose the object this field belongs to.'
  }

  if (draft.name.trim().length === 0 || draft.label.trim().length === 0) {
    return 'A field needs a name and a label.'
  }

  if (!isUsableName(draft.name.trim())) {
    return 'The name must start with a lower-case letter and hold only lower-case letters, digits and underscores.'
  }

  if (isClosedSet(draft.type) && optionsOf(draft.options).length === 0) {
    return 'A picklist with no options is a field nothing can ever be chosen for.'
  }

  if (isClosedSet(draft.type) && optionsOf(draft.options).some((one) => !isUsableName(one.value))) {
    return 'Each option is stored and compared as text, so it follows the same rule as a name.'
  }

  if (draft.type === 'Reference' && draft.references.length === 0) {
    return 'A reference has to point at a declared object.'
  }

  return null
}

/**
 * The request, or null when {@link refusalOf} has something to say.
 *
 * <strong>Exactly one of `appliesTo` and `target`.</strong> A built-in entity is named by its
 * kind and a custom object by its id; the server's `CHECK ((applies_to IS NULL) <> (object_id IS
 * NULL))` makes a field with two owners or none impossible, and a form that sent both would
 * simply be refused.
 */
export function requestOf(draft: FieldDraft): DefineField | null {
  const owner = draft.owner

  if (owner === undefined || refusalOf(draft) !== null) {
    return null
  }

  return {
    appliesTo: owner.objectId === null ? (owner.key as EntityKind) : null,
    target: owner.objectId,
    name: draft.name.trim(),
    label: draft.label.trim(),
    type: draft.type,
    isRequired: draft.required,
    options: isClosedSet(draft.type) ? optionsOf(draft.options) : [],
    references: draft.type === 'Reference' ? draft.references : null,

    // Field-level security and uniqueness are declared here and not offered on this form: both
    // are permanent properties of the field, and neither has a read anywhere in this client that
    // would let an administrator see what they had chosen afterwards. A control whose effect is
    // invisible is the shape this whole screen was.
    requiredPermission: null,
    isUnique: false,
    readPermission: null,
  }
}
