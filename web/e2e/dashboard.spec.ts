import type { Page } from '@playwright/test'
import { test, expect, disclose, register, signIn } from './fixtures'

// The dashboard against the real API: folder tree (E1 nesting/move), tiles (E2 first version is
// 0.0.1), server-side search, and the trash round trip that only became reachable in M4.5 — before
// the trashed=true listing existed, DELETE + :restore were a one-way door unless you kept the GUID.

// Generated once from DocxFixtures.Base()/Edited()/EditedPlusEcho() (the C# suite's canonical minimal
// OOXML) and committed: Playwright cannot build a .docx, and the 5-byte fakes M0 used do not parse.
//
// Three distinct files for two reasons. (1) CommitSaveAsync dedupes on sha (spec §5.2), so uploading
// the same bytes twice is a deliberate no-op and never yields a second version. (2) No two tests may
// upload the SAME first-time sha concurrently: VersioningService's Blobs check-then-insert is not
// guarded against the unique violation, so on a cold database one of two racing uploads 500s. Each
// uploading test therefore owns its bytes.
const DOCX = 'e2e/fixtures/base.docx'
const DOCX_EDITED = 'e2e/fixtures/edited.docx'
const DOCX_ECHO = 'e2e/fixtures/edited-plus-echo.docx'

const tree = (page: Page) => page.getByTestId('folder-tree')
const node = (page: Page, name: string) =>
  page.locator(`[data-testid="folder-node"][data-name="${name}"]`)
// A node's OWN row, so a control lookup never reaches a descendant folder's identical control.
const row = (page: Page, name: string) =>
  page.locator(`[data-testid="folder-node"][data-name="${name}"] > [data-testid="folder-row"]`)
const tile = (page: Page, name: string) =>
  page.locator(`[data-testid="document-tile"][data-name="${name}"]`)

// The dashboard's writes live behind disclosures now, so each of these opens the panel it needs first
// — one click, exactly where a person clicks. The assertions below are unchanged.
const newFolderForm = (page: Page) => tree(page).getByTestId('new-folder-form')
const newDocumentForm = (page: Page) => page.getByTestId('new-document')
// "New document" and "Import document" both label a field "Document name" -- a bare
// page.getByLabel('Document name') matches both and fails. Scope to this disclosure, same idea as
// newDocumentForm above.
const importDocumentForm = (page: Page) => page.getByTestId('import-document')
const tileActions = (page: Page, name: string) => tile(page, name).getByTestId('tile-more')

// "New folder" creates inside whatever folder you are looking at, so nesting is just navigate-then-create.
async function newFolder(page: Page, name: string) {
  await disclose(newFolderForm(page))
  await tree(page).getByLabel('Folder name').fill(name)
  await tree(page).getByRole('button', { name: 'Create folder' }).click()
  await expect(node(page, name)).toBeVisible()
}

async function openFolder(page: Page, name: string) {
  await row(page, name).getByRole('link', { name, exact: true }).click()
  await expect(row(page, name).getByRole('link', { name, exact: true })).toHaveAttribute(
    'aria-current',
    'page',
  )
}

async function newDocument(page: Page, name: string) {
  const form = newDocumentForm(page)
  await disclose(form)
  await form.getByLabel('Document name').fill(name)
  await form.getByRole('button', { name: 'Create document' }).click()
  await expect(tile(page, name)).toBeVisible()
}

test('folders nest three levels deep and the tree shows the path', async ({ signedIn: page }) => {
  await newFolder(page, 'Alpha')
  await openFolder(page, 'Alpha')
  await newFolder(page, 'Bravo')
  await openFolder(page, 'Bravo')
  await newFolder(page, 'Charlie')
  await openFolder(page, 'Charlie')

  await expect(page).toHaveURL(/\/folders\/[0-9a-f-]{36}$/)
  // Structural, not just "all three are on screen": Charlie inside Bravo inside Alpha.
  await expect(
    node(page, 'Alpha').locator(
      '[data-testid="folder-node"][data-name="Bravo"] [data-testid="folder-node"][data-name="Charlie"]',
    ),
  ).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Charlie' })).toBeVisible()
})

