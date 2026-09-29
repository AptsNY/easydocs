import { test, expect, register, signIn } from './fixtures'

// Admin-issued password reset, end to end (spec 2026-09-08).
//
// This is the only spec that exercises a route registered OUTSIDE RequireAuth other than /s/:token, and
// that is the reason it is worth its cost: /password-reset/:token must stay reachable with no session at
// all. A refactor that folds it inside the guard breaks the entire feature — the people who need it are
// locked out by definition — and nothing else in the suite would notice.
//
// Who gets reset matters. Playwright has no database access, so the only second person it can create is
// one who registers (acquiring a personal org) and then accepts an invitation. That is exactly the
// account shape the cross-team gate is built to allow: their only other org is their own, and it has
// nobody else in it. Under the rejected "refuse any multi-org target" rule this test would have been
// unwritable — which is how we learned that rule was wrong.
test('an owner issues a reset link and the locked-out member sets a new password with it', async ({
  signedIn: owner,
  request,
  browser,
}) => {
  // A colleague who registers, then joins the owner's org by invitation — the ordinary path.
  const person = await register(request)
  const invited = await owner.request.post('/api/v1/org/members', {
    data: { email: person.email, role: 'Member' },
  })
  expect(invited.ok(), `invite failed: ${invited.status()} ${await invited.text()}`).toBeTruthy()
  const { invitationToken } = (await invited.json()) as { invitationToken: string }

  const theirContext = await browser.newContext()
  const theirs = await theirContext.newPage()
  await signIn(theirs, person)
  const accepted = await theirs.request.post(`/api/v1/invitations/${invitationToken}:accept`)
  expect(accepted.ok(), `accept failed: ${accepted.status()} ${await accepted.text()}`).toBeTruthy()
  await theirContext.close()

  // The owner issues the link from the org roster.
  await owner.goto('/settings')
  const row = owner.locator(`[data-testid="org-member-row"][data-email="${person.email}"]`)
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: `Reset the password for ${person.email}` }).click()

  // Shown exactly once — only the hash is stored — so the admin copies it here or not at all.
  const link = await owner.getByTestId('password-reset-url').innerText()
  expect(link).toContain('/password-reset/')

  // A brand-new context with no cookies: this is the whole point, someone who cannot sign in.
  const lockedOut = await browser.newContext()
  const page = await lockedOut.newPage()
  await page.goto(new URL(link).pathname)
  await expect(page.getByTestId('password-reset')).toBeVisible()

  const newPassword = 'a-brand-new-password'
  await page.getByLabel('New password', { exact: true }).fill(newPassword)
  await page.getByLabel('Confirm new password').fill(newPassword)
  await page.getByTestId('password-reset-submit').click()

  // No session is issued on purpose, so success lands on the sign-in screen rather than the dashboard.
  await expect(page).toHaveURL(/\/login$/)

  await signIn(page, { ...person, password: newPassword })
  await lockedOut.close()
})

test('a spent reset link says so instead of stranding the visitor', async ({
  signedIn: owner,
  request,
  browser,
}) => {
  const person = await register(request)
  const invited = await owner.request.post('/api/v1/org/members', {
    data: { email: person.email, role: 'Member' },
  })
  const { invitationToken } = (await invited.json()) as { invitationToken: string }

  const theirContext = await browser.newContext()
  const theirs = await theirContext.newPage()
  await signIn(theirs, person)
  await theirs.request.post(`/api/v1/invitations/${invitationToken}:accept`)
  await theirContext.close()

  await owner.goto('/settings')
  const row = owner.locator(`[data-testid="org-member-row"][data-email="${person.email}"]`)
  await row.getByRole('button', { name: `Reset the password for ${person.email}` }).click()
  const link = await owner.getByTestId('password-reset-url').innerText()

  // Spend it once.
  const spend = await owner.request.post('/api/v1/auth/password-reset:complete', {
    data: { token: new URL(link).pathname.split('/').pop(), password: 'first-password-set' },
  })
  expect(spend.ok(), `first consume failed: ${spend.status()} ${await spend.text()}`).toBeTruthy()

  // The second visitor gets told, rather than left on a form that silently fails.
  const lockedOut = await browser.newContext()
  const page = await lockedOut.newPage()
  await page.goto(new URL(link).pathname)
  await page.getByLabel('New password', { exact: true }).fill('another-password-x')
  await page.getByLabel('Confirm new password').fill('another-password-x')
  await page.getByTestId('password-reset-submit').click()

  await expect(page.getByTestId('password-reset-error')).toBeVisible()
  await lockedOut.close()
})
