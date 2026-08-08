import { useId } from 'react'
import type { ReactNode } from 'react'
import { cx } from '@/lib/cx'
import styles from './Charts.module.css'

/**
 * Turns values into heights.
 *
 * **The scale comes from the whole series, never from one column.** A bar sized against its own
 * value is a bar of constant height, which is the bug that makes a chart look plausible and mean
 * nothing. Exported because every chart here needs it and one of them would otherwise inline it.
 *
 * **Nothing is drawn as nothing.** The floor exists so that a small value is still visible, and
 * applied to a zero it drew a block for a number nobody has: a stage with no pipeline in it got
 * the same eighteen pixels as a stage with a thousand pounds, and a period in which every figure
 * was zero came out as a tidy row of equal bars. A value that is not a finite number is not a
 * short bar either — `height: NaN` is a declaration the browser drops, and a block with no height
 * of its own takes its container's.
 */
export function scaleTo(values: readonly number[], pixels: number, floor = 4): (v: number) => number {
  const max = Math.max(1, ...values.filter((value) => Number.isFinite(value)).map((value) => Math.abs(value)))

  return (value) => {
    if (!Number.isFinite(value) || value === 0) return 0
    return Math.max(floor, Math.round((Math.abs(value) / max) * pixels))
  }
}

/** What a segment is worth on screen. A value nobody sent, or one that is not a number, is none. */
function size(value: number | undefined): number {
  return value !== undefined && Number.isFinite(value) && value > 0 ? value : 0
}

/** The same value in the hidden table, where "nothing was sent" and "nought" must stay apart. */
function written(value: number | undefined): string {
  return value === undefined || !Number.isFinite(value) ? '—' : String(value)
}

/**
 * A chart with nothing to draw says so, and says which chart it is.
 *
 * An empty plot area is indistinguishable from one that failed to render, and a panel holding
 * three charts gives no clue which of them came back empty. The caption already names it.
 */
function NoData({ caption }: { caption: string }) {
  return (
    <figure style={{ margin: 0 }}>
      <p className={styles.noData}>{caption}: nothing to draw.</p>
    </figure>
  )
}

export interface Series {
  label: string
  colour: string
  /** What share of the whole this series is, already formatted. */
  share?: string
}

export function Legend({ series }: { series: readonly Series[] }) {
  return (
    <div className={styles.legend}>
      {series.map((item) => (
        <div key={item.label} className={styles.legendItem}>
          <span className={styles.swatch} style={{ background: item.colour }} aria-hidden="true" />
          <span>{item.label}</span>
          {item.share ? <span className={styles.legendShare}>{item.share}</span> : null}
        </div>
      ))}
    </div>
  )
}

export interface StackedBar {
  label: string
  /** One number per series, in the same order as `series`. */
  values: readonly number[]
  /** The total, already formatted. */
  readout: string
  /** What the whole column says, on hover. */
  tip?: string
  /** Marks the column as the one being looked at. */
  emphasis?: boolean
}

export interface StackedBarsProps {
  caption: string
  series: readonly Series[]
  bars: readonly StackedBar[]
  height?: number
}

/**
 * Stacked columns.
 *
 * **A `<table>` behind it, hidden.** A chart is a picture of a table, and this is the only way a
 * reader who cannot see it gets the numbers rather than a shrug. It costs nine lines.
 *
 * **A row is as wide as the series, whatever the caller passed.** A bar with fewer values than
 * there are series used to drop the tail off the picture and off its row in the hidden table,
 * where the remaining cells then slid left under the wrong column headers — the accessible
 * reading of the chart was not merely poorer than the picture, it was different. A missing value
 * is drawn as no segment and written as a dash, which is what it is.
 */
