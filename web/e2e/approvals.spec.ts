import type { APIRequestContext, Browser, Page } from '@playwright/test'
import {
  test,
  expect,
  disclose,
  register,
  signIn,
  createDocument,
  uploadVersion,
  type Account,
} from './fixtures'

// The approvals screen (spec §9) and conformance E7: an approval can only be requested on a PUBLISHED
// version, a decision is single and immutable, and cancel closes the request.
//
// Approvals were write-only before M4.5 — request/respond/cancel with no GET anywhere, so an approver had
// no way to discover they had been asked for anything. Phase A added GET /api/v1/approvals (the inbox) and
// GET /api/v1/versions/{vid}/approvals (one version's panel), and the same phase closed a privilege hole by
// refusing an approverId that is not a member of the document. The UI half of that fix is the picker: it
// lists document members only, so the 400 is unreachable by clicking (test 2).

// The request form is disclosed now (its <summary> reads "Ask for approval", deliberately not the
// submit button's words), so a test opens it exactly as a requester does.
const requestForm = (page: Page) => page.getByTestId('request-approval')
const approvalRows = (page: Page) => page.getByTestId('approval-row')
const approverOption = (page: Page, email: string) =>
  page.locator(`[data-testid="approver-option"][data-email="${email}"]`)

// Anchored, because every decision control carries the document and version in visually-hidden text so that
// each button says which row it decides — which means "Reject" appears inside the Approve button's
// accessible name whenever the document is called something like "Reject Me".
const approve = (row: ReturnType<typeof approvalRows>) =>
  row.getByRole('button', { name: /^Approve\b/ })
const reject = (row: ReturnType<typeof approvalRows>) =>
  row.getByRole('button', { name: /^Reject\b/ })
const cancel = (row: ReturnType<typeof approvalRows>) =>
  row.getByRole('button', { name: /^Cancel\b/ })

const DUE = '2026-12-31'

async function publish(page: Page, versionId: string, name: string) {
  const res = await page.request.post(`/api/v1/versions/${versionId}/publish`, {
    data: { kind: 'minor', name },
  })
  expect(res.ok(), `publish failed: ${res.status()} ${await res.text()}`).toBeTruthy()
}

