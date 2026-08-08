import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'

/**
 * The three chairs, each walking the loop it exists for.
 *
 * <strong>What these catch that nothing else does.</strong> Every screen below is wired to a
 * capability the .NET suite already proves. What no server test can see is a client that stops
 * calling it — a form whose Save toasts without saving, a button whose refusal never reaches the
 * page, a permission checked in the wrong direction. Each of those shipped here at least once, and
 * each was found by opening the screen rather than by reading it.
 *
 * <strong>Not a substitute for the .NET suite, and deliberately thin.</strong> One path per role,
 * asserted on the words the server sent back. Anything a capability test can prove belongs there,
 * where it runs in two minutes without a browser.
 *
 * <strong>The whole file skips unless a base URL is set — and never skips silently.</strong>
 * Running it needs a client, an API and a seeded PostgreSQL; `CRM_E2E_BASE_URL` is the caller
 * saying those exist. Set it to somewhere nothing is listening and these fail rather than pass.
 */
const configured = process.env['CRM_E2E_BASE_URL'] !== undefined

test.skip(
  !configured,
  'CRM_E2E_BASE_URL is unset — start the client and the API, then set it to the client.',
)

/** The persona switch is persisted, so a full page load keeps the chair it was set to. */
async function signIn(page: Page, persona: 'rep' | 'manager' | 'director') {
  await page.goto('/')
  await page.evaluate((who) => localStorage.setItem('crm-web.persona', who), persona)
}

/** The last toast, which is where the server's own sentence lands. */
function toast(page: Page) {
  return page.locator('[role=alert], [role=status]').last()
}

/** A list row, opened properly: the row opens a preview and the preview opens the record. */
async function openFirstRecord(page: Page, path: string, matching?: string) {
  await page.goto(path)

  const row =
    matching === undefined
      ? page.locator('tbody tr').first()
      : page.locator('tbody tr', { hasText: matching }).first()

  await row.click()
  await page.getByRole('button', { name: 'Open record' }).click()
}

