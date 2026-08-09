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

    // AND THERE IS NO FILES TAB. It held three invented documents, then a sentence saying this
    // build stores none. There is no attachment capability in the manifest and no file surface on
    // the server, so it was a destination whose only content was the reason not to have gone
    // there. The three tabs that remain all have something behind them.
    const sections = page.getByRole('tablist', { name: 'Record sections' })

    await expect(sections.getByRole('tab', { name: /Files/ })).toHaveCount(0)
    await expect(sections.getByRole('tab')).toHaveCount(3)
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

  /**
   * Three things can become of an applied trigger, and the screen says which.
   *
   * TWO OF THEM USED TO BE ONE SENTENCE. `crm.opportunity.advance` records that somebody applied a
   * trigger and answers 200; the transition is decided afterwards, off the change feed. So a deal
   * sitting in the stage it started in means either "the feed has not got there yet" or "the
   * engine ran and declined the move" — no transition carries that trigger out of that stage, or
   * one does and a guard did not hold. Neither is an error, so nothing was ever raised and nothing
   * could be caught: this screen polled the record and announced "the process left this deal in
   * Discovery", which is a fabrication in the first case every time.
   *
   * THE DECLINE HERE IS THE ONE A BUSINESS ACTUALLY HITS. Two people have the same deal open; one
   * of them moves it, and the other's buttons are now the moves out of a stage the deal has left.
   * A second tab is that, exactly, and it needs no fixture and no fault injection.
   */
  test('says whether the process moved the deal, declined the trigger, or has not decided', async ({
    page,
    context,
  }) => {
    await signIn(page, 'rep')

    const company = await captureAndConvert(page)

    await openFirstRecord(page, '/records/opportunity', company)

    const move = (open: Page) => open.locator('header button', { hasText: '→' }).first()
    const said = (open: Page, sentence: string) =>
      open.locator('[role=status]', { hasText: sentence })

    // Opened before anything moves the deal, so its buttons are the moves out of the stage it is
    // in now — and they stay that way while the first tab moves it on.
    const other = await context.newPage()

    await other.goto(page.url())
    await expect(move(other)).toBeVisible()

    // NOT DECIDED YET. All the 200 carries is that the application was recorded, so that is what
    // the screen says. Anything about a stage at this point would be invented.
    await move(page).click()
    await expect(said(page, 'has not decided yet')).toBeVisible()

    // MOVED. The engine answered, and the answer names the stage the deal entered.
    await expect(said(page, 'Moved to')).toBeVisible()

    // DECLINED. The stale tab applies a trigger the deal has outgrown. The deal does not move, the
    // request does not fail, and the sentence has to be a third one — the assertion below is the
    // whole point, because the old build said this deal had moved.
    await move(other).click()
    await expect(said(other, 'declined')).toBeVisible()
    await expect(said(other, 'Moved to')).toHaveCount(0)

    await other.close()
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

test.describe('the chrome', () => {
  /**
   * The shell says which organisation this is, and which chair.
   *
   * It read "org: gridline-prod · sandbox" on every tenant — a name nobody is signed in to beside
   * a badge claiming this is not the real one, both fixed strings. And the rail's role label was
   * a chain of three comparisons ending in `: 'Admin'`, so the Contoso seller was labelled an
   * administrator while holding a reader's grants.
   */
  test('names the tenant and the chair it is actually in', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/')

    await expect(page.getByText('org: crm-northwind')).toBeVisible()
    await expect(page.getByText('gridline-prod')).toHaveCount(0)

    await signIn(page, 'contoso')
    await page.goto('/')

    await expect(page.getByText('org: crm-contoso')).toBeVisible()
    await expect(page.locator('nav[aria-label="Applications"]')).toContainText('Contoso')
  })

  /**
   * The permission screen answers "what may I do", from the token.
   *
   * Seven objects by four profiles of `read`/`write`/`admin` used to sit here, describing a
   * per-object model this system does not have: authorization is a scope, checked on the
   * capability, and it is the same scope whatever the object is.
   */
  test('says which permissions this token holds', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/setup/permissions')

    const approve = page.getByRole('row', { name: /crm\.discount\.approve/ })

    await expect(approve).toContainText('not held')

    await signIn(page, 'manager')
    await page.goto('/setup/permissions')

    await expect(page.getByRole('row', { name: /crm\.discount\.approve/ })).toContainText('held')
  })
})

