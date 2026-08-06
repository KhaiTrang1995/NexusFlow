import { useMemo, useState } from 'react'
import { useNavigate } from '@tanstack/react-router'
import {
  Button,
  Columns,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelHeader,
  SelectField,
  StatGrid,
  StatTile,
  Tag,
} from '@/design/primitives'
import { StackedBars } from '@/design/charts'
import { fullMoney, money } from '@/lib/format'
import { modelFor, OBJECT_MODELS } from '@/fixtures/objects'
import styles from './analytics.module.css'

type Aggregate = 'count' | 'sum' | 'average'

interface SavedReport {
  name: string
  label: string
  objectKey: string
  groupBy: string
  measure: string
  aggregate: Aggregate
}

const SAVED: readonly SavedReport[] = [
  { name: 'pipeline_by_stage', label: 'Pipeline by stage', objectKey: 'opportunity', groupBy: 'stage', measure: 'amount', aggregate: 'sum' },
  { name: 'pipeline_by_owner', label: 'Pipeline by owner', objectKey: 'opportunity', groupBy: 'owner', measure: 'amount', aggregate: 'sum' },
  { name: 'deals_by_source', label: 'Deals by source', objectKey: 'opportunity', groupBy: 'source', measure: 'amount', aggregate: 'count' },
  { name: 'average_deal_by_type', label: 'Average deal by type', objectKey: 'opportunity', groupBy: 'type', measure: 'amount', aggregate: 'average' },
  { name: 'accounts_by_tier', label: 'Accounts by tier', objectKey: 'account', groupBy: 'tier', measure: 'arr', aggregate: 'sum' },
  { name: 'leads_by_status', label: 'Leads by status', objectKey: 'lead', groupBy: 'status', measure: 'score', aggregate: 'average' },
]

/**
 * The report builder.
 *
 * A CLOSED VOCABULARY, WHICH IS WHY THE DIMENSION IS SAFE. A report here names an object, a
 * grouping field and a measure, all chosen from the object's own model — never a free-text
 * expression. That is the same shape the backend's report surface takes, and it is the reason a
 * dimension can be a bound value rather than a fragment of SQL somebody assembled.
 */
export function ReportsScreen() {
  const navigate = useNavigate()
  const [selected, setSelected] = useState<SavedReport>(SAVED[0] as SavedReport)

  const model = modelFor(selected.objectKey)

  const rows = useMemo(() => {
    const groups = new Map<string, number[]>()

    for (const record of model.records) {
      const key = String(record[selected.groupBy] ?? '—')
      const value = Number(record[selected.measure] ?? 0)
      groups.set(key, [...(groups.get(key) ?? []), value])
    }

    return [...groups.entries()]
      .map(([group, values]) => ({
        group,
        count: values.length,
        value:
          selected.aggregate === 'count'
            ? values.length
            : selected.aggregate === 'sum'
              ? values.reduce((sum, entry) => sum + entry, 0)
              : Math.round(values.reduce((sum, entry) => sum + entry, 0) / (values.length || 1)),
      }))
      .sort((a, b) => b.value - a.value)
  }, [model, selected])

  const isMoney = selected.measure === 'amount' || selected.measure === 'arr'
  const total = rows.reduce((sum, row) => sum + row.value, 0)

  return (
    <Page>
      <PageHeader
        eyebrow="Analytics"
        title="Reports"
        actions={
          <>
            <Button onClick={() => void navigate({ to: '/analytics/campaigns' })}>Campaigns</Button>
            <Button tone="primary">New report</Button>
          </>
        }
      />

      <StatGrid columns={3}>
        <StatTile label="Groups" value={rows.length} note={`by ${selected.groupBy}`} />
        <StatTile label="Records" value={model.records.length} note={model.plural.toLowerCase()} />
        <StatTile
          label={selected.aggregate === 'count' ? 'Total records' : 'Total'}
          value={isMoney && selected.aggregate !== 'count' ? money(total) : total}
          note={selected.aggregate}
        />
      </StatGrid>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title={selected.label} note={`${model.plural} grouped by ${selected.groupBy}`} />
          <div style={{ padding: '16px 17px 6px' }}>
            <StackedBars
              caption={`${selected.label}`}
              series={[{ label: selected.aggregate, colour: 'var(--color-accent)' }]}
              bars={rows.map((row) => ({
                label: row.group,
                values: [row.value],
                readout: isMoney && selected.aggregate !== 'count' ? money(row.value) : String(row.value),
              }))}
              height={210}
            />
          </div>
          <DataTable
            caption={selected.label}
            rows={rows}
            rowKey={(row) => row.group}
            columns={[
              { id: 'group', header: selected.groupBy, cell: (row) => <Tag tone="outline">{row.group}</Tag> },
              { id: 'count', header: 'Records', numeric: true, cell: (row) => row.count, sortValue: (row) => row.count },
              {
                id: 'value',
                header: selected.aggregate,
                numeric: true,
                cell: (row) =>
                  isMoney && selected.aggregate !== 'count' ? fullMoney(row.value) : row.value,
                sortValue: (row) => row.value,
              },
            ]}
          />
        </Panel>

        <div>
          <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
            <PanelHeader title="Saved reports" note={`${SAVED.length}`} />
            <div>
              {SAVED.map((report) => (
                <button
                  key={report.name}
                  type="button"
                  className={styles.reportRow}
                  aria-pressed={report.name === selected.name}
                  onClick={() => setSelected(report)}
                >
                  <span className={styles.reportName}>{report.label}</span>
                  <span className={styles.sub} style={{ marginLeft: 'auto' }}>
                    {OBJECT_MODELS[report.objectKey]?.plural}
                  </span>
                </button>
              ))}
            </div>
          </Panel>

          <Panel padding="flush">
            <PanelHeader title="Build one" note="a closed vocabulary, not an expression" />
            <div className={styles.builder}>
              <SelectField
                label="Object"
                value={selected.objectKey}
                onChange={(event) => {
                  const next = modelFor(event.target.value)
                  setSelected({
                    ...selected,
                    objectKey: next.key,
                    groupBy: next.listCols[1] ?? next.listCols[0] ?? 'id',
                    measure: next.fields.find((field) => field.type === 'currency' || field.type === 'number')?.name ?? 'id',
                  })
                }}
                options={Object.values(OBJECT_MODELS).map((entry) => ({
                  value: entry.key,
                  label: entry.plural,
                }))}
              />
              <SelectField
                label="Group by"
                value={selected.groupBy}
                onChange={(event) => setSelected({ ...selected, groupBy: event.target.value })}
                options={model.fields
                  .filter((field) => field.type === 'picklist' || field.type === 'lookup' || field.type === 'text')
                  .map((field) => ({ value: field.name, label: field.label }))}
              />
              <SelectField
                label="Measure"
                value={selected.measure}
                onChange={(event) => setSelected({ ...selected, measure: event.target.value })}
                options={model.fields
                  .filter((field) => field.type === 'currency' || field.type === 'number' || field.type === 'percent')
                  .map((field) => ({ value: field.name, label: field.label }))}
              />
              <SelectField
                label="Aggregate"
                value={selected.aggregate}
                onChange={(event) =>
                  setSelected({ ...selected, aggregate: event.target.value as Aggregate })
                }
                options={[
                  { value: 'count', label: 'Count' },
                  { value: 'sum', label: 'Sum' },
                  { value: 'average', label: 'Average' },
                ]}
              />
            </div>
          </Panel>
        </div>
      </Columns>
    </Page>
  )
}
