import { useState } from 'react'
import {
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  SelectField,
  TextAreaField,
} from '@/design/primitives'
import { useAnswerQualification } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { QualificationElement } from '@/api/contracts'
import type { QualificationLine } from './qualification'

/**
 * Records whether one thing about a deal is actually known.
 *
 * KNOWN OR NOT, WITH A NOTE. Not a score: a seller asked to rate their own deal rates it
 * comfortably, and eight comfortable ratings is a pipeline nobody can compare. The server's
 * contract is the same two fields, which is why this form has no third.
 *
 * THE COUNT IN THE TOAST IS THE SERVER'S. `QualificationRecorded` comes back with how many of the
 * eight are now answered; repeating what was just typed would tell somebody their write landed
 * whether or not it did, which is the failure this whole area was swept for.
 *
 * AN ELEMENT THIS BUILD DOES NOT KNOW IS READ-ONLY. The write takes a value from a closed enum;
 * posting back a name that arrived from a newer server would be this client guessing at a
 * vocabulary it has not been told, and the refusal would land on somebody who did nothing wrong.
 */
export function AnswerQualificationDrawer({
  plan,
  line,
  onClose,
}: {
  plan: string
  line: QualificationLine
  onClose: () => void
}) {
  const record = useAnswerQualification()
  const toast = useToast()

  const [isAnswered, setAnswered] = useState(line.isAnswered)
  const [note, setNote] = useState(line.note)

  function submit() {
    record.mutate(
      {
        plan,
        element: line.element as QualificationElement,
        isAnswered,
        note: note.trim(),
      },
      {
        onSuccess: (result) => {
          toast.saved(`${line.label} recorded — ${result.answered} of ${result.outOf} answered.`)
          onClose()
        },
        onError: (error) => toast.failed(error, 'That answer was refused.'),
      },
    )
  }

  return (
    <Drawer
      eyebrow="Qualification"
      title={line.label}
      subtitle={line.asks === '' ? plan : line.asks}
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={record.isPending} onClick={submit}>
            {record.isPending ? 'Recording…' : 'Record'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Is it known" />
      <SelectField
        label="Known"
        value={isAnswered ? 'yes' : 'no'}
        options={[
          { value: 'yes', label: 'Yes — we know this' },
          { value: 'no', label: 'Not yet' },
        ]}
        hint="Whether somebody could answer the question today, not whether they intend to."
        onChange={(event) => setAnswered(event.target.value === 'yes')}
      />

      <DrawerSection label="What is known" />
      <TextAreaField
        label="Note"
        value={note}
        hint={
          isAnswered
            ? 'What the answer is. A yes with no note is a claim nobody can check.'
            : 'What is being done to find out.'
        }
        onChange={(event) => setNote(event.target.value)}
      />

      {record.isError ? <ErrorState error={record.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
