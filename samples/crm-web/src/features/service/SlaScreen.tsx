import { useState } from 'react'
import {
  Button,
  Columns,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Tag,
  TextField,
} from '@/design/primitives'
import { useDefineSlaPolicy, useSetBusinessHours } from '@/api/queries/hooks'
import type { CasePriority, OpeningHoursOfDay } from '@/api/contracts'
import { useToast } from '@/app/ToastProvider'
import styles from './service.module.css'

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const

const DEFAULT_WEEK: readonly OpeningHoursOfDay[] = [
  { day: 1, opens: '09:00', closes: '17:00' },
  { day: 2, opens: '09:00', closes: '17:00' },
  { day: 3, opens: '09:00', closes: '17:00' },
  { day: 4, opens: '09:00', closes: '17:00' },
  { day: 5, opens: '09:00', closes: '17:00' },
]

/**
 * What the desk is open for, and what it promises.
 *
 * THE WEEK IS THE FEATURE. Every promise this backend makes is measured in the hours the desk is
 * open, so a case raised at half past four on a Friday is not breached by the Friday evening. The
 * form below is where that week is set, and the minutes it reads back are the check on a typo:
 * a week meant to be forty hours and entered as four is otherwise found in a breach report.
 */
export function SlaScreen() {
  const toast = useToast()
  const setHours = useSetBusinessHours()
  const definePolicy = useDefineSlaPolicy()

  const [week, setWeek] = useState<readonly OpeningHoursOfDay[]>(DEFAULT_WEEK)
  const [name, setName] = useState('urgent_response')
  const [label, setLabel] = useState('Urgent — one hour')
  const [priority, setPriority] = useState<CasePriority>('Urgent')
  const [firstResponse, setFirstResponse] = useState('60')
  const [resolution, setResolution] = useState('240')
  const [businessHoursOnly, setBusinessHoursOnly] = useState(true)

  const minutes = week.reduce((total, day) => total + minutesBetween(day.opens, day.closes), 0)

  return (
    <Page>
      <PageHeader eyebrow="Service setup" title="Business hours and SLA policies" />

      <Columns layout="halves">
        <Panel padding="flush">
          <PanelHeader
            title="The week the desk is open"
            note={`${week.length} days · ${minutes} minutes`}
            actions={
              <Button
                tone="primary"
                disabled={setHours.isPending}
                onClick={() =>
                  setHours.mutate(
                    { week: [...week] },
                    {
                      onSuccess: (result) =>
                        toast.saved(
                          `${result.days} days, ${result.minutesPerWeek} minutes a week.`,
                        ),
                      onError: (error) => toast.failed(error),
                    },
                  )
                }
              >
                {setHours.isPending ? 'Saving…' : 'Save the week'}
              </Button>
            }
          />
          <PanelBody>
            <p className={styles.sub} style={{ marginBottom: 10 }}>
              A day with no hours is a day the clock does not run — which is how a weekend, or a
              four-day week, is said without a flag for either.
            </p>
            {DAYS.map((dayName, index) => {
              const open = week.find((entry) => entry.day === index)

              return (
                <div key={dayName} className={styles.week}>
                  <label className={styles.dayName}>
                    <input
                      type="checkbox"
                      checked={open !== undefined}
                      onChange={(event) =>
                        setWeek((current) =>
                          event.target.checked
                            ? [...current, { day: index, opens: '09:00', closes: '17:00' }].sort(
                                (a, b) => a.day - b.day,
                              )
                            : current.filter((entry) => entry.day !== index),
                        )
                      }
                    />{' '}
                    {dayName}
                  </label>
                  {open ? (
                    <>
                      <TextField
                        label="Opens"
                        type="time"
                        value={open.opens}
                        onChange={(event) => setDay(index, { opens: event.target.value })}
                      />
                      <TextField
                        label="Closes"
                        type="time"
                        value={open.closes}
                        onChange={(event) => setDay(index, { closes: event.target.value })}
                      />
                    </>
                  ) : (
                    <span className={styles.closed}>Closed — the clock does not run</span>
                  )}
                </div>
              )
            })}
          </PanelBody>
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="What the desk promises" note="one live promise per priority" />
          <PanelBody>
            <form
              style={{ display: 'grid', gap: 12 }}
              onSubmit={(event) => {
                event.preventDefault()
                definePolicy.mutate(
                  {
                    name,
                    label,
                    priority,
                    firstResponseMinutes: Number(firstResponse),
                    resolutionMinutes: Number(resolution),
                    businessHoursOnly,
                  },
                  {
                    onSuccess: (result) =>
                      toast.saved(
                        result.replaced
                          ? `Saved. It replaced the promise that governed ${priority}.`
                          : 'Saved. Nothing governed that priority before.',
                      ),
                    onError: (error) => toast.failed(error),
                  },
                )
              }}
            >
              <TextField
                label="Name"
                required
                hint="Lower case, letters, numbers and underscores."
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              <TextField
                label="What to show a person"
                required
                value={label}
                onChange={(event) => setLabel(event.target.value)}
              />
              <SelectField
                label="Which cases it governs"
                value={priority}
                onChange={(event) => setPriority(event.target.value as CasePriority)}
                options={[
                  { value: 'Low', label: 'Low' },
                  { value: 'Normal', label: 'Normal' },
                  { value: 'High', label: 'High' },
                  { value: 'Urgent', label: 'Urgent' },
                ]}
              />
              <TextField
                label="First response, in minutes"
                type="number"
                min={1}
                required
                value={firstResponse}
                onChange={(event) => setFirstResponse(event.target.value)}
              />
              <TextField
                label="Resolution, in minutes"
                type="number"
                min={1}
                required
                hint="Cannot be sooner than the response it contains."
                error={
                  Number(resolution) < Number(firstResponse)
                    ? 'A promise to fix something sooner than to acknowledge it is not a stricter promise.'
                    : undefined
                }
                value={resolution}
                onChange={(event) => setResolution(event.target.value)}
              />
              <label className={styles.checkbox}>
                <input
                  type="checkbox"
                  checked={businessHoursOnly}
                  onChange={(event) => setBusinessHoursOnly(event.target.checked)}
                />
                Measure it in the hours the desk is open
              </label>
              {!businessHoursOnly ? (
                <Tag tone="warning">
                  Around the clock. Right for a desk that staffs nights, wrong for every desk that
                  does not.
                </Tag>
              ) : null}
              <Button type="submit" tone="primary" disabled={definePolicy.isPending}>
                {definePolicy.isPending ? 'Saving…' : 'Save the promise'}
              </Button>
            </form>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )

  function setDay(day: number, patch: Partial<OpeningHoursOfDay>) {
    setWeek((current) => current.map((entry) => (entry.day === day ? { ...entry, ...patch } : entry)))
  }
}

function minutesBetween(opens: string, closes: string): number {
  const [openHour = 0, openMinute = 0] = opens.split(':').map(Number)
  const [closeHour = 0, closeMinute = 0] = closes.split(':').map(Number)
  return Math.max(0, closeHour * 60 + closeMinute - (openHour * 60 + openMinute))
}
