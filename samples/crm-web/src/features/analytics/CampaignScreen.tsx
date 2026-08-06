import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  ButtonGroup,
  Columns,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { ShareBar } from '@/design/charts'
import { useCampaignPerformance, useDealAttribution } from '@/api/queries/hooks'
import type { AttributionModel, CampaignPerformance } from '@/api/contracts'
import { fullMoney, money, percent } from '@/lib/format'
import { OBJECT_MODELS } from '@/fixtures/objects'
import styles from './analytics.module.css'

const MODELS: readonly { id: AttributionModel; label: string; note: string }[] = [
  { id: 'FirstTouch', label: 'First touch', note: 'Flatters whoever fills the top of the funnel.' },
  { id: 'LastTouch', label: 'Last touch', note: 'Flatters whoever sent the final email.' },
  { id: 'Linear', label: 'Linear', note: 'Flatters everybody equally and nobody in particular.' },
  {
    id: 'PositionBased',
    label: 'Position-based',
    note: 'Two fifths to the first, two fifths to the last, the rest shared.',
  },
]

/**
 * Campaign performance and attribution.
 *
 * THE MODEL IS ALWAYS ON SCREEN. Credit is the output of a model somebody chose, not a fact about
 * a deal — so the model that produced these numbers is named beside them and the picker is the
 * first control on the page. A report that showed an attributed amount without saying how it was
 * shared out is one two departments can argue about for a quarter while both are right.
 *
 * THE GAP IS SHOWN, NOT CLOSED. What was attributed is lower than what was considered, because a
 * deal nothing touched is not attributable. A report that made them agree would be inventing
 * influence.
 */
