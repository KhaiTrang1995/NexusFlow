import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { useExecutiveBoard } from '@/api/queries/hooks'
import { fullMoney, money, percent } from '@/lib/format'
import { OBJECT_MODELS } from '@/fixtures/objects'
import { PERIOD_LABEL, usePeriod } from './period'
import styles from './exec.module.css'

type Category = 'Commit' | 'Best Case' | 'Pipeline' | 'Closed'

const CATEGORIES: readonly Category[] = ['Commit', 'Best Case', 'Pipeline', 'Closed']

/**
 * The forecast, by category and by seller.
 *
 * THE CATEGORIES ARE NOT THE STAGES. A deal in Negotiation may be Best Case and a deal in Qualify
 * may be Commit, because a category is a judgement a seller makes and a stage is where the work
 * has got to. Rolling one up as the other is the single most common way a forecast becomes
 * fiction, so both are shown.
 */
export function ForecastScreen() {
  const [period] = usePeriod()
  const [scope, setScope] = useState<'category' | 'seller'>('category')
  const board = useExecutiveBoard(period)

  const opportunities = (OBJECT_MODELS['opportunity']?.records ?? []).filter(
    (row) => !String(row['stage']).startsWith('Closed'),
  )

  const byCategory = CATEGORIES.map((category) => {
    const rows = opportunities.filter((row) => row['forecast'] === category)
    return {
      category,
      count: rows.length,
      amount: rows.reduce((sum, row) => sum + Number(row['amount'] ?? 0), 0),
      weighted: Math.round(
        rows.reduce(
          (sum, row) => sum + (Number(row['amount'] ?? 0) * Number(row['probability'] ?? 0)) / 100,
          0,
        ),
      ),
    }
  })

  const sellers = [...new Set(opportunities.map((row) => String(row['owner'])))].map((owner) => {
    const rows = opportunities.filter((row) => row['owner'] === owner)
    return {
      owner,
      commit: sum(rows.filter((row) => row['forecast'] === 'Commit')),
      best: sum(rows.filter((row) => row['forecast'] === 'Best Case')),
      pipeline: sum(rows.filter((row) => row['forecast'] === 'Pipeline')),
      total: sum(rows),
    }
  })

  return (
    <Page>
      <PageHeader
        eyebrow={`Executive · ${PERIOD_LABEL[period]}`}
        title="Forecast"
        actions={
          <ButtonGroup label="Roll up by">
            <Button aria-pressed={scope === 'category'} onClick={() => setScope('category')}>
              Category
            </Button>
            <Button aria-pressed={scope === 'seller'} onClick={() => setScope('seller')}>
              Seller
            </Button>
          </ButtonGroup>
        }
      />

      <AsyncBoundary query={board} skeletonRows={4}>
        {(data) => (
          <>
            <StatGrid columns={4}>
              <StatTile
                label="Commit"
                value={money(byCategory[0]?.amount ?? 0)}
                note="the seller will defend this number"
              />
              <StatTile label="Best case" value={money(byCategory[1]?.amount ?? 0)} note="upside" />
              <StatTile label="Pipeline" value={money(byCategory[2]?.amount ?? 0)} note="everything else" />
              <StatTile
                label="Against target"
                value={percent(
                  data.rollUp.target === 0
                    ? null
                    : ((byCategory[0]?.amount ?? 0) + data.deals.wonValue) / data.rollUp.target,
                )}
                direction={
                  (byCategory[0]?.amount ?? 0) + data.deals.wonValue >= data.rollUp.target
                    ? 'up'
                    : 'down'
                }
                note="commit + won"
              />
            </StatGrid>

            <Panel padding="flush">
              <PanelHeader
                title={scope === 'category' ? 'By forecast category' : 'By seller'}
                note="open deals only; closed is not a forecast"
              />
              <div className={styles.forecastGrid}>
                {scope === 'category' ? (
                  <>
                    <div className={`${styles.forecastCell} ${styles.forecastHead} ${styles.forecastName}`}>
                      Category
                    </div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Deals</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Amount</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Weighted</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Share</div>
                    {byCategory.map((row) => (
                      <FragmentRow key={row.category}>
                        <div className={`${styles.forecastCell} ${styles.forecastName}`}>
                          <Tag tone={row.category === 'Commit' ? 'positive' : 'outline'}>
                            {row.category}
                          </Tag>
                        </div>
                        <div className={styles.forecastCell}>{row.count}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.amount)}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.weighted)}</div>
                        <div className={styles.forecastCell}>
                          {percent(
                            row.amount /
                              (byCategory.reduce((total, entry) => total + entry.amount, 0) || 1),
                          )}
                        </div>
                      </FragmentRow>
                    ))}
                  </>
                ) : (
                  <>
                    <div className={`${styles.forecastCell} ${styles.forecastHead} ${styles.forecastName}`}>
                      Seller
                    </div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Commit</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Best case</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Pipeline</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Total</div>
                    {sellers.map((row) => (
                      <FragmentRow key={row.owner}>
                        <div className={`${styles.forecastCell} ${styles.forecastName}`}>{row.owner}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.commit)}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.best)}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.pipeline)}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.total)}</div>
                      </FragmentRow>
                    ))}
                  </>
                )}
              </div>
            </Panel>
          </>
        )}
      </AsyncBoundary>
    </Page>
  )
}

function FragmentRow({ children }: { children: React.ReactNode }) {
  return <>{children}</>
}

function sum(rows: readonly { [key: string]: string | number | undefined }[]): number {
  return rows.reduce((total, row) => total + Number(row['amount'] ?? 0), 0)
}
