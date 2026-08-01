import type { APIRequestContext, Browser, Page } from '@playwright/test'
import { test, expect, register, signIn, createDocument, type Account } from './fixtures'

// The invitation redemption screen (spec §10.1 Auth, POST /invitations/{token}:accept — /invitations/:token
// in the SPA route table).
//
// Every other spec that needs a second person accepts their invitation with `request.post(...)`, because
// what those tests are about is what happens *after* the roster changes. That left the one screen an
// invited colleague actually uses with no browser coverage at all: the route existed, and nothing proved
// a person could reach a document by opening the link they were sent.
//
// Two things only this screen can be wrong about, and both are silent failures:
//   * the accept response re-issues ed_session bound to the INVITING org, so the cached /me and /org in
//     the session context are stale — without the refresh() the header names the wrong organization and
//     the document the visitor was just admitted to reads as cross-org (404);
//   * a token that is spent, or was addressed to somebody else, must say so on screen rather than
//     stranding the visitor on a spinner.

// Mints a document invitation for a brand-new person, as the signed-in owner. The minting UI itself is
// covered by console.spec.ts; this spec is about redeeming what it hands out.
async function invite(
  page: Page,
  request: APIRequestContext,
  documentId: string,
  role = 'Editor',
): Promise<{ person: Account; token: string }> {
  const person = await register(request)
  const res = await page.request.post(`/api/v1/documents/${documentId}/members`, {
    data: { email: person.email, role },
  })
  expect(res.ok(), `invite failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const { invitationToken } = (await res.json()) as { invitationToken: string }
  return { person, token: invitationToken }
}

// The recipient's own browser, signed in as themselves and sitting on nothing but their own empty org.
async function asRecipient(browser: Browser, person: Account) {
  const context = await browser.newContext()
  const page = await context.newPage()
  await signIn(page, person)
  return { context, page }
}

test('an invited colleague redeems the link in the browser and lands inside the document', async ({
  signedIn: page,
  request,
  account,
  browser,
}) => {
  const documentId = await createDocument(page, 'Joint Venture')
  const { person, token } = await invite(page, request, documentId)

  const { context, page: theirs } = await asRecipient(browser, person)
  // Before: their own org, and this document is not theirs to see.
  await expect(theirs.getByTestId('org-name')).toHaveText(person.orgName)

  await theirs.goto(`/invitations/${token}`)

  // The screen redirects to the document it admitted them to — not to a dead end and not to a spinner.
  await expect(theirs).toHaveURL(new RegExp(`/documents/${documentId}$`))
  await expect(theirs.getByTestId('history')).toBeVisible()
  // And the session followed: the header names the INVITING org, so the console is reading it as a member.
  await expect(theirs.getByTestId('org-name')).toHaveText(account.orgName)

  await context.close()
})

test('a spent token is refused on screen, with a way out', async ({
  signedIn: page,
  request,
  browser,
}) => {
  const documentId = await createDocument(page, 'One Use Only')
  const { person, token } = await invite(page, request, documentId)

  const { context, page: theirs } = await asRecipient(browser, person)
  await theirs.goto(`/invitations/${token}`)
  await expect(theirs).toHaveURL(new RegExp(`/documents/${documentId}$`))

  // Each token works once. The second visit must fail loudly on the invitation screen rather than
  // silently navigating as though it had worked.
  await theirs.goto(`/invitations/${token}`)
  const failed = theirs.getByTestId('accept-invitation').getByRole('alert')
  await expect(failed).toBeVisible()
  await expect(theirs).toHaveURL(new RegExp(`/invitations/${token}$`))
  await expect(theirs.getByRole('link', { name: 'Go to your documents' })).toBeVisible()

  await context.close()
})
