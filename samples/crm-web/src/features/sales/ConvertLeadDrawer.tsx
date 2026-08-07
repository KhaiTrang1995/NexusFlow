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
import { useConvertLead, useProcess } from '@/api/queries/hooks'
import { useSession } from '@/session/SessionProvider'
import { useToast } from '@/app/ToastProvider'

/**
 * Converts a lead into an account, a contact and an opportunity.
 *
 * ONE CALL. The server runs the three writes as a saga and unwinds them in reverse when the third
 * fails, so a conversion either happened or left nothing behind. A form that made three calls
 * would strand an account and a contact every time the last one failed, and nobody would go
 * looking for them.
 *
 * THE STARTING STAGE COMES FROM THE PUBLISHED PROCESS, BY ID. Not a list this file was written
 * with — that list is exactly what an administrator changes at run time, and it is the thing this
 * whole sample claims lives in tables rather than in a build. While the process is loading there
 * is nothing to convert into, and the form says so instead of guessing.
 *
 * THE OWNER IS THE SIGNED-IN PERSON. A conversion that let the caller pick an owner would be a
 * way to write rows into somebody else's pipeline, which is the one thing the row-level scope
 * exists to prevent.
 */
export function ConvertLeadDrawer({
  leadId,
  company,
  onClose,
}: {
  leadId: string
  company: string
  onClose: () => void
}) {
  const convert = useConvertLead()
  const process = useProcess('Opportunity')
  const { ownerId } = useSession()
  const toast = useToast()

  const stages = process.data?.stages.filter((stage) => !stage.isTerminal) ?? []
  const [stageId, setStageId] = useState('')
  const [name, setName] = useState(`${company} — new business`)
  const [amount, setAmount] = useState('50000')
  const [industry, setIndustry] = useState('SaaS')
  const [region, setRegion] = useState('NA')
  const [expectedClose, setExpectedClose] = useState(inNinetyDays())

  // The first non-terminal stage until somebody picks another, resolved at render rather than in
  // an effect: the process arrives after the first paint, and an effect here would be a second
  // render that fights whatever the reader has already chosen.
  const chosen = stageId.length > 0 ? stageId : (stages[0]?.stageId ?? '')

  const ready =
    chosen.length > 0 && name.trim().length > 0 && Number(amount) > 0 && expectedClose.length > 0

  function submit() {
    convert.mutate(
      {
        leadId,
        industry: industry.trim(),
        region: region.trim(),
        owner: ownerId,
        opportunityName: name.trim(),
        amount: Number(amount),
        currency: 'EUR',
        stage: chosen,
        expectedClose,
      },
      {
        onSuccess: (result) => {
          toast.saved(`${company} converted — account, contact and opportunity ${result.opportunity.slice(0, 8)}.`)
          onClose()
        },
      },
    )
  }

  return (
    <Drawer
      eyebrow="Convert"
      title={company}
      subtitle="Three writes as one saga; a failure at the third undoes the first two"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || convert.isPending} onClick={submit}>
            {convert.isPending ? 'Converting…' : 'Convert'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="The opportunity it becomes" />

      {process.isPending ? <Skeleton rows={3} /> : null}

      {process.isSuccess && stages.length === 0 ? (
        <p>
          No process is published for opportunities, so there is no stage to start one in. Publish
          one in setup and this form has somewhere to convert into.
        </p>
      ) : null}

      <TextField
        label="Opportunity name"
        value={name}
        required
        onChange={(event) => setName(event.target.value)}
      />
      <TextField
        label="Amount (EUR)"
        type="number"
        value={amount}
        onChange={(event) => setAmount(event.target.value)}
      />
      <SelectField
        label="Starting stage"
        value={chosen}
        hint={
          process.data
            ? `From version ${process.data.version} of the published process.`
            : undefined
        }
        options={stages.map((stage) => ({ value: stage.stageId, label: stage.name }))}
        onChange={(event) => setStageId(event.target.value)}
      />
      <TextField
        label="Expected close"
        type="date"
        value={expectedClose}
        onChange={(event) => setExpectedClose(event.target.value)}
      />

      <DrawerSection label="The account it creates" />
      <TextField
        label="Industry"
        value={industry}
        onChange={(event) => setIndustry(event.target.value)}
      />
      <TextField
        label="Region"
        value={region}
        onChange={(event) => setRegion(event.target.value)}
      />

      {convert.isError ? <ErrorState error={convert.error} onRetry={submit} /> : null}
    </Drawer>
  )
}

/** A default close date far enough out to be plausible and near enough to be this quarter. */
function inNinetyDays(): string {
  const date = new Date()
  date.setDate(date.getDate() + 90)
  return date.toISOString().slice(0, 10)
}
