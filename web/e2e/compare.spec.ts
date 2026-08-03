import type { Page } from '@playwright/test'
import { test, expect, createDocument, uploadVersion } from './fixtures'

// The comparison / redline view (spec §7, §9) — the product's signature feature: a redline between any
// two versions even though nobody turned Track Changes on.
//
// Two things are load-bearing here and both are asserted rather than assumed:
//   1. The redline HTML is produced by WmlComparer from a user-supplied .docx, so it must render inside a
//      sandboxed iframe and NEVER in the app's own DOM. Test 2 proves the app document contains no
//      <ins>/<del> of its own while the frame does.
//   2. `moves` and `formatChanges` are permanently 0 (WmlComparer only classifies Inserted/Deleted), so
//      the screen must not present them as counters. Test 3 asserts they are absent.

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'

const summary = (page: Page) => page.getByTestId('compare-summary')
const frame = (page: Page) => page.getByTestId('redline-frame')

async function seed(page: Page, name: string, fixtures: string[]) {
  const documentId = await createDocument(page, name)
  const ids: string[] = []
  for (const f of fixtures) ids.push(await uploadVersion(page, documentId, f))
  return { documentId, ids }
}

// A version whose bytes are not a .docx at all. The upload route stores whatever it is given (it is
// content-addressed storage, not a validator), so this is the legitimate way to reach the state where
// WmlComparer cannot produce a comparison — no stubbing, no fault injection.
async function uploadGarbage(page: Page, documentId: string) {
  const res = await page.request.post(`/api/v1/documents/${documentId}/versions`, {
    multipart: {
      file: {
        name: 'corrupt.docx',
        mimeType: DOCX,
        buffer: Buffer.from(`not a docx ${Date.now()}-${Math.random()}`),
      },
    },
  })
  expect(res.ok(), `garbage upload failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  return ((await res.json()) as { versionId: string }).versionId
}

async function compare(page: Page, documentId: string, from: string, to: string) {
  await page.goto(`/documents/${documentId}/compare`)
  await page.getByLabel('From version').selectOption(from)
  await page.getByLabel('To version').selectOption(to)
}

test('1. the compare screen offers a from and a to picker listing this document’s versions', async ({
  signedIn: page,
}) => {
  const { documentId, ids } = await seed(page, 'Pickers', ['base.docx', 'edited.docx'])

  await page.goto(`/documents/${documentId}/compare`)
  await expect(page.getByTestId('compare')).toBeVisible()

  for (const label of ['From version', 'To version']) {
    const picker = page.getByLabel(label)
    await expect(picker).toBeVisible()
    await expect(picker.locator('option')).toHaveCount(2)
    // Both versions are selectable in both pickers — the direction of the comparison is the reader's
    // choice, not the list's.
    for (const id of ids) await expect(picker.locator(`option[value="${id}"]`)).toHaveCount(1)
  }
})

test('2. the redline renders inside an iframe and never in the app’s own DOM', async ({
  signedIn: page,
}) => {
  const { documentId, ids } = await seed(page, 'Redline', ['base.docx', 'edited.docx'])
  await compare(page, documentId, ids[0], ids[1])

  await expect(frame(page)).toBeVisible()
  // edited.docx adds " EDITED" and a "Delta" paragraph to base.docx, so the redline has real insertions.
  const inside = page.frameLocator('[data-testid="redline-frame"]').locator('ins')
  await expect(inside.first()).toContainText('EDITED')
  // And it reads as a REDline: the frame carries its own stylesheet (the cached HTML has none).
  await expect(inside.first()).toHaveCSS('color', 'rgb(179, 38, 30)')

  // The proof it is not inlined: the app document itself has no redline markup. A locator does not
  // pierce a frame boundary, so this counts only the page's own elements.
  await expect(page.locator('ins')).toHaveCount(0)
  await expect(page.locator('del')).toHaveCount(0)
  await expect(page.locator('.redline')).toHaveCount(0)

  // Sandboxed, and without allow-scripts — untrusted document markup must not be able to run anything.
  const sandbox = await frame(page).getAttribute('sandbox')
  expect(sandbox).not.toBeNull()
  expect(sandbox).not.toContain('allow-scripts')
})

test('3. the numeric summary matches ?format=summary, and moves/formatChanges are not shown', async ({
  signedIn: page,
}) => {
  const { documentId, ids } = await seed(page, 'Counts', ['base.docx', 'edited.docx'])

  const res = await page.request.get(
    `/api/v1/documents/${documentId}/compare?from=${ids[0]}&to=${ids[1]}`,
  )
  expect(res.ok()).toBeTruthy()
  const counts = (await res.json()) as { insertions: number; deletions: number }
  expect(counts.insertions).toBeGreaterThan(0)

  await compare(page, documentId, ids[0], ids[1])
  await expect(summary(page)).toContainText(`${counts.insertions} insertions`)
  await expect(summary(page)).toContainText(`${counts.deletions} deletions`)

  // WmlComparer.GetRevisions only classifies Inserted/Deleted, so a "0 moves" counter would be a
  // limitation dressed up as data.
  await expect(page.getByTestId('compare')).not.toContainText(/moves/i)
  await expect(page.getByTestId('compare')).not.toContainText(/format changes/i)
})

test('4. comparing a version with itself says so instead of showing a blank pane', async ({
  signedIn: page,
}) => {
  const { documentId, ids } = await seed(page, 'Self', ['base.docx'])
  await compare(page, documentId, ids[0], ids[0])

  await expect(page.getByTestId('compare-empty')).toBeVisible()
  await expect(page.getByTestId('compare-empty')).toContainText(/no (changes|differences)/i)
  await expect(frame(page)).toHaveCount(0)
})

test('5. Download redline yields a .docx', async ({ signedIn: page }) => {
  const { documentId, ids } = await seed(page, 'Redline Download', ['base.docx', 'edited.docx'])
  await compare(page, documentId, ids[0], ids[1])

  const downloading = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download redline' }).click()
  const download = await downloading
  expect(download.suggestedFilename()).toMatch(/\.docx$/)
})

test('6. an uncomparable version degrades to a message, not a broken pane (§12.2)', async ({
  signedIn: page,
}) => {
  const { documentId, ids } = await seed(page, 'Unavailable', ['base.docx'])
  const corrupt = await uploadGarbage(page, documentId)
  await compare(page, documentId, ids[0], corrupt)

  await expect(page.getByTestId('compare-unavailable')).toBeVisible()
  await expect(page.getByTestId('compare-unavailable')).toContainText(/unavailable/i)
  await expect(frame(page)).toHaveCount(0)
  // The counts would read 0/0 here, which is not "no changes" — it is "we could not tell".
  await expect(page.getByTestId('compare-empty')).toHaveCount(0)

  // And the redline .docx that cannot exist is not offered as a download.
  await expect(page.getByRole('button', { name: 'Download redline' })).toHaveCount(0)
})