test('a folder can be renamed', async ({ signedIn: page }) => {
  await newFolder(page, 'Alpha')
  await row(page, 'Alpha').getByRole('button', { name: 'Rename' }).click()
  await node(page, 'Alpha').getByLabel('New name').fill('Alphabet')
  await node(page, 'Alpha').getByRole('button', { name: 'Save' }).click()
  await expect(node(page, 'Alphabet')).toBeVisible()
  await expect(node(page, 'Alpha')).toBeHidden()
})

test('a duplicate folder name surfaces the API problem detail', async ({ signedIn: page }) => {
  await newFolder(page, 'Alpha')
  await disclose(newFolderForm(page))
  await tree(page).getByLabel('Folder name').fill('Alpha')
  await tree(page).getByRole('button', { name: 'Create folder' }).click()
  await expect(page.getByRole('alert')).toContainText(/already exists/i)
})

test('deleting a folder can promote its children or bury them (E1 promote-vs-trash)', async ({
  signedIn: page,
}) => {
  await newFolder(page, 'Alpha')
  await openFolder(page, 'Alpha')
  await newFolder(page, 'Bravo')

  // mode=promote_children: Bravo survives, one level up (here: root).
  await row(page, 'Alpha').getByRole('button', { name: 'Delete', exact: true }).click()
  await node(page, 'Alpha').getByRole('button', { name: 'Delete folder, keep contents' }).click()
  await expect(node(page, 'Alpha')).toBeHidden()
  await expect(node(page, 'Bravo')).toBeVisible()

  // mode=trash: Echo keeps pointing at the deleted Delta, so it is gone from the tree entirely.
  await newFolder(page, 'Delta')
  await openFolder(page, 'Delta')
  await newFolder(page, 'Echo')
  await row(page, 'Delta').getByRole('button', { name: 'Delete', exact: true }).click()
  await node(page, 'Delta').getByRole('button', { name: 'Delete folder and contents' }).click()
  await expect(node(page, 'Delta')).toBeHidden()
  await expect(node(page, 'Echo')).toBeHidden()
})

test('an uploaded first version numbers 0.0.1 (E2)', async ({ signedIn: page }) => {
  await newDocument(page, 'Contract')
  await disclose(tileActions(page, 'Contract'))
  await tile(page, 'Contract').getByLabel('Upload version').setInputFiles(DOCX)
  await expect(tile(page, 'Contract').getByTestId('tile-version')).toHaveText(/0\.0\.1/)
})

// Import is the one form on this page the API can partially fill in for you: it already has the
// bytes, so it can name the document from the filename before anyone types anything. Losing that
// prefill is the whole reason this form exists over just using "New document" + "Upload version".
test('importing a document prefills its name from the file and opens on 0.0.1 (E2)', async ({
  signedIn: page,
}) => {
  const form = importDocumentForm(page)
  await disclose(form)
  await form.getByTestId('import-input').setInputFiles(DOCX_ECHO)
  await expect(form.getByLabel('Document name')).toHaveValue('edited-plus-echo')
  await form.getByRole('button', { name: 'Import', exact: true }).click()

  await expect(page).toHaveURL(/\/documents\/[0-9a-f-]{36}$/)
  await expect(page.getByRole('heading', { name: 'edited-plus-echo' })).toBeVisible()
  await expect(page.getByTestId('doc-head-version')).toHaveText(/0\.0\.1/)
})

