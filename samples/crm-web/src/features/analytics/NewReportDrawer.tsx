import { useState } from 'react'
import {
  Button,
  Drawer,
  DrawerSection,
  ErrorState,
  SelectField,
  TextField,
} from '@/design/primitives'
import { useDefineReport } from '@/api/queries/hooks'
import { useToast } from '@/app/ToastProvider'
import type { ReportMeasure, ReportSource } from '@/api/contracts'
import {
  dimensionsOf,
  measuresOf,
  REPORT_MEASURES,
  REPORT_SOURCES,
  takesAField,
} from './reportVocabulary'

/**
 * Saves a report an administrator built.
 *
 * A CLOSED VOCABULARY, WHICH IS WHY THE DIMENSION IS SAFE. Every field below is a name from the
 * source's own list — never free text. That is what lets the server bind the grouping rather than
 * assemble a statement from it, and it is the difference between a report builder and an
 * injection surface.
 *
 * COUNT TAKES NO FIELD, AND THE FORM STOPS OFFERING ONE. Sending a `measureOf` beside `Count` is
 * refused, and a form that left the last selection lying there would send it.
 */
export function NewReportDrawer({ onClose }: { onClose: () => void }) {
  const save = useDefineReport()
  const toast = useToast()

  const [source, setSource] = useState<ReportSource>('Opportunity')
  const [measure, setMeasure] = useState<ReportMeasure>('Count')
  const [name, setName] = useState('')
  const [label, setLabel] = useState('')
  const [dimension, setDimension] = useState(dimensionsOf('Opportunity')[0] ?? '')
  const [measureOf, setMeasureOf] = useState(measuresOf('Opportunity')[0] ?? '')

  const dimensions = dimensionsOf(source)
  const measures = measuresOf(source)
  const needsField = takesAField(measure)

  const ready = name.trim().length > 0 && label.trim().length > 0 && dimension.length > 0

  function submit() {
    save.mutate(
      {
        name: name.trim(),
        label: label.trim(),
        source,
        // This form does not build a custom-object report: those dimensions are whatever the
        // tenant declared, and offering the built-in list for them would be offering refusals.
        target: null,
        dimension,
        measure,
        measureOf: needsField ? measureOf : null,
      },
      {
        onSuccess: () => {
          toast.saved(`${label.trim()} saved — run it from the list.`)
          onClose()
        },
        onError: (error) => toast.failed(error, 'That report was refused.'),
      },
    )
  }

  return (
    <Drawer
      eyebrow="New"
      title="Build a report"
      subtitle="An object, a grouping and a measure — all from closed lists"
      onClose={onClose}
      actions={
        <>
          <Button tone="primary" disabled={!ready || save.isPending} onClick={submit}>
            {save.isPending ? 'Saving…' : 'Save'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </>
      }
    >
      <DrawerSection label="What to call it" />
      <TextField
        label="Name"
        value={name}
        required
        hint="What it is run by. Lower case, digits and underscores."
        onChange={(event) => setName(event.target.value)}
      />
      <TextField
        label="Label"
        value={label}
        required
        onChange={(event) => setLabel(event.target.value)}
      />

      <DrawerSection label="What it is about" />
      <SelectField
        label="Source"
        value={source}
        options={REPORT_SOURCES.map((option) => ({ value: option, label: option }))}
        onChange={(event) => {
          const next = event.target.value as ReportSource

          // Both selections belong to the old source and neither is valid for the new one.
          // Leaving them would send a dimension the server refuses, which reads as the form
          // being broken rather than as a stale choice.
          setSource(next)
          setDimension(dimensionsOf(next)[0] ?? '')
          setMeasureOf(measuresOf(next)[0] ?? '')
        }}
      />
      <SelectField
        label="Group by"
        value={dimension}
        options={dimensions.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setDimension(event.target.value)}
      />

      <DrawerSection label="How to reduce each group" />
      <SelectField
        label="Measure"
        value={measure}
        options={REPORT_MEASURES.map((option) => ({ value: option, label: option }))}
        onChange={(event) => setMeasure(event.target.value as ReportMeasure)}
      />
      {needsField ? (
        <SelectField
          label="Of"
          value={measureOf}
          options={measures.map((option) => ({ value: option, label: option }))}
          onChange={(event) => setMeasureOf(event.target.value)}
        />
      ) : (
        <p>Count takes no field — it is how many rows are in each group.</p>
      )}

      {save.isError ? <ErrorState error={save.error} onRetry={submit} /> : null}
    </Drawer>
  )
}
