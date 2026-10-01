import { test, expect, createDocument, uploadVersion } from './fixtures'

// History as Simul's version page (spec 2026-09-30): pick a version, its changes against its parent show beside
// the list as real pages (the redline through Gotenberg). The frame is the browser's own PDF viewer, so the
// assertion is on what it loads: the right pair, and a PDF.
test('selecting a version shows its changes as pages; a parentless version shows itself', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Version Page')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const v2 = await uploadVersion(page, documentId, 'edited.docx')

  await page.goto(`/documents/${documentId}`)
  const frame = page.getByTestId('version-pdf')

  // Default: the newest version, against its parent.
  await expect(page.getByTestId('version-preview').getByRole('heading')).toHaveText('Changes in 0.0.2')
  await expect(frame).toHaveAttribute('src', new RegExp(`compare\\?from=${v1}&to=${v2}&format=pdf#`))

  // Pick 0.0.1: the URL remembers it, and with no parent the frame shows the version alone.
  await page.locator('[data-testid="version-row"][data-number="0.0.1"]').getByTestId('version-number').click()
  await expect(page).toHaveURL(new RegExp(`\\?v=${v1}$`))
  await expect(page.getByTestId('version-preview').getByRole('heading')).toHaveText('0.0.1')
  await expect(frame).toHaveAttribute('src', new RegExp(`compare\\?from=${v1}&to=${v1}&format=pdf#`))

  // What the frame loads is a real PDF, for both the pair and the single version.
  for (const [from, to] of [
    [v1, v2],
    [v1, v1],
  ]) {
    const res = await page.request.get(`/api/v1/documents/${documentId}/compare?from=${from}&to=${to}&format=pdf`)
    expect(res.status()).toBe(200)
    expect(res.headers()['content-type']).toContain('application/pdf')
    expect((await res.body()).subarray(0, 5).toString()).toBe('%PDF-')
  }
})

test('a version that cannot be shown as pages says so, with the redline as a download', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Unrenderable')
  await uploadVersion(page, documentId, 'base.docx')
  // Not a real archive: the comparison cannot be made, so format=pdf is a 422 — which must never reach the
  // viewer as raw problem+json.
  const up = await page.request.post(`/api/v1/documents/${documentId}/versions`, {
    multipart: {
      file: {
        name: 'broken.docx',
        mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        buffer: Buffer.from('PK\x03\x04not really a zip'),
      },
    },
  })
  expect(up.ok()).toBeTruthy()

  await page.goto(`/documents/${documentId}`)
  await expect(page.getByTestId('version-pdf-unavailable')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByTestId('version-pdf')).toHaveCount(0)
})

// Production: a long publish name ("MAJOR · BRACKET ACCESS EQUIPMENT LOOP") in the narrow list beside the
// preview ran over the Actions button and into the pages. Everything in a row stays inside the row, and the
// badge never overlaps Actions.
test('a long publish name stays inside its row and clear of Actions', async ({ signedIn: page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  const documentId = await createDocument(page, 'Long Publish Name')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const res = await page.request.post(`/api/v1/versions/${v1}/publish`, {
    data: { kind: 'major', name: 'Bracket access equipment loop and the docxtpl row tags fix' },
  })
  expect(res.ok()).toBeTruthy()

  await page.goto(`/documents/${documentId}`)
  const row = page.locator('[data-testid="version-row"][data-number="1.0.0"]')
  await expect(row.getByTestId('version-badge')).toBeVisible()
  // Actions renders once the caller's role arrives, which can land after the version list.
  await expect(row.locator('.actions')).toBeVisible()

  const fit = await row.evaluate((el) => {
    const r = el.getBoundingClientRect()
    const badge = el.querySelector('[data-testid="version-badge"]')!.getBoundingClientRect()
    const actions = el.querySelector('.actions')!.getBoundingClientRect()
    const overlap = !(badge.right <= actions.left || actions.right <= badge.left || badge.bottom <= actions.top || actions.bottom <= badge.top)
    return { overflow: el.scrollWidth - el.clientWidth, badgeRight: badge.right - r.right, overlap }
  })
  expect(fit.overflow, 'row content is wider than the row').toBeLessThanOrEqual(0)
  expect(fit.badgeRight, 'the badge runs past the row').toBeLessThanOrEqual(0)
  expect(fit.overlap, 'the badge overlaps Actions').toBe(false)
})
