import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * No control in this application is a click that does nothing.
 *
 * <strong>The defect this exists for has shipped here six times.</strong> Clone, Preview,
 * Reschedule, Escalate, a period picker, Save view, and nine stage chevrons on every record page:
 * each was a `<button>` with no handler at all. It is the one failure a reader cannot diagnose —
 * a disabled control explains itself, a refusal explains itself, and a control that swallows the
 * click explains nothing, so the reader concludes the record is stuck.
 *
 * <strong>Source, not a browser.</strong> A browser sweep that clicked every control on every
 * screen found the same six and took seven minutes; this reads the JSX and takes milliseconds, so
 * it can run on every commit rather than when somebody remembers. What it cannot see is a handler
 * that runs and achieves nothing — `e2e/roles.spec.ts` is where that is caught, by asserting on
 * the words the server sent back.
 *
 * <strong>A control is answerable when it carries any of these:</strong> an `onClick`, a
 * `type="submit"` (the form's own submit path), a `disabled` (which the second test below makes
 * explain itself), or an `href`. Anything else is a control with nowhere to go.
 */
const SCREENS = join(__dirname, '..', '..')

/** Every `.tsx` under `src/features`, which is every screen this client has. */
function sources(): { path: string; text: string }[] {
  const found: { path: string; text: string }[] = []

  const walk = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name)

      if (entry.isDirectory()) {
        if (entry.name !== '__tests__') walk(path)
      } else if (entry.name.endsWith('.tsx')) {
        // Comments out, because these files explain themselves at length and several of those
        // explanations quote the very markup this is looking for.
        found.push({
          path,
          text: readFileSync(path, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, ''),
        })
      }
    }
  }

  walk(SCREENS)

  return found
}

/**
 * The opening tags of every button in one file.
 *
 * Brace-aware rather than a regex to the first `>`: an attribute like
 * `onClick={() => f(a > b)}` contains one, and a scan that stopped there would read half a tag.
 */
function buttonTags(text: string): string[] {
  const tags: string[] = []

  for (const opener of ['<Button', '<button']) {
    let at = text.indexOf(opener)

    while (at !== -1) {
      // `<ButtonGroup` is a container, not a control. Without this the group's own tag is read as
      // a button with no handler and every filter strip in the client is a false finding.
      if (/[A-Za-z]/.test(text[at + opener.length] ?? '')) {
        at = text.indexOf(opener, at + opener.length)
        continue
      }

      let depth = 0
      let index = at + opener.length

      while (index < text.length) {
        const character = text[index]

        if (character === '{') depth += 1
        else if (character === '}') depth -= 1
        else if (character === '>' && depth === 0) break

        index += 1
      }

      tags.push(text.slice(at, index))
      at = text.indexOf(opener, index)
    }
  }

  return tags
}

const ANSWERABLE = /\bonClick=|\btype="submit"|\bdisabled\b|\bhref=/

describe('every button', () => {
  it('does something, or is disabled and says why', () => {
    const inert: string[] = []

    for (const { path, text } of sources()) {
      for (const tag of buttonTags(text)) {
        if (!ANSWERABLE.test(tag)) {
          inert.push(`${path.slice(SCREENS.length + 1)}: ${tag.replace(/\s+/g, ' ').slice(0, 90)}`)
        }
      }
    }

    expect(inert, `these swallow the click:\n${inert.join('\n')}`).toEqual([])
  })

  /**
   * A control that is disabled outright says why in a `title`.
   *
   * "Not wired yet" was the old answer and it was three wrong things at once: it read as an
   * unfinished client, it produced "a account", and it was untrue — an account is not missing a
   * form, it is a record this system creates by converting a lead.
   *
   * <strong>Only the unconditional ones.</strong> `disabled={busy}` is a control that works and is
   * momentarily busy; `disabled` on its own is a control that will never work, and that is the one
   * a reader needs a sentence about.
   */
  it('explains a control it disables outright', () => {
    const silent: string[] = []

    for (const { path, text } of sources()) {
      for (const tag of buttonTags(text)) {
        if (/\bdisabled(\s|$)/.test(tag) && !/\btitle=/.test(tag)) {
          silent.push(`${path.slice(SCREENS.length + 1)}: ${tag.replace(/\s+/g, ' ').slice(0, 90)}`)
        }
      }
    }

    expect(silent, `disabled with no reason given:\n${silent.join('\n')}`).toEqual([])
  })
})
