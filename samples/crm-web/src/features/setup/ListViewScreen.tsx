import { useState } from 'react'
import {
  Button,
  Columns,
  DataTable,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Tag,
  TextField,
} from '@/design/primitives'
import { useToast } from '@/app/ToastProvider'
import { modelFor, optionsFor } from '@/fixtures/objects'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

const OPERATORS = ['Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet'] as const

/**
 * Saved list views.
 *
 * FIVE OPERATORS AND NOTHING ELSE. The criterion below is the same closed vocabulary the backend's
 * transition guards, validation rules, roll-up filters, territory rules and approval criteria all
 * use — one evaluator, six features. That is why a criterion is never assembled into SQL, and why
 * this form can offer a picker rather than a text box that accepts anything.
 */
export function ListViewScreen() {
  const toast = useToast()
  const [objectKey, setObjectKey] = useState('opportunity')
  const [name, setName] = useState('')
  const [field, setField] = useState('')
  const [operator, setOperator] = useState<(typeof OPERATORS)[number]>('Equals')
  const [value, setValue] = useState('')

  const model = modelFor(objectKey)
  const options = field ? optionsFor(model, field) : []

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="List views" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher value={objectKey} onChange={setObjectKey} />
      </Panel>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title="Saved views" note={`on ${model.plural.toLowerCase()}`} />
          <DataTable
            caption="Saved list views"
            rows={VIEWS.filter((view) => view.objectKey === objectKey || view.objectKey === 'any')}
            rowKey={(row) => row.name}
            columns={[
              {
                id: 'name',
                header: 'View',
                cell: (row) => (
                  <>
                    <span className={styles.link}>{row.label}</span>
                    <div className={styles.mono}>{row.name}</div>
                  </>
                ),
              },
              { id: 'kind', header: 'Kind', cell: (row) => <Tag tone="outline">{row.kind}</Tag> },
              { id: 'criteria', header: 'Criteria', cell: (row) => <span className={styles.sub}>{row.criteria}</span> },
            ]}
            empty="No saved views on this object."
          />
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Save a view" note="its fields are checked when it is saved" />
          <PanelBody>
            <form
              style={{ display: 'grid', gap: 12 }}
              onSubmit={(event) => {
                event.preventDefault()
                toast.saved(`${name} saved on ${model.plural}.`)
                setName('')
                setValue('')
              }}
            >
              <TextField
                label="Name"
                required
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              <SelectField
                label="Field"
                value={field}
                placeholder="Choose a field"
                onChange={(event) => {
                  setField(event.target.value)
                  setValue('')
                }}
                options={model.fields.map((entry) => ({ value: entry.name, label: entry.label }))}
              />
              <SelectField
                label="Operator"
                value={operator}
                onChange={(event) => setOperator(event.target.value as (typeof OPERATORS)[number])}
                options={OPERATORS.map((entry) => ({ value: entry, label: entry }))}
              />
              {operator !== 'IsSet' ? (
                options.length > 0 ? (
                  <SelectField
                    label="Value"
                    value={value}
                    placeholder="Choose a value"
                    onChange={(event) => setValue(event.target.value)}
                    options={options.map((entry) => ({ value: entry, label: entry }))}
                  />
                ) : (
                  <TextField
                    label="Value"
                    value={value}
                    onChange={(event) => setValue(event.target.value)}
                  />
                )
              ) : (
                <p className={styles.sub}>
                  <strong>IsSet</strong> takes no value — it asks whether the field has one at all.
                </p>
              )}
              <Button
                type="submit"
                tone="primary"
                disabled={name.trim() === '' || field === '' || (operator !== 'IsSet' && value === '')}
              >
                Save the view
              </Button>
            </form>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}

const VIEWS = [
  { name: 'my_open_deals', label: 'My open deals', objectKey: 'opportunity', kind: 'Table', criteria: 'owner Equals me · stage NotEquals Closed Won' },
  { name: 'closing_this_month', label: 'Closing this month', objectKey: 'opportunity', kind: 'Table', criteria: 'closeDate LessThan 2026-09-01' },
  { name: 'commit_board', label: 'Commit board', objectKey: 'opportunity', kind: 'Kanban', criteria: 'forecast Equals Commit' },
  { name: 'strategic_accounts', label: 'Strategic accounts', objectKey: 'account', kind: 'Table', criteria: 'tier Equals Strategic' },
  { name: 'unworked_leads', label: 'Unworked leads', objectKey: 'lead', kind: 'Table', criteria: 'status Equals New' },
  { name: 'overdue_tasks', label: 'Overdue tasks', objectKey: 'task', kind: 'Table', criteria: 'due LessThan today · status NotEquals Done' },
]
