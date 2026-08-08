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
  /**
   * The object's id, or null for a built-in entity, which has none.
   *
   * What a write has to send. `DefineField` takes an owner that is either an entity *kind* or an
   * object *id*, and a form that had only the key could not tell which it was holding.
   */
  objectId: string | null
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
   * The id of the object a `Reference` field points at, and null for every other type.
   *
   * Carried because the schema screen draws the edges from it. It used to draw them from `name`,
   * so a reference called `owner` was rendered as an edge into an entity called "owner" — a
   * relationship diagram naming things the tenant does not have.
   */
  references: string | null
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

/** One edge of the schema: a reference field, and the object it points at. */
export interface SchemaEdge {
  /** The object holding the reference. */
  from: string
  /** The object it points at, by label — or the raw id when it is not one this caller can see. */
  to: string
  /** The field carrying it. */
  via: string
}

/**
 * The relationships, read from the reference fields rather than drawn by hand.
 *
 * THE TARGET IS `references`, NOT `name`. The screen used to render `field.name` as the far end,
 * so a reference field called `owner` drew an edge into an entity called "owner" — a diagram
 * naming things this tenant does not have, on the screen an administrator opens to find out what
 * it does have. `references` is an object id, so it is resolved back to a label here; an id that
 * resolves to nothing is printed as itself rather than dropped, because a dangling reference is
 * worth seeing.
 */
export function schemaEdges(description: SchemaDescription | undefined): SchemaEdge[] {
  const rows = schemaRows(description)
  const byId = new Map(rows.filter((row) => row.objectId !== null).map((row) => [row.objectId, row]))

  return rows.flatMap((row) =>
    row.fields
      .filter((field) => field.references !== null)
      .map((field) => ({
        from: row.label,
        to: byId.get(field.references)?.label ?? (field.references as string),
        via: field.label,
      })),
  )
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
    objectId: null,
    fields: [
      ...entity.columns.map((column) => ({
        name: column.name,
        label: column.label,
        type: 'column',
        required: false,
        computed: false,
        canRead: true,
        canWrite: false,
        // A built-in column's closed set, when it has one. Defended against absence because a
        // server older than the field answers a column with a name and a label and nothing else.
        options: column.options ?? [],
        references: null,
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
    objectId: object.id,
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
    references: field.references,
    declared: true,
  }
}
