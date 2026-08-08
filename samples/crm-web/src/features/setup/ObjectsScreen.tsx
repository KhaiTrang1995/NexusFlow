import { useState } from 'react'
import {
  AsyncBoundary,
  Button,
  DataTable,
  ErrorState,
  Page,
  PageHeader,
  Panel,
  PanelBody,
  PanelHeader,
  Tag,
  TextField,
} from '@/design/primitives'
import { useDefineObject, useSchema } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import { isUsableName } from './fieldDraft'
import { schemaRows } from './schemaModel'
import type { SchemaRow } from './schemaModel'
import styles from './setup.module.css'

/**
 * Every object this tenant has, and what it carries.
 *
 * READ FROM `describe`, NOT FROM A LIST THIS CLIENT KEEPS. The whole claim the dynamic schema
 * makes is that an object declared at run time is indistinguishable from one this build ships —
 * and a setup screen listing seven objects it was compiled with would contradict that on the
 * first screen an administrator opens after declaring the eighth.
 *
 * AND NOW IT CAN DECLARE THE EIGHTH. This screen described the claim and offered no way to make
 * it: `/custom/objects` has been there throughout, the onboarding wizard's first step says "add
 * the ones it has that nobody else does", and there was no control anywhere that added one.
 */
export function ObjectsScreen() {
  const schema = useSchema()
  const declare = useDefineObject()
  const toast = useToast()

  const [name, setName] = useState('')
  const [label, setLabel] = useState('')

  const refusal =
    name.trim().length === 0 || label.trim().length === 0
      ? 'An object needs a name and a label.'
      : !isUsableName(name.trim())
        ? 'The name must start with a lower-case letter and hold only lower-case letters, digits and underscores.'
        : null

  function submit() {
    if (refusal !== null) {
      return
    }

    declare.mutate(
      { name: name.trim(), label: label.trim() },
      {
        onSuccess: (result) => {
          toast.saved(`${result.name} is now an object of this organisation.`)
          setName('')
          setLabel('')
        },
        onError: (error) => toast.failed(error, 'That object was refused.'),
      },
    )
  }

  return (
    <Page>
      <PageHeader eyebrow="Setup" title="Objects" />

      <AsyncBoundary query={schema} skeletonRows={6}>
        {(description) => {
          const rows = schemaRows(description)

          return (
            <Panel padding="flush" style={{ marginBottom: 'var(--section-gap)' }}>
              <PanelHeader
                title="Declared objects"
                note={`${rows.length} · schema v${description.version}`}
              />
              <DataTable
                caption="Objects"
                rows={rows}
                rowKey={(row) => row.key}
                columns={[
                  {
                    id: 'label',
                    header: 'Object',
                    cell: (row: SchemaRow) => (
                      <>
                        <span className={styles.link}>{row.label}</span>
                        <div className={styles.mono}>{row.key}</div>
                      </>
                    ),
                    sortValue: (row: SchemaRow) => row.label,
                  },
                  {
                    id: 'origin',
                    header: 'Origin',
                    // The distinction that matters: one shipped with the build and the other was
                    // typed into a form. Everything else about them is the same on purpose.
                    cell: (row: SchemaRow) =>
                      row.builtIn ? (
                        <Tag>built in</Tag>
                      ) : (
                        <Tag tone="accent">declared at run time</Tag>
                      ),
                    sortValue: (row: SchemaRow) => String(row.builtIn),
                  },
                  {
                    id: 'fields',
                    header: 'Fields',
                    numeric: true,
                    cell: (row: SchemaRow) => row.fields.length,
                    sortValue: (row: SchemaRow) => row.fields.length,
                  },
                  {
                    id: 'readable',
                    header: 'Readable by you',
                    numeric: true,
                    // A count that is lower than the one beside it is field-level security doing
                    // its job, and an administrator should be able to see that it is.
                    cell: (row: SchemaRow) => row.fields.filter((field) => field.canRead).length,
                    sortValue: (row: SchemaRow) =>
                      row.fields.filter((field) => field.canRead).length,
                  },
                  {
                    id: 'views',
                    header: 'Saved views',
                    cell: (row: SchemaRow) =>
                      row.views > 0 ? (
                        <Tag tone="accent">{row.views}</Tag>
                      ) : (
                        <span className={styles.sub}>none</span>
                      ),
                  },
                ]}
                empty="This tenant has no objects, which should be impossible."
              />
            </Panel>
          )
        }}
      </AsyncBoundary>

      <Panel padding="flush">
        <PanelHeader
          title="Declare an object"
          note="an entity this build has never heard of, added without a deployment"
        />
        <PanelBody>
          <form
            style={{ display: 'grid', gap: 12, maxWidth: 420 }}
            onSubmit={(event) => {
              event.preventDefault()
              submit()
            }}
          >
            <TextField
              label="Name"
              required
              hint="Lower case, letters, digits and underscores. Everything refers to it by this."
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
            <Button
              type="submit"
              tone="primary"
              disabled={refusal !== null || declare.isPending}
              {...(refusal !== null ? { title: refusal } : {})}
            >
              {declare.isPending ? 'Declaring…' : 'Declare the object'}
            </Button>

            {/*
              `crm.admin` and not `crm.write`: writing a lead and changing what a lead *is* are
              different acts. A caller without it is told so by the server, in the server's words,
              rather than shown a control that quietly achieves nothing.
            */}
            {declare.isError ? <ErrorState error={declare.error} onRetry={submit} /> : null}
          </form>
        </PanelBody>
      </Panel>
    </Page>
  )
}
