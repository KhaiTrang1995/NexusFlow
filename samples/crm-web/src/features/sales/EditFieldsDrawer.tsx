import { useState } from 'react'
import {
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  SelectField,
  Skeleton,
  TextField,
} from '@/design/primitives'
import { useSchema, useSetCustomFields } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { DescribedField, EntityKind } from '@/api/contracts'

/**
 * Edits the custom fields an administrator declared on a built-in record.
 *
 * ONLY WHAT CHANGED IS SENT. The write is a merge, so two people editing different fields of one
 * account do not overwrite each other — which is what posting the whole record does, and the
 * loser never finds out. A field left alone is absent from the payload; a field cleared is
 * present and null, and the two mean different things.
 *
 * THE FORM IS BUILT FROM `describe`, INCLUDING THE PERMISSIONS. A field this caller may not write
 * is shown and disabled rather than hidden: hiding it makes the screen look like the field does
 * not exist, and the reader goes looking for it in setup.
 *
 * NOTHING HERE EDITS A BUILT-IN COLUMN. There is no write for those on this build, and a form
 * that offered one would be a form whose Save did nothing.
 */
export function EditFieldsDrawer({
  kind,
  id,
  title,
  onClose,
}: {
  kind: EntityKind
  id: string
  title: string
  onClose: () => void
}) {
  const schema = useSchema()
  const save = useSetCustomFields()
  const toast = useToast()

  const [edited, setEdited] = useState<Record<string, string | null>>({})

  const declared =
    schema.data?.entities.find((entity) => entity.kind === kind)?.fields ?? []

  const writable = declared.filter((field) => !field.isComputed)

  function submit() {
    save.mutate(
      { kind, id, values: edited },
      {
        onSuccess: () => {
          toast.saved(`${title} updated.`)
          onClose()
        },
      },
    )
  }

  return (
    <Drawer
      eyebrow="Edit"
      title={title}
      subtitle="A merge, so two clients editing different fields do not collide"
      onClose={onClose}
      actions={
        <>
          <Button
            tone="primary"
            disabled={Object.keys(edited).length === 0 || save.isPending}
            onClick={submit}
          >
            {save.isPending ? 'Saving…' : `Save ${Object.keys(edited).length} change(s)`}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Declared fields" />

      {schema.isPending ? <Skeleton rows={4} /> : null}

      {schema.isSuccess && writable.length === 0 ? (
        <p>
          Nothing has been declared on {kind.toLowerCase()}s. Declare a field in setup and it
          appears here — no deployment, and nothing in this client knows its name.
        </p>
      ) : null}

      {writable.map((field) => (
        <FieldInput
          key={field.name}
          field={field}
          value={edited[field.name] ?? ''}
          onChange={(next) =>
            setEdited((current) => ({
              ...current,
              // Empty clears the field. Null is what the server reads as "remove this value",
              // and "" would be a value of empty string — a different thing on every report.
              [field.name]: next.length > 0 ? next : null,
            }))
          }
        />
      ))}

      {save.isError ? <ErrorState error={save.error} onRetry={submit} /> : null}
    </Drawer>
  )
}

/** One declared field, rendered as whatever its type says it is. */
function FieldInput({
  field,
  value,
  onChange,
}: {
  field: DescribedField
  value: string
  onChange: (next: string) => void
}) {
  const hint = field.canWrite ? undefined : 'This token may not write this field.'

  if (field.type === 'Picklist' && field.options.length > 0) {
    return (
      <SelectField
        label={field.label}
        value={value}
        disabled={!field.canWrite}
        hint={hint}
        placeholder="Leave unchanged"
        options={field.options.map((option) => ({ value: option, label: option }))}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }

  return (
    <TextField
      label={field.label}
      value={value}
      disabled={!field.canWrite}
      hint={hint}
      type={field.type === 'Number' ? 'number' : field.type === 'Date' ? 'date' : 'text'}
      placeholder="Leave unchanged"
      onChange={(event) => onChange(event.target.value)}
    />
  )
}
