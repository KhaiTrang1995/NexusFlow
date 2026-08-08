/**
 * How numbers are written on screen.
 *
 * ONE MODULE, BECAUSE A CURRENCY WRITTEN TWO WAYS IS TWO NUMBERS. A dashboard showing $1.2M in a
 * tile and $1,240,000 in the table below it invites the reader to work out whether they agree.
 * Every amount in this application goes through here.
 */

const exact = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  maximumFractionDigits: 0,
})

/**
 * Whether there is a number to write.
 *
 * NaN AND INFINITY ARE NOT FIGURES ANYBODY MEANT. They arrive from a division by a total that
 * turned out to be zero, from a field that parsed to nothing, from `Number(undefined)` — and
 * every formatter below would otherwise render them: "$NaN", "Infinity%", "NaN late". That reads
 * as a broken screen rather than as a missing number, so the reader cannot tell which it is, and
 * the one thing it is definitely not is a measurement. It gets the same dash as a null.
 *
 * The predicate narrows the other branch to `number`, which is what the callers want; NaN is
 * itself a `number`, so nothing downstream is told a lie about the type.
 */
function absent(value: number | null | undefined): value is null | undefined {
  return value === null || value === undefined || !Number.isFinite(value)
}

/**
 * A tile's amount: $1.24M, $840k, $0.
 *
 * WRITTEN BY HAND RATHER THAN WITH `Intl` COMPACT NOTATION, WHICH KEEPS TRAILING ZEROS.
 * `Intl.NumberFormat` with `notation: 'compact'` renders 840,000 as "$840.00K"; the design writes
 * "$840k". Two decimal places on a headline number is exactly the noise a tile exists to remove,
 * so the trim is the point and not a nicety.
 */
export function money(value: number | null | undefined): string {
  if (absent(value)) return '—'
  if (value === 0) return '$0'

  const sign = value < 0 ? '-' : ''
  const size = Math.abs(value)

  if (size >= 1_000_000) return `${sign}$${trim((size / 1_000_000).toFixed(2))}M`
  if (size >= 1_000) return `${sign}$${trim((size / 1_000).toFixed(size >= 100_000 ? 0 : 1))}k`

  return `${sign}$${size}`
}

/**
 * Drops trailing zeros from the fractional part only. `1.20` → `1.2`, `1.00` → `1`, `840` → `840`.
 *
 * The guard matters: without it "840" loses its own zero and becomes "84", which is a formatter
 * that silently divides by ten on exactly the round numbers a headline tile is most likely to show.
 */
function trim(text: string): string {
  return text.includes('.') ? text.replace(/0+$/, '').replace(/\.$/, '') : text
}

/** A table cell's amount, in full. */
export function fullMoney(value: number | null | undefined): string {
  return absent(value) ? '—' : exact.format(value)
}

/**
 * The point at which a "fraction" is certainly a percentage somebody passed to the wrong one.
 *
 * 1000% IS THE LINE, NOT 100%. Attainment of 1.4 is a real fraction and reads 140%, so warning
 * above 1 would cry wolf on every seller over quota. Nothing in this application is a rate of ten
 * times over, and the defects that shipped were not near the line: a win rate of 58 rendered as
 * 5,800%, an open pipeline of 16,260 rendered as 1,626,000%.
 */
const IMPLAUSIBLE_FRACTION = 10

/**
 * A rate, given as a 0–1 fraction.
 *
 * **Null is not zero.** A campaign that reached nobody has no response rate; showing 0% sorts it
 * below one that reached a thousand people and converted one. The backend is careful to send null
 * for an empty denominator, and this is where that care would otherwise be thrown away.
 *
 * **A 0–100 field passed here is off by two orders of magnitude, and says so in development.**
 * `winRate` and `responseRate` are both `number | null` and both mean "a rate"; one is 0–100 and
 * the other is 0–1, and the compiler cannot tell them apart. The screen can: 1,626,000% is not a
 * number anybody has to interpret, but it only ever appeared on a board nobody was watching.
 */
export function percent(fraction: number | null | undefined, digits = 0): string {
  if (absent(fraction)) return '—'

  if (import.meta.env.DEV && Math.abs(fraction) >= IMPLAUSIBLE_FRACTION) {
    // eslint-disable-next-line no-console
    console.warn(
      `percent(${fraction}) renders ${(fraction * 100).toFixed(0)}% — percent takes a 0–1 fraction. A 0–100 value wants pct().`,
    )
  }

  return `${(fraction * 100).toFixed(digits)}%`
}

/** A percentage already expressed 0–100. */
export function pct(value: number | null | undefined, digits = 0): string {
  return absent(value) ? '—' : `${value.toFixed(digits)}%`
}

/** A count, grouped. */
export function count(value: number | null | undefined): string {
  return absent(value) ? '—' : value.toLocaleString('en-US')
}

/** A signed delta, so "up four" and "down four" are visibly different. */
export function delta(value: number | null | undefined, unit = ''): string {
  if (absent(value)) return '—'
  const arrow = value > 0 ? '▲' : value < 0 ? '▼' : '—'
  return `${arrow} ${Math.abs(value)}${unit}`
}

const day = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })
const dayShort = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short' })
const clock = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit' })

export function date(value: string | Date | null | undefined): string {
  if (!value) return '—'
  const parsed = value instanceof Date ? value : new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : day.format(parsed)
}

export function dateTime(value: string | Date | null | undefined): string {
  if (!value) return '—'
  const parsed = value instanceof Date ? value : new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : `${dayShort.format(parsed)} ${clock.format(parsed)}`
}

/**
 * How long from now, signed.
 *
 * **Negative means late, and says so** rather than clamping at zero. "Four hours late" and "due
 * now" are the same string to anything that clamps, and they are not the same situation.
 */
export function fromNow(minutes: number | null | undefined): string {
  if (absent(minutes)) return '—'
  const late = minutes < 0
  const total = Math.abs(Math.round(minutes))
  const text =
    total < 60
      ? `${total}m`
      : total < 60 * 24
        ? `${Math.round(total / 60)}h`
        : `${Math.round(total / (60 * 24))}d`
  return late ? `${text} late` : `in ${text}`
}

/**
 * A width for a meter, clamped so a number over target cannot overflow its track.
 *
 * A non-finite value is not a short bar, it is no bar: `width: NaN%` is an invalid declaration
 * the browser drops, and a fill div with no width of its own is a fill div at its container's
 * width — a full meter, drawn from a number that does not exist. `Meter` refuses to draw a
 * proportion at all in that case; this is the second lock on the same door.
 */
export function widthOf(value: number, target: number): string {
  if (!Number.isFinite(value) || !Number.isFinite(target) || target <= 0) return '0%'
  return `${Math.max(0, Math.min(100, Math.round((value / target) * 100)))}%`
}
