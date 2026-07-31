import type { Page } from '@playwright/test'
import { test, expect, createDocument, disclose, uploadVersion } from './fixtures'

// The accessibility FLOOR, as tests rather than as good intentions. Task 17 restyled all eight screens,
// and the three things a design pass reliably destroys are the focus ring, the keyboard path through a
// custom control, and the labels that make a control nameable at all. Each gets an assertion here so the
// floor cannot silently rot the next time someone touches index.css.

// A focus ring is present when :focus-visible resolves to a real outline. `outline: none`, a 0px width, or
// a colour equal to the background all read as "no ring" to a keyboard user, and the first two are exactly
// what a CSS reset or a component-level override produces.
async function focusRing(page: Page) {
  return page.evaluate(() => {
    const el = document.activeElement
    if (!el) return null
    const s = getComputedStyle(el)
    return { style: s.outlineStyle, width: s.outlineWidth, color: s.outlineColor }
  })
}

function expectVisibleRing(ring: { style: string; width: string; color: string } | null) {
  expect(ring).not.toBeNull()
  expect(ring!.style).not.toBe('none')
  expect(Number.parseFloat(ring!.width)).toBeGreaterThanOrEqual(2)
  expect(ring!.color).not.toBe('transparent')
  expect(ring!.color).not.toMatch(/rgba\(0, 0, 0, 0\)/)
}

test('keyboard focus stays visible after the restyle, on a link, a button and a field', async ({
  signedIn: page,
}) => {
  // Tab once from a fresh page: the very first stop must be the skip link, and it must become visible when
  // it takes focus — a skip link that stays off-screen while focused is worse than none at all.
  await page.keyboard.press('Tab')
  const skip = page.getByRole('link', { name: 'Skip to content' })
  await expect(skip).toBeFocused()
  await expect(skip).toBeInViewport()
  expectVisibleRing(await focusRing(page))

  // A text field.
  await page.getByLabel('Search documents').focus()
  expectVisibleRing(await focusRing(page))

  // A disclosure summary, which is now the tab stop that reveals every form on the screen. It has to
  // wear the ring like anything else focusable, or the whole progressive-disclosure pass is invisible
  // to a keyboard.
  const newDocument = page.getByTestId('new-document')
  await newDocument.locator('summary').focus()
  expectVisibleRing(await focusRing(page))

  // A primary button, inside that disclosure.
  await disclose(newDocument)
  await page.getByRole('button', { name: 'Create document' }).focus()
  expectVisibleRing(await focusRing(page))

  // And a nav link, which the restyle gave a border rather than a colour — the ring is separate from that.
  await page.getByRole('link', { name: 'Settings', exact: true }).focus()
  expectVisibleRing(await focusRing(page))
})

test('the Actions menu is fully operable from the keyboard, and hands focus back', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Keyboard Floor')
  await uploadVersion(page, documentId, 'base.docx')
  await page.goto(`/documents/${documentId}`)

  const trigger = page.getByRole('button', { name: 'Actions' })
  await expect(trigger).toBeVisible()

  // Opened with the keyboard, not a click.
  await trigger.focus()
  expectVisibleRing(await focusRing(page))
  await page.keyboard.press('Enter')
  await expect(trigger).toHaveAttribute('aria-expanded', 'true')

  // Task 13 built this as a DISCLOSURE, not role="menu", with the documented reason that Tab already walks
  // the items. That is the contract being asserted: the next tab stop is inside the menu, and it has a
  // focus ring of its own.
  await page.keyboard.press('Tab')
  await expect(page.getByTestId('actions-menu').getByRole('button').first()).toBeFocused()
  expectVisibleRing(await focusRing(page))

  // Escape closes it from inside, and focus returns to the trigger — not to the body, which would strand a
  // keyboard user at the top of the page.
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('actions-menu')).toHaveCount(0)
  await expect(trigger).toHaveAttribute('aria-expanded', 'false')
  await expect(trigger).toBeFocused()
})

// Many specs locate controls with getByLabel, so a nameless input breaks the suite as well as the screen.
// This asserts the property directly instead of one label at a time: every control that takes input has an
// accessible name from SOMEWHERE — a <label for>, a wrapping <label>, aria-label or aria-labelledby.
async function namelessControls(page: Page) {
  return page.$$eval(
    'input:not([type="hidden"]), select, textarea',
    (els) =>
      els
        .filter((el) => {
          const labelled = (el as HTMLInputElement).labels?.length
          const aria = el.getAttribute('aria-label')?.trim()
          const by = el.getAttribute('aria-labelledby')
          const named = by ? !!document.getElementById(by)?.textContent?.trim() : false
          // The Import file picker is aria-hidden and out of the tab order on purpose: its real control is
          // the menu item that opens it, so it is not a stop that needs a name.
          if (el.getAttribute('aria-hidden') === 'true') return false
          return !labelled && !aria && !named
        })
        .map((el) => `${el.tagName.toLowerCase()}[${el.getAttribute('type') ?? ''}] ${el.outerHTML}`),
  )
}

test('every input on every screen has a real label', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Labelled')
  await uploadVersion(page, documentId, 'base.docx')

  const screens = [
    '/',
    '/trash',
    '/approvals',
    '/settings',
    `/documents/${documentId}`,
    `/documents/${documentId}/major-versions`,
    `/documents/${documentId}/copies`,
    `/documents/${documentId}/approvals`,
    `/documents/${documentId}/audit`,
    `/documents/${documentId}/compare`,
  ]

  for (const path of screens) {
    await page.goto(path)
    // Wait for the screen's own content, so this never asserts against a half-mounted route.
    await expect(page.locator('[data-testid]').first()).toBeVisible()
    expect(await namelessControls(page), `unlabelled control(s) on ${path}`).toEqual([])
  }

  // And inside a modal, which is where four of the app's fields live.
  await page.goto(`/documents/${documentId}`)
  await page.getByRole('button', { name: 'Actions' }).click()
  await page.getByRole('button', { name: 'Name' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  expect(await namelessControls(page), 'unlabelled control(s) in a modal').toEqual([])
})
