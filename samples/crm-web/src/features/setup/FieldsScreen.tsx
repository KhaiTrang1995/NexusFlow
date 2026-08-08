import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  Columns,
  DataTable,
  ErrorState,
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
import { useDefineField, useSchema } from '@/api/queries/hooks'
import type { CustomFieldType } from '@/api/contracts'
import { schemaRowOf, schemaRows } from './schemaModel'
import type { SchemaFieldRow } from './schemaModel'
import { FIELD_TYPES, isClosedSet, refusalOf, requestOf } from './fieldDraft'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

/**
 * The fields on an object, and the form that declares one.
 *
 * THE FORM WROTE NOTHING AND SAID IT HAD. "Declare the field" toasted
 * `${label} declared on ${object}`, cleared itself and never touched the network — so the field
 * was on no screen afterwards, including the table beside the form. `/custom/fields` has been
 * there throughout, and it is what this posts to now.
 *
 * THE TYPES WERE INVENTED TOO. `email`, `phone`, `currency`, `percent`, `lookup` and `formula`:
 * six of the ten offered are words this backend has never heard of. The seven in `fieldDraft` are
 * the server's `CustomFieldType`, which is closed because a type is a parse and a comparison
 * rather than a label.
 *
 * A PICKLIST WITH NO OPTIONS IS REFUSED, NOT SAVED EMPTY. It is the shape that silently breaks a
 * form later: the field exists, the control renders, and nothing can ever be chosen. The rule is
 * the backend's; saying it here means the administrator finds out while they are still typing.
 */
export function FieldsScreen() {
  const toast = useToast()
  const schema = useSchema()
  const declare = useDefineField()

  const [objectKey, setObjectKey] = useState('Opportunity')
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [type, setType] = useState<CustomFieldType>('Text')
  const [required, setRequired] = useState(false)
  const [options, setOptions] = useState('')
  const [references, setReferences] = useState('')

  const owner = schemaRowOf(schema.data, objectKey)

  // Only the declared objects can be pointed at: a reference holds the id of a row of a custom
  // object, and a built-in entity has no id in this model to hold.
  const targets = schemaRows(schema.data).filter((row) => row.objectId !== null)

  const draft = { owner, name, label, type, required, options, references }
  const refusal = refusalOf(draft)

  function submit() {
    const request = requestOf(draft)

    if (request === null) {
      return
    }

    declare.mutate(request, {
      onSuccess: (result) => {
        toast.saved(`${result.name} is now a field of ${owner?.label ?? objectKey}.`)
        setName('')
        setLabel('')
        setOptions('')
        setReferences('')
      },
      onError: (error) => toast.failed(error, 'That field was refused.'),
    })
  }

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Fields" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher value={objectKey} onChange={setObjectKey} />
      </Panel>

      {/*
        One boundary over both halves. A refused `describe` used to leave the table headed
        "Opportunity fields · 0" — an empty object, stated as a fact, from a request that never
        answered — above a form offering to add a field to it.
      */}
      <AsyncBoundary query={schema} skeletonRows={8}>
        {() => (
          <Columns layout="split">
            <Panel padding="flush">
              <PanelHeader
                title={`${owner?.label ?? objectKey} fields`}
                note={`${owner?.fields.length ?? 0}`}
              />
              <DataTable
                caption={`${owner?.label ?? objectKey} fields`}
                rows={owner?.fields ?? []}
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
                      ) : row.references !== null ? (
                        <span className={styles.sub}>points at another record</span>
                      ) : !row.declared ? (
                        <span className={styles.sub}>a column of the table</span>
                      ) : (
                        <span className={styles.sub}>—</span>
                      ),
                  },
                  {
                    id: 'required',
                    header: 'Required',
                    cell: (row: SchemaFieldRow) =>
                      row.required ? <Tag tone="accent">yes</Tag> : '—',
                  },
                ]}
                empty="Nothing has been declared on this object, and its columns are the table's."
              />
            </Panel>

            <Panel padding="flush">
              <PanelHeader
                title="Declare a field"
                note={`on ${(owner?.label ?? objectKey).toLowerCase()}`}
              />
              <PanelBody>
                <form
                  style={{ display: 'grid', gap: 12 }}
                  onSubmit={(event) => {
                    event.preventDefault()
                    submit()
                  }}
                >
                  <TextField
                    label="Name"
                    required
                    hint="Lower case, letters, digits and underscores. It never changes again."
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
                    onChange={(event) => {
                      setType(event.target.value as CustomFieldType)
                      setOptions('')
                      setReferences('')
                    }}
                    options={FIELD_TYPES.map((entry) => ({ value: entry, label: entry }))}
                  />
                  <SelectField
                    label="Required"
                    value={required ? 'yes' : 'no'}
                    hint="A required field every future record must carry, including ones written by flows."
                    onChange={(event) => setRequired(event.target.value === 'yes')}
                    options={[
                      { value: 'no', label: 'no' },
                      { value: 'yes', label: 'yes' },
                    ]}
                  />
                  {isClosedSet(type) ? (
                    <TextField
                      label="Options"
                      required
                      hint="Comma separated. Each is stored as text, so each follows the name rule."
                      value={options}
                      onChange={(event) => setOptions(event.target.value)}
                    />
                  ) : null}
                  {type === 'Reference' ? (
                    targets.length === 0 ? (
                      <p className={styles.sub}>
                        A reference points at a declared object, and this tenant has none. Declare
                        one on the objects screen and it appears here.
                      </p>
                    ) : (
                      <SelectField
                        label="Points at"
                        value={references}
                        placeholder="Choose an object"
                        onChange={(event) => setReferences(event.target.value)}
                        options={targets.map((row) => ({
                          value: row.objectId as string,
                          label: row.label,
                        }))}
                      />
                    )
                  ) : null}

                  <Button
                    type="submit"
                    tone="primary"
                    disabled={refusal !== null || declare.isPending}
                    {...(refusal !== null ? { title: refusal } : {})}
                  >
                    {declare.isPending ? 'Declaring…' : 'Declare the field'}
                  </Button>

                  {/*
                    The reason the button is off, said on the page as well as in its title — a
                    picklist with no options is the mistake this screen exists to catch, and a
                    tooltip is not where somebody looks for it.
                  */}
                  {refusal !== null && (name.length > 0 || label.length > 0) ? (
                    <p className={styles.sub}>{refusal}</p>
                  ) : null}

                  {declare.isError ? <ErrorState error={declare.error} onRetry={submit} /> : null}
                </form>
              </PanelBody>
            </Panel>
          </Columns>
        )}
      </AsyncBoundary>
    </Page>
  )
}
