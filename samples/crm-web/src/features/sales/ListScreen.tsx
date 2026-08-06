import { useNavigate } from '@tanstack/react-router'
import { useMemo, useState } from 'react'
import {
  Button,
  ButtonGroup,
  DataTable,
  Drawer,
  DrawerSection,
  FieldRow,
  Page,
  PageHeader,
  Tag,
  TextField,
} from '@/design/primitives'
import type { Column } from '@/design/primitives'
import { modelFor, optionsFor } from '@/fixtures/objects'
import type { RecordRow } from '@/fixtures/objects'
import { isNumeric, renderCell } from './RecordCell'
import styles from './ListScreen.module.css'

/**
 * The list view — one screen for every object, driven by the object's own model.
 *
 * SEVEN OBJECTS AND ONE COMPONENT. The prototype renders each list from the same metadata, and so
 * does this: the columns, the filters and the peek panel are read from `listCols`, `fields` and
 * `layout`. A new object is a row in the model, not a new screen — which is the whole claim the
 * backend's dynamic schema makes, honoured on the client rather than contradicted by it.
 */
export function ListScreen({ objectKey }: { objectKey: string }) {
  const navigate = useNavigate()
  const model = modelFor(objectKey)

  const [search, setSearch] = useState('')
  const [stage, setStage] = useState<string>('all')
  const [peek, setPeek] = useState<RecordRow | null>(null)
  const [hidden, setHidden] = useState<readonly string[]>([])
  const [showColumns, setShowColumns] = useState(false)

  const stages = model.stageField ? optionsFor(model, model.stageField) : []

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase()

    return model.records.filter((record) => {
      if (stage !== 'all' && model.stageField && record[model.stageField] !== stage) return false
      if (term === '') return true
      // Every field, not just the first column: a list whose search only looked at the name is a
      // list you cannot use to find the account somebody mentioned on the telephone.
      return Object.values(record).some((value) => String(value ?? '').toLowerCase().includes(term))
    })
  }, [model, search, stage])

  const columns: readonly Column<RecordRow>[] = model.listCols
    .filter((name) => !hidden.includes(name))
    .map((name) => ({
      id: name,
      header: model.fields.find((field) => field.name === name)?.label ?? name,
      numeric: isNumeric(model, name),
      cell: (row: RecordRow) => renderCell(model, row, name),
      sortValue: (row: RecordRow) => {
        const value = row[name]
        return typeof value === 'number' ? value : String(value ?? '')
      },
    }))

  return (
    <Page layout="full">
      <PageHeader
        bar
        small
        eyebrow={`${rows.length} of ${model.records.length} · All ${model.plural.toLowerCase()}`}
        title={model.plural}
        actions={
          <>
            <TextField
              label="Search this list"
              className={styles.search}
              placeholder={`Search ${model.plural.toLowerCase()}…`}
              value={search}
              onChange={(event) => setSearch(event.target.value)}
            />
            <Button onClick={() => setShowColumns((open) => !open)} aria-expanded={showColumns}>
              Columns
            </Button>
            {model.stageField ? (
              <Button onClick={() => void navigate({ to: '/kanban' })}>Kanban</Button>
            ) : null}
            <Button tone="primary">New {model.label.toLowerCase()}</Button>
          </>
        }
      />

      {stages.length > 0 ? (
        <div className={styles.filterStrip}>
          <ButtonGroup label={`Filter by ${model.stageField}`}>
            <Button size="sm" aria-pressed={stage === 'all'} onClick={() => setStage('all')}>
              All
            </Button>
            {stages.map((option) => (
              <Button
                key={option}
                size="sm"
                aria-pressed={stage === option}
                onClick={() => setStage(option)}
              >
                {option}
              </Button>
            ))}
          </ButtonGroup>
          <span className={styles.filterNote}>
            {stage === 'all' ? 'Every record in this view' : `Filtered to ${stage}`}
          </span>
        </div>
      ) : null}

      {showColumns ? (
        <div className={styles.columnPanel}>
          <span className={styles.columnLabel}>Columns</span>
          {model.listCols.map((name) => (
            <label key={name} className={styles.columnToggle}>
              <input
                type="checkbox"
                checked={!hidden.includes(name)}
                onChange={(event) =>
                  setHidden((current) =>
                    event.target.checked
                      ? current.filter((hiddenName) => hiddenName !== name)
                      : [...current, name],
                  )
                }
              />
              {model.fields.find((field) => field.name === name)?.label ?? name}
            </label>
          ))}
        </div>
      ) : null}

      <div className={styles.body}>
        <DataTable
          caption={`All ${model.plural.toLowerCase()}`}
          columns={columns}
          rows={rows}
          rowKey={(row) => row.id}
          onRowClick={setPeek}
          isRowSelected={(row) => row.id === peek?.id}
          empty={
            search
              ? `Nothing in ${model.plural.toLowerCase()} matches “${search}”.`
              : `No ${model.plural.toLowerCase()} yet.`
          }
        />
      </div>

      {peek ? (
        <Drawer
          eyebrow={peek.id}
          title={String(peek[model.listCols[0] ?? 'name'] ?? peek.id)}
          subtitle={model.label}
          onClose={() => setPeek(null)}
          actions={
            <>
              <Button
                tone="primary"
                onClick={() =>
                  void navigate({
                    to: '/records/$object/$id',
                    params: { object: model.key, id: peek.id },
                  })
                }
              >
                Open record
              </Button>
              <Button>Edit</Button>
            </>
          }
        >
          <DrawerSection label="Details" note="as configured on the page layout" />
          {model.fields.map((field) => (
            <FieldRow key={field.name} label={field.label}>
              {renderCell(model, peek, field.name)}
            </FieldRow>
          ))}
          {model.stageField ? (
            <>
              <DrawerSection label="Stage" />
              <div className={styles.peekStages}>
                {stages.map((option) => (
                  <Tag key={option} tone={peek[model.stageField as string] === option ? 'accent' : 'outline'}>
                    {option}
                  </Tag>
                ))}
              </div>
            </>
          ) : null}
        </Drawer>
      ) : null}
    </Page>
  )
}
