import type { ReportMeasure, ReportSource } from '@/api/contracts'

/**
 * What each report source may be grouped by and aggregated over.
 *
 * <strong>The same closed lists the server holds in `ReportVocabulary`, and that duplication is
 * the cost of not having a read for them.</strong> The alternative is a form offering every column
 * a client can imagine and finding out at declaration which ones exist — a drop-down whose entries
 * are refusals. The server is still the one that decides: a name it does not know is refused there
 * whatever this file says, so the two can disagree without a wrong report ever being saved.
 *
 * <strong>`CustomObject` is absent on purpose.</strong> Its dimensions are whatever the tenant
 * declared, so they are read from the schema rather than listed; this build's form does not offer
 * it rather than offering it and guessing.
 */
export const REPORT_SOURCES: readonly ReportSource[] = ['Opportunity', 'Lead', 'Activity']

const DIMENSIONS: Readonly<Record<string, readonly string[]>> = {
  Opportunity: ['Outcome', 'Currency', 'Probability'],
  Lead: ['Source', 'Status'],
  Activity: ['Kind', 'Status', 'RelatesToKind'],
}

const MEASURES: Readonly<Record<string, readonly string[]>> = {
  Opportunity: ['Amount', 'Probability'],
  Lead: ['Score'],
  Activity: ['EscalationCount'],
}

export function dimensionsOf(source: ReportSource): readonly string[] {
  return DIMENSIONS[source] ?? []
}

export function measuresOf(source: ReportSource): readonly string[] {
  return MEASURES[source] ?? []
}

/** Every way of reducing a group. `Count` is the one that takes no field. */
export const REPORT_MEASURES: readonly ReportMeasure[] = ['Count', 'Sum', 'Average', 'Min', 'Max']

export function takesAField(measure: ReportMeasure): boolean {
  return measure !== 'Count'
}
