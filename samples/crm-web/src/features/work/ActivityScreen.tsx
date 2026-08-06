import { useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  Button,
  ButtonGroup,
  Drawer,
  DrawerSection,
  FieldRow,
  Page,
  PageHeader,
  Tag,
} from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import styles from './work.module.css'

const DAYS = ['Mon 3', 'Tue 4', 'Wed 5', 'Thu 6', 'Fri 7'] as const
const HOURS = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17] as const

interface CalendarEvent {
  id: string
  title: string
  day: number
  hour: number
  minutes: number
  kind: 'meeting' | 'task'
  related: string
  attendees?: string
}

/**
 * The week, with what is on it.
 *
 * MEETINGS AND TASKS ARE DRAWN DIFFERENTLY, AND THAT IS THE POINT. A task at ten o'clock is a
 * thing somebody has to do; a meeting at ten o'clock is a thing they have to be at. A calendar
 * that drew them the same way is one people stop trusting to tell them where to be.
 */
export function ActivityScreen() {
  const navigate = useNavigate()
  const [scope, setScope] = useState<'mine' | 'team'>('mine')
  const [selected, setSelected] = useState<CalendarEvent | null>(null)

  const events = scope === 'mine' ? EVENTS.filter((event) => event.kind !== 'task' || event.day < 4) : EVENTS

  return (
    <Page>
      <PageHeader
        eyebrow="My work · week of 3 August"
        title="Activity calendar"
        actions={
          <>
            <ButtonGroup label="Whose week">
              <Button size="sm" aria-pressed={scope === 'mine'} onClick={() => setScope('mine')}>
                Mine
              </Button>
              <Button size="sm" aria-pressed={scope === 'team'} onClick={() => setScope('team')}>
                Team
              </Button>
            </ButtonGroup>
            <Button onClick={() => void navigate({ to: '/work/inbox' })}>Inbox</Button>
            <Button tone="primary">New meeting</Button>
          </>
        }
      />

      <div className={styles.calendar}>
        <div className={styles.calendarHead} />
        {DAYS.map((day) => (
          <div key={day} className={styles.calendarHead}>
            {day}
          </div>
        ))}

        {HOURS.map((hour) => (
          <FragmentRow key={hour} hour={hour}>
            {DAYS.map((day, dayIndex) => (
              <div key={day} className={styles.calendarCell}>
                {events
                  .filter((event) => event.day === dayIndex && event.hour === hour)
                  .map((event) => (
                    <button
                      key={event.id}
                      type="button"
                      className={`${styles.event} ${event.kind === 'task' ? styles.eventTask : ''}`}
                      onClick={() => setSelected(event)}
                    >
                      <span className={styles.eventTime}>
                        {String(event.hour).padStart(2, '0')}:{String(event.minutes).padStart(2, '0')}
                      </span>{' '}
                      {event.title}
                    </button>
                  ))}
              </div>
            ))}
          </FragmentRow>
        ))}
      </div>

      {selected ? (
        <Drawer
          eyebrow={selected.kind === 'task' ? 'Task' : 'Meeting'}
          title={selected.title}
          subtitle={selected.related}
          onClose={() => setSelected(null)}
          actions={
            <>
              <Button tone="primary">Open record</Button>
              <Button>Reschedule</Button>
            </>
          }
        >
          <DrawerSection label="Details" />
          <FieldRow label="When">
            {DAYS[selected.day]} at {String(selected.hour).padStart(2, '0')}:
            {String(selected.minutes).padStart(2, '0')}
          </FieldRow>
          <FieldRow label="Related to">{selected.related}</FieldRow>
          <FieldRow label="Kind">
            <Tag tone={selected.kind === 'task' ? 'neutral' : 'accent'}>{selected.kind}</Tag>
          </FieldRow>
          {selected.attendees ? <FieldRow label="Attendees">{selected.attendees}</FieldRow> : null}
        </Drawer>
      ) : null}
    </Page>
  )
}

function FragmentRow({ hour, children }: { hour: number; children: React.ReactNode }) {
  return (
    <>
      <div className={styles.calendarGutter}>{String(hour).padStart(2, '0')}:00</div>
      {children}
    </>
  )
}

/** The week's events. Tasks are the fixture tasks, placed on their due dates. */
const TASKS = OBJECT_MODELS['task']?.records ?? []

const EVENTS: readonly CalendarEvent[] = [
  { id: 'm1', title: 'Discovery — Perimeter ops', day: 0, hour: 9, minutes: 30, kind: 'meeting', related: 'Perimeter — Fleet Rollout', attendees: 'D. Whitfield, A. Ruiz' },
  { id: 'm2', title: 'Pipeline review', day: 0, hour: 15, minutes: 0, kind: 'meeting', related: 'Q3 FY26', attendees: 'The team' },
  { id: 'm3', title: 'Legal — MSA redlines', day: 1, hour: 11, minutes: 0, kind: 'meeting', related: 'Northwind — Platform Expansion', attendees: 'R. Petrov, legal' },
  { id: 'm4', title: 'Security questionnaire walkthrough', day: 2, hour: 10, minutes: 0, kind: 'meeting', related: 'Cardinal — Enterprise Pilot', attendees: 'P. Raman, J. Park' },
  { id: 'm5', title: 'Churn risk — Baltic', day: 3, hour: 14, minutes: 0, kind: 'meeting', related: 'Baltic Freight — Renewal FY27', attendees: 'CS, K. Osei' },
  { id: 'm6', title: 'Pricing workshop', day: 4, hour: 9, minutes: 0, kind: 'meeting', related: 'Perimeter — Fleet Rollout', attendees: 'Ops, A. Ruiz' },
  ...TASKS.map((task, index) => ({
    id: task.id,
    title: String(task['subject']),
    day: index % 5,
    hour: 12 + (index % 4),
    minutes: 0,
    kind: 'task' as const,
    related: String(task['related']),
  })),
]