// Clicking Import with nothing chosen used to return early in silence -- no navigation, no alert, no
// change of any kind. This file's own comment on act() calls a silent no-op "the worst outcome
// available", and this was the one write on the screen that dodged it. Asserting `disabled` rather than
// "nothing happened" is deliberate: "nothing happened" is exactly what the bug looked like too.
test('Import is not clickable until a file is chosen', async ({ signedIn: page }) => {
  const form = importDocumentForm(page)
  await disclose(form)
  const submit = form.getByRole('button', { name: 'Import', exact: true })
  await expect(submit).toBeDisabled()

  await form.getByTestId('import-input').setInputFiles(DOCX)
  await expect(submit).toBeEnabled()
})

// The prefill above is a courtesy, not a requirement -- picking a file must never force the
// filename on you. Asserting the input still held the typed text would not prove this: the same
// bug that shipped .ToLower() past its own name test would ship a hardcoded name past a spec that
// only reads the input. What has to be checked is what the document is actually called once it exists.
test('editing the prefilled name before importing is what gets created, not the filename', async ({
  signedIn: page,
}) => {
  const form = importDocumentForm(page)
  await disclose(form)
  await form.getByTestId('import-input').setInputFiles(DOCX_EDITED)
  await form.getByLabel('Document name').fill('Renamed On The Way In')
  await form.getByRole('button', { name: 'Import', exact: true }).click()

  await expect(page).toHaveURL(/\/documents\/[0-9a-f-]{36}$/)
  await expect(page.getByRole('heading', { name: 'Renamed On The Way In' })).toBeVisible()
})

test('a document with no versions says so, and never null or 0.0.0', async ({ signedIn: page }) => {
  await newDocument(page, 'Empty Draft')
  await expect(tile(page, 'Empty Draft')).toContainText('No versions yet')
  await expect(tile(page, 'Empty Draft')).not.toContainText('null')
  await expect(tile(page, 'Empty Draft')).not.toContainText('0.0.0')
})

test('a tile shows its version number, modified time and last author', async ({
  signedIn: page,
}) => {
  await newDocument(page, 'Handbook')
  const t = tile(page, 'Handbook')
  await disclose(tileActions(page, 'Handbook'))
  await t.getByLabel('Upload version').setInputFiles(DOCX_EDITED)
  await expect(t.getByTestId('tile-version')).toHaveText(/0\.0\.1 · 1 version/)
  await disclose(tileActions(page, 'Handbook'))
  await t.getByLabel('Upload version').setInputFiles(DOCX_ECHO)
  await expect(t.getByTestId('tile-version')).toHaveText(/0\.0\.2 · 2 versions/)

  await expect(t.getByTestId('tile-author')).toContainText('E2E User')
  await expect(t.getByTestId('tile-updated')).toContainText(String(new Date().getFullYear()))
})

test('search runs on the server via ?q=', async ({ signedIn: page }) => {
  await newDocument(page, 'Quokka Agreement')
  await newDocument(page, 'Zebra Policy')

  // The list is only ever one page deep, so filtering client-side would silently hide matches that
  // live past the cursor. Assert the query actually reaches the server.
  const served = page.waitForRequest(
    (r) => r.url().includes('/api/v1/documents?') && /[?&]q=quokka(&|$)/.test(r.url()),
  )
  await page.getByLabel('Search documents').fill('quokka')
  await served

  await expect(tile(page, 'Quokka Agreement')).toBeVisible()
  await expect(tile(page, 'Zebra Policy')).toBeHidden()
})

test('a document moves between folders (E1)', async ({ signedIn: page }) => {
  await newFolder(page, 'Alpha')
  await newFolder(page, 'Bravo')
  await openFolder(page, 'Alpha')
  await newDocument(page, 'Lease')

  await disclose(tileActions(page, 'Lease'))
  await tile(page, 'Lease').getByLabel('Move to').selectOption({ label: 'Bravo' })
  await expect(tile(page, 'Lease')).toBeHidden()

  await openFolder(page, 'Bravo')
  await expect(tile(page, 'Lease')).toBeVisible()
})

