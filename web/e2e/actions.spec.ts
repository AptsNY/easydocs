import type { Browser, APIRequestContext, Page } from '@playwright/test'
import { test, expect, register, signIn, createDocument, fixtureBytes, uploadVersion } from './fixtures'

// E8 at the UI level: "the v1 action set present and functional". Until Task 13 this criterion was
// satisfied by eight API calls in the C# suite — nothing exercised a menu, because there was no menu.
// Each test below drives one action through the real menu against the real API.
//
// Collabora is NOT running here, and is not needed: the editor is a separate product that may legitimately
// be absent in any dev or CI environment. So the Collabora test asserts the iframe's `src` attribute and
// never anything inside the frame.

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'

const row = (page: Page, number: string) =>
  page.locator(`[data-testid="version-row"][data-number="${number}"]`)

// The trigger's accessible name is "Actions for version 0.0.1" (visible "Actions" plus a visually-hidden
// suffix), so it is unambiguous to a screen reader even though there is one per row.
const trigger = (page: Page, number: string) =>
  row(page, number).getByRole('button', { name: 'Actions' })

async function openMenu(page: Page, number: string) {
  await trigger(page, number).click()
  await expect(trigger(page, number)).toHaveAttribute('aria-expanded', 'true')
  return row(page, number)
}

const dialog = (page: Page) => page.getByRole('dialog')

// Seed before navigating: history is fetched on mount, so uploading afterwards would need a reload.
async function seed(page: Page, name: string, fixtures = ['base.docx']) {
  const documentId = await createDocument(page, name)
  const versionIds: string[] = []
  for (const f of fixtures) versionIds.push(await uploadVersion(page, documentId, f))
  await page.goto(`/documents/${documentId}`)
  await expect(row(page, '0.0.1')).toBeVisible()
  return { documentId, versionIds }
}

