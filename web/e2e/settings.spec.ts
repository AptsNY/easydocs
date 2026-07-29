import type { APIRequestContext, Page } from '@playwright/test'
import { test, expect, register, signIn, type Account } from './fixtures'

// The settings screen (spec §9): profile, `ed_` API tokens, and org management. None of this existed before
// M4.5 — there were no org endpoints at all, so the only way to get a second person anywhere near the
// product was to add them to a specific document. Phase A added GET/PATCH /org, the org member list, an
// org-only invitation, role changes and removal, with a last-owner guard on the sharp two.

const tokenRow = (page: Page, name: string) =>
  page.locator(`[data-testid="token-row"][data-name="${name}"]`)
const memberRow = (page: Page, email: string) =>
  page.locator(`[data-testid="org-member-row"][data-email="${email}"]`)

// A second real person in the OWNER's org. The accept re-issues ed_session bound to the inviting org, so it
// has to be made from the context that will use it: headless (the isolated `request`, which register() just
// bound to them) when they only need to exist, their own browser when they have to look at a screen.
async function orgMember(
  page: Page,
  request: APIRequestContext,
  role: string,
): Promise<{ person: Account; invitationToken: string }> {
  const person = await register(request)
  const res = await page.request.post('/api/v1/org/members', {
    data: { email: person.email, role },
  })
  expect(res.ok(), `invite failed: ${res.status()} ${await res.text()}`).toBeTruthy()
  const { invitationToken } = (await res.json()) as { invitationToken: string }
  return { person, invitationToken }
}

async function accept(request: APIRequestContext, invitationToken: string) {
  const res = await request.post(`/api/v1/invitations/${invitationToken}:accept`)
  expect(res.ok(), `accept failed: ${res.status()} ${await res.text()}`).toBeTruthy()
}

test('9. profile shows the signed-in user’s email and display name', async ({
  signedIn: page,
  account,
}) => {
  await page.goto('/settings')
  await expect(page.getByTestId('profile-email')).toHaveText(account.email)
  await expect(page.getByTestId('profile-name')).toHaveText('E2E User')
})

test('10. an API token’s raw value is shown exactly once, then revocable', async ({
  signedIn: page,
}) => {
  await page.goto('/settings')
  await page.getByLabel('Token name').fill('ci-pipeline')
  await page.getByRole('button', { name: 'Create token' }).click()

  // Only the hash is stored, so this is the one moment the raw token exists anywhere the user can reach.
  const raw = page.getByTestId('token-value')
  await expect(raw).toContainText(/^ed_\S+$/)
  await expect(tokenRow(page, 'ci-pipeline')).toHaveCount(1)

  // Gone after a reload, and the UI says so rather than pretending it can be recovered.
  await page.reload()
  await expect(page.getByTestId('token-value')).toHaveCount(0)
  await expect(tokenRow(page, 'ci-pipeline')).toHaveCount(1)

  await tokenRow(page, 'ci-pipeline').getByRole('button', { name: 'Revoke' }).click()
  await expect(tokenRow(page, 'ci-pipeline')).toHaveCount(0)
})

test('11. renaming the org leaves the slug untouched', async ({ signedIn: page }) => {
  await page.goto('/settings')
  const before = await page.getByTestId('org-slug').textContent()
  expect(before?.trim()).toBeTruthy()

  await page.getByLabel('Organization name').fill('Renamed Holdings')
  await page.getByRole('button', { name: 'Rename' }).click()
  // The shell header carries the org name, so a successful rename is visible outside this screen too.
  await expect(page.getByTestId('org-name')).toHaveText('Renamed Holdings')

  // Byte-identical: the slug is baked into R8 download filenames, so a rename must never re-slug.
  await expect(page.getByTestId('org-slug')).toHaveText(before!)
  await page.reload()
  await expect(page.getByTestId('org-slug')).toHaveText(before!)
})

test('12. the org member list renders, and an invitation token is shown once', async ({
  signedIn: page,
  account,
}) => {
  await page.goto('/settings')
  await expect(memberRow(page, account.email)).toHaveCount(1)
  await expect(memberRow(page, account.email).getByTestId('org-member-role')).toHaveText('Owner')

  await page.getByLabel('Invite by email').fill('newcomer@example.com')
  await page.getByRole('button', { name: 'Invite' }).click()
  await expect(page.getByTestId('org-invitation-token')).toContainText(/\S{16,}/)
})

test('13. an owner can change another member’s org role', async ({ signedIn: page, request }) => {
  const { person, invitationToken } = await orgMember(page, request, 'Member')
  await accept(request, invitationToken)

  await page.goto('/settings')
  const row = memberRow(page, person.email)
  await expect(row.getByTestId('org-member-role')).toHaveText('Member')
  await row.getByTestId('org-member-role-select').selectOption('Admin')
  await expect(row.getByTestId('org-member-role')).toHaveText('Admin')
})

test('14. the last owner cannot be demoted or removed', async ({ signedIn: page, account }) => {
  await page.goto('/settings')
  const mine = memberRow(page, account.email)

  await mine.getByTestId('org-member-role-select').selectOption('Member')
  await expect(page.getByRole('alert')).toContainText('An organization must keep at least one owner.')
  await expect(mine.getByTestId('org-member-role')).toHaveText('Owner')

  await mine.getByRole('button', { name: 'Remove' }).click()
  await expect(page.getByRole('alert')).toContainText('An organization must keep at least one owner.')
  await expect(mine).toHaveCount(1)
})

test('15. a plain member gets no org-management controls but still reads the roster', async ({
  signedIn: page,
  request,
  browser,
  account,
}) => {
  const { person, invitationToken } = await orgMember(page, request, 'Member')
  const context = await browser.newContext()
  const theirPage = await context.newPage()
  await signIn(theirPage, person)
  await accept(theirPage.request, invitationToken)

  await theirPage.goto('/settings')
  // The roster read is deliberately open to any member: the SPA's person pickers depend on it.
  await expect(memberRow(theirPage, account.email)).toHaveCount(1)
  await expect(memberRow(theirPage, person.email).getByTestId('org-member-role')).toHaveText('Member')

  // Everything sharp is Owner/Admin-only in the API; hiding it is courtesy, not the enforcement.
  await expect(theirPage.getByTestId('org-member-role-select')).toHaveCount(0)
  await expect(theirPage.getByRole('button', { name: 'Remove' })).toHaveCount(0)
  await expect(theirPage.getByLabel('Invite by email')).toHaveCount(0)
  await expect(theirPage.getByLabel('Organization name')).toHaveCount(0)
  // And nothing on the screen failed: absence above is gating, not a read the member was refused.
  await expect(theirPage.getByRole('alert')).toHaveCount(0)

  await context.close()
})
