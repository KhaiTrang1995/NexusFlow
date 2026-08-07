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
import { useSchema } from '@/api/queries/hooks'
import { schemaRowOf } from './schemaModel'
import type { SchemaFieldRow } from './schemaModel'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

const TYPES = ['text', 'email', 'phone', 'number', 'currency', 'percent', 'date', 'picklist', 'lookup', 'formula'] as const

/**
 * The fields on an object.
 *
 * A PICKLIST WITH NO OPTIONS IS REFUSED, NOT SAVED EMPTY. It is the shape that silently breaks a
 * form later: the field exists, the control renders, and nothing can ever be chosen. The rule is
 * the backend's; showing it here means the administrator finds out while they are still typing.
 */
export function FieldsScreen() {
  const toast = useToast()
  const [objectKey, setObjectKey] = useState('Opportunity')
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [type, setType] = useState<(typeof TYPES)[number]>('text')
  const [options, setOptions] = useState('')

  const schema = useSchema()

  // An empty stand-in rather than an early return: the object switcher above is what changes
  // `objectKey`, and a screen that unmounted it while the description loaded would take the
  // control away from under the administrator's cursor.
  const model = schemaRowOf(schema.data, objectKey) ?? { label: objectKey, fields: [] }
  const needsOptions = type === 'picklist'
  const optionList = options.split(',').map((entry) => entry.trim()).filter(Boolean)

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Fields" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher value={objectKey} onChange={setObjectKey} />
      </Panel>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title={`${model.label} fields`} note={`${model.fields.length}`} />
          <DataTable
            caption={`${model.label} fields`}
            rows={model.fields}
            rowKey={(row) => row.name}
            columns={[
              {
                id: 'label',
                header: 'Field',
                cell: (row: SchemaFieldRow) => (
                  <>
                    <span className={styles.link}>{row.label}</span>
                    <div className={styles.mono}>{row.name}</div>
                  </>
                ),
                sortValue: (row: SchemaFieldRow) => row.label,
              },
              {
                id: 'type',
                header: 'Type',
                cell: (row: SchemaFieldRow) => <Tag tone="outline">{row.type}</Tag>,
                sortValue: (row: SchemaFieldRow) => row.type,
              },
              {
                id: 'detail',
                header: 'Detail',
                cell: (row: SchemaFieldRow) =>
                  row.options.length > 0 ? (
                    <span className={styles.sub}>{row.options.join(' · ')}</span>
                  ) : row.computed ? (
                    <span className={styles.sub}>ƒ computed by the server</span>
                  ) : !row.declared ? (
                    <span className={styles.sub}>a column of the table</span>
                  ) : (
                    <span className={styles.sub}>—</span>
                  ),
              },
              {
                id: 'required',
                header: 'Required',
                cell: (row: SchemaFieldRow) => (row.required ? <Tag tone="accent">yes</Tag> : '—'),
              },
            ]}
          />
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Declare a field" note={`on ${model.label.toLowerCase()}`} />
          <PanelBody>
            <form
              style={{ display: 'grid', gap: 12 }}
              onSubmit={(event) => {
                event.preventDefault()
                toast.saved(`${label} declared on ${model.label}.`)
                setName('')
                setLabel('')
                setOptions('')
              }}
            >
              <TextField
                label="Name"
                required
                hint="Lower case, letters, numbers and underscores. It never changes again."
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              <TextField
                label="Label"
                required
                hint="What a person sees. This one can be renamed."
                value={label}
                onChange={(event) => setLabel(event.target.value)}
              />
              <SelectField
                label="Type"
                value={type}
                onChange={(event) => setType(event.target.value as (typeof TYPES)[number])}
                options={TYPES.map((entry) => ({ value: entry, label: entry }))}
              />
              {needsOptions ? (
                <TextField
                  label="Options"
                  required
                  hint="Comma separated."
                  error={
                    options.trim() !== '' && optionList.length === 0
                      ? 'A picklist with no options is a field nothing can ever be chosen for.'
                      : undefined
                  }
                  value={options}
                  onChange={(event) => setOptions(event.target.value)}
                />
              ) : null}
              <Button
                type="submit"
                tone="primary"
                disabled={name.trim() === '' || label.trim() === '' || (needsOptions && optionList.length === 0)}
              >
                Declare the field
              </Button>
            </form>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}