test.describe('setup, opened by somebody who has configured nothing', () => {
  /**
   * The page-layout screen showed a layout that was not anybody's.
   *
   * Three sections and twelve fields came out of `fixtures/objects` — "Forecast Category",
   * "Weighted Amount", "Next Step" — headed "Opportunity layout · 3 sections" and drawn as chips
   * with drag handles and `draggable`. Nothing in this build stores a page layout, so there was
   * nowhere for a drag to go and nothing that tried to send one. An empty organisation was shown
   * a fully configured record page and invited to rearrange it.
   */
  test('is not shown a page layout that belongs to nobody', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/setup/layout')

    await expect(page.locator('main')).toContainText('does not store page layouts')

    // The prototype's fields, none of which this tenant — or any tenant — has declared.
    for (const invented of ['Forecast Category', 'Weighted Amount', 'Next Step']) {
      await expect(page.locator('main'), invented).not.toContainText(invented)
    }

    // A grab handle is a promise. There is nothing here to pick up.
    await expect(page.locator('main [draggable=true]')).toHaveCount(0)
  })

  /**
   * A refused read is not a step nobody has done.
   *
   * Three of the seven onboarding steps read `/config`, which needs `crm.admin`. Every count came
   * through as `?? 0`, so this seller was told "0 of 5 done" and shown "Until this is done" beside
   * three questions the server had refused to answer.
   */
  test('is told which onboarding steps it may not read, not that they are undone', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/setup/onboarding')

    await expect(page.locator('main')).toContainText('you may not read')

    await page.getByRole('button', { name: /Business hours and SLA/ }).click()
    await expect(page.locator('main')).toContainText('may not read whether this is done')

    // The denominator drops the ones nobody was allowed to look at, rather than scoring them nought.
    await expect(page.locator('main')).not.toContainText('0 of 5 done')
  })

  /**
   * The permissions screen made a claim about field-level security out of a read that failed.
   *
   * With no boundary over `describe`, a refusal produced "0 · resolved by the server for this
   * caller" above "Every declared field is readable and writable by this token" — which is the
   * one sentence somebody opens this screen to check.
   */
  test('does not report an unrestricted token from a schema read that failed', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.route('**/api/v1/crm/describe', (route) => route.abort())
    await page.goto('/setup/permissions')

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('readable and writable by this token')

    // The half that is about the token and not about the schema still answers.
    await expect(page.getByRole('row', { name: /crm\.admin/ })).toContainText('not held')
  })
})

test.describe('an administrator changing the schema', () => {
  /**
   * The two forms that declared the sample's headline feature, one of which did not exist.
   *
   * "Declare the field" toasted `${label} declared on ${object}` and never called the API — so
   * the field appeared on no screen, including the table beside the form — and it offered ten
   * types of which six (`email`, `phone`, `currency`, `percent`, `lookup`, `formula`) are words
   * this backend has never heard of. Declaring an *object* had no control anywhere, on the screen
   * whose subtitle is "entities this build has never heard of, declared at run time".
   */
  test('declares an object and a field, and both come back from the server', async ({ page }) => {
    await signIn(page, 'manager')

    const stamp = Date.now()
    const object = `widget_${stamp}`

    await page.goto('/setup/objects')
    await page.getByLabel('Name').fill(object)
    await page.getByLabel('Label').fill(`Widget ${stamp}`)
    await page.getByRole('button', { name: 'Declare the object' }).click()

    await expect(toast(page)).toContainText(object)

    // Read back from `describe`, not from what the form was holding.
    await expect(page.locator('tbody tr', { hasText: object })).toContainText('declared at run time')

    await page.goto('/setup/fields')
    await page.getByRole('button', { name: `Widget ${stamp}` }).click()
    await page.getByLabel('Name').fill('grade')
    await page.getByLabel('Label').fill('Grade')
    await page.getByLabel('Type', { exact: true }).selectOption('Picklist')
    await page.getByLabel('Options').fill('gold, silver')
    await page.getByRole('button', { name: 'Declare the field' }).click()

    await expect(toast(page)).toContainText(`grade is now a field of Widget ${stamp}`)

    // The values are the server's, read back through `describe` — a picklist with no options is
    // the shape this form exists to refuse.
    await expect(page.locator('tbody tr', { hasText: 'grade' })).toContainText('gold · silver')
  })

  /** The types offered are the seven the backend can parse, and no others. */
  test('offers no field type the server would refuse', async ({ page }) => {
    await signIn(page, 'manager')
    await page.goto('/setup/fields')

    const types = await page.getByLabel('Type', { exact: true }).locator('option').allInnerTexts()

    expect(types.sort()).toEqual([
      'Boolean',
      'Date',
      'MultiPicklist',
      'Number',
      'Picklist',
      'Reference',
      'Text',
    ])
  })

  /**
   * A caller without `crm.admin` is told by the server, in the server's words.
   *
   * The control is offered rather than hidden: declaring an object is an act a representative can
   * reasonably attempt, and the refusal naming the capability and the permission is a better
   * answer than a screen with a form missing from it.
   */
  test('is refused in the server’s own words when it holds no admin grant', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/setup/objects')

    await page.getByLabel('Name').fill(`nope_${Date.now()}`)
    await page.getByLabel('Label').fill('Nope')
    await page.getByRole('button', { name: 'Declare the object' }).click()

    // In the page and not only in a toast. A toast is gone in under three seconds, and a refusal
    // somebody looked away from is a form that appears to have done nothing.
    await expect(page.locator('main')).toContainText('crm.custom.define_object')
    await expect(page.locator('main')).toContainText('crm.admin')

    // A 403 is settled. Asking again produces the same 403 for ever.
    await expect(page.getByRole('button', { name: 'Try again' })).toHaveCount(0)
  })
})

