import type { APIRequestContext, Browser, Page } from '@playwright/test'
import { test, expect, register, signIn, createDocument, type Account } from './fixtures'

// What a session IS, and how it ends. Redeeming an invitation is invitations.spec.ts; this is the
// three things around it that only break for a second person or a second visit:
//   * signing out left the httpOnly ed_session cookie in place, so Back restored the session;
//   * a session binds to exactly ONE org and login picks the oldest membership, so an invited
//     colleague reached the inviting org for a single session and never again;
//   * the route guard threw away where you were going, so every deep link landed on the dashboard.

async function inviteInto(
  page: Page,
  request: APIRequestContext,
  documentId: string,
): Promise<{ person: Account; token: string }> {
  const person = await register(request)
  const res = await page.request.post(`/api/v1/documents/${documentId}/members`, {
    data: { email: person.email, role: 'Editor' },
  })
  expect(res.ok(), `invite failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const { invitationToken } = (await res.json()) as { invitationToken: string }
  return { person, token: invitationToken }
}

// A member of two orgs: their own from registration, plus the one that invited them.
async function twoOrgRecipient(browser: Browser, person: Account, token: string) {
  const context = await browser.newContext()
  const page = await context.newPage()
  await signIn(page, person)
  await page.goto(`/invitations/${token}`)
  return { context, page }
}

test('signing out ends the session — going back does not restore it', async ({ signedIn }) => {
  await signedIn.getByRole('button', { name: 'Sign out' }).click()
  await expect(signedIn.getByTestId('login')).toBeVisible()

  // The assertion that matters. Clearing client state only would leave the cookie valid, and this
  // navigation would put the previous user straight back into the app on a shared machine.
  await signedIn.goto('/')
  await expect(signedIn.getByTestId('login')).toBeVisible()
  await expect(signedIn.getByTestId('dashboard')).toHaveCount(0)
})

test('the org switcher is absent for someone who belongs to one org', async ({ signedIn }) => {
  await expect(signedIn.getByTestId('dashboard')).toBeVisible()
  await expect(signedIn.getByTestId('org-switcher')).toHaveCount(0)
})

test('a member of two orgs can move between them, and the move survives a fresh sign-in', async ({
  signedIn: page,
  request,
  account,
  browser,
}) => {
  const documentId = await createDocument(page, 'Cross Org Lease')
  const { person, token } = await inviteInto(page, request, documentId)

  const { context, page: theirs } = await twoOrgRecipient(browser, person, token)
  await expect(theirs.getByTestId('org-name')).toHaveText(account.orgName)

  // Belonging to two orgs is what makes the control exist at all.
  const switcher = theirs.getByTestId('org-switcher')
  await expect(switcher).toBeVisible()

  await switcher.selectOption({ label: person.orgName })
  await expect(theirs.getByTestId('org-name')).toHaveText(person.orgName)

  await theirs.getByTestId('org-switcher').selectOption({ label: account.orgName })
  await expect(theirs.getByTestId('org-name')).toHaveText(account.orgName)

  // The regression proper: sign out and back in. Login binds to the OLDEST membership — their own org —
  // so without a switcher this is where the inviting org became permanently unreachable.
  await theirs.getByRole('button', { name: 'Sign out' }).click()
  await signIn(theirs, person)
  await expect(theirs.getByTestId('org-name')).toHaveText(person.orgName)

  await theirs.getByTestId('org-switcher').selectOption({ label: account.orgName })
  await expect(theirs.getByTestId('org-name')).toHaveText(account.orgName)
  // And membership is real, not cosmetic: the document they were invited to opens.
  await theirs.goto(`/documents/${documentId}`)
  await expect(theirs.getByTestId('history')).toBeVisible()

  await context.close()
})

test('a deep link survives the sign-in instead of dumping you on the dashboard', async ({
  page,
  request,
}) => {
  const account = await register(request)
  await signIn(page, account)
  const documentId = await createDocument(page, 'Deep Linked')
  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByTestId('login')).toBeVisible()

  // Arriving cold at a guarded URL: bounced to /login, and then returned to where you were going.
  // An invitation link is the case that made this matter — its whole payload is in the path.
  await page.goto(`/documents/${documentId}/audit`)
  await expect(page.getByTestId('login')).toBeVisible()
  await page.getByLabel('Email').fill(account.email)
  await page.getByLabel('Password').fill(account.password)
  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page).toHaveURL(new RegExp(`/documents/${documentId}/audit$`))
})
