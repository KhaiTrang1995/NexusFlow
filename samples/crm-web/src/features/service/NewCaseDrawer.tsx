import { useState } from 'react'
import {
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  SelectField,
  TextAreaField,
  TextField,
} from '@/design/primitives'
import { useEntityPage, useOpenCase } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { CaseOrigin, CasePriority } from '@/api/contracts'

const PRIORITIES: readonly CasePriority[] = ['Low', 'Normal', 'High', 'Urgent']
const ORIGINS: readonly CaseOrigin[] = ['Email', 'Phone', 'Web', 'Chat']

/**
 * Opens a case.
 *
 * <strong>The button existed and did nothing.</strong> "New case" sat at the top of the service
 * console with no handler at all, on the one screen whose whole purpose is what has come in —
 * so the queue could be read and worked but never added to. The capability has been there
 * throughout: `crm.case.open` takes an account, a subject and a priority, and answers with the
 * case's number and the two deadlines its SLA policy implies.
 *
 * <strong>The account is chosen, not typed.</strong> A case hangs off an account and optionally a
 * contact; a form that asked for a uuid would be asking somebody to paste one and would fail on
 * the foreign key when they got it wrong.
 *
 * <strong>The deadlines are said back, because they are the answer.</strong> Opening a case with
 * no business-hours week behind it produces a promise measured in calendar time, and the only
 * moment anybody would notice is when the response is late. The toast names them.
 */
export function NewCaseDrawer({ onClose }: { onClose: () => void }) {
  const open = useOpenCase()
  const toast = useToast()

  const accounts = useEntityPage('Account', 100)
  const contacts = useEntityPage('Contact', 100)

  const [accountId, setAccountId] = useState('')
  const [contactId, setContactId] = useState('')
  const [subject, setSubject] = useState('')
  const [description, setDescription] = useState('')
  const [priority, setPriority] = useState<CasePriority>('Normal')
  const [origin, setOrigin] = useState<CaseOrigin>('Email')

  const ready = accountId.length > 0 && subject.trim().length > 0 && description.trim().length > 0

  function submit() {
    open.mutate(
      {
        accountId,
        // Empty is nobody, not the empty string: the server takes a null contact and a case
        // raised by an account with no named person is an ordinary thing.
        contactId: contactId.length > 0 ? contactId : null,
        subject: subject.trim(),
        description: description.trim(),
        priority,
        origin,
      },
      {
        onSuccess: (result) => {
          toast.saved(
            result.policy === null
              ? `Case #${result.number} opened. No SLA policy matched it, so nothing is promised.`
              : `Case #${result.number} opened under ${result.policy}.`,
          )
          onClose()
        },
        onError: (error) => toast.failed(error),
      },
    )
  }

  return (
    <Drawer
      eyebrow="New"
      title="Open a case"
      subtitle="The policy that matches decides what is promised, and when"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || open.isPending} onClick={submit}>
            {open.isPending ? 'Opening…' : 'Open the case'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Who" />
      <SelectField
        label="Account"
        value={accountId}
        required
        placeholder={accounts.isPending ? 'reading…' : 'Choose an account'}
        options={(accounts.data?.records ?? []).map((record) => ({
          value: record.recordId,
          label: record.values['name'] ?? record.recordId,
        }))}
        onChange={(event) => setAccountId(event.target.value)}
      />
      <SelectField
        label="Contact"
        value={contactId}
        placeholder="Nobody in particular"
        hint="Optional — a case can be raised by an account with no named person."
        options={(contacts.data?.records ?? []).map((record) => ({
          value: record.recordId,
          label: record.values['full_name'] ?? record.recordId,
        }))}
        onChange={(event) => setContactId(event.target.value)}
      />

      <DrawerSection label="What" />
      <TextField
        label="Subject"
        value={subject}
        required
        onChange={(event) => setSubject(event.target.value)}
      />
      <TextAreaField
        label="Description"
        value={description}
        required
        hint="What happened, in the words it was reported in."
        onChange={(event) => setDescription(event.target.value)}
      />

      <DrawerSection label="How urgent, and where from" />
      <SelectField
        label="Priority"
        value={priority}
        options={PRIORITIES.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setPriority(event.target.value as CasePriority)}
      />
      <SelectField
        label="Origin"
        value={origin}
        options={ORIGINS.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setOrigin(event.target.value as CaseOrigin)}
      />

      {open.isError ? <ErrorState error={open.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
