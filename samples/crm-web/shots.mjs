// Every screen of the client, twice: once as a representative and once as an administrator.
//
//   node shots.mjs                       # needs `npm run dev` and samples/crm both running
//   CRM_WEB_SHOTS=/somewhere node shots.mjs
//
// Two personas because six routes read a `crm.admin` surface, and what a representative sees
// on those is the refusal the server sent. A single-persona sweep photographs one of the two
// and calls it the screen. The saved set is docs/screenshots/crm-web.
import { chromium } from '@playwright/test'
import fs from 'node:fs'

const base = process.env.CRM_WEB_BASE ?? 'http://127.0.0.1:5173'
const root = process.env.CRM_WEB_SHOTS ?? '.artifacts/screenshots'

const routes = [
  '/', '/kanban', '/search',
  '/service/cases', '/service/board', '/service/sla',
  '/work/inbox', '/work/calendar', '/work/mobile',
  '/exec', '/exec/board', '/exec/forecast', '/exec/insights', '/exec/kpis',
  '/exec/org', '/exec/reviews', '/exec/sales-performance', '/exec/deal-performance',
  '/plan/portfolio', '/plan/strategy', '/plan/accounts', '/plan/leads',
  '/plan/opportunities', '/plan/operations',
  '/analytics/reports', '/analytics/campaigns',
  '/setup', '/setup/objects', '/setup/fields', '/setup/layout', '/setup/list-views',
  '/setup/flows', '/setup/stages', '/setup/approvals', '/setup/permissions',
  '/setup/validation', '/setup/schema', '/setup/quality', '/setup/onboarding',
  '/records/lead', '/records/account', '/records/contact', '/records/opportunity',
  '/records/quote', '/records/task', '/records/workorder',
]

// PLAYWRIGHT_CHROMIUM_PATH for a machine whose browser is not where this Playwright expects
// it — a preinstalled Chromium under a different build number, most often.
const browser = await chromium.launch(
  process.env.PLAYWRIGHT_CHROMIUM_PATH
    ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH }
    : {})

const report = []

for (const persona of ['rep', 'admin']) {
  const out = `${root}/${persona}`
  fs.mkdirSync(out, { recursive: true })

  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } })
  await context.addInitScript(p => globalThis.localStorage.setItem('crm-web.persona', p), persona)

  const page = await context.newPage()
  const errors = []
  page.on('pageerror', e => errors.push('pageerror: ' + String(e)))
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()) })
  page.on('response', r => { if (r.status() >= 400) errors.push(`${r.status()} ${new URL(r.url()).pathname}`) })

  for (const route of routes) {
    const name = route === '/' ? 'index' : route.slice(1).replaceAll('/', '-')
    errors.length = 0
    let status = 'ok'
    try {
      await page.goto(base + route, { waitUntil: 'networkidle', timeout: 30000 })
      await page.waitForTimeout(500)
      const text = (await page.locator('body').innerText()).replace(/\s+/g, ' ').trim()
      if (text.length < 40) status = 'blank'
      else if (errors.some(e => e.startsWith('pageerror'))) status = 'threw'
      else if (errors.length) status = 'api-error'
      await page.screenshot({ path: `${out}/${name}.png`, fullPage: true })
      report.push({ persona, route, status, chars: text.length, errors: [...new Set(errors)].slice(0, 3) })
    } catch (failure) {
      report.push({ persona, route, status: 'navigation-failed', chars: 0, errors: [String(failure).slice(0, 160)] })
    }
    console.log(`${persona.padEnd(6)} ${(report.at(-1).status).padEnd(17)} ${route}`)
  }

  await context.close()
}

await browser.close()
fs.writeFileSync(`${root}/report.json`, JSON.stringify(report, null, 2))

const bad = report.filter(r => r.status !== 'ok')
console.log(`\n${report.length} screen loads, ${bad.length} with a problem.`)
for (const r of bad) console.log(`  ${r.persona}  ${r.status}  ${r.route}  ${r.errors.join(' | ')}`)