test.describe('every setup screen whose schema read fails', () => {
  /**
   * A schema that never arrived is not a tenant with an empty schema.
   *
   * Five screens read `describe` through `?? []` or `?? undefined` and drew a confident page from
   * it: "Entities · reading…" above nothing with "0 edges" beside it, "Opportunity fields · 0"
   * above a form offering to add one, and a list-view form that rendered a heading and no body at
   * all because neither of its two branches matched an error.
   */
  test('says the read failed rather than drawing an empty schema', async ({ page }) => {
    await signIn(page, 'manager')
    await page.route('**/api/v1/crm/describe', (route) => route.abort())

    for (const path of ['/setup/objects', '/setup/fields', '/setup/layout', '/setup/schema', '/setup/list-views']) {
      await page.goto(path)

      await expect(page.getByRole('alert').first(), path).toBeVisible()
      await expect(page.locator('main'), path).not.toContainText('0 edges')
    }
  })
})

test.describe('a data-quality screen whose rows cannot be read', () => {
  /**
   * "Nothing here is measurable yet" was advice, and it was being given about rows this client
   * never saw. Five reads fed the screen and every one came through as `?? []`, so a refusal
   * produced the same empty state as an organisation that genuinely has nothing.
   */
  test('names the entities it could not score instead of scoring nothing quietly', async ({ page }) => {
    await signIn(page, 'rep')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.goto('/setup/quality')

    await expect(page.getByText('Accounts could not be read')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Nothing here is measurable yet')
  })
})

test.describe('the executive screens, which drew figures nobody sent', () => {
  /**
   * A KPI is drawn in the unit its source produces, and captioned the way the server named it.
   *
   * The board pack rendered every scorecard figure with `pct`, so an open pipeline of 1,626,000
   * appeared as "1626000%" against a target of "2000000%" — the scorecard screen had been fixed
   * and the page a board actually reads had not. Beside it, the direction was compared against
   * `'Up'`, a value `KpiDirection` has never carried, so all five KPIs were captioned "lower is
   * better" including won revenue.
   */
  test('reads a KPI in its own unit and names the direction the server sent', async ({ page }) => {
    await signIn(page, 'director')
    await page.goto('/exec/board')

    await expect(page.getByRole('heading', { name: 'Scorecard' })).toBeVisible()

    // Nothing on this page is a percentage in the tens of thousands. A money figure drawn with
    // `pct` is, and there is no other way to produce one.
    await expect(page.locator('main')).not.toContainText(/\d{5,}%/)

    // Both halves of the vocabulary appear, which they cannot when the comparison never matches.
    await expect(page.locator('main')).toContainText('higher is better')
    await expect(page.locator('main')).toContainText('lower is better')
  })

  /**
   * The funnel is the published process, not three fractions of one total.
   *
   * "Early", "Mid" and "Late" held 42%, 34% and 24% of the open value — written in this client,
   * identical on every tenant and in every period, under a heading reading "open deals by stage".
   * Asserted against the stages the tenant actually published rather than against any names this
   * file knows.
   */
  test('groups the pipeline by the stages the tenant published', async ({ page }) => {
    await signIn(page, 'director')

    await page.goto('/setup/stages')

    const stages = page.getByRole('table', { name: 'Published stages' }).locator('tbody tr')

    await expect(stages.first()).toBeAttached()

    const published = await stages.locator('td:nth-child(2)').allInnerTexts()

    await page.goto('/exec')

    // The funnel is behind two reads, and `allInnerTexts` does not wait for anything — without
    // this the assertion below reads an empty list and passes whatever the screen drew.
    const funnel = page.getByRole('table', { name: 'Open pipeline by stage' }).locator('tbody tr')

    await expect(funnel.first()).toBeAttached()

    const drawn = await funnel.locator('td:first-child').allInnerTexts()

    expect(drawn.length).toBeGreaterThan(0)

    for (const stage of drawn) {
      expect(published, `${stage} is not a stage this tenant published`).toContain(stage)
    }

    // The invented bands and the invented captions under them.
    await expect(page.locator('main')).not.toContainText('prospecting → qualify')
  })

  /**
   * No trend, because nothing serves one.
   *
   * Four sparklines carried eight weeks of numbers written in the file — the same rising line on
   * every tenant — and one of them, "Weighted", was the open value times 0.36. A trend is the one
   * chart on a page that cannot be checked against anything else, which is why an invented one
   * survives.
   */
  test('draws no eight-week trend and says why', async ({ page }) => {
    await signIn(page, 'director')
    await page.goto('/exec/insights')

    await expect(page.locator('main')).toContainText('no endpoint returns a series')
    await expect(page.locator('main')).not.toContainText('the last eight weeks')

    // The row whose figure was a ratio from nowhere.
    await expect(page.locator('main')).not.toContainText('Weighted')
  })

  /**
   * Every figure on the forecast comes from `/entities`, and only `/board` was guarded.
   *
   * With the deals read refused, the screen reported "$0 open · 0 deal(s) with no outcome yet"
   * over four empty bands — a forecast of nothing, made from a request that never answered, and
   * it stayed there.
   */
  test('says the deals could not be read rather than forecasting nothing', async ({ page }) => {
    await signIn(page, 'director')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.goto('/exec/forecast')

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('deal(s) with no outcome yet')
  })

  /**
   * `hidden` empties a boundary; it does not empty the panel around it.
   *
   * A tenant with no periods was told so, and then shown two headed boxes with nothing inside
   * them — which reads as two panels that failed underneath the sentence explaining why they
   * could not have.
   */
  test('shows no empty panels under the sentence explaining the emptiness', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/exec/reviews')

    await expect(page.getByText('No periods have been declared')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('What was last said')
    await expect(page.locator('main')).not.toContainText('Record a review')
  })
})

test.describe('my work, where a week and a phone were drawn from nothing', () => {
  /** The uuid this sample's rows are owned by — `SessionProvider` states it for every persona. */
  const OWNER = '33333333-3333-3333-3333-333333333333'

  /** An activity page of this client's own making, so the two settings can differ. */
  async function activitiesAre(page: Page, records: unknown[]) {
    await page.route('**/api/v1/crm/entities', async (route) => {
      const body = route.request().postDataJSON() as { entity?: string } | null

      if (body?.entity !== 'Activity') {
        return route.fallback()
      }

      await route.fulfill({ json: { records, redacted: [], nextCursor: null } })
    })
  }

  /** A day inside the week the reader is in, so the row lands on the grid rather than under it. */
  function thisWednesday(): string {
    const day = new Date()

    day.setDate(day.getDate() - ((day.getDay() + 6) % 7) + 2)
    day.setHours(10, 0, 0, 0)

    return day.toISOString()
  }

  /**
   * "Whose week" asked whose, and it had been asking something else entirely.
   *
   * Mine filtered on `status === 'Open'`: it hid everybody's finished activities and showed
   * everybody's unfinished ones. Both settings were about state and neither was about ownership,
   * which no seeded tenant could show — every seeded row is open and owned by the same person.
   */
  test('filters the calendar by who owns an activity, not by whether it is open', async ({ page }) => {
    await signIn(page, 'rep')

    await activitiesAre(page, [
      {
        recordId: 'a-mine',
        values: {
          subject: 'Finished, and mine',
          kind: 'Task',
          due_at: thisWednesday(),
          owner_id: OWNER,
          status: 'Completed',
        },
      },
      {
        recordId: 'a-theirs',
        values: {
          subject: 'Open, and somebody else"s',
          kind: 'Task',
          due_at: thisWednesday(),
          owner_id: '99999999-9999-9999-9999-999999999999',
          status: 'Open',
        },
      },
    ])

    await page.goto('/work/calendar')

    // Mine is mine whether it is finished or not, and is only mine.
    await expect(page.locator('main')).toContainText('Finished, and mine')
    await expect(page.locator('main')).not.toContainText('Open, and somebody else')

    await page.getByRole('button', { name: 'Team', exact: true }).click()

    await expect(page.locator('main')).toContainText('Open, and somebody else')
    await expect(page.locator('main')).toContainText('Finished, and mine')
  })

  /**
   * Ten rows of empty cells were what a failed read looked like, and what an empty week looked
   * like, and there was no telling them apart.
   */
  test('says the week is empty rather than drawing an empty grid', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/work/calendar')

    await expect(page.getByText(/falls in this week/)).toBeVisible()
  })

  test('says so when the activity read fails, instead of drawing the week anyway', async ({ page }) => {
    await signIn(page, 'rep')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.goto('/work/calendar')

    await expect(page.getByRole('alert')).toBeVisible()

    // The hour gutter is the grid. It has no business being drawn over a read that failed.
    await expect(page.locator('main')).not.toContainText('08:00')
  })

  /**
   * The phone preview's own inventions: a clock reading 09:41 at every hour of the day, and a
   * "Recent" tab that was the first six rows in primary-key order — rendering nothing at all on a
   * tenant with none.
   */
  test('shows the reader"s own clock, and a Recent tab that says when it has nothing', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/work/mobile')

    await expect
      .poll(async () => {
        const now = await page.evaluate(() =>
          new Date().toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }),
        )

        return (await page.locator('main').innerText()).includes(now)
      })
      .toBe(true)

    await page.getByRole('button', { name: 'Recent' }).click()

    await expect(page.locator('main')).toContainText('nothing has moved recently')
  })
})

