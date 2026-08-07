import { defineConfig } from '@playwright/test'

/**
 * The browser suite, against a running client and a running server.
 *
 * NOT RUN BY DEFAULT, AND THE VARIABLE IS WHY. `vitest` covers the pure pieces with no processes
 * behind it; this one drives a real browser at a real API over a real PostgreSQL, so it takes a
 * base URL rather than starting six things and guessing. Unset means the spec skips — which is
 * the same bargain `FLOWX_POSTGRES_CONNECTION` strikes on the .NET side.
 *
 * A SKIP IS NOT A PASS, AND THE SPEC SAYS SO OUT LOUD. Set the variable and the tests run; set it
 * to somewhere nothing is listening and they fail. What they must never do is quietly report
 * green because nobody was home.
 *
 * ONE WORKER, NOT FOUR. The three roles walk one tenant's rows and one of them approves what
 * another submitted; running them concurrently would make each one's world depend on where the
 * others had got to.
 */
export default defineConfig({
  testDir: './e2e',
  workers: 1,
  fullyParallel: false,
  reporter: 'list',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  use: {
    baseURL: process.env['CRM_E2E_BASE_URL'] ?? 'http://localhost:5173',
    trace: 'retain-on-failure',

    // The image the environment already carries. Downloading one at test time turns a red
    // suite into a network problem, which is the slowest possible way to find out.
    launchOptions: process.env['CRM_E2E_CHROMIUM']
      ? { executablePath: process.env['CRM_E2E_CHROMIUM'] }
      : {},
  },
})
