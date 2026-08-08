import { useState } from 'react'
import {
  Button,
  Columns,
  EmptyState,
  ErrorState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  SelectField,
  Skeleton,
  TextField,
} from '@/design/primitives'
import { useDefineListView, useSchema } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { GuardOperator } from '@/api/contracts'
import { DeclaredList } from './DeclaredList'
import styles from './setup.module.css'

const OPERATORS: readonly GuardOperator[] = [
  'Equals',
  'NotEquals',
  'GreaterThan',
  'LessThan',
  'IsSet',
]

/**
 * Saved list views, saved for real.
 *
 * SIX VIEWS WERE WRITTEN OUT IN THIS FILE — "my open deals", "commit board", "overdue tasks" —
 * shown as though they were the tenant's, above a Save that toasted and posted nothing. The
 * backend has had `/custom/list-views` throughout.
 *
 * A VIEW BELONGS TO A CUSTOM OBJECT, WHICH IS WHY THE PICKER IS OF OBJECTS AND NOT OF SCREENS.
 * `target` is an object id: the built-in entities have list screens of their own and no saved
 * views, and a form offering them would offer a write the server has nowhere to put. A tenant
 * that has declared no objects is told that rather than shown an empty drop-down.
 *
 * FIVE OPERATORS AND NOTHING ELSE. The criterion is the same closed vocabulary the transition
 * guards, validation rules, roll-up filters, territory rules and approval criteria all use — one
 * evaluator, six features. That is why it is never assembled into SQL, and why this can be a
 * picker rather than a text box that accepts anything.
 */
export function ListViewScreen() {
  const schema = useSchema()
  const save = useDefineListView()
  const toast = useToast()

  const objects = schema.data?.objects ?? []

  const [target, setTarget] = useState('')
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [field, setField] = useState('')
  const [operator, setOperator] = useState<GuardOperator>('Equals')
  const [value, setValue] = useState('')

  const chosen = objects.find((object) => object.id === target) ?? objects[0]
  const fields = chosen?.fields ?? []
  const picked = fields.find((entry) => entry.name === field)

  const ready = chosen !== undefined && name.trim().length > 0 && label.trim().length > 0

  function submit() {
    if (chosen === undefined) {
      return
    }

    save.mutate(
      {
        target: chosen.id,
        name: name.trim(),
        label: label.trim(),
        // A view with no criterion is every row of the object, which is a legitimate view and
        // not a missing filter. Null says that; an empty criteria list would say it too, and one
        // of the two is what the server reads.
        filter:
          field.length === 0
            ? null
            : {
                match: 'All',
                criteria: [
                  { field, operator, value: operator === 'IsSet' ? 'true' : value },
                ],
              },
        order: null,
        limit: 50,
      },
      {
        onSuccess: (result) => {
          toast.saved(`${result.name} saved on ${chosen.label}.`)
          setName('')
          setLabel('')
        },
        onError: (error) => toast.failed(error, 'That view was refused.'),
      },
    )
  }

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="List views" />

      <Columns layout="split">
        <DeclaredList
          kind="ListView"
          title="Saved views"
          empty="No views are saved. Every list is the whole object until one is."
        />

        <Panel padding="flush">
          <PanelHeader title="Save a view" note="its fields are checked when it is saved" />
          <PanelBody>
            {schema.isPending ? <Skeleton rows={5} /> : null}

            {schema.isSuccess && objects.length === 0 ? (
              <EmptyState
                title="Nothing has been declared to build a view over"
                detail="A list view belongs to a custom object. Declare one in setup and it appears here."
              />
            ) : null}

            {chosen !== undefined ? (
              <form
                style={{ display: 'grid', gap: 12 }}
                onSubmit={(event) => {
                  event.preventDefault()
                  submit()
                }}
              >
                <SelectField
                  label="Object"
                  value={chosen.id}
                  options={objects.map((object) => ({ value: object.id, label: object.label }))}
                  onChange={(event) => {
                    // The field belongs to the old object and is not one of the new one's.
                    setTarget(event.target.value)
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
                <TextField
                  label="Label"
                  required
                  value={label}
                  onChange={(event) => setLabel(event.target.value)}
                />

                <SelectField
                  label="Keep rows where"
                  value={field}
                  placeholder="Every row — no criterion"
                  options={fields.map((entry) => ({ value: entry.name, label: entry.label }))}
                  onChange={(event) => {
                    setField(event.target.value)
                    setValue('')
                  }}
                />
                {field.length > 0 ? (
                  <>
                    <SelectField
                      label="Operator"
                      value={operator}
                      options={OPERATORS.map((entry) => ({ value: entry, label: entry }))}
                      onChange={(event) => setOperator(event.target.value as GuardOperator)}
                    />
                    {operator !== 'IsSet' ? (
                      (picked?.options.length ?? 0) > 0 ? (
                        <SelectField
                          label="Value"
                          value={value}
                          placeholder="Choose a value"
                          options={(picked?.options ?? []).map((entry) => ({
                            value: entry,
                            label: entry,
                          }))}
                          onChange={(event) => setValue(event.target.value)}
                        />
                      ) : (
                        <TextField
                          label="Value"
                          value={value}
                          onChange={(event) => setValue(event.target.value)}
                        />
                      )
                    ) : null}
                  </>
                ) : null}

                <div className={styles.readback}>
                  <div className={styles.sub} style={{ marginBottom: 4 }}>
                    Read it back
                  </div>
                  <p style={{ fontSize: 14 }}>
                    {field.length === 0 ? (
                      <>
                        Every <strong>{chosen.label}</strong>, up to fifty rows.
                      </>
                    ) : (
                      <>
                        Every <strong>{chosen.label}</strong> whose{' '}
                        <strong>{picked?.label ?? field}</strong>{' '}
                        {operator === 'IsSet'
                          ? 'has a value'
                          : `${operator.toLowerCase()} ${value || '…'}`}
                        , up to fifty rows.
                      </>
                    )}
                  </p>
                </div>

                <Button type="submit" tone="primary" disabled={!ready || save.isPending}>
                  {save.isPending ? 'Saving…' : 'Save the view'}
                </Button>

                {save.isError ? <ErrorState error={save.error} onRetry={submit} /> : null}
              </form>
            ) : null}
          </PanelBody>
        </Panel>
      </Columns>
    </Page>
  )
}