test.describe('planning, where a screen spoke for a tenant it had not asked about', () => {
  /** The period the seeded plans are committed against. The current one has none. */
  async function openPlanningAt(page: Page, path: string, period: string) {
    await page.goto(path)
    await page.getByRole('button', { name: period, exact: true }).click()
  }

  /**
   * The capacity table was outside every boundary, over `quota.data ?? []`.
   *
   * An organisation that has declared no periods makes neither read, so nothing was pending and
   * nothing had failed — and the table printed its empty message as a finding: "nobody carries a
   * revenue number this period", underneath the sentence saying no period exists.
   */
  test('says nothing about the capacity of a tenant it never asked about', async ({ page }) => {
    await signIn(page, 'contoso')
    await page.goto('/plan/operations')

    await expect(page.getByText('No periods have been declared')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Nobody carries a revenue number')
    await expect(page.locator('main')).not.toContainText('By team')
  })

  /**
   * A team is a manager, so the org chart is half of every figure on that screen — and only the
   * quotas were guarded.
   *
   * With `/org/chart` refused nobody has a manager, which `capacityOf` cannot tell from a flat
   * organisation: all of them collapse into one "Top of the line" row and the screen draws a
   * single team that does not exist, carrying the whole company's number.
   */
  test('reports a refused org chart instead of inventing one team for everybody', async ({ page }) => {
    await signIn(page, 'manager')
    await page.route('**/api/v1/crm/org/chart', (route) => route.abort())

    await page.goto('/plan/operations')

    await expect(page.getByRole('alert').first()).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Top of the line')
  })

  /**
   * Every planning screen asks for a period first, and a refused period list left them blank.
   *
   * Each hook takes a period and asks nothing for null, so a disabled query is pending for ever
   * and a skeleton is `aria-hidden`: the page was a heading over white space. "This organisation
   * has not declared a period" is not the answer either — that is a claim about the tenant made
   * from a request that failed.
   */
  test('says the period list was refused rather than showing an empty portfolio', async ({ page }) => {
    await signIn(page, 'manager')
    await page.route('**/api/v1/crm/planning/periods/list', (route) =>
      route.fulfill({
        status: 403,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          title: 'That was refused',
          detail: 'The caller may not read the periods of this tenant.',
          status: 403,
          code: 'authorization.permission_denied',
        }),
      }),
    )

    await page.goto('/plan/portfolio')

    await expect(page.getByRole('alert').first()).toContainText('may not read the periods')
    await expect(page.locator('main')).not.toContainText('No periods have been declared')
  })

  /**
   * The qualification checklist is a closed eight and the panel counted the rows it was sent.
   *
   * `/planning/plan` returns only what somebody has recorded, so an unqualified deal arrived as
   * an empty list and the panel read "0 of 0 answered" — the sentence a finished checklist
   * produces — while the portfolio's readiness column, which divides by the vocabulary, read 0/8
   * about the same deal on the same afternoon.
   */
  test('counts the qualification against the whole vocabulary, as the roll-up does', async ({ page }) => {
    await signIn(page, 'manager')
    await openPlanningAt(page, '/plan/opportunities', 'FY26 Q3')

    await expect(page.locator('main')).toContainText(/\d of 8 answered/)

    // Every element is a row whether or not anybody has answered it: the unanswered ones are the
    // entire value of a checklist.
    await expect(page.getByRole('row', { name: /Economic buyer/ })).toBeVisible()
    await expect(page.getByRole('row', { name: /Paper process/ })).toBeVisible()
  })

  /**
   * `/planning/qualifications` has been there throughout, and this screen offered no way to reach
   * it: it read the answers and could only ever report a gap that never closed.
   *
   * The count in the toast is the server's, not the one this page just drew.
   */
  test('records a qualification answer and reports the server"s own count', async ({ page }) => {
    await signIn(page, 'manager')
    await openPlanningAt(page, '/plan/opportunities', 'FY26 Q3')

    const champion = page.getByRole('row', { name: /Champion/ })

    await champion.getByRole('button').click()
    await page.getByLabel('Known').selectOption('yes')
    await page.getByLabel('Note').fill('Their compliance officer wants this shipped.')
    await page.getByRole('button', { name: 'Record' }).click()

    await expect(toast(page)).toContainText(/of 8 answered/)
    await expect(champion).toContainText('yes')
  })

  /**
   * Setting a strategy needs `crm.admin` and the form was offered to everybody.
   *
   * A representative filled it in, pressed the button and was told they may not — on the screen
   * that sets the number every executive surface rolls up to.
   */
  test('does not offer the strategy form to somebody the server refuses', async ({ page }) => {
    await signIn(page, 'rep')
    await openPlanningAt(page, '/plan/strategy', 'FY26 Q3')

    await expect(page.getByRole('button', { name: 'Set the strategy' })).toHaveCount(0)
    await expect(page.locator('main')).toContainText('needs crm.admin')

    // The vision is still theirs to read. Hiding the form is not hiding the period.
    await expect(page.locator('main')).toContainText('Prove the platform')
  })
})

