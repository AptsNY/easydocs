import type { APIRequestContext, Browser, BrowserContext, Page } from '@playwright/test'
import {
  test,
  expect,
  register,
  signIn,
  createDocument,
  fixtureBytes,
  uploadVersion,
  type Account,
} from './fixtures'

// The document console against the real API: spec §9's revision history (main spine + grouped
// concurrent-branch entries + Merge), and the members panel with its invitation and last-owner rules.
//
// The concurrent branch is produced the way the C# E4 suite produces it — two edit sessions minted from
// the same head, then two WOPI saves of different bytes. Collabora is not running here and is not
// needed: WOPI is a server-to-server contract, so the race is real without any editor UI.

const versionRows = (page: Page) => page.getByTestId('version-row')
const row = (page: Page, number: string) =>
  page.locator(`[data-testid="version-row"][data-number="${number}"]`)
const concurrentGroup = (page: Page) =>
  page.locator('[data-testid="branch-group"][data-kind="Concurrent"]')

const panel = (page: Page) => page.getByTestId('members-panel')
const memberRow = (page: Page, email: string) =>
  page.locator(`[data-testid="member-row"][data-email="${email}"]`)

type Session = { sessionId: string; editorUrl: string; accessToken: string }

async function mintSession(page: Page, versionId: string): Promise<Session> {
  const res = await page.request.post(`/api/v1/versions/${versionId}/sessions`)
  expect(res.ok(), `mint session failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const session = (await res.json()) as Session
  // Assert the URL, never anything inside it: nothing serves it in this environment.
  expect(session.editorUrl).toContain(`WOPISrc=`)
  return session
}

// The WOPI routes authorize on the access_token query param, not the session cookie.
async function wopiSave(page: Page, session: Session, fixture: string) {
  const q = `access_token=${session.accessToken}`
  const lock = await page.request.post(`/wopi/files/${session.sessionId}?${q}`, {
    headers: { 'X-WOPI-Override': 'LOCK', 'X-WOPI-Lock': 'L1' },
  })
  expect(lock.ok(), `WOPI lock failed: ${lock.status()} ${await lock.text()}`).toBeTruthy()

  const put = await page.request.post(`/wopi/files/${session.sessionId}/contents?${q}`, {
    headers: { 'X-WOPI-Lock': 'L1', 'Content-Type': 'application/octet-stream' },
    data: fixtureBytes(fixture),
  })
  expect(put.ok(), `WOPI put failed: ${put.status()} ${await put.text()}`).toBeTruthy()
}

// Branch-on-stale-base: both sessions open on 0.0.1, the first save fast-forwards main to 0.0.2, the
// second lands on a Concurrent branch as 0.0.3 rather than overwriting it (E4, zero lost edits).
async function raceConcurrentBranch(page: Page, name: string) {
  const documentId = await createDocument(page, name)
  const head = await uploadVersion(page, documentId, 'base.docx')
  const mine = await mintSession(page, head)
  const theirs = await mintSession(page, head)
  await wopiSave(page, mine, 'edited.docx')
  await wopiSave(page, theirs, 'edited-plus-echo.docx')
  return documentId
}

// DiffSummaryWorker computes parent->child summaries off the request thread, so a freshly uploaded
// version legitimately has `summary: null` for a moment. Waiting on the API keeps the rendering test
// about rendering; sse.spec.ts is where the diff.ready refresh path is proven.
async function waitForSummary(page: Page, documentId: string) {
  await expect
    .poll(async () => {
      const res = await page.request.get(`/api/v1/documents/${documentId}/versions`)
      const body = (await res.json()) as { items: { summary: unknown }[] }
      return body.items.some((v) => v.summary !== null)
    })
    .toBeTruthy()
}

// Invite an email that belongs to no member of this org, then accept it as that person. The accept has
// to happen in the browser context that will view the document: its response rebinds ed_session to the
// inviting org, and a plain login would bind to the invitee's own (oldest) org instead — from which
// this document is cross-org, i.e. a 404.
async function addSecondMember(
  browser: Browser,
  page: Page,
  request: APIRequestContext,
  role: string,
): Promise<{ other: Account; theirContext: BrowserContext; theirPage: Page }> {
  const other = await register(request)

  await panel(page).getByLabel('Email').fill(other.email)
  // Exact, or it also matches the per-row "Change role for …" labels.
  await panel(page).getByLabel('Role', { exact: true }).selectOption(role)
  await panel(page).getByRole('button', { name: 'Add member' }).click()
  const token = await panel(page).getByTestId('invitation-token').textContent()
  expect(token).toBeTruthy()

  const theirContext = await browser.newContext()
  const theirPage = await theirContext.newPage()
  await signIn(theirPage, other)
  const accept = await theirPage.request.post(`/api/v1/invitations/${token!}:accept`)
  expect(accept.ok(), `accept failed: ${accept.status()} ${await accept.text()}`).toBeTruthy()

  return { other, theirContext, theirPage }
}

test('history lists versions newest first with author, time and change summary', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Contract')
  await uploadVersion(page, documentId, 'base.docx')
  await uploadVersion(page, documentId, 'edited.docx')
  await waitForSummary(page, documentId)

  // The API's default order is ascending (the C# suite asserts oldest-first), so the console has to
  // opt into desc rather than reversing a page client-side — reversing would only reorder page one.
  const descending = page.waitForRequest(
    (r) => r.url().includes(`/documents/${documentId}/versions`) && /[?&]order=desc(&|$)/.test(r.url()),
  )
  await page.goto(`/documents/${documentId}`)
  await descending

  await expect(page.getByRole('heading', { name: 'Contract' })).toBeVisible()
  await expect(versionRows(page)).toHaveCount(2)
  await expect(versionRows(page).first()).toHaveAttribute('data-number', '0.0.2')

  const newest = row(page, '0.0.2')
  await expect(newest.getByTestId('version-author')).toHaveText('E2E User')
  await expect(newest.locator('time')).toContainText(String(new Date().getFullYear()))
  await expect(newest.getByTestId('version-summary')).toHaveText(/\d+ insertions/)
})

test('a version with no parent shows a dash, never 0 insertions', async ({ signedIn: page }) => {
  const documentId = await createDocument(page, 'Solo')
  await uploadVersion(page, documentId, 'base.docx')
  await page.goto(`/documents/${documentId}`)

  const first = row(page, '0.0.1')
  await expect(first.getByTestId('version-summary')).toHaveText('—')
  await expect(first).not.toContainText('0 insertions')
})

test('a concurrent branch renders as an indented group with a Merge button (E4)', async ({
  signedIn: page,
}) => {
  const documentId = await raceConcurrentBranch(page, 'Race')
  await page.goto(`/documents/${documentId}`)

  // The spine is main only; the concurrent save is not in it.
  await expect(page.locator('[data-testid="version-row"][data-branch-kind="Main"]')).toHaveCount(2)
  await expect(concurrentGroup(page)).toHaveCount(1)
  await expect(concurrentGroup(page).getByTestId('version-row')).toHaveAttribute(
    'data-number',
    '0.0.3',
  )
  // Indented, not a sibling list: the group is nested inside the main spine at its fork point.
  await expect(page.locator('[data-testid="branch-spine"] [data-testid="branch-group"]')).toBeVisible()
  await expect(concurrentGroup(page).getByRole('button', { name: 'Merge' })).toBeVisible()
})

test('merging a concurrent branch adds a version and loses nothing (E4)', async ({
  signedIn: page,
}) => {
  const documentId = await raceConcurrentBranch(page, 'Merge Me')
  await page.goto(`/documents/${documentId}`)
  await concurrentGroup(page).getByRole('button', { name: 'Merge' }).click()

  await expect(row(page, '0.0.4')).toBeVisible()
  await expect(concurrentGroup(page)).toHaveAttribute('data-merged', 'true')
  await expect(concurrentGroup(page).getByRole('button', { name: 'Merge' })).toHaveCount(0)

  // E4: nothing is lost. Both racing versions are still in history after the merge.
  await expect(row(page, '0.0.2')).toBeVisible()
  await expect(row(page, '0.0.3')).toBeVisible()
})

test('the members panel lists members with their roles', async ({ signedIn: page, account }) => {
  const documentId = await createDocument(page, 'Members')
  await page.goto(`/documents/${documentId}`)

  await expect(panel(page).getByTestId('member-row')).toHaveCount(1)
  await expect(memberRow(page, account.email).getByTestId('member-role')).toHaveText('Owner')
})

test('adding an email from outside the org shows the invitation token exactly once', async ({
  signedIn: page,
}) => {
  const documentId = await createDocument(page, 'Invite')
  await page.goto(`/documents/${documentId}`)

  await panel(page).getByLabel('Email').fill(`outsider-${Date.now()}@example.com`)
  await panel(page).getByLabel('Role', { exact: true }).selectOption('Editor')
  await panel(page).getByRole('button', { name: 'Add member' }).click()

  const token = panel(page).getByTestId('invitation-token')
  await expect(token).toHaveCount(1)
  await expect(token).toHaveText(/^[A-Za-z0-9_-]{20,}$/)

  // Once means once: the API returns the raw token only from the create call, so a reload cannot
  // resurrect it and the UI must not pretend otherwise.
  await page.reload()
  await expect(panel(page).getByTestId('invitation-token')).toHaveCount(0)
})

test('the last owner cannot be removed or demoted, and the API detail is surfaced', async ({
  signedIn: page,
  account,
}) => {
  const documentId = await createDocument(page, 'Sole Owner')
  await page.goto(`/documents/${documentId}`)
  const me = memberRow(page, account.email)

  await me.getByRole('button', { name: /Remove/ }).click()
  await expect(panel(page).getByRole('alert')).toContainText(
    'A document must keep at least one owner.',
  )
  await expect(me).toBeVisible()

  await me.getByLabel(/Change role/).selectOption('Viewer')
  await expect(panel(page).getByRole('alert')).toContainText(
    'A document must keep at least one owner.',
  )
  await expect(me.getByTestId('member-role')).toHaveText('Owner')
})

test('an owner changes a second member’s role', async ({ signedIn: page, request, browser }) => {
  const documentId = await createDocument(page, 'Role Change')
  await page.goto(`/documents/${documentId}`)
  const { other, theirContext } = await addSecondMember(browser, page, request, 'Viewer')
  await theirContext.close()

  // The API publishes no member.added SSE event, so the owner's roster needs a refetch to see the
  // accepted invitation. The reload here is about that gap, not about the role change under test.
  await page.reload()
  const them = memberRow(page, other.email)
  await expect(them.getByTestId('member-role')).toHaveText('Viewer')

  await them.getByLabel(/Change role/).selectOption('Editor')
  await expect(them.getByTestId('member-role')).toHaveText('Editor')
  await page.reload()
  await expect(memberRow(page, other.email).getByTestId('member-role')).toHaveText('Editor')
})

test('a Viewer sees the roster but no mutating member controls (E12)', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'Viewer View')
  await page.goto(`/documents/${documentId}`)
  const { other, theirContext, theirPage } = await addSecondMember(browser, page, request, 'Viewer')

  await theirPage.goto(`/documents/${documentId}`)
  await expect(panel(theirPage).getByTestId('member-row')).toHaveCount(2)
  await expect(memberRow(theirPage, other.email).getByTestId('member-role')).toHaveText('Viewer')

  await expect(panel(theirPage).getByRole('button', { name: 'Add member' })).toHaveCount(0)
  await expect(panel(theirPage).getByRole('button', { name: /Remove/ })).toHaveCount(0)
  await expect(panel(theirPage).getByLabel(/Change role/)).toHaveCount(0)

  await theirContext.close()
})
