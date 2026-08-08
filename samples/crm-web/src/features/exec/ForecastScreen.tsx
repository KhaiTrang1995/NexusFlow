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
import { useEntityPage, useExecutiveBoard } from '@/api/queries/hooks'
import { fullMoney, money, percent } from '@/lib/format'
import { usePeriod, withPeriod } from './period'
import { NoPeriods, PeriodPicker } from './PeriodPicker'
import styles from './exec.module.css'


/**
 * The probability bands a forecast is read in.
 *
 * NOT "COMMIT / BEST CASE / PIPELINE". Those are forecast categories, and `opportunity` has no
 * such column — the four this screen grouped by matched nothing on any real row, so it showed
 * three confident zeroes. A probability is what a deal actually carries and what a weighted
 * number is made of.
 */
const BANDS: readonly { label: string; from: number; to: number }[] = [
  { label: '0–25%', from: 0, to: 25 },
  { label: '26–50%', from: 26, to: 50 },
  { label: '51–75%', from: 51, to: 75 },
  { label: '76–100%', from: 76, to: 100 },
]

/**
 * The forecast, by category and by seller.
 *
 * THE CATEGORIES ARE NOT THE STAGES. A deal in Negotiation may be Best Case and a deal in Qualify
 * may be Commit, because a category is a judgement a seller makes and a stage is where the work
 * has got to. Rolling one up as the other is the single most common way a forecast becomes
 * fiction, so both are shown.
 */
export function ForecastScreen() {
  const choice = usePeriod()
  const [scope, setScope] = useState<'category' | 'seller'>('category')
  const board = useExecutiveBoard(choice.period)

  // Live open deals. The category half of this screen grouped by `forecast` — Commit, Best Case,
  // Pipeline — which is not a column on `opportunity` anywhere in this schema. Against real rows
  // every one of those three groups was empty, so the screen showed three zeroes with confidence.
  //
  // What a deal actually carries is a probability, which is what a weighted forecast is made of.
  const deals = useEntityPage('Opportunity')

  const opportunities = (deals.data?.records ?? []).filter(
    (row) => row.values['outcome'] === null,
  )

  const byBand = BANDS.map((band) => {
    const rows = opportunities.filter((row) => {
      const probability = Number(row.values['probability'] ?? 0)
      return probability >= band.from && probability <= band.to
    })

    const amount = rows.reduce((total, row) => total + Number(row.values['amount'] ?? 0), 0)

    return {
      band: band.label,
      count: rows.length,
      amount,
      weighted: Math.round(
        rows.reduce(
          (total, row) =>
            total
            + (Number(row.values['amount'] ?? 0) * Number(row.values['probability'] ?? 0)) / 100,
          0,
        ),
      ),
    }
  })

  const byStage = [
    ...opportunities
      .reduce((groups, row) => {
        const stage = row.values['stage'] ?? '—'
        const current = groups.get(stage) ?? { stage, count: 0, amount: 0 }

        current.count += 1
        current.amount += Number(row.values['amount'] ?? 0)
        groups.set(stage, current)

        return groups
      }, new Map<string, { stage: string; count: number; amount: number }>())
      .values(),
  ]

  // The likeliest band is the closest thing this schema has to a commit number, and it is called
  // what it is rather than "Commit" — a category nobody in this tenant ever assigned.
  const likeliest = byBand[byBand.length - 1]

  return (
    <Page>
      <PageHeader
        eyebrow={withPeriod('Executive', choice)}
        title="Forecast"
        actions={
          <>
            <PeriodPicker choice={choice} />
            <ButtonGroup label="Roll up by">
            <Button aria-pressed={scope === 'category'} onClick={() => setScope('category')}>
              Category
            </Button>
            <Button aria-pressed={scope === 'seller'} onClick={() => setScope('seller')}>
              Stage
            </Button>
            </ButtonGroup>
          </>
        }
      />

      {choice.isUndeclared ? <NoPeriods what="the forecast" /> : null}

      <AsyncBoundary query={board} skeletonRows={4} hidden={choice.isUndeclared}>
        {(data) => (
          <>
            <StatGrid columns={4}>
              <StatTile
                label="Open"
                value={money(byBand.reduce((total, row) => total + row.amount, 0))}
                note={`${opportunities.length} deal(s) with no outcome yet`}
              />
              <StatTile
                label="Weighted"
                value={money(byBand.reduce((total, row) => total + row.weighted, 0))}
                note="amount × probability, deal by deal"
              />
              <StatTile
                label="Likeliest"
                value={money(likeliest?.amount ?? 0)}
                note={`${likeliest?.band ?? '—'} · the closest thing here to a commit`}
              />
              <StatTile
                label="Against target"
                value={percent(
                  data.rollUp.target === 0
                    ? null
                    : (byBand.reduce((total, row) => total + row.weighted, 0) + data.deals.wonValue)
                      / data.rollUp.target,
                )}
                direction={
                  byBand.reduce((total, row) => total + row.weighted, 0) + data.deals.wonValue
                  >= data.rollUp.target
                    ? 'up'
                    : 'down'
                }
                note="weighted + won"
              />
            </StatGrid>

            <Panel padding="flush">
              <PanelHeader
                title={scope === 'category' ? 'By likelihood' : 'By stage'}
                note="open deals only; closed is not a forecast"
              />
              <div className={styles.forecastGrid}>
                {scope === 'category' ? (
                  <>
                    <div className={`${styles.forecastCell} ${styles.forecastHead} ${styles.forecastName}`}>
                      Likelihood
                    </div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Deals</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Amount</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Weighted</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Share</div>
                    {byBand.map((row) => (
                      <FragmentRow key={row.band}>
                        <div className={`${styles.forecastCell} ${styles.forecastName}`}>
                          <Tag tone={row.band === '76–100%' ? 'positive' : 'outline'}>
                            {row.band}
                          </Tag>
                        </div>
                        <div className={styles.forecastCell}>{row.count}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.amount)}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.weighted)}</div>
                        <div className={styles.forecastCell}>
                          {percent(
                            row.amount
                              / (byBand.reduce((total, entry) => total + entry.amount, 0) || 1),
                          )}
                        </div>
                      </FragmentRow>
                    ))}
                  </>
                ) : (
                  <>
                    {/*
                      By stage rather than by seller. A seller breakdown needs an owner's name and
                      an opportunity carries an owner uuid; the board above is scoped by the
                      reporting line and is where that question is answered.
                    */}
                    <div className={`${styles.forecastCell} ${styles.forecastHead} ${styles.forecastName}`}>
                      Stage
                    </div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Deals</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Amount</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`}>Share</div>
                    <div className={`${styles.forecastCell} ${styles.forecastHead}`} />
                    {byStage.map((row) => (
                      <FragmentRow key={row.stage}>
                        <div className={`${styles.forecastCell} ${styles.forecastName}`}>
                          <Tag tone="outline">{row.stage}</Tag>
                        </div>
                        <div className={styles.forecastCell}>{row.count}</div>
                        <div className={styles.forecastCell}>{fullMoney(row.amount)}</div>
                        <div className={styles.forecastCell}>
                          {percent(
                            row.amount
                              / (byStage.reduce((total, entry) => total + entry.amount, 0) || 1),
                          )}
                        </div>
                        <div className={styles.forecastCell} />
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

