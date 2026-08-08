import { useEffect, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import { AsyncBoundary, Page, PageHeader, Panel, Tag } from '@/design/primitives'
import { useCaseWorklist, useEntityPage } from '@/api/queries/hooks'
import { date, dateTime, fullMoney } from '@/lib/format'
import { recentlyMoved } from './mobile'
import styles from './work.module.css'

type MobileTab = 'home' | 'recent' | 'deals' | 'more'

/**
 * Where the "More" tab goes.
 *
 * IT WENT NOWHERE, AND THAT IS WHY IT WAS A MOCK-UP. Six words were listed in this file — one of
 * them naming a screen this application does not have — and none of them did anything when
 * pressed. A preview whose menu is a picture of a menu is the screenshot this screen exists not
 * to be, so each entry is a route the router actually serves.
 */
type MoreItem =
  | { label: string; kind: 'record'; object: string }
  | { label: string; kind: 'route'; to: '/analytics/reports' | '/setup' }

const MORE: readonly MoreItem[] = [
  { label: 'Accounts', kind: 'record', object: 'account' },
  { label: 'Contacts', kind: 'record', object: 'contact' },
  { label: 'Leads', kind: 'record', object: 'lead' },
  { label: 'Quotes', kind: 'record', object: 'quote' },
  { label: 'Reports', kind: 'route', to: '/analytics/reports' },
  { label: 'Setup', kind: 'route', to: '/setup' },
]

/**
 * The application at phone width.
 *
 * A PREVIEW, NOT A SECOND APPLICATION. It reads the same hooks the desktop screens do, so what it
 * shows is what the server said — a mock-up with its own fixtures would be a screen that agrees
 * with nothing and gets stale within a week.
 */
export function MobileScreen() {
  const navigate = useNavigate()
  const [tab, setTab] = useState<MobileTab>('home')
  const cases = useCaseWorklist({ mineOnly: true, priority: null, breachedOnly: false })
  // The tenant's own rows. A phone preview showing the prototype's six deals is a screenshot,
  // not a preview — and it is the one screen somebody points a phone at to check the data is
  // there.
  const deals = useEntityPage('Opportunity')
  const activities = useEntityPage('Activity')

  const opportunities = deals.data?.records ?? []
  const open = opportunities.filter((row) => row.values['outcome'] === null)

  const tasks = (activities.data?.records ?? []).filter(
    (row) => row.values['status'] !== 'Completed',
  )

  const clock = useClock()

  return (
    <Page>
      <PageHeader eyebrow="My work" title="Mobile" />

      <div className={styles.phone}>
        <div className={styles.phoneBar}>
          {/*
            THE CLOCK SAID 09:41 AT EVERY HOUR OF THE DAY. It is the time on every phone in every
            advertisement, which is exactly what made it invisible — and this screen's whole claim
            is that nothing on it is a picture of an application.
          */}
          <span>{clock}</span>
          <span>▮▮▮ ▲ 100%</span>
        </div>

        <div className={styles.phoneBody}>
          {tab === 'home' ? (
            <>
              {/*
                THE TILE READ "$0" BEFORE ANYTHING HAD BEEN ASKED. It summed `deals.data ?? []`
                outside any boundary, so an empty pipeline, a pipeline still loading and a refused
                read were one number — and the refused one stayed on screen.
              */}
              <AsyncBoundary query={deals} skeletonRows={2}>
                {() => (
                  <Panel padding="tight">
                    <div className={styles.sub}>Open pipeline</div>
                    <div className={styles.phoneTitle}>
                      {fullMoney(open.reduce((sum, row) => sum + Number(row.values['amount'] ?? 0), 0))}
                    </div>
                    <div className={styles.sub}>{open.length} deal(s) with no outcome yet</div>
                  </Panel>
                )}
              </AsyncBoundary>

              <AsyncBoundary query={cases} skeletonRows={2}>
                {(data) => (
                  <div className={styles.phoneCard}>
                    <div className={styles.sub}>My cases</div>
                    <div className={styles.phoneTitle}>{data.cases.length} open</div>
                    {data.breached > 0 ? <Tag tone="critical">{data.breached} late</Tag> : null}
                  </div>
                )}
              </AsyncBoundary>

              <AsyncBoundary query={activities} skeletonRows={2}>
                {() => (
                  <>
                    {tasks.slice(0, 3).map((task) => (
                      <div key={task.recordId} className={styles.phoneCard}>
                        <div>{task.values['subject'] ?? '—'}</div>
                        <div className={styles.sub}>
                          {task.values['kind'] ?? 'Task'} · due {dateTime(task.values['due_at'])}
                        </div>
                      </div>
                    ))}
                    {tasks.length === 0 ? (
                      <div className={styles.phoneCard}>
                        <div className={styles.sub}>Nothing is open against this tenant.</div>
                      </div>
                    ) : null}
                  </>
                )}
              </AsyncBoundary>
            </>
          ) : null}

          {tab === 'recent' ? (
            // SIX ROWS IN KEY ORDER UNDER A HEADING SAYING "RECENT", and nothing at all when the
            // tenant had none — a blank panel that reads as a broken tab.
            <AsyncBoundary query={deals} skeletonRows={3}>
              {() => (
                <>
                  {recentlyMoved(opportunities, 6).map((row) => (
                    <div key={row.recordId} className={styles.phoneCard}>
                      <div>{row.values['name'] ?? '—'}</div>
                      <div className={styles.sub}>
                        {row.values['stage'] ?? '—'} · {fullMoney(Number(row.values['amount'] ?? 0))}
                      </div>
                      <div className={styles.sub}>
                        moved {dateTime(row.values['stage_entered_at'])}
                      </div>
                    </div>
                  ))}
                  {opportunities.length === 0 ? (
                    <div className={styles.phoneCard}>
                      <div className={styles.sub}>
                        This tenant has no opportunities, so nothing has moved recently.
                      </div>
                    </div>
                  ) : null}
                </>
              )}
            </AsyncBoundary>
          ) : null}

          {tab === 'deals' ? (
            <AsyncBoundary query={deals} skeletonRows={3}>
              {() => (
                <>
                  {open.map((row) => (
                    <div key={row.recordId} className={styles.phoneCard}>
                      <div className={styles.phoneTitle}>
                        {fullMoney(Number(row.values['amount'] ?? 0))}
                      </div>
                      <div>{row.values['name'] ?? '—'}</div>
                      <div className={styles.sub}>
                        {row.values['stage'] ?? '—'} · closes {date(row.values['expected_close'])}
                      </div>
                    </div>
                  ))}
                  {open.length === 0 ? (
                    <div className={styles.phoneCard}>
                      <div className={styles.sub}>Nothing is open.</div>
                    </div>
                  ) : null}
                </>
              )}
            </AsyncBoundary>
          ) : null}

          {tab === 'more' ? (
            <>
              {MORE.map((item) => (
                <button
                  key={item.label}
                  type="button"
                  className={styles.phoneCard}
                  onClick={() =>
                    void (item.kind === 'record'
                      ? navigate({ to: '/records/$object', params: { object: item.object } })
                      : navigate({ to: item.to }))
                  }
                >
                  {item.label}
                </button>
              ))}
            </>
          ) : null}
        </div>

        <div className={styles.phoneTabs}>
          {(
            [
              ['home', '⌂', 'Home'],
              ['recent', '▤', 'Recent'],
              ['deals', '◉', 'Deals'],
              ['more', '☰', 'More'],
            ] as const
          ).map(([id, glyph, label]) => (
            <button
              key={id}
              type="button"
              className={styles.phoneTab}
              aria-pressed={tab === id}
              onClick={() => setTab(id)}
            >
              <span aria-hidden="true">{glyph}</span>
              {label}
            </button>
          ))}
        </div>
      </div>
    </Page>
  )
}

/** The reader's own clock, to the minute, so the frame stops advertising a fixed time. */
function useClock(): string {
  const [now, setNow] = useState(() => new Date())

  useEffect(() => {
    const tick = setInterval(() => setNow(new Date()), 30_000)

    return () => clearInterval(tick)
  }, [])

  return now.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}
