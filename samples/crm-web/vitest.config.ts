import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

/**
 * Separate from `vite.config.ts` so the Vite config stays a Vite config. Merging them makes the
 * dev server's type depend on the test runner's, which is the kind of coupling that turns a
 * version bump into an afternoon.
 *
 * Not in `tsconfig.json`'s include: Vitest ships its own copy of Vite, and type-checking this
 * file makes the compiler compare two structurally-identical `Plugin` types from two node_modules
 * paths. The file is checked by Vitest when it loads it, which is the only place it runs.
 */
export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  test: {
    // Only the unit tests. `e2e/` is Playwright's, and Vitest collected it happily — importing
    // `@playwright/test` into a jsdom worker, where `test.skip(condition, reason)` is a different
    // function with a different signature. A red suite for the right reason is still the wrong
    // runner reporting it.
    include: ['src/**/*.{test,spec}.{ts,tsx}'],

    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,

    coverage: {
      provider: 'v8',
      reporter: ['text', 'json-summary', 'lcov'],

      // `all`, so a file with no test counts as uncovered rather than as absent. Without it
      // the number describes the files somebody remembered to test, which goes UP when a
      // screen is added with no test — the one direction a coverage number must never move.
      all: true,
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/__tests__/**',
        'src/test/**',
        'src/**/*.d.ts',
        // The bootstrap: `createRoot(...).render(<App/>)` and nothing else. Mounting it in a
        // test proves the test can mount it. Everything it wires is covered where it lives.
        'src/main.tsx',
      ],

      // MEASURED, NOT ASPIRED TO. Every number below is what `npm run test:coverage` reports
      // today, rounded down by about a point. A threshold above what the tree achieves fails
      // the build the moment it is committed and gets deleted by the next person; one set at
      // a round 80% here would be the same thing.
      //
      // The globals are low because they are honest: the unit suite covers the pure layers
      // and deliberately does not mount the feature screens — `e2e/roles.spec.ts` walks those
      // in a browser, and 60-odd screens rendered into jsdom to raise a percentage would be
      // slow tests that assert nothing. So the global figure mostly catches a suite being
      // deleted wholesale, and the per-directory entries below are what actually hold a
      // line: they sit under the layers the unit suite owns, where a regression is real.
      //
      // Vitest counts glob-matched files toward the globals as well (unlike Jest), so these
      // are additional gates rather than carve-outs.
      //
      // A directory entry is a floor under a directory, so it moves when a file moves into
      // it. `src/lib` read 93% until `recents.ts` was lifted out of `features/search` — the
      // same untested ninety-six lines, now sitting under a threshold written for
      // `format.ts`. Re-derive the number when that happens; do not delete the entry.
      thresholds: {
        lines: 23,
        statements: 23,
        functions: 36,
        branches: 85,

        'src/lib/**': { lines: 72, functions: 68, branches: 94 },
        'src/session/**': { lines: 94, functions: 95, branches: 78 },
        'src/app/**': { lines: 93, functions: 84, branches: 93 },
        'src/shell/**': { lines: 91, functions: 62, branches: 75 },
        'src/design/**': { lines: 76, functions: 62, branches: 73 },
        'src/fixtures/**': { lines: 97, functions: 75, branches: 80 },
      },
    },
  },
})