export function CampaignScreen() {
  const [model, setModel] = useState<AttributionModel>('Linear')
  const [deal, setDeal] = useState<string | null>(null)

  const report = useCampaignPerformance(model)
  const attribution = useDealAttribution(deal, model)

  const decided = (OBJECT_MODELS['opportunity']?.records ?? []).filter((row) =>
    String(row['stage']).startsWith('Closed'),
  )

  const chosen = MODELS.find((candidate) => candidate.id === model)

  return (
    <Page>
      <PageHeader
        eyebrow="Analytics"
        title="Campaign performance"
        actions={
          <ButtonGroup label="Attribution model">
            {MODELS.map((option) => (
              <Button key={option.id} aria-pressed={model === option.id} onClick={() => setModel(option.id)}>
                {option.label}
              </Button>
            ))}
          </ButtonGroup>
        }
      />

      <AsyncBoundary query={report} skeletonRows={6}>
        {(data) => {
          const unattributed = data.amountConsidered - data.amountAttributed

          return (
            <>
              <StatGrid columns={4}>
                <StatTile label="Deals considered" value={data.dealsConsidered} note="decided in the window" />
                <StatTile label="Value considered" value={money(data.amountConsidered)} note="of those deals" />
                <StatTile
                  label="Attributed"
                  value={money(data.amountAttributed)}
                  delta={percent(
                    data.amountConsidered === 0 ? null : data.amountAttributed / data.amountConsidered,
                  )}
                  note={`under ${data.model}`}
                />
                <StatTile
                  label="Not attributable"
                  value={money(unattributed)}
                  note="nothing touched these deals"
                />
              </StatGrid>

              <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
                <div className={styles.modelNote}>
                  <strong>{data.model}.</strong> {chosen?.note} Every number below is shared out
                  under this model and changes when it changes — which is the point of naming it.
                </div>
                <PanelHeader title="By campaign" note="most credit first" />
                <DataTable
                  caption="Campaign performance"
                  rows={data.campaigns}
                  rowKey={(row) => row.campaignId}
                  columns={[
                    {
                      id: 'campaign',
                      header: 'Campaign',
                      cell: (row: CampaignPerformance) => (
                        <>
                          <span className={styles.link}>{row.label}</span>
                          <div className={styles.sub}>
                            {row.campaign} · {row.channel}
                          </div>
                        </>
                      ),
                      sortValue: (row: CampaignPerformance) => row.label,
                    },
                    {
                      id: 'people',
                      header: 'People',
                      numeric: true,
                      cell: (row: CampaignPerformance) => row.people,
                      sortValue: (row: CampaignPerformance) => row.people,
                    },
                    {
                      id: 'responses',
                      header: 'Responses',
                      numeric: true,
                      cell: (row: CampaignPerformance) => (
                        <>
                          {row.responses}
                          <div className={styles.sub}>{percent(row.responseRate)}</div>
                        </>
                      ),
                      sortValue: (row: CampaignPerformance) => row.responseRate ?? -1,
                    },
                    {
                      id: 'deals',
                      header: 'Deals',
                      numeric: true,
                      cell: (row: CampaignPerformance) => row.influencedDeals,
                      sortValue: (row: CampaignPerformance) => row.influencedDeals,
                    },
                    {
                      id: 'attributed',
                      header: 'Attributed',
                      numeric: true,
                      cell: (row: CampaignPerformance) => fullMoney(row.attributedAmount),
                      sortValue: (row: CampaignPerformance) => row.attributedAmount,
                    },
                    {
                      id: 'spent',
                      header: 'Spent',
                      numeric: true,
                      cell: (row: CampaignPerformance) => (
                        <>
                          {fullMoney(row.spent)}
                          {row.spent > row.budget ? (
                            <div className={styles.gap}>over {fullMoney(row.budget)}</div>
                          ) : null}
                        </>
                      ),
                      sortValue: (row: CampaignPerformance) => row.spent,
                    },
                    {
                      id: 'return',
                      header: 'Return',
                      numeric: true,
                      cell: (row: CampaignPerformance) =>
                        row.return === null ? (
                          <span className={styles.sub}>nothing spent</span>
                        ) : (
                          <span className={row.return >= 1 ? styles.covered : styles.gap}>
                            {row.return.toFixed(1)}×
                          </span>
                        ),
                      sortValue: (row: CampaignPerformance) => row.return ?? -1,
                    },
                  ]}
                  empty="No campaigns have been declared for this tenant."
                />
              </Panel>

              <Columns layout="split">
                <Panel padding="flush">
                  <PanelHeader title="Who influenced one deal" note="pick a decided deal" />
                  <div>
                    {decided.map((row) => (
                      <button
                        key={row.id}
                        type="button"
                        className={styles.reportRow}
                        aria-pressed={deal === row.id}
                        onClick={() => setDeal(row.id)}
                      >
                        <span className={styles.reportName}>{row['name']}</span>
                        <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                          {row['stage']} · {fullMoney(Number(row['amount']))}
                        </span>
                      </button>
                    ))}
                    {decided.length === 0 ? (
                      <div style={{ padding: 17 }} className={styles.sub}>
                        Nothing has been decided yet, so there is nothing to share out.
                      </div>
                    ) : null}
                  </div>
                </Panel>

                <Panel padding="flush">
                  <PanelHeader
                    title="The split"
                    note={deal ? `under ${model}` : 'nothing selected'}
                  />
                  {deal === null ? (
                    <div style={{ padding: 17 }} className={styles.sub}>
                      Pick a deal on the left. The credit sums to the deal to the penny — the last
                      campaign takes the remainder rather than everybody taking a rounded share.
                    </div>
                  ) : (
                    <AsyncBoundary query={attribution} skeletonRows={3}>
                      {(split) => (
                        <div>
                          <div style={{ padding: '14px 17px 6px' }}>
                            <ShareBar
                              caption="Credit by campaign"
                              height={14}
                              parts={split.credits.map((credit, index) => ({
                                label: credit.campaign,
                                value: Number(credit.amount),
                                colour: [
                                  'var(--color-accent-800)',
                                  'var(--color-accent)',
                                  'var(--color-accent-400)',
                                  'var(--color-accent-300)',
                                ][index % 4] as string,
                              }))}
                            />
                          </div>
                          <DataTable
                            caption="Attributed credit"
                            rows={split.credits}
                            rowKey={(row) => row.campaignId}
                            columns={[
                              { id: 'campaign', header: 'Campaign', cell: (row) => row.campaign },
                              {
                                id: 'share',
                                header: 'Share',
                                numeric: true,
                                cell: (row) => percent(row.share, 1),
                              },
                              {
                                id: 'amount',
                                header: 'Credit',
                                numeric: true,
                                cell: (row) => fullMoney(row.amount),
                              },
                            ]}
                            empty="Nothing touched this deal before it was decided."
                          />
                          <div style={{ padding: '12px 17px' }}>
                            <div className={styles.sub}>
                              Sums to {fullMoney(split.credits.reduce((sum, row) => sum + row.amount, 0))} of{' '}
                              {fullMoney(split.amount)}.
                            </div>
                            {split.touchesAfterTheDecision > 0 ? (
                              <Tag tone="warning" dot>
                                {split.touchesAfterTheDecision} touches landed after the decision and
                                were not counted
                              </Tag>
                            ) : null}
                          </div>
                        </div>
                      )}
                    </AsyncBoundary>
                  )}
                </Panel>
              </Columns>
            </>
          )
        }}
      </AsyncBoundary>
    </Page>
  )
}