// Registers a fresh person and mints them an invitation: to this DOCUMENT when documentId is given,
// org-only when it is not. register() binds the isolated `request` context to the new person, which is why
// the invite itself goes through page.request (the owner) — page.request shares the page's cookie jar.
async function invitePerson(
  page: Page,
  request: APIRequestContext,
  opts: { documentId?: string; role: string },
) {
  const person = await register(request)
  const res = opts.documentId
    ? await page.request.post(`/api/v1/documents/${opts.documentId}/members`, {
        data: { email: person.email, role: opts.role },
      })
    : await page.request.post('/api/v1/org/members', {
        data: { email: person.email, role: opts.role },
      })
  expect(res.ok(), `invite failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const { invitationToken } = (await res.json()) as { invitationToken: string }
  return { person, invitationToken }
}

// Accepting without a browser, for the people who only need to EXIST (a name in a picker, a row in a
// roster). The accept has to be made as the invitee, and `request` is already signed in as them.
async function acceptHeadless(request: APIRequestContext, invitationToken: string) {
  const res = await request.post(`/api/v1/invitations/${invitationToken}:accept`)
  expect(res.ok(), `accept failed: ${res.status()} ${await res.text()}`).toBeTruthy()
}

// A second person with their own browser, for the tests where they have to act in the UI. The accept must
// happen from THAT context: its response re-issues ed_session bound to the inviting org, and a plain login
// binds to the invitee's own oldest org — from which this document (and this inbox) is cross-org.
// Same dance as console.spec.ts and copies.spec.ts.
async function browserFor(browser: Browser, person: Account, invitationToken: string) {
  const context = await browser.newContext()
  const theirPage = await context.newPage()
  await signIn(theirPage, person)
  await acceptHeadless(theirPage.request, invitationToken)
  return { context, theirPage }
}

test('1. E7: no request control on a draft, one on a published version', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Draft Only')
  const v1 = await uploadVersion(page, documentId, 'base.docx')

  // Absence, not a disabled control: an approval on a draft is not a thing the product can do, so there is
  // nothing to offer. (The API answers 400 "Approvals can only be requested on a published version.")
  await page.goto(`/documents/${documentId}/approvals`)
  await expect(page.getByTestId('approvals')).toBeVisible()
  await expect(requestForm(page)).toHaveCount(0)

  await publish(page, v1, 'For review')
  await page.reload()
  await expect(requestForm(page)).toHaveCount(1)
  // The version picker offers the published version only — the same reason.
  await disclose(requestForm(page))
  await expect(requestForm(page).getByLabel('Version').locator('option')).toHaveText(['0.1.0'])
})

test('2. the approver picker lists document members only', async ({
  signedIn: page,
  request,
}) => {
  const documentId = await createDocument(page, 'Members Only')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'For review')

  const onDocument = await invitePerson(page, request, { documentId, role: 'Viewer' })
  await acceptHeadless(request, onDocument.invitationToken)
  const orgOnly = await invitePerson(page, request, { role: 'Member' })
  await acceptHeadless(request, orgOnly.invitationToken)

  // The org roster genuinely contains both — otherwise the assertion below proves nothing.
  const roster = await page.request.get('/api/v1/org/members')
  const emails = ((await roster.json()) as { email: string }[]).map((m) => m.email)
  expect(emails).toContain(onDocument.person.email)
  expect(emails).toContain(orgOnly.person.email)

  await page.goto(`/documents/${documentId}/approvals`)
  await expect(approverOption(page, onDocument.person.email)).toHaveCount(1)
  // Not offerable: the API refuses a non-member approverId (it would hand a decision right over a document
  // the approver cannot read), so the picker must never present one.
  await expect(approverOption(page, orgOnly.person.email)).toHaveCount(0)
})

test('3. requesting approval with a due date shows on the document’s Approvals tab', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Needs Sign-off')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'For review')

  await page.goto(`/documents/${documentId}/approvals`)
  await disclose(requestForm(page))
  await approverOption(page, (await me(page)).email).getByRole('checkbox').check()
  await requestForm(page).getByLabel('Due date').fill(DUE)
  await requestForm(page).getByRole('button', { name: 'Request approval' }).click()

  const row = approvalRows(page)
  await expect(row).toHaveCount(1)
  await expect(row).toHaveAttribute('data-status', 'open')
  await expect(row.getByTestId('approval-version')).toHaveText('0.1.0')
  await expect(row.getByTestId('approval-due')).toHaveAttribute('datetime', new RegExp(`^${DUE}`))
})

test('4. the approver’s inbox shows the pending item, denormalised', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'Inbox Contract')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'For review')
  const { person, invitationToken } = await invitePerson(page, request, {
    documentId,
    role: 'Viewer',
  })
  const { context, theirPage } = await browserFor(browser, person, invitationToken)

  await requestApprovalOf(page, documentId, person.email)

  // Document name, version number and due date all come off the row itself — the inbox renders without a
  // request per item. A Viewer is enough to be asked: responding is a read plus a decision, not an edit.
  await theirPage.goto('/approvals')
  const row = approvalRows(theirPage)
  await expect(row).toHaveCount(1)
  await expect(row.getByTestId('approval-document')).toHaveText('Inbox Contract')
  await expect(row.getByTestId('approval-version')).toHaveText('0.1.0')
  await expect(row.getByTestId('approval-due')).toHaveAttribute('datetime', new RegExp(`^${DUE}`))
  await expect(row).toHaveAttribute('data-status', 'open')

  await context.close()
})

test('5. E7: approve with a comment, and the decision is immutable', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'Approve Me')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'For review')
  const { person, invitationToken } = await invitePerson(page, request, {
    documentId,
    role: 'Viewer',
  })
  const { context, theirPage } = await browserFor(browser, person, invitationToken)
  await requestApprovalOf(page, documentId, person.email)

  await theirPage.goto('/approvals')
  const row = approvalRows(theirPage)
  await row.getByLabel('Comment').fill('Fine by me.')
  await approve(row).click()

  await expect(row).toHaveAttribute('data-status', 'approved')
  await expect(row.getByTestId('approval-status')).toHaveText('approved')
  await expect(row.getByTestId('approval-comment')).toHaveText('Fine by me.')

  // Immutable: one decision, ever. The API answers 409 "Already closed" — the UI offers nothing to click.
  await expect(approve(row)).toHaveCount(0)
  await expect(reject(row)).toHaveCount(0)
  await expect(row.getByLabel('Comment')).toHaveCount(0)

  // The requester sees the decision on the document's tab, comment and all.
  await page.reload()
  await expect(approvalRows(page)).toHaveAttribute('data-status', 'approved')
  await expect(approvalRows(page).getByTestId('approval-comment')).toHaveText('Fine by me.')

  await context.close()
})

test('6. reject works symmetrically', async ({ signedIn: page, request, browser }) => {
  const documentId = await createDocument(page, 'Reject Me')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'For review')
  const { person, invitationToken } = await invitePerson(page, request, {
    documentId,
    role: 'Viewer',
  })
  const { context, theirPage } = await browserFor(browser, person, invitationToken)
  await requestApprovalOf(page, documentId, person.email)

  await theirPage.goto('/approvals')
  const row = approvalRows(theirPage)
  await row.getByLabel('Comment').fill('Clause 4 is wrong.')
  await reject(row).click()

  await expect(row).toHaveAttribute('data-status', 'rejected')
  await expect(row.getByTestId('approval-comment')).toHaveText('Clause 4 is wrong.')
  await expect(reject(row)).toHaveCount(0)

  await context.close()
})

test('7. E7: cancel closes an open request', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Cancelled Sign-off')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  await publish(page, v1, 'For review')

  await page.goto(`/documents/${documentId}/approvals`)
  await disclose(requestForm(page))
  await approverOption(page, (await me(page)).email).getByRole('checkbox').check()
  await requestForm(page).getByRole('button', { name: 'Request approval' }).click()
  await expect(approvalRows(page)).toHaveAttribute('data-status', 'open')

  await cancel(approvalRows(page)).click()
  await expect(approvalRows(page)).toHaveAttribute('data-status', 'cancelled')
  // Closed is closed: nothing left to decide or to cancel again (the API answers 409 either way).
  await expect(cancel(approvalRows(page))).toHaveCount(0)
  await expect(approve(approvalRows(page))).toHaveCount(0)
})

test('8. the inbox’s open filter excludes a decided approval', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'Filtered')
  const v1 = await uploadVersion(page, documentId, 'base.docx')
  const v2 = await uploadVersion(page, documentId, 'edited.docx')
  await publish(page, v1, 'First')
  await publish(page, v2, 'Second')
  const { person, invitationToken } = await invitePerson(page, request, {
    documentId,
    role: 'Viewer',
  })
  const { context, theirPage } = await browserFor(browser, person, invitationToken)

  // Two minor publications from the document counter: 0.1.0 then 0.2.0.
  await requestApprovalOf(page, documentId, person.email, '0.1.0')
  await requestApprovalOf(page, documentId, person.email, '0.2.0')

  await theirPage.goto('/approvals')
  await expect(approvalRows(theirPage)).toHaveCount(2)
  await approve(decided(theirPage, '0.1.0')).click()
  await expect(decided(theirPage, '0.1.0')).toHaveAttribute('data-status', 'approved')

  // status=open is a real API filter, not a client-side hide: the decided row is not in the response.
  await theirPage.getByLabel('Status').selectOption({ label: 'Open' })
  await expect(theirPage).toHaveURL(/status=open/)
  await expect(approvalRows(theirPage)).toHaveCount(1)
  await expect(approvalRows(theirPage).getByTestId('approval-version')).toHaveText('0.2.0')

  // And "asked by me" is the requester's side of the same inbox — both approvals, from her point of view.
  await page.goto('/approvals?filter=requested')
  await expect(approvalRows(page)).toHaveCount(2)

  await context.close()
})

const decided = (page: Page, version: string) =>
  page.locator('[data-testid="approval-row"]', { has: page.getByText(version, { exact: true }) })

async function me(page: Page) {
  const res = await page.request.get('/api/v1/me')
  expect(res.ok(), `me failed: ${res.status()}`).toBeTruthy()
  return (await res.json()) as { id: string; email: string; displayName: string }
}

// Drives the real request form: pick the version (the newest published one by default), tick the approver
// by email, set a due date, submit.
async function requestApprovalOf(
  page: Page,
  documentId: string,
  email: string,
  versionNumber?: string,
) {
  await page.goto(`/documents/${documentId}/approvals`)
  await disclose(requestForm(page))
  if (versionNumber) {
    await requestForm(page).getByLabel('Version').selectOption({ label: versionNumber })
  }
  await approverOption(page, email).getByRole('checkbox').check()
  await requestForm(page).getByLabel('Due date').fill(DUE)
  await requestForm(page).getByRole('button', { name: 'Request approval' }).click()
  await expect(approvalRows(page).first()).toBeVisible()
}
