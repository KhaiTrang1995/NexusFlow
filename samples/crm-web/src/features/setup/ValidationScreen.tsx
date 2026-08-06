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
  TextAreaField,
  TextField,
} from '@/design/primitives'
import { useToast } from '@/app/ToastProvider'
import { modelFor, optionsFor } from '@/fixtures/objects'
import { ObjectSwitcher } from './ObjectSwitcher'
import styles from './setup.module.css'

const OPERATORS = ['Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'IsSet'] as const

interface Rule {
  name: string
  objectKey: string
  attribute: string
  operator: (typeof OPERATORS)[number]
  value: string
  message: string
}

const RULES: readonly Rule[] = [
  { name: 'close_date_required', objectKey: 'opportunity', attribute: 'closeDate', operator: 'IsSet', value: '', message: 'A deal with no close date is a deal nobody is forecasting.' },
  { name: 'next_step_on_late_stage', objectKey: 'opportunity', attribute: 'nextStep', operator: 'IsSet', value: '', message: 'Say what happens next before moving past Proposal.' },
  { name: 'discount_ceiling', objectKey: 'quote', attribute: 'discount', operator: 'LessThan', value: '35', message: 'Discounts over 35% are not approvable by anybody. Restructure the deal.' },
  { name: 'lead_source_required', objectKey: 'lead', attribute: 'source', operator: 'IsSet', value: '', message: 'Without a source, this lead cannot be attributed to anything.' },
]

/**
 * Validation rules.
 *
 * THE MESSAGE IS THE ADMINISTRATOR'S OWN WORDS. A rule that refused with "validation failed" would
 * make the person who hit it guess; the message field is required, and it is what the caller sees.
 *
 * A RULE IS REFUSED WHEN IT *HOLDS*, WHICH READS BACKWARDS UNTIL YOU SAY IT ALOUD. So the preview
 * on the right says it aloud, before anybody saves a rule that means the opposite of what they
 * meant.
 */
export function ValidationScreen() {
  const toast = useToast()
  const [objectKey, setObjectKey] = useState('opportunity')
  const [name, setName] = useState('')
  const [attribute, setAttribute] = useState('')
  const [operator, setOperator] = useState<(typeof OPERATORS)[number]>('IsSet')
  const [value, setValue] = useState('')
  const [message, setMessage] = useState('')

  const model = modelFor(objectKey)
  const options = attribute ? optionsFor(model, attribute) : []
  const attributeLabel = model.fields.find((field) => field.name === attribute)?.label ?? attribute

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Validation rules" />

      <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
        <ObjectSwitcher value={objectKey} onChange={setObjectKey} />
      </Panel>

      <Columns layout="split">
        <Panel padding="flush">
          <PanelHeader title="Rules in force" note={`on ${model.plural.toLowerCase()}`} />
          <DataTable
            caption="Validation rules"
            rows={RULES.filter((rule) => rule.objectKey === objectKey)}
            rowKey={(row) => row.name}
            columns={[
              {
                id: 'name',
                header: 'Rule',
                cell: (row: Rule) => (
                  <>
                    <span className={styles.link}>{row.name}</span>
                    <div className={styles.sub}>{row.message}</div>
                  </>
                ),
              },
              {
                id: 'when',
                header: 'Refuses when',
                cell: (row: Rule) => (
                  <Tag tone="outline">
                    {row.attribute} {row.operator} {row.operator === 'IsSet' ? '' : row.value}
                  </Tag>
                ),
              },
            ]}
            empty={`No rules on ${model.plural.toLowerCase()} yet.`}
          />
        </Panel>

        <Panel padding="flush">
          <PanelHeader title="Declare a rule" note="in your own words" />
          <PanelBody>
            <form
              style={{ display: 'grid', gap: 12 }}
              onSubmit={(event) => {
                event.preventDefault()
                toast.saved(`${name} declared on ${model.plural}.`)
                setName('')
                setMessage('')
              }}
            >
              <TextField label="Name" required value={name} onChange={(event) => setName(event.target.value)} />
              <SelectField
                label="Field"
                value={attribute}
                placeholder="Choose a field"
                onChange={(event) => {
                  setAttribute(event.target.value)
                  setValue('')
                }}
                options={model.fields.map((field) => ({ value: field.name, label: field.label }))}
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
                  <TextField label="Value" value={value} onChange={(event) => setValue(event.target.value)} />
                )
              ) : null}
              <TextAreaField
                label="What to tell the person"
                required
                hint="Shown verbatim when the rule refuses. Say what to do, not that something failed."
                value={message}
                onChange={(event) => setMessage(event.target.value)}
              />

              {attribute ? (
                <div style={{ padding: 11, border: '1px solid var(--color-accent-300)', borderRadius: 9, background: 'var(--color-accent-100)' }}>
                  <div className={styles.sub} style={{ marginBottom: 4 }}>
                    Read it back
                  </div>
                  <p style={{ fontSize: 14 }}>
                    A {model.label.toLowerCase()} is <strong>refused</strong> when{' '}
                    <strong>{attributeLabel}</strong>{' '}
                    {operator === 'IsSet'
                      ? 'has a value'
                      : `${operator.toLowerCase()} ${value || '…'}`}
                    , and the person is told: “{message || '…'}”
                  </p>
                </div>
              ) : null}

              <Button
                type="submit"
                tone="primary"
                disabled={name.trim() === '' || attribute === '' || message.trim() === ''}
              >
                Declare the rule
              </Button>
            </form>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}
