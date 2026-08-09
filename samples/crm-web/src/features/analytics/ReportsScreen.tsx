import { useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  AsyncBoundary,
  Button,
  Columns,
  DataTable,
  EmptyState,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  Skeleton,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { StackedBars } from '@/design/charts'
import type { ReportResult } from '@/api/contracts'
import { useConfig, useReportRun } from '@/api/queries/hooks'
import { count, fullMoney } from '@/lib/format'
import { DeclaredList } from '@/config/DeclaredList'
import { NewReportDrawer } from './NewReportDrawer'
import styles from './analytics.module.css'

/**
 * The reports the tenant has saved, run against the tenant's own rows.
 *
 * THIS SCREEN CALLED NOTHING. Six reports were written out in this file — pipeline by stage,
 * deals by source, accounts by tier — and grouped the prototype's fixture records in the browser.
 * The backend has had `/reports`, `/reports/runs` and a closed dimension vocabulary the whole
 * time; a screen headed "Reports" that grouped four invented opportunities is the most confident
 * kind of wrong, because a bar chart looks like evidence.
 *
 * THE GROUPING IS THE SERVER'S. It reduces in SQL against the saved definition, so what is drawn
 * is what the tenant's rows say at the moment of asking. Grouping here would be a second
 * implementation of the aggregate and the one that runs out of memory first.
 *
 * A FIGURE IS FORMATTED BY THE FIELD IT REDUCED, WHICH THE RESULT NOW CARRIES. "Sum" says how the
 * groups were reduced and not over what, so a client had two wrong answers to choose between —
 * every figure as money, or every figure bare. Drawing twelve opportunities as "$12.00" is the
 * same defect this application has now fixed on four other screens.
 */
export function ReportsScreen() {
  const navigate = useNavigate()
  const saved = useConfig('Report')

  const [chosen, setChosen] = useState<string | null>(null)
  const [building, setBuilding] = useState(false)

  const reports = saved.data?.items ?? []
  const name = chosen ?? reports[0]?.name ?? null
  const run = useReportRun(name)

  return (
    <Page>
      <PageHeader
        eyebrow="Analytics"
        title="Reports"
        actions={
          <>
            <Button onClick={() => void navigate({ to: '/analytics/campaigns' })}>Campaigns</Button>
            <Button tone="primary" onClick={() => setBuilding(true)}>
              New report
            </Button>
          </>
        }
      />

      <AsyncBoundary query={saved} skeletonRows={3}>
        {(list) =>
          list.items.length === 0 ? (
            <EmptyState
              title="No reports are saved"
              detail="Build one and it is stored on the server and run there. Nothing on this screen is an illustration."
              action={
                <Button tone="primary" onClick={() => setBuilding(true)}>
                  Build one
                </Button>
              }
            />
          ) : (
            <Columns layout="split">
              <div>
                {run.isPending ? <Skeleton rows={6} /> : null}

                {run.isError ? (
                  <Panel>
                    <EmptyState title="That report could not be run" detail={run.error.message} />
                  </Panel>
                ) : null}

                {run.isSuccess ? <Result result={run.data} /> : null}
              </div>

              <div>
                <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
                  <PanelHeader title="Saved reports" note={`${list.items.length}`} />
                  <div>
                    {list.items.map((report) => (
                      <button
                        key={report.name}
                        type="button"
                        className={styles.reportRow}
                        aria-pressed={report.name === name}
                        onClick={() => setChosen(report.name)}
                      >
                        <span className={styles.reportName}>{report.label}</span>
                        <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                          {report.summary}
                        </span>
                      </button>
                    ))}
                  </div>
                </Panel>

                <DeclaredList kind="Dashboard" title="Dashboards" empty="No dashboards are saved." />
              </div>
            </Columns>
          )
        }
      </AsyncBoundary>

      {building ? <NewReportDrawer onClose={() => setBuilding(false)} /> : null}
    </Page>
  )
}

/** One run's groups, charted and tabulated. */
function Result({ result }: { result: ReportResult }) {
  // The server formats the measure invariantly, so this is the one place a string becomes a
  // number — and it is only for the bar's length, never for what the reader is shown.
  const bars = result.groups.map((group) => ({
    label: group.dimension,
    values: [Number(group.value) || 0],
    readout: show(result, group.value),
  }))

  const rows = result.groups.length

  return (
    <>
      <StatGrid columns={3}>
        <StatTile label="Groups" value={rows} note={`by the saved dimension`} />
        <StatTile
          label="Rows"
          value={count(result.groups.reduce((sum, group) => sum + group.rows, 0))}
          note="behind the groups"
        />
        <StatTile
          label="Measure"
          value={result.measure}
          note={result.measureOf === null ? 'of rows' : `of ${result.measureOf}`}
        />
      </StatGrid>

      <Panel padding="flush">
        <PanelHeader title={result.label} note={`${rows} group(s), largest first`} />

        {rows === 0 ? (
          <EmptyState
            title="The report ran and matched nothing"
            detail="Which is an answer, not a failure — there are no rows of that source yet."
          />
        ) : (
          <>
            <div style={{ padding: '16px 17px 6px' }}>
              <StackedBars
                caption={result.label}
                series={[{ label: result.measure, colour: 'var(--color-accent)' }]}
                bars={bars}
                height={210}
              />
            </div>
            <DataTable
              caption={result.label}
              rows={result.groups}
              rowKey={(group) => group.dimension}
              columns={[
                {
                  id: 'dimension',
                  header: 'Group',
                  cell: (group) => <Tag tone="outline">{group.dimension}</Tag>,
                },
                {
                  id: 'rows',
                  header: 'Rows',
                  numeric: true,
                  cell: (group) => group.rows,
                  sortValue: (group) => group.rows,
                },
                {
                  id: 'value',
                  header: result.measure,
                  numeric: true,
                  cell: (group) => show(result, group.value),
                  sortValue: (group) => Number(group.value) || 0,
                },
              ]}
            />
          </>
        )}
      </Panel>
    </>
  )
}

/**
 * A measure in the unit of the field it reduced.
 *
 * THE FIELD IS THE FACT THAT DECIDES IT, and the report result carries it precisely so this does
 * not have to guess. "Sum" alone would leave two wrong answers to choose between: every figure as
 * money, which makes a sum of probabilities read as euros, or every figure bare, which makes a
 * pipeline total read as a tally. Amount is the only money field in the built-in vocabulary;
 * a name this table does not know is drawn as a plain number, which is wrong in scale at worst
 * and never wrong in kind.
 */
const MONEY: readonly string[] = ['Amount']

function show(result: ReportResult, value: string): string {
  const parsed = Number(value) || 0

  if (result.measureOf !== null && MONEY.includes(result.measureOf)) {
    return fullMoney(parsed)
  }

  return count(parsed)
}
