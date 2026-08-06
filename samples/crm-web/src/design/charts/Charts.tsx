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
 */
export function scaleTo(values: readonly number[], pixels: number, floor = 4): (v: number) => number {
  const max = Math.max(1, ...values.map((value) => Math.abs(value)))
  return (value) => Math.max(floor, Math.round((Math.abs(value) / max) * pixels))
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
 */
export function StackedBars({ caption, series, bars, height = 118 }: StackedBarsProps) {
  const totals = bars.map((bar) => bar.values.reduce((a, b) => a + b, 0))
  const scale = scaleTo(totals, height - 34)

  return (
    <figure style={{ margin: 0 }}>
      <Legend series={series} />
      <div className={styles.bars} style={{ '--chart-height': `${height}px` } as React.CSSProperties}>
        {bars.map((bar, index) => (
          <div key={bar.label} className={styles.barCol} title={bar.tip ?? `${bar.label}: ${bar.readout}`}>
            <div className={styles.barValue}>{bar.readout}</div>
            <div className={styles.barStack} style={{ height: scale(totals[index] ?? 0) }}>
              {bar.values.map((value, part) => (
                <div
                  key={series[part]?.label ?? part}
                  style={{
                    background: series[part]?.colour ?? 'var(--color-accent)',
                    flexGrow: value,
                    minHeight: value > 0 ? 2 : 0,
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
        columns={['', ...series.map((s) => s.label)]}
        rows={bars.map((bar) => [bar.label, ...bar.values.map(String)])}
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
  let running = 0
  const placed = steps.map((step) => {
    const from = step.anchor ? 0 : running
    const to = step.anchor ? step.delta : running + step.delta
    if (!step.anchor) running = to
    else running = step.delta
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
  const scale = scaleTo(
    stages.map((stage) => stage.value),
    height - 92,
    18,
  )

  return (
    <figure style={{ margin: 0 }}>
      <div className={styles.funnel} style={{ '--chart-height': `${height}px` } as React.CSSProperties}>
        {stages.map((stage) => (
          <div key={stage.name} className={styles.funnelCol}>
            <div className={styles.funnelAmount}>{stage.amount}</div>
            <div
              className={styles.funnelBlock}
              style={{ height: scale(stage.value), cursor: onSelect ? 'pointer' : undefined }}
              onClick={onSelect ? () => onSelect(stage.name) : undefined}
              title={`${stage.name}: ${stage.amount}`}
            />
            <div className={styles.funnelFoot}>
              <div className={styles.funnelName}>{stage.name}</div>
              <div className={styles.funnelMeta}>{stage.meta}</div>
            </div>
          </div>
        ))}
      </div>
      <ChartTable
        caption={caption}
        columns={['Stage', 'Amount', 'Detail']}
        rows={stages.map((stage) => [stage.name, stage.amount, stage.meta])}
      />
    </figure>
  )
}

/** A trend, drawn small. */
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
  const max = Math.max(1, ...values)
  const min = Math.min(0, ...values)
  const span = max - min || 1
  const step = values.length > 1 ? 100 / (values.length - 1) : 100

  const points = values
    .map((value, index) => `${(index * step).toFixed(2)},${(100 - ((value - min) / span) * 100).toFixed(2)}`)
    .join(' ')

  return (
    <svg
      className={styles.spark}
      viewBox="0 0 100 100"
      preserveAspectRatio="none"
      role="img"
      aria-labelledby={id}
      style={{ '--chart-height': `${height}px` } as React.CSSProperties}
    >
      <title id={id}>{caption}</title>
      <polyline
        points={points}
        fill="none"
        stroke={colour}
        strokeWidth="2.5"
        vectorEffect="non-scaling-stroke"
        strokeLinejoin="round"
        strokeLinecap="round"
      />
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

/** A row of coloured segments summing to one, for a share-of-total bar. */
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
  const total = parts.reduce((a, b) => a + b.value, 0) || 1

  return (
    <div
      className={cx(className)}
      role="img"
      aria-label={`${caption}: ${parts.map((p) => `${p.label} ${Math.round((p.value / total) * 100)}%`).join(', ')}`}
      style={{ display: 'flex', height, borderRadius: 4, overflow: 'hidden', background: 'var(--color-neutral-200)' }}
    >
      {parts.map((part) => (
        <div
          key={part.label}
          title={`${part.label}: ${Math.round((part.value / total) * 100)}%`}
          style={{ background: part.colour, width: `${(part.value / total) * 100}%` }}
        />
      ))}
    </div>
  )
}
