import { useState } from 'react'
import { AsyncBoundary, Page, PageHeader, Panel, Tag } from '@/design/primitives'
import { useCaseWorklist } from '@/api/queries/hooks'
import { OBJECT_MODELS } from '@/fixtures/objects'
import { fullMoney } from '@/lib/format'
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
  const opportunities = OBJECT_MODELS['opportunity']?.records ?? []
  const tasks = OBJECT_MODELS['task']?.records ?? []

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
                  {fullMoney(
                    opportunities
                      .filter((row) => !String(row['stage']).startsWith('Closed'))
                      .reduce((sum, row) => sum + Number(row['amount'] ?? 0), 0),
                  )}
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
                <div key={task.id} className={styles.phoneCard}>
                  <div>{task['subject']}</div>
                  <div className={styles.sub}>
                    {task['related']} · due {task['due']}
                  </div>
                </div>
              ))}
            </>
          ) : null}

          {tab === 'recent' ? (
            <>
              {opportunities.slice(0, 6).map((row) => (
                <div key={row.id} className={styles.phoneCard}>
                  <div>{row['name']}</div>
                  <div className={styles.sub}>
                    {row['stage']} · {fullMoney(Number(row['amount']))}
                  </div>
                </div>
              ))}
            </>
          ) : null}

          {tab === 'deals' ? (
            <>
              {opportunities
                .filter((row) => !String(row['stage']).startsWith('Closed'))
                .map((row) => (
                  <div key={row.id} className={styles.phoneCard}>
                    <div className={styles.phoneTitle}>{fullMoney(Number(row['amount']))}</div>
                    <div>{row['name']}</div>
                    <div className={styles.sub}>
                      {row['stage']} · closes {row['closeDate']}
                    </div>
                  </div>
                ))}
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
