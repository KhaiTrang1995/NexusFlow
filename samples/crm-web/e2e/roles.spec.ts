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
async function signIn(page: Page, persona: 'rep' | 'manager' | 'director' | 'contoso') {
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

/**
 * Captures a lead and converts it, returning the company both now carry.
 *
 * <strong>A test that borrows a row passes once.</strong> Two of these walked "the first
 * opportunity", and the second run of the suite landed on the deal the first run had moved to
 * Closed Won — a stage with no move out of it. Making the deal is also the first half of the
 * loop they are named after, so it is not scaffolding: a converted lead starts in the first
 * stage of the published process, which is the one place both tests can move forwards from.
 */
async function captureAndConvert(page: Page): Promise<string> {
  const company = `Aster ${Date.now()}`

  await page.goto('/records/lead')
  await page.getByRole('button', { name: 'New lead' }).click()
  await page.getByLabel('Company').fill(company)
  await page.getByLabel('Contact name').fill('R. Halloran')
  await page.getByLabel('Email').fill(`r.halloran@${Date.now()}.example`)
  await page.getByRole('button', { name: 'Capture' }).click()
  await expect(toast(page)).toContainText('captured')

  await openFirstRecord(page, '/records/lead', company)
  await page.getByRole('button', { name: 'Convert' }).click()
  await page.getByRole('button', { name: 'Convert', exact: true }).last().click()
  await expect(toast(page)).toContainText('converted')

  return company
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

    const company = await captureAndConvert(page)

    await openFirstRecord(page, '/records/opportunity', company)

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

    // Its own deal, so the count below starts from nought whatever the tenant has been through.
    const company = await captureAndConvert(page)

    await openFirstRecord(page, '/records/opportunity', company)

    await page.getByRole('tab', { name: /Related/ }).click()

    // The panel is there before anything points at the record. It used to render nothing at all
    // when the list was empty, so the whole tab was blank on a new deal.
    await expect(page.getByText('Nothing points at this record from quotes.')).toBeVisible()

    await page.getByRole('tab', { name: /Details/ }).click()
    await page.getByRole('button', { name: 'New quote' }).click()
    await page.getByLabel('Item 1').fill('PLAT')
    await page.getByLabel('Quantity').fill('1')
    await page.getByLabel(/Unit price/).fill('12000')
    await page.getByRole('button', { name: 'Issue quote' }).click()

    await expect(page).toHaveURL(/\/records\/quote\//)

    await page.goBack()
    await page.getByRole('tab', { name: /Related/ }).click()

    await expect(page.getByRole('table', { name: 'Quotes' }).locator('tbody tr')).toHaveCount(1)
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

  /**
   * The landing page is about this tenant.
   *
   * IT MADE NO REQUEST AT ALL. Pipeline, weighted value, won this quarter, what is closing and
   * the task list were all derived from the prototype's fixture records, filtered by
   * `owner === 'A. Ruiz'` — a name in a file, not the person signed in. A sweep of all
   * forty-six routes found it, along with three other screens that never called the server.
   *
   * Asserted by switching a filter and watching a figure move: a fixture answers the same
   * whoever asks, so "mine" and "everyone" agreeing is the shape of the defect.
   */
  test('sees a console built from the tenant, not from a fixture', async ({ page }) => {
    await signIn(page, 'rep')

    await page.goto('/')

    // Names from the object model that this tenant has never heard of. A. Ruiz is deliberately
    // not one of them — the seed places her, so she appears in the live attainment panel, and
    // asserting her absence would fail against correct data.
    for (const invented of ['M. Chen', 'J. Park', 'K. Osei', 'L. Novak']) {
      await expect(page.locator('main'), invented).not.toContainText(invented)
    }

    // Three tiles claimed a trend against a week nobody stores, and a $160k target nobody set.
    await expect(page.locator('main')).not.toContainText('vs last week')
    await expect(page.locator('main')).not.toContainText('$160k target')

    await expect(page.getByRole('button', { name: /^Open pipeline/ })).toBeVisible()

    const mine = await page.locator('main').innerText()

    await page.getByRole('button', { name: 'Everyone' }).click()
    await expect(page.getByRole('button', { name: 'Everyone' })).toHaveAttribute(
      'aria-pressed',
      'true',
    )

    // Everyone's pipeline is a superset of one seller's, so the page cannot read identically
    // unless the rows behind it were never filtered by owner in the first place.
    await expect
      .poll(async () => (await page.locator('main').innerText()) !== mine)
      .toBe(true)
  })

  /**
   * The board is the published process, and moving a card writes.
   *
   * ITS LANES WERE THE PROTOTYPE'S — a stage list this client was compiled with, each carrying an
   * invented probability drawn under the column total. They matched the seed by coincidence of
   * naming; a tenant that named its stages anything else would have seen every card fall into no
   * lane and an empty board rather than a wrong one.
   *
   * A DROP NAMES WHAT HAPPENED, NOT WHERE IT LANDS. The transition between two stages carries the
   * trigger; the server decides whether its guards hold. A lane with no transition into it is
   * refused, which is the process talking rather than the board.
   */
  test('moves a card through the published process, and cannot move it back', async ({ page }) => {
    await signIn(page, 'rep')

    // Its own deal, in the first stage of the published process — see `captureAndConvert`.
    const company = await captureAndConvert(page)

    await page.goto('/kanban')

    // The version comes from the published definition — a fixture has none.
    await expect(page.locator('main')).toContainText(/version \d+/)

    const card = page.locator('section button', { hasText: company }).first()

    await expect(card).toBeVisible()

    const name = (await card.innerText()).split('\n')[0] ?? ''
    const before = (await card.getAttribute('aria-label')) ?? ''

    await card.focus()
    await page.keyboard.press('ArrowRight')

    await expect(toast(page)).toContainText(`${name} →`)

    // And back is refused, because this process runs one way. The refusal names both stages,
    // which is what tells a reader it is the configuration and not the drag that failed.
    const moved = page.locator('section button', { hasText: name }).first()

    // THE TOAST NAMES WHERE THE DEAL ACTUALLY IS. The trigger is applied synchronously and the
    // transition is decided afterwards, off the change feed — so this used to announce the move
    // before the engine had run, and announce it again when the engine had declined to move
    // anything. The card's own label carries its stage, and the two have to agree.
    await expect(moved).not.toHaveAttribute('aria-label', before)

    const landed = ((await moved.getAttribute('aria-label')) ?? '').split(', ')[1]?.split('.')[0]

    await expect(toast(page)).toContainText(`${name} → ${landed}`)

    await moved.focus()
    await page.keyboard.press('ArrowLeft')

    await expect(toast(page)).toContainText('no move from')
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

  /**
   * A report runs on the server, and its figure is in the unit of the field it reduced.
   *
   * SIX REPORTS USED TO BE WRITTEN OUT IN THE SCREEN and grouped the prototype's fixture records
   * in the browser. A bar chart looks like evidence, which makes a fabricated one the most
   * confident kind of wrong. This asserts the two things a fixture cannot fake: the list is what
   * the tenant saved, and a sum of amounts is money while a count of rows is not.
   */
  test('runs a saved report and reads it in the right unit', async ({ page }) => {
    await signIn(page, 'manager')

    await page.goto('/analytics/reports')

    const saved = page.locator('main [aria-pressed]')

    await expect(saved.filter({ hasText: 'Pipeline by outcome' })).toBeVisible()
    await saved.filter({ hasText: 'Pipeline by outcome' }).click()

    // Sum of Amount: money, because the result says which field it reduced.
    await expect(page.getByText('of Amount')).toBeVisible()
    await expect(page.locator('main')).toContainText(/\$[0-9,]+/)

    await saved.filter({ hasText: 'Leads by status' }).click()

    // Count of rows: a tally, and drawing it as "$2.00" is the defect this catches.
    await expect(page.getByText('of rows')).toBeVisible()
    await expect(page.locator('table').last()).not.toContainText('$')
  })

  /**
   * The setup screens write, and the stages screen shows what is published.
   *
   * FOUR FORMS ON THESE SCREENS TOASTED AND POSTED NOTHING — a validation rule, a list view, a
   * strategy, and a stage reorder. Three of them had a real surface behind them the whole time;
   * the fourth was reordering a list this client was compiled with, on the page somebody opens to
   * check that stages live in tables. That one is gone rather than wired: reordering is
   * publishing a new version, and this build does not publish from the browser.
   */
  test('declares a validation rule that then appears in force', async ({ page }) => {
    await signIn(page, 'manager')

    await page.goto('/setup/validation')

    // Unique per run. Naming it from the current row count collided the moment two runs saw the
    // same count — a name is unique per tenant, so the second run's refusal read as the write
    // being broken.
    await page.getByLabel('Name').fill(`min_deal_${Date.now()}`)
    await page.getByLabel('Field').selectOption('amount')
    await page.getByLabel('Operator').selectOption('LessThan')
    await page.getByLabel('Value').fill('1000')
    await page.getByLabel('What to tell the person').fill('Too small to track.')
    await page.getByRole('button', { name: 'Declare the rule' }).click()

    await expect(toast(page)).toContainText('in force on every opportunity')

    // The list is the server's, and its sentence is the server's too — this client never
    // composes "refuses X when Y".
    await expect(page.locator('main table').first()).toContainText('refuses Opportunity when amount')
  })

  /** The stages screen shows the published process and offers nothing that would have to lie. */
  test('sees the published stages and no control that cannot write', async ({ page }) => {
    await signIn(page, 'manager')

    await page.goto('/setup/stages')

    await expect(page.getByRole('heading', { name: 'Published stages' })).toBeVisible()
    await expect(page.locator('main')).toContainText('version 1')

    // The reorder editor is gone: its Save toasted "reordered" and moved a fixture.
    await expect(page.getByRole('button', { name: 'Save the order' })).toHaveCount(0)
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

/**
 * The state every real customer starts in, which nothing had ever looked at.
 *
 * <strong>Empty and broken looked identical.</strong> Every screen in this application had been
 * checked against a seeded tenant. Opened as an organisation that has declared nothing, twelve of
 * them showed "the resource was not found — no period named 'fy26_q3' has been declared" under a
 * selector printing Q3 FY26, because the period names were constants in the client rather than
 * the tenant's own; three more rendered a heading and nothing at all, because a failed read fell
 * through to a query that stayed pending for ever and a skeleton is `aria-hidden`.
 */
test.describe('an organisation that has just started', () => {
  test('is told what it has not configured, rather than shown a not-found', async ({ page }) => {
    await signIn(page, 'contoso')

    for (const path of ['/exec/board', '/exec/kpis', '/plan/portfolio', '/plan/operations']) {
      await page.goto(path)

      await expect(page.getByText('No periods have been declared'), path).toBeVisible()
      await expect(page.getByText('crm.period_not_found'), path).toHaveCount(0)
    }
  })

  test('renders something on a plan screen whose read cannot succeed', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/plan/accounts')

    // The whole page used to be 22 characters: a heading, and a skeleton nobody could see.
    await expect(page.locator('main')).toContainText('No periods have been declared')
  })

  test('shows no other tenant rows in the search panel', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/search')

    // "Recent — what you looked at last" was four hard-coded records of the seeded tenant, shown
    // to everybody, each linking to an id that resolves to nothing.
    await expect(page.getByText('Nothing opened yet')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Northwind')
  })

  test('offers no retry on a refusal the server will repeat', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/analytics/reports')

    await expect(page.getByText('authorization.permission_denied')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Try again' })).toHaveCount(0)
  })

  /**
   * A list shows the rows it was given and no others.
   *
   * The seven list screens fell back to the prototype's records whenever the page had not yet
   * arrived or the read had failed. An organisation with nothing in it was shown five accounts,
   * six opportunities and a €184,000 deal — every row clickable, every id resolving to nothing —
   * under a grey eyebrow reading "sample data", which is not a thing anybody reads.
   */
  test('lists no rows it was not given', async ({ page }) => {
    await signIn(page, 'contoso')

    for (const object of ['account', 'contact', 'opportunity', 'quote']) {
      await page.goto(`/records/${object}`)

      await expect(page.locator('tbody tr'), object).toHaveCount(0)
      await expect(page.locator('main'), object).not.toContainText('Northwind')
    }
  })
})

test.describe('a list whose read fails', () => {
  /**
   * The failure mode the empty tenant could not reach.
   *
   * An empty tenant gets an empty page, which is a successful read; the fixtures only surfaced
   * while a page was pending or after it had failed. So this one refuses the read outright —
   * the shape of a 500, an expired token or a dropped connection — and asserts the two things
   * that were wrong: the table filled with somebody else's rows, and the refusal never appeared.
   */
  test('says so, and shows nobody else’s rows instead', async ({ page }) => {
    await signIn(page, 'rep')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.goto('/records/account')

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.locator('tbody tr')).toHaveCount(0)
    await expect(page.locator('main')).not.toContainText('Northwind')
  })
})

test.describe('the recent panel', () => {
  /**
   * It is a fact about this browser, and it has to actually be written.
   *
   * The empty case is asserted above; without this, the panel could be permanently empty and
   * still pass, which is the fixture replaced by nothing rather than by the truth.
   */
  test('fills from the record that was opened', async ({ page }) => {
    await signIn(page, 'rep')
    await openFirstRecord(page, '/records/account')

    const opened = await page.locator('h1').first().innerText()

    await page.goto('/search')

    await expect(page.getByText('Nothing opened yet')).toHaveCount(0)
    await expect(page.locator('main')).toContainText(opened)
  })
})


test.describe('the two buttons that did nothing', () => {
  /**
   * Both had no handler at all, on screens whose whole purpose they were.
   *
   * The service console could be read and worked but never added to; the calendar was the one
   * screen that could not put anything in itself. Each capability had been there throughout.
   */
  test('opens a case from the service console', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/service/cases')

    await page.getByRole('button', { name: 'New case' }).click()

    await page.getByLabel('Account').selectOption({ index: 1 })
    await page.getByLabel('Subject').fill(`Cannot sign in ${Date.now()}`)
    await page.getByLabel('Description').fill('Reported by telephone; the console rejects the password.')
    await page.getByRole('button', { name: 'Open the case' }).click()

    // The server's own answer: the number it assigned and the policy that matched it.
    await expect(toast(page)).toContainText(/Case #\d+ opened/)
  })

  test('adds a meeting from the calendar', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/work/calendar')

    await page.getByRole('button', { name: 'New meeting' }).click()

    await expect(page.getByRole('dialog')).toContainText('Create a meeting')

    await page.getByLabel('Subject').fill(`Renewal review ${Date.now()}`)
    await page.getByLabel('Opportunity').selectOption({ index: 1 })
    await page.getByRole('button', { name: 'Create' }).click()

    await expect(toast(page)).toContainText('created')
  })
})
