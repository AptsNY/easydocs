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
