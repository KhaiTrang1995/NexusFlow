import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * Every colour this design system writes words in, against the ground it writes them on.
 *
 * <strong>A ramp is not a palette until somebody checks it.</strong> The tokens were picked for
 * how the ramp looks laid out in a row, which is the one context none of them is ever used in.
 * Drawn as twelve-pixel text on a white panel, `--color-neutral-600` reached 3.6:1 — under the
 * 4.5:1 that ordinary text needs — and that token is the colour of the second line under every
 * record name, every stat tile's label, every table's empty sentence, every field hint and every
 * chart's axis: fifty-five declarations, all of them the quiet half of a pair where the loud half
 * is a name and the quiet half is what the name is about.
 *
 * <strong>Which is the same defect as a meter drawn from no target.</strong> The fact was given
 * to the component and the component put it somewhere a reader cannot get it back out of. It
 * differs only in that no assertion about behaviour can see it, so this arithmetic is the
 * assertion.
 *
 * <strong>Contrast is computed, not eyeballed.</strong> WCAG 2.1 relative luminance, the same
 * formula a browser's auditor uses, so the numbers here are the numbers a report will quote.
 */
const TOKENS = join(__dirname, '..', 'tokens.css')

/** The declared value of one custom property, from the token file itself. */
function token(name: string): string {
  const found = new RegExp(`--color-${name}:\\s*(#[0-9a-fA-F]{6})`).exec(readFileSync(TOKENS, 'utf8'))
  if (!found?.[1]) throw new Error(`--color-${name} is not a hex token in tokens.css`)
  return found[1]
}

/** WCAG relative luminance of an sRGB hex. */
function luminance(hex: string): number {
  const channel = (offset: number) => {
    const value = Number.parseInt(hex.slice(offset, offset + 2), 16) / 255
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4
  }

  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5)
}

function contrast(foreground: string, background: string): number {
  const [light, dark] = [luminance(foreground), luminance(background)].sort((a, b) => b - a)
  return ((light ?? 0) + 0.05) / ((dark ?? 0) + 0.05)
}

/**
 * The pairs this system actually draws, read off the modules that draw them.
 *
 * Two grounds, because a panel is white and the page behind it is not, and the page ground is
 * the harder of the two. Everything below appears as `color:` on that `background:` somewhere in
 * `src/design`; nothing is listed speculatively, and a pair that stops being drawn should leave.
 */
const TEXT: readonly { on: string; use: string; drawn: string }[] = [
  { use: 'text', on: 'surface', drawn: 'every primary line' },
  { use: 'text', on: 'bg', drawn: 'a heading outside a panel' },
  { use: 'neutral-600', on: 'surface', drawn: 'the second line under a record name' },
  { use: 'neutral-600', on: 'bg', drawn: 'a page eyebrow' },
  { use: 'neutral-700', on: 'surface', drawn: "a stat tile's footnote" },
  { use: 'neutral-700', on: 'bg', drawn: "the filter bar's trailing note" },
  { use: 'neutral-800', on: 'surface', drawn: 'an outline tag' },
  { use: 'neutral-800', on: 'neutral-200', drawn: 'a neutral tag' },
  { use: 'accent-700', on: 'surface', drawn: 'a link in a panel' },
  { use: 'accent-800', on: 'accent-100', drawn: 'an accent tag' },
  { use: 'neutral-100', on: 'accent-700', drawn: 'a pressed filter chip' },
  { use: 'positive', on: 'positive-bg', drawn: 'a Won tag' },
  { use: 'warning', on: 'warning-bg', drawn: 'an At risk tag' },
  { use: 'critical', on: 'critical-bg', drawn: 'a Breached tag' },
  { use: 'positive', on: 'surface', drawn: 'an upward delta' },
  { use: 'critical', on: 'surface', drawn: 'a downward delta' },
  { use: 'warning', on: 'surface', drawn: "a waterfall step's label" },
]

describe('the token ramp, as text', () => {
  it('is readable on both grounds', () => {
    const thin = TEXT.filter(({ use, on }) => contrast(token(use), token(on)) < 4.5).map(
      ({ use, on, drawn }) =>
        `--color-${use} on --color-${on} (${drawn}): ${contrast(token(use), token(on)).toFixed(2)}:1`,
    )

    // 4.5:1 and not 3:1, because none of these is large text: the tokens that fail are the ones
    // used at twelve and thirteen pixels, which is the size the smaller half of a pair is drawn at.
    expect(thin, `under 4.5:1 for ordinary text:\n${thin.join('\n')}`).toEqual([])
  })

  /**
   * The ramp still reads as a ramp.
   *
   * Darkening a step to clear the threshold is only a fix if the step above it stays darker. A
   * ramp that has collapsed two of its steps into one value is a ramp with a step nobody can use,
   * and the next person to reach for a "quieter grey" picks the same colour twice.
   */
  it('is still ordered, lightest to darkest', () => {
    const ramp = ['neutral-400', 'neutral-500', 'neutral-600', 'neutral-700', 'neutral-800', 'neutral-900']
    const levels = ramp.map((step) => luminance(token(step)))

    expect(levels).toEqual([...levels].sort((a, b) => b - a))
    expect(new Set(levels).size).toBe(ramp.length)
  })

  /**
   * The chevron on an activatable stat tile.
   *
   * It is `aria-hidden`, so a screen reader gets the tile's button role instead — but it is the
   * only thing on the tile that says the tile opens something, and a sighted reader who cannot
   * see it is the reader it is drawn for. It was drawn in the ramp's border step at 2.2:1.
   *
   * Read from the stylesheet rather than named here, so moving it back to a pale grey fails this
   * rather than leaving the assertion pointing at a token the chevron no longer uses.
   */
  it('keeps the one affordance drawn as a glyph visible', () => {
    const tile = readFileSync(join(__dirname, '..', 'primitives', 'StatTile.module.css'), 'utf8')
    const chevron = /\.chevron\s*\{[^}]*color:\s*var\(--color-([a-z0-9-]+)\)/.exec(tile)?.[1]

    expect(chevron).toBeDefined()

    // 3:1, the threshold for a graphic rather than for text.
    expect(contrast(token(chevron ?? ''), token('surface'))).toBeGreaterThanOrEqual(3)
  })
})
