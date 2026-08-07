import { useState } from 'react'
import { Button, Drawer, DrawerSection, ErrorState, SelectField, TextField } from '@/design/primitives'
import { useCreateTask, useEntityPage } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { useToast } from '@/app/ToastProvider'
import type { ActivityKind, EntityKind } from '@/api/contracts'
import { dueAtFromDays } from './newWork'

const KINDS: readonly ActivityKind[] = ['Task', 'Call', 'Meeting', 'Note']

/** The four things an activity can hang off, and where to read their names from. */
const SUBJECTS: readonly { kind: EntityKind; label: string; nameColumn: string }[] = [
  { kind: 'Account', label: 'Account', nameColumn: 'name' },
  { kind: 'Opportunity', label: 'Opportunity', nameColumn: 'name' },
  { kind: 'Contact', label: 'Contact', nameColumn: 'full_name' },
  { kind: 'Lead', label: 'Lead', nameColumn: 'company' },
]

/**
 * Creates a task, call, meeting or note against a record.
 *
 * THE RECORD IS CHOSEN, NOT TYPED. `relatesTo` is a kind and an id together — the server's
 * polymorphic trigger checks that the pair exists, and a form that asked for an id would be
 * asking somebody to paste a UUID and would fail on the trigger when they got it wrong.
 *
 * The list of candidates is read from the same page endpoint the record screens use, so a record
 * created a minute ago is selectable here without this form knowing anything about how it was.
 */
export function NewTaskDrawer({ onClose }: { onClose: () => void }) {
  const create = useCreateTask()
  const toast = useToast()
  const { ownerId } = useSession()

  const [kind, setKind] = useState<ActivityKind>('Task')
  const [subject, setSubject] = useState('')
  const [relatesToKind, setRelatesToKind] = useState<EntityKind>('Opportunity')
  const [relatesToId, setRelatesToId] = useState('')
  const [dueInDays, setDueInDays] = useState('7')

  const chosen = SUBJECTS.find((entry) => entry.kind === relatesToKind) ?? SUBJECTS[0]!
  const candidates = useEntityPage(relatesToKind, 100)

  const options = (candidates.data?.records ?? []).map((record) => ({
    value: record.recordId,
    label: record.values[chosen.nameColumn] ?? record.recordId,
  }))

  const ready = subject.trim().length > 0 && relatesToId.length > 0

  function submit() {
    create.mutate(
      {
        kind,
        subject: subject.trim(),
        relatesTo: { kind: relatesToKind, id: relatesToId },
        owner: ownerId,
        // A due date is optional, and "no date" is not "today": a task with no date is one
        // nobody promised a day for, and the overdue sweep leaves it alone.
        dueAt: dueAtFromDays(dueInDays, new Date()),
      },
      {
        onSuccess: () => {
          toast.saved(`${subject.trim()} created.`)
          onClose()
        },
      },
    )
  }

  return (
    <Drawer
      eyebrow="New"
      title="Create a task"
      subtitle="A polymorphic reference held up by a trigger, not a foreign key"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || create.isPending} onClick={submit}>
            {create.isPending ? 'Creating…' : 'Create'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="What" />
      <SelectField
        label="Kind"
        value={kind}
        options={KINDS.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setKind(event.target.value as ActivityKind)}
      />
      <TextField
        label="Subject"
        value={subject}
        required
        onChange={(event) => setSubject(event.target.value)}
      />

      <DrawerSection label="Against what" />
      <SelectField
        label="Related to"
        value={relatesToKind}
        options={SUBJECTS.map((entry) => ({ value: entry.kind, label: entry.label }))}
        onChange={(event) => {
          setRelatesToKind(event.target.value as EntityKind)
          // The chosen record belongs to the old kind. Keeping it would send a pair the
          // trigger refuses — an id that exists, of a kind it is not.
          setRelatesToId('')
        }}
      />
      <SelectField
        label={chosen.label}
        value={relatesToId}
        placeholder={candidates.isPending ? 'reading…' : `Choose an ${chosen.label.toLowerCase()}`}
        options={options}
        required
        onChange={(event) => setRelatesToId(event.target.value)}
      />

      <DrawerSection label="When" />
      <TextField
        label="Due in days"
        type="number"
        value={dueInDays}
        hint="Leave empty for a task nobody promised a day for."
        onChange={(event) => setDueInDays(event.target.value)}
      />

      {create.isError ? <ErrorState error={create.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