test('a trashed document is recoverable from the trash view', async ({ signedIn: page }) => {
  await newDocument(page, 'Minutes')
  await disclose(tileActions(page, 'Minutes'))
  await tile(page, 'Minutes').getByRole('button', { name: 'Move to trash' }).click()
  await expect(tile(page, 'Minutes')).toBeHidden()

  await page.getByTestId('trash-link').click()
  await expect(page).toHaveURL(/\/trash$/)
  await expect(tile(page, 'Minutes')).toBeVisible()

  await tile(page, 'Minutes').getByTestId('restore-button').click()
  await expect(tile(page, 'Minutes')).toBeHidden()

  await page.getByRole('link', { name: 'Documents', exact: true }).click()
  await expect(tile(page, 'Minutes')).toBeVisible()
})

// Sorting has to be server-side and it has to stick: reordering only the tiles already fetched would
// be a lie the moment the list is longer than one page, and a sort that resets when you come back
// from a document is not a sort anyone would use.
test('sorting reorders the tiles and survives a reload', async ({ page, request }) => {
  const account = await register(request)
  await signIn(page, account)

  for (const name of ['zulu-sort', 'alpha-sort', 'mike-sort']) {
    await disclose(newDocumentForm(page))
    await newDocumentForm(page).getByLabel('Document name').fill(name)
    await newDocumentForm(page).getByRole('button', { name: 'Create document' }).click()
    await expect(tile(page, name)).toBeVisible()
  }

  const names = () => page.locator('[data-testid="document-tile"]').evaluateAll(
    (tiles) => tiles.map((t) => t.getAttribute('data-name')),
  )

  await page.getByTestId('sort').selectOption('name:asc')
  await expect(page).toHaveURL(/[?&]sort=name(&|$)/)
  await expect(page).toHaveURL(/[?&]order=asc(&|$)/)
  await expect.poll(names).toEqual(['alpha-sort', 'mike-sort', 'zulu-sort'])

  // The URL is the state, so a hard reload has to come back to the same order.
  await page.reload()
  await expect(page.getByTestId('sort')).toHaveValue('name:asc')
  await expect.poll(names).toEqual(['alpha-sort', 'mike-sort', 'zulu-sort'])

  await page.getByTestId('sort').selectOption('name:desc')
  await expect.poll(names).toEqual(['zulu-sort', 'mike-sort', 'alpha-sort'])
})

// The URL is the state, which means anyone can arrive with a pair that is not one of the six options
// — a stale link, an edited query string. Asserting the select's VALUE cannot catch this: a <select>
// whose value matches no option has its selectedness reset to the first option, so it reads
// "updated:desc" whether the fallback happened or not. What differs is the LIST — and that is the
// whole bug, because a control reading "Last updated" over an A–Z list is also a dead control:
// picking the option it already displays fires no change event.
test('a sort pair the select cannot show falls back to the default rather than lying', async ({
  page,
  request,
}) => {
  const account = await register(request)
  await signIn(page, account)

  // Created in an order that name-ascending would NOT produce, so the two candidate sorts are
  // distinguishable: no versions here, so `updated` is creation time.
  for (const name of ['zulu-lie', 'alpha-lie', 'mike-lie']) {
    await disclose(newDocumentForm(page))
    await newDocumentForm(page).getByLabel('Document name').fill(name)
    await newDocumentForm(page).getByRole('button', { name: 'Create document' }).click()
    await expect(tile(page, name)).toBeVisible()
  }

  await page.goto('/?sort=name&order=bogus')
  await expect(page.getByTestId('dashboard')).toBeVisible()
  await expect(page.getByTestId('sort')).toHaveValue('updated:desc')

  const names = () =>
    page
      .locator('[data-testid="document-tile"]')
      .evaluateAll((tiles) => tiles.map((t) => t.getAttribute('data-name')))

  // What the control says it is doing, not the ?sort=name the URL asked for.
  await expect.poll(names).toEqual(['mike-lie', 'alpha-lie', 'zulu-lie'])
})