export function StackedBars({ caption, series, bars, height = 118 }: StackedBarsProps) {
  if (bars.length === 0) return <NoData caption={caption} />

  // One cell per series per bar. `undefined` is a value the caller did not send; it is not zero,
  // and the two are not written the same way below.
  const rows = bars.map((bar) => series.map((_, index) => bar.values[index]))
  const totals = rows.map((row) => row.reduce<number>((sum, value) => sum + size(value), 0))
  const scale = scaleTo(totals, height - 34)

  if (import.meta.env.DEV) {
    const ragged = bars.find((bar) => bar.values.length !== series.length)
    if (ragged) {
      // eslint-disable-next-line no-console
      console.warn(
        `StackedBars: "${ragged.label}" has ${ragged.values.length} value(s) for ${series.length} series. The chart can only draw what the legend names.`,
      )
    }
  }

  return (
    <figure style={{ margin: 0 }}>
      <Legend series={series} />
      <div className={styles.bars} style={{ '--chart-height': `${height}px` } as React.CSSProperties}>
        {bars.map((bar, index) => (
          <div key={bar.label} className={styles.barCol} title={bar.tip ?? `${bar.label}: ${bar.readout}`}>
            <div className={styles.barValue}>{bar.readout}</div>
            <div className={styles.barStack} style={{ height: scale(totals[index] ?? 0) }}>
              {(rows[index] ?? []).map((value, part) => (
                <div
                  key={series[part]?.label ?? part}
                  style={{
                    background: series[part]?.colour ?? 'var(--color-accent)',
                    flexGrow: size(value),
                    minHeight: size(value) > 0 ? 2 : 0,
                  }}
                />
              ))}
            </div>
            <div
              className={styles.barLabel}
              style={bar.emphasis ? { color: 'var(--color-text)', fontWeight: 500 } : undefined}
            >
              {bar.label}
            </div>
          </div>
        ))}
      </div>
      <ChartTable
        caption={caption}
        columns={['', ...series.map((s) => s.label), 'Total']}
        rows={bars.map((bar, index) => [
          bar.label,
          ...(rows[index] ?? []).map(written),
          // The total is on the picture, over every column. Leaving it out of the table meant the
          // one number the chart shouts was the one number the hidden copy did not have.
          bar.readout,
        ])}
      />
    </figure>
  )
}

export interface WaterfallStep {
  label: string
  /** The change this step makes. Positive adds, negative takes away. */
  delta: number
  /** A step that sits on the baseline rather than floating — the opening and closing balances. */
  anchor?: boolean
  readout: string
}

export interface WaterfallProps {
  caption: string
  steps: readonly WaterfallStep[]
  height?: number
}

/**
 * Where a balance went.
 *
 * The floating blocks are computed from a running total rather than given, because a caller
 * passing both the delta and the position is a caller who can make them disagree.
 */
export function Waterfall({ caption, steps, height = 104 }: WaterfallProps) {
  if (steps.length === 0) return <NoData caption={caption} />

  let running = 0
  const placed = steps.map((step) => {
    // A step whose change is not a number moves the running total nowhere. Letting NaN into the
    // total poisons every step after it and the ceiling with it, so the whole chart collapses to
    // one flat row of minimum-height blocks — a picture of a balance that never moved.
    const delta = Number.isFinite(step.delta) ? step.delta : 0
    const from = step.anchor ? 0 : running
    const to = step.anchor ? delta : running + delta
    running = step.anchor ? delta : to
    return { step, from, to }
  })

  const ceiling = Math.max(1, ...placed.flatMap((p) => [Math.abs(p.from), Math.abs(p.to)]))
  const px = (value: number) => Math.round((Math.abs(value) / ceiling) * (height - 18))

  return (
    <figure style={{ margin: 0 }}>
      <div
        className={styles.waterfall}
        style={{ '--chart-height': `${height}px` } as React.CSSProperties}
      >
        <div className={styles.baseline} />
        <div className={styles.wfTrack}>
          {placed.map(({ step, from, to }) => {
            const bottom = px(Math.min(from, to))
            const size = Math.max(3, px(Math.max(from, to)) - bottom)
            const colour = step.anchor
              ? 'var(--color-accent-800)'
              : step.delta >= 0
                ? 'var(--color-accent)'
                : 'var(--color-neutral-500)'

            return (
              <div key={step.label} className={styles.wfCol} title={`${step.label}: ${step.readout}`}>
                <div
                  className={styles.wfValue}
                  style={{
                    bottom: bottom + size + 4,
                    color: step.delta >= 0 ? 'var(--color-accent-800)' : 'var(--color-neutral-700)',
                  }}
                >
                  {step.readout}
                </div>
                <div className={styles.wfBlock} style={{ bottom, height: size, background: colour }} />
              </div>
            )
          })}
        </div>
      </div>
      <div className={styles.wfAxis}>
        {steps.map((step) => (
          <div
            key={step.label}
            className={styles.wfAxisLabel}
            style={{ color: step.anchor ? 'var(--color-text)' : 'var(--color-neutral-600)' }}
          >
            {step.label}
          </div>
        ))}
      </div>
      <ChartTable
        caption={caption}
        columns={['Step', 'Change']}
        rows={steps.map((step) => [step.label, step.readout])}
      />
    </figure>
  )
}