test.describe('the quote builder, which took typing and kept none of it', () => {
  /**
   * Issues a quote with a real discount and opens the builder on it.
   *
   * <strong>Its own quote, because the seeded ones disagree.</strong> Twenty of this tenant's
   * quotes are discounted and twenty-one are not, and the list is ordered by id — so a test that
   * opened "the first Issued quote" would assert against a nought discount about half the time
   * and pass without touching the arithmetic it exists for. 3,000 off 24,000 is 12.5%, under the
   * 15% threshold, so the quote comes back Issued rather than Draft.
   */
  async function openBuilder(page: Page): Promise<void> {
    const company = await captureAndConvert(page)

    await openFirstRecord(page, '/records/opportunity', company)
    await page.getByRole('button', { name: 'New quote' }).click()
    await page.getByLabel('Item 1').fill('PLAT')
    await page.getByLabel('Quantity').fill('2')
    await page.getByLabel(/Unit price/).fill('12000')
    await page.getByLabel(/^Discount/).fill('3000')
    await page.getByRole('button', { name: 'Issue quote' }).click()

    await expect(page).toHaveURL(/\/records\/quote\//)
    await page.getByRole('button', { name: 'Open builder' }).click()
    await expect(page.getByRole('table', { name: 'Quote lines' }).locator('tbody tr')).toHaveCount(1)
  }

  /** A figure by its label, off the rendered page. The totals are a two-column grid. */
  async function money(page: Page, label: string): Promise<number> {
    const shown = await page.locator('main').innerText()
    const found = new RegExp(`${label}\\s*\\n?−?\\$([0-9,]+)`).exec(shown)

    expect(found, `no figure beside '${label}'`).not.toBeNull()

    return Number(found![1]!.replace(/,/g, ''))
  }

  /**
   * The totals panel said what the quote would have been if nobody had discounted it.
   *
   * <strong>Headed "derived, never typed", beside the record's own figures.</strong> It summed the
   * lines and applied a per-line discount of nought — a column the schema does not have — so a
   * quote the server priced at 21,000 after 3,000 off reported a net total of 24,000 and an
   * effective rate of 0.0%. Two numbers the tenant has never held, on the screen a seller opens to
   * read the price they are about to quote, one panel away from the true one.
   */
  test('shows the price the server set, not the one it would be with no discount', async ({
    page,
  }) => {
    await signIn(page, 'rep')
    await openBuilder(page)

    const list = await money(page, 'List price')
    const given = await money(page, 'Discount given')
    const net = await money(page, 'Net total')
    const recorded = await money(page, 'Recorded total')

    expect(given, 'the fixture must be discounted or this asserts nothing').toBeGreaterThan(0)
    expect(list - given).toBe(net)
    expect(net).toBe(recorded)
    await expect(page.locator('main')).not.toContainText('0.0%')
  })

  /**
   * A table of inputs is a promise, and this build cannot keep it.
   *
   * <strong>Every cell was a text field and there was an "Add line" button above them.</strong>
   * Nothing was written: the manifest's whole quote surface is issue, approve-the-discount and
   * place-the-order, and the first inserts a quote with its lines rather than updating one. What
   * the screen offered instead of saying so was a note in the panel header — "editing here changes
   * nothing on it" — over a table somebody had already typed into by the time they read it.
   */
  test('offers no control that implies a save until the reader asks for a sandbox', async ({
    page,
  }) => {
    await signIn(page, 'rep')
    await openBuilder(page)

    const lines = page.getByRole('table', { name: 'Quote lines' })

    await expect(lines.getByRole('textbox')).toHaveCount(0)
    await expect(lines.getByRole('spinbutton')).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Add line' })).toHaveCount(0)

    // Asked for, and then said out loud rather than noted over the table.
    await page.getByRole('button', { name: 'Model a change' }).click()

    await expect(page.locator('main')).toContainText('nothing in it is written')
    await expect(lines.getByRole('textbox')).not.toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Add line' })).toBeVisible()

    // And the sandbox has the one capability that takes lines behind it.
    await expect(page.getByRole('button', { name: 'Issue as a new quote' })).toBeEnabled()

    const before = await money(page, 'Net total')

    await page.getByRole('button', { name: 'Discard' }).click()

    await expect(lines.getByRole('textbox')).toHaveCount(0)
    expect(await money(page, 'Net total')).toBe(before)
  })

  /**
   * The sandbox's lines reach the form that can actually price them.
   *
   * A what-if with nowhere to go is the reason the table was editable and dead. Issuing writes a
   * new quote against the same opportunity and leaves this one alone, which is the only thing the
   * server offers — so the modelled lines arrive in the drawer rather than being retyped.
   */
  test('carries the modelled lines into the quote it can issue', async ({ page }) => {
    await signIn(page, 'rep')
    await openBuilder(page)

    await page.getByRole('button', { name: 'Model a change' }).click()
    await page.getByRole('button', { name: 'Issue as a new quote' }).click()

    await expect(page.getByLabel('Item 1')).toHaveValue('PLAT')
    await expect(page.getByLabel('Quantity').first()).toHaveValue('2')
  })
})

test.describe('the panels that drew an empty tenant from a failed read', () => {
  /**
   * The landing page reported a quarter of nothing.
   *
   * <strong>`useConsole` has carried an `error` throughout and the screen read neither it nor the
   * pending flag.</strong> With `/entities` refused, the first screen every persona opens on said
   * €0 open, €0 weighted, 0 deals, 0 tasks, an empty funnel and "Nothing is open in this filter" —
   * nine confident sentences about rows nobody had managed to read, and each one is what a seller
   * would take to mean their quarter is empty.
   */
  test('says the console could not be read rather than reporting an empty pipeline', async ({
    page,
  }) => {
    await signIn(page, 'rep')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.goto('/')

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Nothing is open in this filter')
    await expect(page.locator('main')).not.toContainText('open deals in this filter')
  })

  /**
   * Only the process read was guarded on the board.
   *
   * With the stages published and the opportunities refused, every lane drew a count of 0 over a
   * total of €0 — a whole pipeline reported as empty, in the view a seller scans to decide there
   * is nothing to work on.
   */
  test('says the board could not be read rather than drawing empty lanes', async ({ page }) => {
    await signIn(page, 'rep')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.goto('/kanban')

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Discovery')
  })

  /**
   * The record page's two lists, on a refusal rather than on an absence.
   *
   * Both already distinguish the two and neither had a test that made them prove it: an aborted
   * read has to reach the panel as a refusal, not as "nothing has been logged against this
   * record" — which is the sentence that tells a seller a customer has never been called.
   */
  test('tells a refused activity read from a record nothing has happened to', async ({ page }) => {
    await signIn(page, 'rep')

    await openFirstRecord(page, '/records/account')
    await page.route('**/api/v1/crm/entities', (route) => route.abort())

    await page.getByRole('tab', { name: /Activity/ }).click()

    await expect(page.getByText('The activity could not be read')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Nothing has been logged')

    await page.getByRole('tab', { name: /Related/ }).click()

    await expect(page.getByText('Contacts could not be read')).toBeVisible()
    await expect(page.locator('main')).not.toContainText('Nothing points at this record')
  })

  /**
   * Two defects in one tile, and each was hiding the other.
   *
   * <strong>It could not be anything but $0.</strong> "Closed won" was counted after the outcome
   * filter, and the console opens with Outcome set to Open — which drops every won deal before
   * the count runs. The note said "won, in this filter", which was true, and was why nobody
   * looked at a figure that was structurally incapable of moving.
   *
   * <strong>And it was dated to a quarter nothing implemented.</strong> The horizon filter had an
   * upper bound and no lower one, so the deal below — won two years ago, closing before the end
   * of this quarter — belonged in the figure, and the tile had to be relabelled around it. The
   * window has a near end now, so the same deal is outside the quarter and inside all open: this
   * asserts both, which is the only way to tell a bounded window from an empty one.
   *
   * The seeded tenant has no won deals at all, so all of this was wrong on data no seed could
   * produce and no reader could check. This supplies one.
   */
  test('leaves a deal won two years ago outside the quarter, and finds it under all open', async ({
    page,
  }) => {
    await signIn(page, 'rep')

    await page.route('**/api/v1/crm/entities', async (route) => {
      const body = route.request().postDataJSON() as { entity?: string } | null

      if (body?.entity !== 'Opportunity') {
        return route.fallback()
      }

      await route.fulfill({
        json: {
          records: [
            {
              recordId: '11111111-1111-1111-1111-111111111111',
              values: {
                opportunity_id: '11111111-1111-1111-1111-111111111111',
                name: 'A deal won two years ago',
                stage: 'Discovery',
                amount: '90000.0000',
                probability: '100',
                expected_close: '2024-03-01',
                outcome: 'Won',
                owner_id: '33333333-3333-3333-3333-333333333333',
              },
            },
          ],
          redacted: [],
          nextCursor: null,
        },
      })
    })

    await page.goto('/')

    // Out of the window: it closed in another year, and "This quarter" is a quarter now.
    await expect(page.locator('main')).not.toContainText('$90k')
    await expect(page.locator('main')).not.toContainText('QTD')
    await expect(page.locator('main')).not.toContainText('quarter to date')

    // And in the one horizon that keeps history, which is the only one that ever should have.
    await page.getByRole('button', { name: 'All open' }).click()

    await expect(page.locator('main')).toContainText('$90k')
  })
})

test.describe('search, where a chip spoke for the server', () => {
  /**
   * "Nothing matches" was said over results the reader had filtered out themselves.
   *
   * The kind chips narrow what the panel draws and nothing narrows what the server found. Pick a
   * kind, then refine the phrase to something of another kind, and the panel reported that the
   * server had matched nothing — over a hit it was holding and had chosen not to draw. That is
   * the one wrong direction: a seller concludes the customer is not in the system and creates
   * them again.
   */
  test('says which chip is hiding the hits, not that there were none', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/search')

    const box = page.getByLabel('What are you looking for')

    // Two kinds come back for this phrase, so there is a chip to pick that is not Everything.
    await box.fill('northwind')
    await page.getByRole('button', { name: 'Opportunity', exact: true }).click()

    // And this one matches a contact and nothing else, so the chip now hides every hit there is.
    await box.fill('petrov')

    await expect(page.locator('main')).not.toContainText('Nothing matches')
    await expect(page.getByText('No opportunity matches')).toBeVisible()

    // The way back is a control, not a chip the reader has to remember pressing.
    await page.getByRole('button', { name: 'Show everything' }).click()

    await expect(page.locator('main')).toContainText('Contact')
  })
})

test.describe('the edit drawer, on a schema that never arrived', () => {
  /**
   * The same defect the five setup screens had, on the drawer that writes.
   *
   * Both of its branches are guarded on `isSuccess`, so a refused `describe` left a drawer with a
   * "Declared fields" heading, empty space and a Save button — indistinguishable from one still
   * loading, and it never stops looking like that.
   */
  test('says the fields could not be read rather than rendering an empty form', async ({ page }) => {
    await signIn(page, 'rep')
    await openFirstRecord(page, '/records/account')

    await page.route('**/api/v1/crm/describe', (route) => route.abort())
    await page.reload()

    await page.getByRole('button', { name: 'Edit' }).click()

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.locator('[role=dialog], aside')).not.toContainText('Nothing has been declared')
  })
})

