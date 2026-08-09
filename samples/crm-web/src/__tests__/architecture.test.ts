import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * The shape of this client, asserted rather than described.
 *
 * <strong>The .NET side of this repository enforces its architecture as tests and the client
 * enforced nothing, so the client drifted.</strong> Twenty imports across thirteen files reached
 * from one feature into another — the period was `exec`'s and planning, sales and exec all used
 * it; the declared-configuration table was `setup`'s and service and analytics both drew one.
 * Nothing refused any of them, and each was a reasonable thing to type at the time.
 *
 * <strong>What a feature is, and what it costs when that stops being true.</strong> A feature is a
 * slice that can be read, changed and deleted on its own. The moment `planning` imports `exec`,
 * deleting `exec` breaks planning, a change to the picker has to be reasoned about on fourteen
 * screens across two features, and the directory names stop predicting anything. A concept two
 * features share is not one feature's — it belongs outside `features/`, where both may reach it.
 *
 * <strong>Source, not a bundler.</strong> This reads the import specifiers out of every `.ts` and
 * `.tsx` under `src` and resolves them the way Vite and the compiler do. It needs no dependency
 * and takes milliseconds, so it runs on every commit rather than when somebody remembers — the
 * same trade `controls.test.ts` beside it makes for the same reason.
 *
 * <strong>The scan is only worth as much as its resolution</strong>, which is why "every import
 * resolves" is one of the assertions below. A resolver that quietly returns nothing makes every
 * other rule here pass, and a green test that checks nothing is worse than no test.
 */
const SRC = join(__dirname, '..')

/**
 * The one file allowed to reach into `features/` — the route table, whose whole job is to name
 * every screen this client has.
 *
 * A composition root depends on everything by definition; that is what makes it the root and not
 * a layer. Pinning it to one named file is what keeps "the root may do it" from becoming "`app`
 * may do it", which is how the exemption would spread.
 */
const COMPOSITION_ROOT = join(SRC, 'app', 'router.tsx')

interface Edge {
  /** The file the import was written in. */
  from: string
  /** The file it resolved to, whether or not that file exists. */
  to: string
  /** As written, for a failure message somebody can search for. */
  specifier: string
  resolved: boolean
}

/**
 * Every source file under `src`, with its comments removed.
 *
 * Comments out, because the files in this codebase explain themselves at length and several of
 * those explanations name the very import paths this is looking for.
 */
function sources(): { file: string; text: string }[] {
  const found: { file: string; text: string }[] = []

  const walk = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name)

      if (entry.isDirectory()) {
        walk(path)
      } else if (entry.name.endsWith('.ts') || entry.name.endsWith('.tsx')) {
        found.push({
          file: path,
          text: readFileSync(path, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, ''),
        })
      }
    }
  }

  walk(SRC)

  return found
}

/**
 * The three ways this codebase names a module: `from '…'`, a dynamic `import('…')`, and the
 * side-effect `import '…'` that `main.tsx` uses for the stylesheets.
 */
function specifiers(text: string): string[] {
  const found: string[] = []

  for (const pattern of [
    /\bfrom\s*['"]([^'"]+)['"]/g,
    /\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
    /\bimport\s+['"]([^'"]+)['"]/g,
  ]) {
    for (const match of text.matchAll(pattern)) {
      found.push(match[1]!)
    }
  }

  return found
}

/** Where a specifier points, or null when it names a package rather than a file of ours. */
function targetOf(from: string, specifier: string): string | null {
  const base = specifier.startsWith('@/')
    ? join(SRC, specifier.slice(2))
    : specifier.startsWith('.')
      ? resolve(dirname(from), specifier)
      : null

  if (base === null) {
    return null
  }

  // The compiler's order, and Vite's: the path itself (a stylesheet, or an extension somebody
  // wrote out), then the two source extensions, then the directory's barrel.
  for (const suffix of ['', '.ts', '.tsx', `${sep}index.ts`, `${sep}index.tsx`]) {
    const candidate = base + suffix

    if (existsSync(candidate) && statSync(candidate).isFile()) {
      return candidate
    }
  }

  return base
}

function edges(): Edge[] {
  const found: Edge[] = []

  for (const { file, text } of sources()) {
    for (const specifier of specifiers(text)) {
      const to = targetOf(file, specifier)

      if (to !== null) {
        found.push({ from: file, to, specifier, resolved: existsSync(to) })
      }
    }
  }

  return found
}

/**
 * The module a file belongs to.
 *
 * Each feature is its own module — `features` as a whole is not one, or the rule this file exists
 * for would have nothing to say. A file sitting directly in `src` is the entry point, which
 * belongs to no module and is named so it reads that way in a cycle.
 */
function moduleOf(file: string): string {
  const parts = relative(SRC, file).split(sep)

  if (parts.length === 1) {
    return '(entry)'
  }

  return parts[0] === 'features' ? `features/${parts[1]}` : parts[0]!
}

function isFeature(module: string): boolean {
  return module.startsWith('features/')
}

/** A path relative to `src`, which is how these files are talked about. */
function shown(file: string): string {
  return relative(SRC, file)
}

const ALL = edges()