export interface FunnelStage {
  name: string
  /** The bar's size. */
  value: number
  /** The amount, already formatted. */
  amount: string
  /** What sits under the name — a count and a probability. */
  meta: string
}

/**
 * The pipeline, stage by stage.
 *
 * **A block that opens something is a button.** It was a `<div>` with an `onClick` and a pointer
 * cursor: live to a mouse, invisible to the keyboard and to anybody being read the page, which is
 * the exact shape of a control that looks like it works and does nothing when it is used.
 *
 * **A stage worth nothing draws no block.** The floor under the scale kept every empty stage
 * eighteen pixels tall, so a pipeline with nothing in three of its stages was drawn as a pipeline
 * with something in all of them.
 */
export function Funnel({
  caption,
  stages,
  height = 210,
  onSelect,
}: {
  caption: string
  stages: readonly FunnelStage[]
  height?: number
  onSelect?: (name: string) => void
}) {
  if (stages.length === 0) return <NoData caption={caption} />

  const scale = scaleTo(
    stages.map((stage) => stage.value),
    height - 92,
    18,
  )

  return (
    <figure style={{ margin: 0 }}>
      <div className={styles.funnel} style={{ '--chart-height': `${height}px` } as React.CSSProperties}>
        {stages.map((stage) => {
          const block = scale(stage.value)

          return (
            <div key={stage.name} className={styles.funnelCol}>
              <div className={styles.funnelAmount}>{stage.amount}</div>
              {block > 0 ? (
                onSelect ? (
                  <button
                    type="button"
                    className={cx(styles.funnelBlock, styles.funnelHit)}
                    style={{ height: block }}
                    onClick={() => onSelect(stage.name)}
                    title={`${stage.name}: ${stage.amount}`}
                    aria-label={`${stage.name}: ${stage.amount}`}
                  />
                ) : (
                  <div
                    className={styles.funnelBlock}
                    style={{ height: block }}
                    title={`${stage.name}: ${stage.amount}`}
                  />
                )
              ) : null}
              <div className={styles.funnelFoot}>
                <div className={styles.funnelName}>{stage.name}</div>
                <div className={styles.funnelMeta}>{stage.meta}</div>
              </div>
            </div>
          )
        })}
      </div>
      <ChartTable
        caption={caption}
        columns={['Stage', 'Amount', 'Detail']}
        rows={stages.map((stage) => [stage.name, stage.amount, stage.meta])}
      />
    </figure>
  )
}

/**
 * A trend, drawn small.
 *
 * **No readings is not a flat line, and one reading is not a trend.** An empty series produced a
 * `<polyline>` with no points — a blank box under a heading promising a trend, which reads as a
 * chart that failed. A single reading produced a one-point line, which SVG draws as nothing at
 * all. Both now say what they have: the title carries it, because that is the whole accessible
 * text of this chart.
 */
