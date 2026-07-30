import { test, expect, createDocument, uploadVersion } from './fixtures'

// There is deliberately no page.reload() anywhere in this file. If the second row only showed up after
// a reload, the EventSource would not be wired and the assertion would still go green — which is the
// entire thing this test exists to rule out.
test('a version created elsewhere appears in the console with no reload', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Live')
  await uploadVersion(page, documentId, 'base.docx')

  await page.goto(`/documents/${documentId}`)
  await expect(page.getByTestId('version-row')).toHaveCount(1)

  // Same user, a different client. To this console that is indistinguishable from a colleague saving
  // in Collabora: a version.created event on GET /documents/{id}/events.
  await uploadVersion(page, documentId, 'edited.docx')
  await expect(page.getByTestId('version-row')).toHaveCount(2)
  await expect(page.locator('[data-testid="version-row"][data-number="0.0.2"]')).toBeVisible()
})
