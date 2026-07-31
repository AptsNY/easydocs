import type { Browser, Page } from '@playwright/test'
import { test, expect, createDocument, uploadVersion } from './fixtures'

// The public share landing (spec §9, §11) — the only screen an outsider ever sees. They have no account,
// no session and no idea what easydocs is, so everything here is asserted from a context with genuinely
// no cookie (`browser.newContext()`), never from the signed-in page with the session hidden.
//
// Before M4.5 this URL answered a browser with raw JSON. Phase A made GET /s/{token} content-negotiate:
// Accept: text/html gets the SPA shell BEFORE any database work (so the shell hit neither audits nor
// counts a view, and an unknown token is indistinguishable from a live one), and the SPA then re-requests
// the same URL as JSON. These tests drive that second hit.

// Creates a live share link on a fresh document's only version, and returns the anonymous /s/{token} URL.
async function share(page: Page, name: string, expiresAt: string | null = null) {
  const documentId = await createDocument(page, name)
  const versionId = await uploadVersion(page, documentId, 'base.docx')
  const res = await page.request.post(`/api/v1/versions/${versionId}/share-links`, {
    data: { expiresAt },
  })
  expect(res.ok(), `create share link failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  return ((await res.json()) as { url: string }).url
}

// A brand-new browser context: no ed_session cookie, no localStorage, nothing. The recipient's real
// situation, and the only honest way to test a screen whose whole point is having no session.
async function asOutsider(browser: Browser, url: string) {
  const context = await browser.newContext()
  const page = await context.newPage()
  await page.goto(url)
  return { context, page }
}

test('the recipient sees the document name and the version number', async ({ signedIn, browser }) => {
  const url = await share(signedIn, 'Client Draft')
  const { context, page } = await asOutsider(browser, url)

  await expect(page.getByTestId('share-document-name')).toHaveText('Client Draft')
  await expect(page.getByTestId('share-version')).toContainText('0.0.1')
  // Still on the share URL: no session, and no bounce to /login.
  await expect(page).toHaveURL(new RegExp(`${url}$`))

  await context.close()
})

test('the recipient can download the file with no account', async ({ signedIn, browser }) => {
  const url = await share(signedIn, 'Down Load')
  const { context, page } = await asOutsider(browser, url)

  const download = page.waitForEvent('download')
  await page.getByTestId('share-download').click()

  // R8: {orgSlug}__{Sanitized_Name}-v{M}.{m}.{r}.docx — the space in "Down Load" becomes an underscore.
  // The public route serves the same filename as the authenticated one; there is no PDF here.
  expect((await download).suggestedFilename()).toMatch(/^[a-z0-9-]+__Down_Load-v0\.0\.1\.docx$/)

  await context.close()
})

// A dead link and a made-up one must read the same. The API already answers both with an identical
// 404 problem+json from ResolveLiveAsync, and the screen must not undo that by wording them differently
// — "expired" versus "never existed" is an oracle for guessing tokens.
//
// ponytail: the dead link here is EXPIRED, not revoked — revoked and expired are the same branch of the
// same query (RevokedAt == null && ExpiresAt > now), so one of them covers this screen. The revoke path
// itself is driven through the UI in actions.spec.ts ("3b"), now that GET /documents/{id}/share-links
// makes the row id reachable.
test('a dead link and an unknown token show the same human message', async ({ signedIn, browser }) => {
  const dead = await share(signedIn, 'Expired Draft', '2020-01-01T00:00:00Z')

  const expired = await asOutsider(browser, dead)
  const unknown = await asOutsider(browser, '/s/definitely-not-a-real-token')

  for (const { page } of [expired, unknown]) {
    const gone = page.getByTestId('share-unavailable')
    await expect(gone).toBeVisible()
    // A human sentence, not a status code, and not a blank page.
    await expect(gone).toContainText(/no longer available/i)
    await expect(gone).toHaveAttribute('role', 'alert')
    await expect(page.getByTestId('share-document-name')).toHaveCount(0)
  }

  // Character-identical, so nothing on screen distinguishes a real token from a fake one.
  expect(await expired.page.getByTestId('share-unavailable').textContent()).toBe(
    await unknown.page.getByTestId('share-unavailable').textContent(),
  )

  await expired.context.close()
  await unknown.context.close()
})

test('the app shell is absent: this person is not a user of this install', async ({
  signedIn,
  browser,
}) => {
  const url = await share(signedIn, 'No Chrome')
  const { context, page } = await asOutsider(browser, url)
  await expect(page.getByTestId('share-document-name')).toBeVisible()

  // No org identity, no navigation, no session controls. The recipient has no account here, so every one
  // of these would be either a lie or a dead end.
  await expect(page.getByTestId('org-name')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Documents' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Approvals' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Settings' })).toHaveCount(0)
  await expect(page.locator('.shell')).toHaveCount(0)

  await context.close()
})