describe('the import graph', () => {
  /**
   * <strong>A feature never imports another feature.</strong>
   *
   * The rule all twenty violations broke. Two features needing the same thing is not a reason
   * for one to import the other; it is the evidence that the thing belongs to neither.
   */
  it('keeps every feature out of every other feature', () => {
    const crossings = ALL
      .filter((edge) => {
        const from = moduleOf(edge.from)
        const to = moduleOf(edge.to)

        return isFeature(from) && isFeature(to) && from !== to
      })
      .map((edge) => `${shown(edge.from)} → ${edge.specifier}`)

    expect(crossings, `these reach into another feature:\n${crossings.join('\n')}`).toEqual([])
  })

  /**
   * <strong>Only the composition root imports a feature.</strong>
   *
   * The other half of the same rule, and the one that keeps the shared modules shareable:
   * `design`, `api`, `lib`, `shell`, `session` and the concept modules beside them are what
   * features are built out of, so a single import back into a feature makes the whole graph
   * circular and the shared module undeletable.
   */
  it('lets nothing but the composition root import a feature', () => {
    const reaching = ALL
      .filter((edge) => (
        !isFeature(moduleOf(edge.from))
        && isFeature(moduleOf(edge.to))
        && edge.from !== COMPOSITION_ROOT
      ))
      .map((edge) => `${shown(edge.from)} → ${edge.specifier}`)

    expect(
      reaching,
      `only ${shown(COMPOSITION_ROOT)} may name a feature:\n${reaching.join('\n')}`,
    ).toEqual([])
  })

  /**
   * <strong>No cycle between modules.</strong>
   *
   * Two modules that import each other are one module with a directory boundary drawn through it:
   * neither can be read, tested or removed without the other, and at runtime whichever is
   * evaluated second sees the first half-initialised.
   *
   * The composition root's own edges are left out — it names every feature, which is its job, and
   * counting those would make the check report the one cycle that is not a mistake. Nothing else
   * is exempt, and the test above is what stops that exemption from spreading past one file.
   */
  it('has no cycle', () => {
    const graph = new Map<string, Set<string>>()

    for (const edge of ALL) {
      if (edge.from === COMPOSITION_ROOT) continue

      const from = moduleOf(edge.from)
      const to = moduleOf(edge.to)

      if (from === to) continue

      const out = graph.get(from) ?? new Set<string>()
      out.add(to)
      graph.set(from, out)
    }

    const done = new Set<string>()
    const stack: string[] = []
    const cycles: string[] = []

    const visit = (module: string) => {
      const seenAt = stack.indexOf(module)

      if (seenAt !== -1) {
        cycles.push([...stack.slice(seenAt), module].join(' → '))
        return
      }

      if (done.has(module)) return

      stack.push(module)
      for (const next of graph.get(module) ?? []) visit(next)
      stack.pop()
      done.add(module)
    }

    for (const module of graph.keys()) visit(module)

    expect(cycles, `these modules depend on each other:\n${cycles.join('\n')}`).toEqual([])
  })

  /**
   * <strong>The only thing a feature takes from `app` is the toast.</strong>
   *
   * `app` is both the ambient providers every screen uses and the route table that names every
   * screen, which is why the cycle check has to know which of the two an edge came from. Holding
   * features to the provider keeps that subtlety to one file: a screen that reached for the router
   * or the query client would make `app` a layer in both directions at once.
   */
  it('lets a feature take only the toast from app', () => {
    const reaching = ALL
      .filter((edge) => (
        isFeature(moduleOf(edge.from))
        && moduleOf(edge.to) === 'app'
        && edge.to !== join(SRC, 'app', 'ToastProvider.tsx')
      ))
      .map((edge) => `${shown(edge.from)} → ${edge.specifier}`)

    expect(reaching, `a screen reached past the toast:\n${reaching.join('\n')}`).toEqual([])
  })

  /**
   * <strong>`design` draws; it does not fetch.</strong>
   *
   * It knows `ApiError`, because a refusal is a thing it has to render. Knowing a query hook would
   * make a primitive decide when the server is asked, which is the caller's decision on every
   * screen that has ever had to make it twice.
   */
  it('keeps the query layer out of design', () => {
    const fetching = ALL
      .filter((edge) => moduleOf(edge.from) === 'design' && edge.to.startsWith(join(SRC, 'api', 'queries')))
      .map((edge) => `${shown(edge.from)} → ${edge.specifier}`)

    expect(fetching, `a primitive reads from the server:\n${fetching.join('\n')}`).toEqual([])
  })

  /**
   * <strong>`lib` is the leaf.</strong>
   *
   * Formatting, class names, the two rules about what an empty box means, and what this browser
   * has opened: none of it may know about React, the contracts or a screen. It is what everything
   * else is allowed to depend on precisely because it depends on nothing.
   */
  it('keeps lib depending on nothing of ours', () => {
    const reaching = ALL
      .filter((edge) => moduleOf(edge.from) === 'lib' && moduleOf(edge.to) !== 'lib')
      .map((edge) => `${shown(edge.from)} → ${edge.specifier}`)

    expect(reaching, `lib is not a leaf any more:\n${reaching.join('\n')}`).toEqual([])
  })

  /**
   * <strong>Every import of ours resolves to a file that exists.</strong>
   *
   * Not a rule about the architecture but the thing that makes the rules above mean anything: an
   * unresolved specifier is a module this scan silently did not follow, and each one is a rule it
   * silently did not apply.
   */
  it('resolves every import it was asked to judge', () => {
    const missing = ALL
      .filter((edge) => !edge.resolved)
      .map((edge) => `${shown(edge.from)} → ${edge.specifier}`)

    expect(missing, `nothing is there:\n${missing.join('\n')}`).toEqual([])
  })
})
