import { useState } from 'react'
import { AsyncBoundary, Page, PageHeader, Panel, Tag } from '@/design/primitives'
import { useCaseWorklist, useEntityPage } from '@/api/queries/hooks'
import { date, dateTime, fullMoney } from '@/lib/format'
import styles from './work.module.css'

type MobileTab = 'home' | 'recent' | 'deals' | 'more'

/**
 * The application at phone width.
 *
 * A PREVIEW, NOT A SECOND APPLICATION. It reads the same hooks the desktop screens do, so what it
 * shows is what the server said — a mock-up with its own fixtures would be a screen that agrees
 * with nothing and gets stale within a week.
 */
export function MobileScreen() {
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

  return (
    <Page>
      <PageHeader eyebrow="My work" title="Mobile" />

      <div className={styles.phone}>
        <div className={styles.phoneBar}>
          <span>09:41</span>
          <span>▮▮▮ ▲ 100%</span>
        </div>

        <div className={styles.phoneBody}>
          {tab === 'home' ? (
            <>
              <Panel padding="tight">
                <div className={styles.sub}>Open pipeline</div>
                <div className={styles.phoneTitle}>
                  {fullMoney(open.reduce((sum, row) => sum + Number(row.values['amount'] ?? 0), 0))}
                </div>
              </Panel>

              <AsyncBoundary query={cases} skeletonRows={2}>
                {(data) => (
                  <div className={styles.phoneCard}>
                    <div className={styles.sub}>My cases</div>
                    <div className={styles.phoneTitle}>{data.cases.length} open</div>
                    {data.breached > 0 ? <Tag tone="critical">{data.breached} late</Tag> : null}
                  </div>
                )}
              </AsyncBoundary>

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
          ) : null}

          {tab === 'recent' ? (
            <>
              {opportunities.slice(0, 6).map((row) => (
                <div key={row.recordId} className={styles.phoneCard}>
                  <div>{row.values['name'] ?? '—'}</div>
                  <div className={styles.sub}>
                    {row.values['stage'] ?? '—'} · {fullMoney(Number(row.values['amount'] ?? 0))}
                  </div>
                </div>
              ))}
            </>
          ) : null}

          {tab === 'deals' ? (
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
          ) : null}

          {tab === 'more' ? (
            <>
              {['Accounts', 'Contacts', 'Leads', 'Quotes', 'Reports', 'Settings'].map((item) => (
                <div key={item} className={styles.phoneCard}>
                  {item}
                </div>
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
