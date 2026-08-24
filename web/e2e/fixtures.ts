import { readFileSync } from 'node:fs'
import { test as base, expect, type Locator, type Page, type APIRequestContext } from '@playwright/test'

export type Account = { email: string; password: string; orgName: string }

export async function register(request: APIRequestContext): Promise<Account> {
  const stamp = `${Date.now()}-${Math.random().toString(36).slice(2)}`
  const account = {
    email: `e2e-${stamp}@example.com`,
    password: 'pw-at-least-12',
    orgName: `E2E ${stamp}`,
  }
  const res = await request.post('/api/v1/auth/register', {
    data: { ...account, displayName: 'E2E User' },
  })
  expect(res.ok(), `register failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  return account
}

export async function signIn(page: Page, account: Account) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(account.email)
  await page.getByLabel('Password').fill(account.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByTestId('dashboard')).toBeVisible()
}

// Every form that WRITES now lives behind a native <details> disclosure (M5.5), so a test opens the
// one it is about to use — the same click a person makes to reach it. Idempotent on purpose: a test
// may drive the same panel several times, and clicking an open summary would close it.
export async function disclose(details: Locator) {
  const open = await details.evaluate((d) => (d as HTMLDetailsElement).open)
  if (!open) await details.locator('summary').first().click()
  await expect(details).toHaveAttribute('open', '')
}

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'

// Seeding for screens that have no create/upload control of their own — the document console reads
// history, it does not write it. `page.request` shares the page's cookie jar, so these are the same
// authenticated calls the SPA would make. Fixture bytes come from the committed .docx files (see the
// note in dashboard.spec.ts about why there are three of them).
export async function createDocument(page: Page, name: string): Promise<string> {
  const res = await page.request.post('/api/v1/documents', { data: { name } })
  expect(res.ok(), `create document failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  return ((await res.json()) as { id: string }).id
}

export function fixtureBytes(fixture: string) {
  return readFileSync(`e2e/fixtures/${fixture}`)
}

export async function uploadVersion(page: Page, documentId: string, fixture: string): Promise<string> {
  const res = await page.request.post(`/api/v1/documents/${documentId}/versions`, {
    multipart: { file: { name: fixture, mimeType: DOCX, buffer: fixtureBytes(fixture) } },
  })
  expect(res.ok(), `upload failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  return ((await res.json()) as { versionId: string }).versionId
}

// A concurrent branch, produced the way the C# E4 suite produces it — two edit sessions minted from the
// same head, then two WOPI saves of different bytes. Collabora is not running here and is not needed:
// WOPI is a server-to-server contract, so the race is real without any editor UI.
//
// Lives here rather than in console.spec.ts because two specs now need the same branch: the console's
// own history rendering, and the three-way merge review.
export type Session = { sessionId: string; editorUrl: string; accessToken: string }

export async function mintSession(page: Page, versionId: string): Promise<Session> {
  const res = await page.request.post(`/api/v1/versions/${versionId}/sessions`)
  expect(res.ok(), `mint session failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const session = (await res.json()) as Session
  // Assert the URL, never anything inside it: nothing serves it in this environment.
  expect(session.editorUrl).toContain(`WOPISrc=`)
  return session
}

// The WOPI routes authorize on the access_token query param, not the session cookie.
export async function wopiSave(page: Page, session: Session, fixture: string) {
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
export async function raceConcurrentBranch(page: Page, name: string) {
  const documentId = await createDocument(page, name)
  const head = await uploadVersion(page, documentId, 'base.docx')
  const mine = await mintSession(page, head)
  const theirs = await mintSession(page, head)
  await wopiSave(page, mine, 'edited.docx')
  await wopiSave(page, theirs, 'edited-plus-echo.docx')
  return documentId
}

// `signedIn` gives a test a page that is already inside a fresh, empty org.
export const test = base.extend<{ account: Account; signedIn: Page }>({
  account: async ({ request }, use) => use(await register(request)),
  signedIn: async ({ page, account }, use) => {
    await signIn(page, account)
    await use(page)
  },
})

export { expect }