test.describe('an address this build has no screen for', () => {
  /**
   * Only a browser reaches this one.
   *
   * A deep link that matches nothing has to survive the static host's fallback and be answered by
   * the router, and the router had no not-found component configured — so it drew its own
   * `<p>Not Found</p>`, which inside a CRM reads as the record being missing. Nothing had been
   * asked of the server at all.
   */
  test('says the address is unknown rather than that a record is missing', async ({ page }) => {
    await signIn(page, 'rep')
    await page.goto('/exec/forcast')

    await expect(page.getByText(/no screen at that address/i)).toBeVisible()
    await expect(page.locator('main')).toContainText('/exec/forcast')

    // No server refused anything, so this is not dressed as a refusal.
    await expect(page.getByRole('alert')).toHaveCount(0)

    // The shell survives, and so does the way out.
    await page.getByRole('link', { name: 'Go to the console' }).click()
    await expect(page).toHaveURL(/\/$/)
  })
})

test.describe('the console horizon, which had an upper bound and no lower one', () => {
  /**
   * "This quarter" is the tenant's quarter, by name.
   *
   * <strong>Only a browser sees which quarter the client picked.</strong> The window used to be
   * `close < quarterEnd` with nothing at the near end, so every figure on the landing page was
   * scoped to all of history and the won tile had to be relabelled because it could not mean
   * quarter-to-date. Both ends come from the period the server marks current — a fiscal quarter is
   * whatever an administrator declared, and one computed in the browser would agree with the
   * target, the quota and the roll-up only by luck of the calendar. Read off the executive
   * picker rather than named here: which periods exist is the tenant's business.
   */
  test('names the declared period it is scoped to, and drops the name for all open', async ({
    page,
  }) => {
    await signIn(page, 'rep')

    await page.goto('/exec/board')

    const current = page.getByRole('group', { name: 'Period' }).locator('[aria-pressed=true]')

    await expect(current).toBeVisible()

    const declared = (await current.innerText()).trim()

    await page.goto('/')

    // The filter strip says what the chip cannot: which quarter "This quarter" turned out to be.
    await expect(page.locator('main')).toContainText(declared)

    // And the tile that was relabelled says the period again, which is the claim it lost.
    await expect(page.getByText(`won, closing in ${declared}`)).toBeVisible()

    await page.getByRole('button', { name: 'All open' }).click()
    await expect(page.getByRole('button', { name: 'All open' })).toHaveAttribute(
      'aria-pressed',
      'true',
    )

    // Unbounded is a different window and says so, rather than keeping a period's name over a
    // figure that is no longer inside it.
    await expect(page.locator('main')).toContainText('any close date')
    await expect(page.getByText(`won, closing in ${declared}`)).toHaveCount(0)
  })

  /**
   * A tenant that has declared no periods still gets a bounded quarter, and is told it is the
   * calendar's. The fallback used to be the whole of the filter: unbounded, on every tenant.
   */
  test('falls back to a calendar quarter, named as one, where nothing is declared', async ({
    page,
  }) => {
    await signIn(page, 'contoso')

    await page.goto('/')

    await expect(page.locator('main')).toContainText('this quarter')
  })
})

