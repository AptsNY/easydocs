import type { Page } from '@playwright/test'
import { test, expect, createDocument, uploadVersion } from './fixtures'

// Visual regression: the key screens, compared pixel-for-pixel with approved reference images. Every layout
// bug of the 2026-09-30 week (names out of their tiles, a publish badge over Actions, the Members panel
// squeezing the version page) was caught by eye; each would have failed here.
//
// The references are Linux renders made by the CI runner itself (workflow visual-baselines.yml), because
// font rasterisation differs per OS. On any other OS these tests skip; CI is where they run.
//
// Only layout may vary the image, never data: every name below is fixed, and what cannot be (times, the
// per-run org name and email, the PDF frame, which headless Chromium leaves blank) is masked.
test.skip(process.platform !== 'linux', 'reference images are Linux renders; CI runs these')

const LONG_NAMES = [
  'aces__Laundry_Room_Lease_Template.docx',
  'MOVIE_LOCATION_AGREEMENT092025.docx',
  'Lease_Modification_and_Extension_Agreement (1).docx',
  'ClickPay — RealPage One Master Agreement (OMA)',
]

const masks = (page: Page) => [
  page.locator('time'),
  page.getByTestId('org-name'),
  page.getByTestId('tile-updated'),
  page.locator('.member-who .muted'),
  page.getByTestId('version-pdf'),
  page.getByTestId('editor-frame'),
]

async function shot(page: Page, name: string) {
  await expect(page).toHaveScreenshot(name, {
    fullPage: true,
    animations: 'disabled',
    caret: 'hide',
    mask: masks(page),
    // Antialiasing differs by a few pixels between otherwise identical runs; a layout change moves far more.
    maxDiffPixelRatio: 0.01,
  })
}

async function versionPage(page: Page) {
  const documentId = await createDocument(page, 'aces__proposal_template.docx')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await page.request.post(`/api/v1/versions/${v1}/publish`, { data: { kind: 'major', name: 'Fix docxtpl row tags' } })
  const v2 = await uploadVersion(page, documentId, 'edited.docx')
  await page.request.post(`/api/v1/versions/${v2}/publish`, {
    data: { kind: 'major', name: 'Bracket access in equipment loop' },
  })
  return { documentId, v1, v2 }
}

for (const scheme of ['light', 'dark'] as const) {
  test.describe(`${scheme}`, () => {
    test.use({ colorScheme: scheme })

    test(`documents dashboard with long names (${scheme})`, async ({ signedIn: page }) => {
      for (const n of LONG_NAMES) await createDocument(page, n)
      await page.setViewportSize({ width: 1440, height: 900 })
      await page.goto('/')
      await expect(page.getByTestId('document-tile')).toHaveCount(LONG_NAMES.length)
      await shot(page, `dashboard-${scheme}-1440.png`)
      await page.setViewportSize({ width: 390, height: 844 })
      await shot(page, `dashboard-${scheme}-390.png`)
    })

    test(`version page (${scheme})`, async ({ signedIn: page }) => {
      const { documentId } = await versionPage(page)
      await page.setViewportSize({ width: 1440, height: 900 })
      await page.goto(`/documents/${documentId}`)
      await expect(page.getByTestId('version-pdf')).toBeVisible({ timeout: 60_000 })
      await shot(page, `version-page-${scheme}-1440.png`)
      await page.setViewportSize({ width: 390, height: 844 })
      await shot(page, `version-page-${scheme}-390.png`)
    })
  })
}

test('compare versions', async ({ signedIn: page }) => {
  const { documentId, v1, v2 } = await versionPage(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(`/documents/${documentId}/compare?from=${v1}&to=${v2}`)
  await expect(page.getByTestId('version-pdf')).toBeVisible({ timeout: 60_000 })
  await shot(page, 'compare-1440.png')
})

test('members tab', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Members Layout')
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(`/documents/${documentId}/members`)
  await expect(page.getByTestId('member-row')).toHaveCount(1)
  await shot(page, 'members-1440.png')
})

test('editor page', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Residential Lease — 12 Main St')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  // The frame is masked, so Collabora itself is not part of the picture: only the page around it is.
  await page.setViewportSize({ width: 1280, height: 720 })
  await page.goto(`/versions/${v1}/edit`)
  await expect(page.getByTestId('editor-bar')).toBeVisible()
  await expect(page.getByTestId('editor-rail')).toContainText('0.0.1')
  // The save status depends on whether Collabora answered in time; it is not layout.
  await expect(page).toHaveScreenshot('editor-1280.png', {
    animations: 'disabled',
    caret: 'hide',
    mask: [...masks(page), page.getByTestId('editor-status')],
    maxDiffPixelRatio: 0.01,
  })
})
