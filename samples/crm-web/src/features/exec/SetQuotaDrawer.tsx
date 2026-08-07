import { useState } from 'react'
import {
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  FieldRow,
  SelectField,
  TextField,
} from '@/design/primitives'
import { useSetQuota } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { fullMoney } from '@/lib/format'
import type { QuotaMeasure } from '@/api/contracts'

const MEASURES: readonly QuotaMeasure[] = ['Revenue', 'Leads', 'Activities']

/**
 * Assigns a seller their number for a period.
 *
 * THE PEOPLE COME FROM THE PERIOD'S OWN ROWS, NOT FROM A LIST OF NAMES. Whoever the server already
 * reports on is who a manager can assign to; a free-text user id would let somebody set a quota
 * against a typo and then wonder why it never appears on the review.
 *
 * THE RAMPED FIGURE IS THE SERVER'S. The line below the fields is arithmetic to help somebody
 * type — the number that lands on the register is the one that comes back, and the toast says it
 * rather than repeating what was entered.
 */
export function SetQuotaDrawer({
  period,
  people,
  onClose,
}: {
  period: string
  people: readonly { userId: string; displayName: string }[]
  onClose: () => void
}) {
  const save = useSetQuota()
  const toast = useToast()

  const [userId, setUserId] = useState(people[0]?.userId ?? '')
  const [measure, setMeasure] = useState<QuotaMeasure>('Revenue')
  const [target, setTarget] = useState('500000')
  const [ramp, setRamp] = useState('1')

  const ready = userId.length > 0 && Number(target) > 0 && Number(ramp) > 0

  function submit() {
    save.mutate(
      {
        period,
        userId,
        measure,
        target: Number(target),
        rampFactor: Number(ramp),
      },
      {
        onSuccess: (result) => {
          toast.saved(`${result.userId} carries ${fullMoney(result.target)} for ${period}.`)
          onClose()
        },
        onError: (error) => toast.failed(error, 'That quota was refused.'),
      },
    )
  }

  return (
    <Drawer
      eyebrow="Quota"
      title={`Assign a number for ${period}`}
      subtitle="Assigned downwards; a commitment is what comes back up"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || save.isPending} onClick={submit}>
            {save.isPending ? 'Assigning…' : 'Assign'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="Who" />
      <SelectField
        label="Seller"
        value={userId}
        options={people.map((person) => ({
          value: person.userId,
          label: `${person.displayName} · ${person.userId}`,
        }))}
        onChange={(event) => setUserId(event.target.value)}
      />

      <DrawerSection label="What" />
      <SelectField
        label="Measure"
        value={measure}
        options={MEASURES.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setMeasure(event.target.value as QuotaMeasure)}
      />
      <TextField
        label="Target"
        type="number"
        value={target}
        onChange={(event) => setTarget(event.target.value)}
      />
      <TextField
        label="Ramp"
        type="number"
        value={ramp}
        hint="A fraction of the target, for somebody who joined part-way through. 1 is the whole number."
        onChange={(event) => setRamp(event.target.value)}
      />

      <FieldRow label="Which comes to">
        {fullMoney(Number(target) * Number(ramp) || 0)} — before the server says so
      </FieldRow>

      {save.isError ? <ErrorState error={save.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
