import { useMemo, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  Button,
  ButtonGroup,
  Drawer,
  DrawerSection,
  FieldRow,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  Tag,
} from '@/design/primitives'
import { useEntityPage } from '@/api/queries/hooks'
import { offGrid, weekEvents, weekOf } from './week'
import type { WeekEvent } from './week'
import styles from './work.module.css'


const HOURS = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17] as const



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
  const [selected, setSelected] = useState<WeekEvent | null>(null)

  // The week the reader is actually in, not the week the prototype was drawn in. A calendar
  // headed "week of 3 August" in October is one nobody looks at twice.
  const { labels: DAYS, monday } = useMemo(() => weekOf(new Date()), [])

  const page = useEntityPage('Activity')

  const all = useMemo(
    () => weekEvents(page.data?.records ?? [], monday),
    [page.data, monday],
  )

  const events = scope === 'mine' ? all.filter((event) => event.status === 'Open') : all
  const elsewhere = offGrid(events)

  return (
    <Page>
      <PageHeader
        eyebrow={`My work · week of ${DAYS[0] ?? ""}`}
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

      {elsewhere.length > 0 ? (
        <Panel padding="flush" style={{ marginTop: 'var(--section-gap)' }}>
          <PanelHeader
            title="Not on this week"
            note={`${elsewhere.length} · overdue, later, or with no date`}
          />
          <div>
            {elsewhere.map((event) => (
              <div key={event.id} className={styles.offGridRow}>
                <Tag tone={event.kind === 'task' ? 'neutral' : 'accent'}>{event.kind}</Tag>
                <button type="button" className={styles.offGridTitle} onClick={() => setSelected(event)}>
                  {event.title}
                </button>
                <span style={{ marginLeft: 'auto' }}>
                  <Tag tone={event.status === 'Open' ? 'outline' : 'positive'}>{event.status}</Tag>
                </span>
              </div>
            ))}
          </div>
        </Panel>
      ) : null}

      {selected ? (
        <Drawer
          eyebrow={selected.kind === 'task' ? 'Task' : 'Meeting'}
          title={selected.title}
          subtitle={selected.related}
          onClose={() => setSelected(null)}
          actions={
            <>
              {/*
                The calendar's events are the prototype's, carrying a related record as a name
                rather than an id — so there is nothing to navigate to and no activity to move.
                Both said so by doing nothing at all until now.
              */}
              <Button disabled title="This calendar's events carry a name, not a record to open.">
                Open record
              </Button>
              <Button disabled title="This build has no reschedule capability.">
                Reschedule
              </Button>
            </>
          }
        >
          <DrawerSection label="Details" />
          <FieldRow label="When">
            {selected.day < 0
              ? 'not in this week'
              : `${DAYS[selected.day]} at ${String(selected.hour).padStart(2, '0')}:${String(
                  selected.minutes,
                ).padStart(2, '0')}`}
          </FieldRow>
          <FieldRow label="Related to">{selected.related}</FieldRow>
          <FieldRow label="Kind">
            <Tag tone={selected.kind === 'task' ? 'neutral' : 'accent'}>{selected.kind}</Tag>
          </FieldRow>
          <FieldRow label="Status">
            <Tag tone={selected.status === 'Open' ? 'outline' : 'positive'}>{selected.status}</Tag>
          </FieldRow>
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