export function Sparkline({
  caption,
  values,
  height = 38,
  colour = 'var(--color-accent)',
}: {
  caption: string
  values: readonly number[]
  height?: number
  colour?: string
}) {
  const id = useId()

  // A reading that is not a number is dropped rather than written into the path: one NaN in the
  // points list invalidates the attribute, and the browser drops the whole line silently.
  const readings = values.filter((value) => Number.isFinite(value))
  const max = Math.max(1, ...readings)
  const min = Math.min(0, ...readings)
  const span = max - min || 1
  const step = readings.length > 1 ? 100 / (readings.length - 1) : 0

  const points = readings
    .map((value, index) => `${(index * step + (readings.length === 1 ? 50 : 0)).toFixed(2)},${(100 - ((value - min) / span) * 100).toFixed(2)}`)
    .join(' ')

  const title =
    readings.length === 0
      ? `${caption}: no readings`
      : readings.length === 1
        ? `${caption}: one reading`
        : caption

  return (
    <svg
      className={styles.spark}
      viewBox="0 0 100 100"
      preserveAspectRatio="none"
      role="img"
      aria-labelledby={id}
      style={{ '--chart-height': `${height}px` } as React.CSSProperties}
    >
      <title id={id}>{title}</title>
      {readings.length > 0 ? (
        <polyline
          // A single reading is drawn as a round cap on a zero-length line: a dot in the middle,
          // which is a point and does not pretend to be a direction.
          points={readings.length === 1 ? `${points} ${points}` : points}
          fill="none"
          stroke={colour}
          strokeWidth="2.5"
          vectorEffect="non-scaling-stroke"
          strokeLinejoin="round"
          strokeLinecap="round"
        />
      ) : null}
    </svg>
  )
}

/** The numbers behind a chart, for anybody who cannot see it. */
function ChartTable({
  caption,
  columns,
  rows,
}: {
  caption: string
  columns: readonly string[]
  rows: readonly (readonly string[])[]
}) {
  return (
    <table className="sr-only">
      <caption>{caption}</caption>
      <thead>
        <tr>
          {columns.map((column, index) => (
            <th key={index} scope="col">
              {column}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, index) => (
          <tr key={index}>
            {row.map((cell, cellIndex) => (
              <td key={cellIndex}>{cell}</td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  )
}

/**
 * A row of coloured segments summing to one, for a share-of-total bar.
 *
 * **A total of nothing has no shares.** The divisor was `total || 1`, which turns an empty total
 * into a real one: every part came out at 0% and the bar announced "Won 0%, Lost 0%" — a
 * statement about a quarter in which nothing has been decided yet, dressed as a measurement. It
 * says it has nothing instead.
 *
 * **A negative part is not a share of anything**, and left in the sum it shrinks the divisor
 * until the other segments run past the end of the track.
 */
export function ShareBar({
  caption,
  parts,
  height = 11,
  className,
}: {
  caption: string
  parts: readonly { label: string; value: number; colour: string }[]
  height?: number
  className?: string
}): ReactNode {
  const drawn = parts.filter((part) => Number.isFinite(part.value) && part.value > 0)
  const total = drawn.reduce((a, b) => a + b.value, 0)
  const share = (value: number) => Math.round((value / total) * 100)

  return (
    <div
      className={cx(className)}
      role="img"
      aria-label={
        total > 0
          ? `${caption}: ${drawn.map((p) => `${p.label} ${share(p.value)}%`).join(', ')}`
          : `${caption}: nothing recorded`
      }
      style={{ display: 'flex', height, borderRadius: 4, overflow: 'hidden', background: 'var(--color-neutral-200)' }}
    >
      {drawn.map((part) => (
        <div
          key={part.label}
          title={`${part.label}: ${share(part.value)}%`}
          style={{ background: part.colour, width: `${(part.value / total) * 100}%` }}
        />
      ))}
    </div>
  )
}
