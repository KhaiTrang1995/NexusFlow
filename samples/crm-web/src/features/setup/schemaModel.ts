import type { DescribedEntity, DescribedField, DescribedObject, SchemaDescription } from '@/api/contracts'

/**
 * One row per thing an administrator can look at, from what the server described.
 *
 * WHY THE TWO ARE FLATTENED. The server distinguishes a built-in entity from a custom object,
 * and it is right to: one is a table this build ships and the other is a row somebody added at
 * run time. A setup screen listing them is answering a different question — "what does this
 * tenant have" — and an administrator who has to look in two tables to find out is being shown
 * the implementation. `builtIn` keeps the distinction where it still matters.
 */
export interface SchemaRow {
  /** What to key a row on and ask the server for it by. */
  key: string
  /** What this tenant calls it, which is not always what this build calls it. */
  label: string
  /** Whether it ships with the build, or was declared at run time. */
  builtIn: boolean
  /** Its columns and fields together, in the order the description gave them. */
  fields: SchemaFieldRow[]
  /** The saved views over it. Only a custom object has any. */
  views: number
}

/** One field or column, with the permission already resolved for this caller. */
export interface SchemaFieldRow {
  name: string
  label: string
  type: string
  required: boolean
  computed: boolean
  /** Whether this caller may see it. Answered by the server, never re-derived here. */
  canRead: boolean
  /** Whether this caller may change it. */
  canWrite: boolean
  options: readonly string[]
  /**
   * Whether it is a column of the table rather than something an administrator added. A built-in
   * column has no type or permission of its own in the description — it is always readable by
   * anyone who may read the entity — so those are stated rather than invented.
   */
  declared: boolean
}

/** Every object and entity the caller may see, built-ins first. */
export function schemaRows(description: SchemaDescription | undefined): SchemaRow[] {
  if (description === undefined) {
    return []
  }

  return [
    ...description.entities.map(fromEntity),
    ...description.objects.map(fromObject),
  ]
}

/** One row by key, or undefined when the tenant has no such thing. */
export function schemaRowOf(
  description: SchemaDescription | undefined,
  key: string,
): SchemaRow | undefined {
  return schemaRows(description).find((row) => row.key === key)
}

function fromEntity(entity: DescribedEntity): SchemaRow {
  return {
    key: entity.kind,
    label: entity.label,
    builtIn: true,
    views: 0,
    fields: [
      ...entity.columns.map((column) => ({
        name: column.name,
        label: column.label,
        type: 'column',
        required: false,
        computed: false,
        canRead: true,
        canWrite: false,
        options: [],
        declared: false,
      })),
      ...entity.fields.map(fromField),
    ],
  }
}

function fromObject(object: DescribedObject): SchemaRow {
  return {
    key: object.name,
    label: object.label,
    builtIn: false,
    views: object.views.length,
    fields: object.fields.map(fromField),
  }
}

function fromField(field: DescribedField): SchemaFieldRow {
  return {
    name: field.name,
    label: field.label,
    type: field.type,
    required: field.isRequired,
    computed: field.isComputed,
    canRead: field.canRead,
    canWrite: field.canWrite,
    options: field.options,
    declared: true,
  }
}