// A Viewer on this document, in their own browser context. The invitation is accepted from THAT context:
// the accept response rebinds ed_session to the inviting org, and a plain login would bind to the
// invitee's own org, from which this document is cross-org — i.e. a 404. (Same reasoning as
// console.spec.ts; done over the API here rather than through the members panel, which Task 12 owns.)
async function addViewer(browser: Browser, page: Page, request: APIRequestContext, documentId: string) {
  const other = await register(request)
  const res = await page.request.post(`/api/v1/documents/${documentId}/members`, {
    data: { email: other.email, role: 'Viewer' },
  })
  expect(res.ok(), `add member failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const { invitationToken } = (await res.json()) as { invitationToken: string }

  const theirContext = await browser.newContext()
  const theirPage = await theirContext.newPage()
  await signIn(theirPage, other)
  const accept = await theirPage.request.post(`/api/v1/invitations/${invitationToken}:accept`)
  expect(accept.ok(), `accept failed: ${accept.status()} ${await accept.text()}`).toBeTruthy()
  return { theirContext, theirPage }
}

test('1. Open in Collabora frames exactly the minted editorUrl', async ({ signedIn: page }) => {
  const { versionIds } = await seed(page, 'Editable')
  const vid = versionIds[0]

  // Every mint is collected rather than just the first: React StrictMode double-invokes effects in
  // development, so the dev server legitimately mints twice (the editor closes the session it does not
  // render). Asserting the src is ONE OF the minted URLs proves the frame shows a real minted session
  // without pinning the test to a dev-only render count.
  const minted: string[] = []
  page.on('response', (r) => {
    if (r.request().method() !== 'POST' || !r.url().includes(`/versions/${vid}/sessions`)) return
    void r
      .json()
      .then((b: { editorUrl?: string }) => b.editorUrl && minted.push(b.editorUrl))
      .catch(() => {})
  })

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Open in Collabora' }).click()
  await expect(page).toHaveURL(new RegExp(`/versions/${vid}/edit$`))

  // The src attribute only. Nothing serves this URL in this environment, and asserting on frame content
  // would make the test require a product that is legitimately optional.
  const frame = page.getByTestId('editor-frame')
  await expect(frame).toHaveAttribute('src', /^http.+WOPISrc=.+&access_token=.+$/)
  await expect(frame).toHaveAttribute('title', /\S/)
  const src = await frame.getAttribute('src')
  await expect.poll(() => minted).toContain(src)
})

test('2. Import adds a version whose source is Import', async ({ signedIn: page }) => {
  await seed(page, 'Importable')

  const menu = await openMenu(page, '0.0.1')
  const chooser = page.waitForEvent('filechooser')
  await menu.getByRole('button', { name: 'Import' }).click()
  await (
    await chooser
  ).setFiles({ name: 'edited.docx', mimeType: DOCX, buffer: fixtureBytes('edited.docx') })

  await expect(row(page, '0.0.2')).toBeVisible()
  await expect(row(page, '0.0.2')).toHaveAttribute('data-source', 'Import')
})

test('3. Share shows the URL once, and the link resolves with no session', async ({
  signedIn: page,
  browser,
}) => {
  await seed(page, 'Shareable')

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Share' }).click()
  await dialog(page).getByRole('button', { name: 'Create link' }).click()

  const shareUrl = await page.getByTestId('share-url').textContent()
  expect(shareUrl).toMatch(/^\/s\/[A-Za-z0-9_-]{20,}$/)

  // Once means once: the API returns the raw token only from the create call, so neither reopening the
  // dialog nor a reload can resurrect it, and the UI must not pretend otherwise.
  await dialog(page).getByRole('button', { name: 'Close' }).click()
  await expect(dialog(page)).toHaveCount(0)
  const again = await openMenu(page, '0.0.1')
  await again.getByRole('button', { name: 'Share' }).click()
  await expect(page.getByTestId('share-url')).toHaveCount(0)
  await page.reload()
  await expect(page.getByTestId('share-url')).toHaveCount(0)

  const anonContext = await browser.newContext()
  const anonPage = await anonContext.newPage()
  await anonPage.goto(shareUrl!)
  // ShareLanding.tsx is still a stub (Task 17 owns it), so what is legitimately assertable here is that
  // the anonymous route resolves to the SPA shell outside RequireAuth — no redirect to /login...
  await expect(anonPage.getByTestId('share-landing')).toBeVisible()
  await expect(anonPage).toHaveURL(new RegExp(`${shareUrl!}$`))
  // ...and that the same URL is readable as JSON with no cookie at all, which is the contract the
  // finished screen will render.
  const json = await anonPage.request.get(shareUrl!, { headers: { Accept: 'application/json' } })
  expect(json.ok(), `anonymous share fetch failed: ${json.status()}`).toBeTruthy()
  expect((await json.json()) as { documentName: string }).toMatchObject({
    documentName: 'Shareable',
    version: '0.0.1',
  })
  await anonContext.close()
})

// M5 gap: DELETE /api/v1/share-links/{id} shipped in M2 and no client could call it, because the create
// response carries only {token, url} and nothing listed links. A shared document could not be un-shared.
test('3b. an existing share link can be revoked from the Share dialog', async ({
  signedIn: page,
  browser,
}) => {
  await seed(page, 'Revocable')

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Share' }).click()
  await dialog(page).getByRole('button', { name: 'Create link' }).click()
  const shareUrl = (await page.getByTestId('share-url').textContent())!

  // The new link is listed, live, and pointed at the version it was made from.
  const link = dialog(page).getByTestId('share-link-row')
  await expect(link).toHaveCount(1)
  await expect(link).toHaveAttribute('data-state', 'Live')
  await expect(link).toContainText('0.0.1')

  // Still reachable by the recipient...
  const anonContext = await browser.newContext()
  const anonPage = await anonContext.newPage()
  const live = await anonPage.request.get(shareUrl, { headers: { Accept: 'application/json' } })
  expect(live.ok(), `share link should be live: ${live.status()}`).toBeTruthy()

  await link.getByRole('button', { name: /^Revoke the share link/ }).click()

  // ...and dead the moment it is revoked. The row stays, flagged, so "did I revoke that?" has an answer.
  await expect(link).toHaveAttribute('data-state', 'Revoked')
  await expect(link.getByRole('button', { name: /^Revoke the share link/ })).toHaveCount(0)
  const dead = await anonPage.request.get(shareUrl, { headers: { Accept: 'application/json' } })
  expect(dead.status()).toBe(404)

  await anonContext.close()
})

test('4. Download streams the file under the R8 name', async ({ signedIn: page }) => {
  await seed(page, 'Down Load')

  const menu = await openMenu(page, '0.0.1')
  const download = page.waitForEvent('download')
  await menu.getByRole('button', { name: 'Download' }).click()

  // R8: {orgSlug}__{Sanitized_Name}-v{M}.{m}.{r}.docx — the space in "Down Load" becomes an underscore.
  expect((await download).suggestedFilename()).toMatch(/^[a-z0-9-]+__Down_Load-v0\.0\.1\.docx$/)
})

test('5. Name labels a version and the row shows it', async ({ signedIn: page }) => {
  await seed(page, 'Nameable')

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Name' }).click()
  await dialog(page).getByLabel('Version name').fill('Signed original')
  await dialog(page).getByRole('button', { name: 'Save' }).click()

  await expect(row(page, '0.0.1').getByTestId('version-name')).toHaveText('Signed original')
  await page.reload()
  await expect(row(page, '0.0.1').getByTestId('version-name')).toHaveText('Signed original')
})

test('6. Publish renumbers minor then major (R3/R4)', async ({ signedIn: page }) => {
  await seed(page, 'Publishable')

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Publish' }).click()
  await dialog(page).getByLabel('Kind').selectOption('minor')
  await dialog(page).getByRole('button', { name: 'Publish' }).click()

  // Publishing renumbers THAT version and advances the document counter (R6).
  await expect(row(page, '0.1.0')).toBeVisible()
  await expect(row(page, '0.1.0').getByTestId('version-badge')).toContainText('minor')
  await expect(row(page, '0.0.1')).toHaveCount(0)

  const again = await openMenu(page, '0.1.0')
  await again.getByRole('button', { name: 'Publish' }).click()
  await dialog(page).getByLabel('Kind').selectOption('major')
  await dialog(page).getByLabel('Publish name').fill('Board approved')
  await dialog(page).getByRole('button', { name: 'Publish' }).click()

  await expect(row(page, '1.0.0')).toBeVisible()
  await expect(row(page, '1.0.0').getByTestId('version-badge')).toContainText('major · Board approved')
})

test('7. Revert adds a new head and leaves history intact (E11)', async ({ signedIn: page }) => {
  await seed(page, 'Revertible', ['base.docx', 'edited.docx'])
  await expect(row(page, '0.0.2')).toBeVisible()

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Revert' }).click()

  // A revert is a NEW head whose content equals the target's — not a rewrite. E11: history is untouched.
  await expect(row(page, '0.0.3')).toBeVisible()
  await expect(row(page, '0.0.1')).toBeVisible()
  await expect(row(page, '0.0.2')).toBeVisible()
  await expect(row(page, '0.0.3')).toHaveAttribute('data-source', 'Revert')
})

test('8. Push To Copy forks an isolated copy', async ({ signedIn: page }) => {
  const { documentId } = await seed(page, 'Forkable')

  const menu = await openMenu(page, '0.0.1')
  await menu.getByRole('button', { name: 'Push To Copy' }).click()
  await dialog(page).getByLabel('Copy name').fill('Vendor review copy')
  await dialog(page).getByRole('button', { name: 'Create copy' }).click()
  await expect(dialog(page)).toHaveCount(0)

  const res = await page.request.get(`/api/v1/documents/${documentId}/copies`)
  expect(res.ok(), `list copies failed: ${res.status()}`).toBeTruthy()
  const copies = (await res.json()) as { name: string; forkedFromVersionId: string }[]
  expect(copies.map((c) => c.name)).toContain('Vendor review copy')
})

test('9. a Viewer sees only the read-only actions, and the rest are absent (E12)', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const { documentId } = await seed(page, 'Viewer Actions')
  const { theirContext, theirPage } = await addViewer(browser, page, request, documentId)

  await theirPage.goto(`/documents/${documentId}`)
  const menu = await openMenu(theirPage, '0.0.1')

  // Reads. Share is Viewer+ in the API (ShareEndpoints.Create resolves access but never calls CanEdit),
  // so a Viewer may hand out a read-only link to something they can already read.
  await expect(menu.getByRole('button', { name: 'Download' })).toBeVisible()
  await expect(menu.getByRole('button', { name: 'Share' })).toBeVisible()

  // ABSENT, not disabled: a disabled control is still in the DOM and invites a pointer-events bypass.
  for (const label of ['Open in Collabora', 'Import', 'Name', 'Publish', 'Revert', 'Push To Copy'])
    await expect(menu.getByRole('button', { name: label })).toHaveCount(0)

  await theirContext.close()
})

test('10. the menu opens by keyboard, and Escape closes menu and modal alike', async ({
  signedIn: page,
}) => {
  await seed(page, 'Keyboard')
  const button = trigger(page, '0.0.1')

  await button.focus()
  await page.keyboard.press('Enter')
  await expect(button).toHaveAttribute('aria-expanded', 'true')
  await expect(row(page, '0.0.1').getByRole('button', { name: 'Download' })).toBeVisible()

  await page.keyboard.press('Escape')
  await expect(button).toHaveAttribute('aria-expanded', 'false')
  await expect(button).toBeFocused()

  // A modal traps focus and gives it back to the trigger on close.
  await page.keyboard.press('Enter')
  await row(page, '0.0.1').getByRole('button', { name: 'Name' }).click()
  await expect(dialog(page).getByLabel('Version name')).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(dialog(page)).toHaveCount(0)
  await expect(button).toBeFocused()
})
