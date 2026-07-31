import type { APIRequestContext, Browser, Page } from '@playwright/test'
import { test, expect, disclose, register, signIn, createDocument, uploadVersion } from './fixtures'

// Major Versions, copies management and the audit trail (spec §9), plus conformance E9 driven entirely
// through the UI: fork → push back → accept/reject on the target → an accepted push shows up as an
// IncomingPush branch group in the target's history.
//
// The push in tests 4 and 5 is made by a member of the COPY ONLY, which is the real E9 shape (an external
// reviewer returning a redline) and the only shape that produces a *pending* request: PushEndpoints
// auto-accepts a push whose author independently holds an editing role on the target, so the copy's
// creator pushing back to her own document never reaches review.

const publicationRow = (page: Page) => page.getByTestId('publication-row')
const pushRow = (page: Page) => page.getByTestId('push-request-row')
const incoming = (page: Page) => page.locator('[data-testid="branch-group"][data-kind="IncomingPush"]')

async function publish(page: Page, versionId: string, kind: 'minor' | 'major', name: string) {
  const res = await page.request.post(`/api/v1/versions/${versionId}/publish`, { data: { kind, name } })
  expect(res.ok(), `publish failed: ${res.status()} ${await res.text()}`).toBeTruthy()
}

async function fork(page: Page, versionId: string, name: string): Promise<string> {
  const res = await page.request.post(`/api/v1/versions/${versionId}/copies`, { data: { name } })
  expect(res.ok(), `fork failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  return ((await res.json()) as { id: string }).id
}

// An external reviewer: an Editor of the COPY who holds no role on the parent. The invitation is accepted
// from their own browser context because that response rebinds ed_session to the inviting org — a plain
// login would bind to the invitee's own org, from which this copy is cross-org, i.e. a 404. (Same dance
// as console.spec.ts and actions.spec.ts.)
async function copyOnlyReviewer(
  browser: Browser,
  page: Page,
  request: APIRequestContext,
  copyId: string,
) {
  const other = await register(request)
  const res = await page.request.post(`/api/v1/documents/${copyId}/members`, {
    data: { email: other.email, role: 'Editor' },
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

test('1. Major Versions lists each publication with kind, number, publisher name and date', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Contract')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const v2 = await uploadVersion(page, documentId, 'edited.docx')
  await publish(page, v1, 'minor', 'Draft for review')
  await publish(page, v2, 'major', 'Signed')

  await page.goto(`/documents/${documentId}/major-versions`)
  await expect(publicationRow(page)).toHaveCount(2)

  // Publishing renumbers from the document counter: minor -> 0.1.0, then major -> 1.0.0.
  const major = page.locator('[data-testid="publication-row"][data-kind="major"]')
  await expect(major).toHaveCount(1)
  await expect(major).toHaveAttribute('data-number', '1.0.0')
  await expect(major).toContainText('Signed')

  const minor = page.locator('[data-testid="publication-row"][data-kind="minor"]')
  await expect(minor).toHaveAttribute('data-number', '0.1.0')
  await expect(minor).toContainText('Draft for review')

  // A name, never the raw publishedBy id — every other read surface in the product resolves names.
  await expect(major.getByTestId('publication-publisher')).toHaveText('E2E User')
  await expect(major).not.toContainText(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}/i)
  await expect(major.locator('time')).toContainText(String(new Date().getFullYear()))
})

test('2. a published version with no PDF offers no PDF link', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'No PDF Here')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'major', 'Executed')

  // Rendering needs LibreOffice, which is not installed in this environment, so hasPdf is false even
  // after publishing (and ?format=pdf answers 409). Asserted against the API first so this test states
  // the precondition it is testing rather than assuming it.
  const res = await page.request.get(`/api/v1/documents/${documentId}/versions`)
  const items = ((await res.json()) as { items: { hasPdf: boolean }[] }).items
  expect(items.some((v) => v.hasPdf)).toBeFalsy()

  await page.goto(`/documents/${documentId}/major-versions`)
  await expect(publicationRow(page)).toHaveCount(1)
  // The .docx is always downloadable; the PDF link exists only when a PDF does.
  await expect(publicationRow(page).getByTestId('publication-docx')).toHaveCount(1)
  await expect(publicationRow(page).getByTestId('publication-pdf')).toHaveCount(0)
})

test('3. the Copies tab lists the copies made from this document', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Forked')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const copyId = await fork(page, v1, 'Counsel review')

  await page.goto(`/documents/${documentId}/copies`)
  const copy = page.locator(`[data-testid="copy-row"][data-copy-id="${copyId}"]`)
  await expect(copy).toHaveCount(1)
  await expect(copy).toContainText('Counsel review')

  // A copy is a document of its own, so its name links to its own console.
  await copy.getByRole('link', { name: 'Counsel review' }).click()
  await expect(page).toHaveURL(new RegExp(`/documents/${copyId}$`))
  await expect(page.getByRole('heading', { name: 'Counsel review' })).toBeVisible()
})

test('4. E9 round trip: an external reviewer pushes back and the target accepts it', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'Master Agreement')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const copyId = await fork(page, v1, 'Reviewer copy')
  const { theirContext, theirPage } = await copyOnlyReviewer(browser, page, request, copyId)

  // The owner is watching the parent's Copies tab before the push happens, so the pending request has to
  // arrive over SSE (push.requested is published to the TARGET) rather than on a reload.
  await page.goto(`/documents/${documentId}/copies`)
  await expect(pushRow(page)).toHaveCount(0)

  // The reviewer edits the copy and sends it back through the UI.
  await uploadVersion(theirPage, copyId, 'edited.docx')
  await theirPage.goto(`/documents/${copyId}/copies`)
  // Send-back is disclosed on the Copies tab now, so the reviewer opens it first.
  await disclose(theirPage.getByTestId('push-back'))
  await theirPage.getByLabel('Version to send back').selectOption({ label: '0.0.2' })
  await theirPage.getByRole('button', { name: 'Send back' }).click()
  await expect(theirPage.getByTestId('push-request-row')).toHaveAttribute('data-status', 'pending')

  const pending = pushRow(page)
  await expect(pending).toHaveCount(1)
  await expect(pending).toHaveAttribute('data-status', 'pending')
  // Named by the copy it came from: the target's reviewers are not members of the copy, so the row must
  // not depend on reading anything inside it.
  await expect(pending).toContainText('Reviewer copy')

  await pending.getByRole('button', { name: 'Accept' }).click()
  await expect(pending).toHaveAttribute('data-status', 'accepted')

  // E9: accepting materialises the pushed content as an incoming branch on the target's history.
  await page.goto(`/documents/${documentId}`)
  await expect(incoming(page)).toHaveCount(1)
  await expect(incoming(page).getByTestId('version-row')).toHaveCount(1)
  await expect(incoming(page)).toContainText('Pushed from a copy')

  await theirContext.close()
})

test('5. a rejected push never enters the target’s history', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'Rejected Push')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const copyId = await fork(page, v1, 'Unwanted copy')
  const { theirContext, theirPage } = await copyOnlyReviewer(browser, page, request, copyId)

  await uploadVersion(theirPage, copyId, 'edited.docx')
  await theirPage.goto(`/documents/${copyId}/copies`)
  // Send-back is disclosed on the Copies tab now, so the reviewer opens it first.
  await disclose(theirPage.getByTestId('push-back'))
  await theirPage.getByLabel('Version to send back').selectOption({ label: '0.0.2' })
  await theirPage.getByRole('button', { name: 'Send back' }).click()

  await page.goto(`/documents/${documentId}/copies`)
  await expect(pushRow(page)).toHaveAttribute('data-status', 'pending')
  await pushRow(page).getByRole('button', { name: 'Reject' }).click()
  await expect(pushRow(page)).toHaveAttribute('data-status', 'rejected')

  // Nothing was materialised: the parent still has exactly its own one version and no incoming branch.
  await page.goto(`/documents/${documentId}`)
  await expect(page.getByTestId('version-row')).toHaveCount(1)
  await expect(incoming(page)).toHaveCount(0)

  await theirContext.close()
})

test('6. the audit tab resolves actor names and shows an anonymous share-link read as anonymous', async ({
  signedIn: page,
  request,
}) => {
  const documentId = await createDocument(page, 'Audited')
  const v1 = await uploadVersion(page, documentId, 'base.docx')

  const share = await page.request.post(`/api/v1/versions/${v1}/share-links`, {
    data: { expiresAt: null },
  })
  expect(share.ok(), `share failed: ${share.status()} ${await share.text()}`).toBeTruthy()
  const { token } = (await share.json()) as { token: string }

  // A cookie-less context, the way a recipient outside the org arrives. Accept: application/json asks for
  // the data rather than the SPA shell (the shell request deliberately neither audits nor counts a view).
  const viewed = await request.get(`/s/${token}`, { headers: { Accept: 'application/json' } })
  expect(viewed.ok(), `public view failed: ${viewed.status()} ${await viewed.text()}`).toBeTruthy()

  await page.goto(`/documents/${documentId}/audit`)
  const created = page.locator('[data-testid="audit-row"][data-action="document.created"]')
  await expect(created.getByTestId('audit-actor')).toHaveText('E2E User')

  // The share-link read has no actor at all — actorUserId and actorName are both null. "(unknown)" would
  // claim the actor could not be resolved; the truth is that there was nobody to resolve.
  const anonymous = page.locator('[data-testid="audit-row"][data-action="share_link.viewed"]')
  await expect(anonymous).toHaveCount(1)
  await expect(anonymous.getByTestId('audit-actor')).toHaveText('anonymous')
  await expect(anonymous).not.toContainText('unknown')
})
