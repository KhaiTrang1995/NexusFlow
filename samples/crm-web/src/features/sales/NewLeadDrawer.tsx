import { useState } from 'react'
import { Button, Drawer, DrawerSection, ErrorState, SelectField, TextField } from '@/design/primitives'
import { useCaptureLead } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { LeadSource } from '@/api/contracts'
import { emailOrNull } from './newWork'

const SOURCES: readonly LeadSource[] = ['Web', 'Referral', 'Event', 'Outbound', 'Partner']

/**
 * Captures a lead, for real.
 *
 * THE SERVER'S REFUSAL IS SHOWN, NOT A SENTENCE THIS FORM INVENTED. Every rule about a lead
 * lives on the capability — this form checks nothing the server does not, and when the server
 * says no it says so in the server's own words. A client that pre-empted the rules would be a
 * second copy of them, and the copy is what goes stale.
 *
 * WHAT IT DOES CHECK is only what would make the request pointless: two required fields, empty.
 * That is not validation, it is not sending a request nobody could answer.
 *
 * THE DRAWER STAYS OPEN ON FAILURE, with what was typed still in it. A form that clears itself
 * when the server refuses makes the reader retype everything to find out whether they fixed it.
 */
export function NewLeadDrawer({ onClose }: { onClose: () => void }) {
  const capture = useCaptureLead()
  const toast = useToast()

  const [company, setCompany] = useState('')
  const [contactName, setContactName] = useState('')
  const [email, setEmail] = useState('')
  const [source, setSource] = useState<LeadSource>('Web')

  const ready = company.trim().length > 0 && contactName.trim().length > 0

  function submit() {
    capture.mutate(
      {
        company: company.trim(),
        contactName: contactName.trim(),
        // Empty is not an address. Null says "nobody gave one", which is what the column means.
        email: emailOrNull(email),
        source,
      },
      {
        onSuccess: () => {
          toast.saved(`${company.trim()} captured.`)
          onClose()
        },
      },
    )
  }

  return (
    <Drawer
      eyebrow="New"
      title="Capture a lead"
      subtitle="One write and one event, staged in the same transaction"
      onClose={onClose}
      actions={
        <>
          <Button
            tone="primary"
            disabled={!ready || capture.isPending}
            onClick={submit}
          >
            {capture.isPending ? 'Capturing…' : 'Capture'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Who" />
      <TextField
        label="Company"
        value={company}
        required
        onChange={(event) => setCompany(event.target.value)}
      />
      <TextField
        label="Contact name"
        value={contactName}
        required
        onChange={(event) => setContactName(event.target.value)}
      />
      <TextField
        label="Email"
        type="email"
        value={email}
        onChange={(event) => setEmail(event.target.value)}
      />

      <DrawerSection label="Where from" />
      <SelectField
        label="Source"
        value={source}
        options={SOURCES.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setSource(event.target.value as LeadSource)}
      />

      {capture.isError ? <ErrorState error={capture.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