test.describe('a seller', () => {
  /**
   * Capture to order, with the approval in the middle.
   *
   * The assertion that matters is the last one: an order against a Draft quote is refused, and the
   * refusal reaches the screen. A silent 409 is a button that looks dead, and that is the reading
   * a person actually arrives at.
   */
  test('walks from an opportunity to an order, and is stopped at the discount', async ({ page }) => {
    await signIn(page, 'rep')

    await openFirstRecord(page, '/records/opportunity')

    // The moves come from the published process, not from a list this file knows. Asserting the
    // arrow rather than a stage name: which stages exist is the tenant's business.
    await expect(page.locator('header button', { hasText: '→' }).first()).toBeVisible()

    await page.getByRole('button', { name: 'New quote' }).click()
    await page.getByLabel('Item 1').fill('PLAT')
    await page.getByLabel('Quantity').fill('2')
    await page.getByLabel(/Unit price/).fill('50000')
    await page.getByLabel(/^Discount/).fill('30000')
    await page.getByRole('button', { name: 'Issue quote' }).click()

    await expect(toast(page)).toContainText('a manager has to approve the discount')

    // Issuing opens the quote it made, which is what makes this run act on its own row rather
    // than on whichever Draft happens to be first in a tenant somebody else has been working in.
    await expect(page).toHaveURL(/\/records\/quote\//)

    await page.getByRole('button', { name: 'Submit for approval' }).click()
    await expect(toast(page)).toContainText('waiting on')

    // A seller holds no discount grant, so the button is absent rather than disabled: it is not a
    // field they go looking for, it is an act they are not party to.
    await expect(page.getByRole('button', { name: 'Approve the discount' })).toHaveCount(0)

    await page.getByRole('button', { name: 'Place order' }).click()
    await expect(toast(page)).toContainText('Draft')
  })

  /**
   * The related list and the activity feed are the record's own, not a specimen.
   *
   * Both panels held written-out examples on every record in the tenant — the same redlined MSA
   * against every account, four activities whatever the record. What replaced them is a filter on
   * a foreign key, and a wrong column name there is refused by the server rather than silently
   * matching nothing. Only a browser sees the difference, which is why this assertion is here and
   * not in a unit test.
   */
  test('sees its own quotes under Related', async ({ page }) => {
    await signIn(page, 'rep')

    await openFirstRecord(page, '/records/opportunity')

    await page.getByRole('tab', { name: /Related/ }).click()

    const quotes = page.getByRole('table', { name: 'Quotes' })

    await expect(quotes).toBeVisible()

    const before = await quotes.locator('tbody tr').count()

    await page.getByRole('tab', { name: /Details/ }).click()
    await page.getByRole('button', { name: 'New quote' }).click()
    await page.getByLabel('Item 1').fill('PLAT')
    await page.getByLabel('Quantity').fill('1')
    await page.getByLabel(/Unit price/).fill('12000')
    await page.getByRole('button', { name: 'Issue quote' }).click()

    await expect(page).toHaveURL(/\/records\/quote\//)

    await page.goBack()
    await page.getByRole('tab', { name: /Related/ }).click()

    await expect(quotes.locator('tbody tr')).toHaveCount(before + 1)
  })

  /**
   * Nothing on the record page is a click that does nothing.
   *
   * Seven buttons in this client had no handler at all — Clone, Preview, Reschedule, Escalate and
   * three more. A disabled button with a title is a different fact from a live one, and both are
   * different from a button that looks live and is not; the third is the only one a reader cannot
   * diagnose, so it is the one this asserts against.
   */
  test('finds no button that looks live and is not', async ({ page }) => {
    await signIn(page, 'rep')

    await openFirstRecord(page, '/records/opportunity')

    // Clone has no capability behind it, so it is disabled and says why rather than swallowing
    // the click.
    const clone = page.getByRole('button', { name: 'Clone' })

    await expect(clone).toBeDisabled()
    await expect(clone).toHaveAttribute('title', /no duplicate-record capability/)

    // And the Files tab says the build stores none, rather than listing three that cannot open.
    await page.getByRole('tab', { name: /Files/ }).click()
    await expect(page.getByText('This build stores no files')).toBeVisible()
  })

  /**
   * The builder's lines are the quote's, and they reconcile with what the server holds.
   *
   * Four written-out products used to sit here on every quote in the tenant — a platform licence,
   * a residency add-on, onboarding and support — priced identically whatever the record. This
   * asserts the arithmetic instead: the lines add up to the list price, and the list price less
   * the recorded discount is the recorded total. Three numbers from two sources agreeing is the
   * only check that catches a builder drawing somebody else's quote.
   */
  test('sees a builder whose lines reconcile with the record', async ({ page }) => {
    await signIn(page, 'rep')

    await openFirstRecord(page, '/records/quote', 'Issued')
    await page.getByRole('button', { name: 'Open builder' }).click()

    await expect(page.locator('table tbody tr').first()).toBeVisible()

    // Read off the rendered page rather than by walking to a parent element: the totals are a
    // two-column grid, so a label's parent is the whole grid and "the number next to it" is every
    // number in the panel concatenated.
    const shown = await page.locator('main').innerText()

    const money = (label: string) => {
      const found = new RegExp(`${label}\\s*\\n?−?\\$([0-9,]+)`).exec(shown)

      expect(found, `no figure beside '${label}'`).not.toBeNull()

      return Number(found![1]!.replace(/,/g, ''))
    }

    const list = money('List price')
    const discount = money('Recorded discount')
    const total = money('Recorded total')

    expect(list).toBeGreaterThan(0)
    expect(list - discount).toBe(total)
  })
})

test.describe('a manager', () => {
  /**
   * The inbox, and the grant a seller does not hold.
   *
   * Deciding the request and clearing the quote are two different acts, and this walks both —
   * a build that conflated them would pass with either one missing.
   */
  test('decides what is waiting and clears the discount', async ({ page }) => {
    await signIn(page, 'manager')

    await page.goto('/work/inbox')

    const waiting = page.locator('tbody tr').first()

    await expect(waiting).toBeVisible()
    await waiting.click()

    await page.getByLabel('Why').fill('Volume commitment for the year carries it.')
    await page.getByRole('button', { name: 'Approve' }).click()

    // Either answer is correct and they mean different things: a chain with another step ahead
    // stays pending, and the last step ends it. Asserting one would be asserting the seed.
    await expect(toast(page)).toContainText(/Now waiting on|The request is/)

    await openFirstRecord(page, '/records/quote', 'Draft')

    await page.getByRole('button', { name: 'Approve the discount' }).click()
    await expect(toast(page)).toContainText('Issued')
  })

  /** Assigning a number is administrative, and the people come from the period's own rows. */
  test('assigns a quota to somebody the period already reports on', async ({ page }) => {
    await signIn(page, 'manager')

    await page.goto('/exec/sales-performance')

    await page.getByRole('button', { name: 'Set a quota' }).click()
    await page.getByRole('button', { name: 'Assign' }).click()

    await expect(toast(page)).toContainText('carries')
  })
})

test.describe('a director', () => {
  /**
   * The reporting line, which is what every scoped number above is scoped by — and the one act a
   * director is not party to.
   */
  test('reads and changes the line, and holds no discount grant', async ({ page }) => {
    await signIn(page, 'director')

    await page.goto('/exec/org')

    // Managers before their reports — asserted as an ordering rather than as a position, because
    // a tenant may legitimately have more than one person at the top and the chart draws them all.
    const line = page.locator('ul li')

    await expect(line.filter({ hasText: 'director-northwind-1' })).toContainText('Director')

    const order = await line.allInnerTexts()
    const above = order.findIndex((row) => row.includes('director-northwind-1'))
    const below = order.findIndex((row) => row.includes('manager-northwind-1'))

    expect(above).toBeGreaterThanOrEqual(0)
    expect(below).toBeGreaterThan(above)

    await page.getByRole('button', { name: 'Place somebody' }).click()
    await page.getByLabel('Subject').fill('rep-e2e-1')
    await page.getByLabel('Display name').fill('E. Tester')

    // By value, which is the subject, and under a named person rather than whoever is first in
    // the list. Choosing by position placed them under themselves on the second run — the server
    // refused the loop, correctly, and the test read it as the write being broken.
    await page.getByLabel('Reports to').selectOption('director-northwind-1')
    await page.getByRole('button', { name: 'Save' }).click()

    await expect(toast(page)).toContainText('placed')

    // Seniority is not a grant. A director is above the manager and is still not in the discount
    // chain, and a token that gave them every permission would make the three personas one.
    await openFirstRecord(page, '/records/quote')
    await expect(page.getByRole('button', { name: 'Approve the discount' })).toHaveCount(0)
  })

  /** A review is recorded against the server's own reading, not against one typed here. */
  test('records a KPI review and the register keeps it', async ({ page }) => {
    await signIn(page, 'director')

    await page.goto('/exec/reviews')

    await page.getByLabel('Commentary').fill('Down on the quarter; two of five losses were on price.')
    await page.getByRole('button', { name: 'Record the review' }).click()

    await expect(toast(page)).toContainText('Recorded at')

    // The panel beside the form is the tenant's register. It used to be four invented minutes,
    // which beside a form that now saves would read as the record this review had just joined.
    await expect(page.getByText('Down on the quarter')).toBeVisible()
  })
})

test.describe('a seller, again', () => {
  /** The line is readable by everybody and changeable by an administrator. */
  test('reads the reporting line but cannot change it', async ({ page }) => {
    await signIn(page, 'rep')

    await page.goto('/exec/org')

    await expect(page.locator('ul li').first()).toBeVisible()
    await expect(page.getByRole('button', { name: 'Place somebody' })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Move' })).toHaveCount(0)
  })
})
