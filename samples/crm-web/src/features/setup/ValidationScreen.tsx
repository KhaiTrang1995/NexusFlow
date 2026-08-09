import { useState } from 'react'
import {
  Button,
  Columns,
  ErrorState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Skeleton,
  TextAreaField,
  TextField,
} from '@/design/primitives'
import { useDefineValidationRule, useSchema } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { EntityKind, GuardOperator } from '@/api/contracts'
import { DeclaredList } from '@/config/DeclaredList'
import styles from './setup.module.css'

const OPERATORS: readonly GuardOperator[] = [
  'Equals',
  'NotEquals',
  'GreaterThan',
  'LessThan',
  'IsSet',
]

const ENTITIES: readonly EntityKind[] = ['Lead', 'Account', 'Contact', 'Opportunity']

/**
 * The six fields an opportunity's rule may name besides its declared ones.
 *
 * <strong>The server's `ProcessFields`, restated because there is no read for it.</strong> An
 * administrator who can guard a transition on `amount` expects to be able to validate on it, so
 * the capability allows both; every other entity has only what the tenant declared. The server
 * still decides — a name it does not know is refused there whatever this list says.
 */
const PROCESS_FIELDS: readonly string[] = [
  'amount',
  'currency',
  'probability',
  'region',
  'industry',
  'owner',
]

/**
 * Validation rules, declared for real.
 *
 * THE FORM WROTE NOTHING. Four rules were written out in this file and shown in a table headed
 * "Rules in force" — beside a real list of the tenant's own, and above a Declare button that
 * toasted "declared" and posted nothing. Somebody reading this screen had two lists claiming the
 * same thing and a control that agreed with neither.
 *
 * THE FIELD LIST IS WHAT THE OWNER ACTUALLY HAS. Declared fields come from `describe`; an
 * opportunity additionally offers the six built-in process fields, because that is what the
 * capability allows. Offering every field the prototype knows would be offering refusals.
 *
 * A RULE IS REFUSED WHEN IT *HOLDS*, WHICH READS BACKWARDS UNTIL YOU SAY IT ALOUD. So the preview
 * says it aloud, before anybody saves a rule that means the opposite of what they meant.
 */
export function ValidationScreen() {
  const schema = useSchema()
  const declare = useDefineValidationRule()
  const toast = useToast()

  const [appliesTo, setAppliesTo] = useState<EntityKind>('Opportunity')
  const [name, setName] = useState('')
  const [field, setField] = useState('')
  const [operator, setOperator] = useState<GuardOperator>('IsSet')
  const [value, setValue] = useState('')
  const [message, setMessage] = useState('')

  const declared = schema.data?.entities.find((entity) => entity.kind === appliesTo)?.fields ?? []

  const nameable = [
    ...(appliesTo === 'Opportunity' ? PROCESS_FIELDS : []),
    ...declared.map((entry) => entry.name),
  ]

  const chosen = declared.find((entry) => entry.name === field)
  const options = chosen?.options ?? []

  const ready = name.trim().length > 0 && field.length > 0 && message.trim().length > 0

  function submit() {
    declare.mutate(
      {
        appliesTo,
        // A rule belongs to a built-in entity or to a custom object, never both. This form
        // declares the first; the second needs an object id, which belongs on the object's page.
        target: null,
        name: name.trim(),
        field,
        operator,
        // IsSet compares against "true" or "false" rather than against a bound, so a value left
        // over from another operator would be sent as one.
        value: operator === 'IsSet' ? 'true' : value,
        message: message.trim(),
      },
      {
        onSuccess: (result) => {
          // Not `${kind}s`: "opportunitys" is what a naive plural produces, and this is the
          // sentence somebody reads to confirm the rule landed where they meant.
          toast.saved(`${result.name} is in force on every ${appliesTo.toLowerCase()}.`)
          setName('')
          setMessage('')
        },
        onError: (error) => toast.failed(error, 'That rule was refused.'),
      },
    )
  }

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Validation rules" />

      <Columns layout="split">
        {/* Each rule's sentence is the server's: it knows what an operator and a value mean. */}
        <DeclaredList
          kind="ValidationRule"
          title="Rules in force"
          empty="No rules are declared, so nothing is refused."
        />

        <Panel padding="flush">
          <PanelHeader title="Declare a rule" note="in your own words" />
          <PanelBody>
            {schema.isPending ? <Skeleton rows={5} /> : null}

            <form
              style={{ display: 'grid', gap: 12 }}
              onSubmit={(event) => {
                event.preventDefault()
                submit()
              }}
            >
              <SelectField
                label="On"
                value={appliesTo}
                options={ENTITIES.map((entry) => ({ value: entry, label: entry }))}
                onChange={(event) => {
                  // The field belongs to the old entity and is not nameable on the new one.
                  setAppliesTo(event.target.value as EntityKind)
                  setField('')
                  setValue('')
                }}
              />
              <TextField
                label="Name"
                required
                hint="Lower case, digits and underscores."
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              <SelectField
                label="Field"
                value={field}
                placeholder={
                  nameable.length === 0 ? 'Nothing is declared on this entity' : 'Choose a field'
                }
                onChange={(event) => {
                  setField(event.target.value)
                  setValue('')
                }}
                options={nameable.map((entry) => ({
                  value: entry,
                  label: declared.find((one) => one.name === entry)?.label ?? entry,
                }))}
              />
              <SelectField
                label="Operator"
                value={operator}
                onChange={(event) => setOperator(event.target.value as GuardOperator)}
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
              ) : null}
              <TextAreaField
                label="What to tell the person"
                required
                hint="Shown verbatim when the rule refuses. Say what to do, not that something failed."
                value={message}
                onChange={(event) => setMessage(event.target.value)}
              />

              {field.length > 0 ? (
                <div className={styles.readback}>
                  <div className={styles.sub} style={{ marginBottom: 4 }}>
                    Read it back
                  </div>
                  <p style={{ fontSize: 14 }}>
                    A {appliesTo.toLowerCase()} is <strong>refused</strong> when{' '}
                    <strong>{chosen?.label ?? field}</strong>{' '}
                    {operator === 'IsSet'
                      ? 'has a value'
                      : `${operator.toLowerCase()} ${value || '…'}`}
                    , and the person is told: “{message || '…'}”
                  </p>
                </div>
              ) : null}

              <Button type="submit" tone="primary" disabled={!ready || declare.isPending}>
                {declare.isPending ? 'Declaring…' : 'Declare the rule'}
              </Button>

              {declare.isError ? <ErrorState error={declare.error} onRetry={submit} /> : null}
            </form>
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}