test.describe('an order, whose money had nowhere to go', () => {
  /**
   * The total is an amount, and it used to be ninety engineer-years.
   *
   * <strong>The heading is the defect.</strong> `Order.total` was mapped onto the object model's
   * one spare numeric field — `Est. Hours`, left over from a design where a work order was an
   * engineering visit — so a €184,000 order rendered as 184,000 hours. Dropping the mapping left
   * the order with no total on any screen at all. Both formatters were always correct and no unit
   * test can see which column a figure is drawn under, which is why this reads the header.
   */
  test('draws its total under Total, on the list, the record and the quote it came from', async ({
    page,
  }) => {
    await signIn(page, 'rep')

    await page.goto('/records/workorder')

    const orders = page.getByRole('table', { name: 'All work orders' })
    const first = orders.locator('tbody tr').first()

    await expect(first).toBeVisible()

    await expect(orders.getByRole('columnheader', { name: /Est. Hours/ })).toHaveCount(0)
    await expect(orders.getByRole('columnheader', { name: /Total/ })).toBeVisible()

    // The figure itself, taken off the page rather than written here: what the order is worth is
    // the tenant's business, and that it is money is this test's.
    const amount = /\$[0-9,]+/.exec(await first.innerText())?.[0]

    expect(amount, 'no amount in the order list').toBeTruthy()

    await first.click()
    await page.getByRole('button', { name: 'Open record' }).click()

    // The same figure on the record page, where it appeared under Scheduling as a duration.
    await expect(page.locator('main')).toContainText(amount as string)
    await expect(page.locator('main')).not.toContainText('Est. Hours')
    await expect(page.locator('main')).not.toContainText('Assigned To')

    // And the related list on the quote, which carried the status alone once the hours column was
    // taken out — an order panel that could not say what the order was worth.
    await openFirstRecord(page, '/records/quote', 'Issued')
    await page.getByRole('tab', { name: /Related/ }).click()

    const related = page.getByRole('table', { name: 'Orders' })

    await expect(related.getByRole('columnheader', { name: /Total/ })).toBeVisible()
    await expect(related.getByRole('columnheader', { name: /Est. Hours/ })).toHaveCount(0)
  })
})
