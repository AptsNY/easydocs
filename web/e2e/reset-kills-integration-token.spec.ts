import { test, expect, createDocument, uploadVersion, disclose } from './fixtures'

// An integration authenticates with an `ed_` token owned by a PERSON's account. Completing a password
// reset for that account revokes every token it owns (PasswordResetEndpoints.Complete, by design), so
// the integration starts getting 401s until someone mints a replacement. This replays that in the UI.
test('resetting the token owner\'s password 401s the integration until a new token is minted', async ({
  signedIn: owner,
  account,
  request,
  browser,
}) => {
  const docId = await createDocument(owner, 'Lease Agreement')
  await uploadVersion(owner, docId, 'base.docx')

  const mintToken = async (name: string) => {
    await owner.goto('/settings')
    await disclose(owner.getByTestId('new-token'))
    await owner.getByPlaceholder('Token name').fill(name)
    await owner.getByRole('button', { name: 'Create token' }).click()
    return owner.getByTestId('token-value').innerText()
  }

  // The integration: bearer token only, the same lookup a template resolver makes.
  const lookup = async (token: string) =>
    (await request.get(`/api/v1/documents/${docId}/versions?order=desc&limit=100`, {
      headers: { Authorization: `Bearer ${token}` },
    })).status()

  const token = await mintToken('integration')
  expect(await lookup(token), 'works before the reset').toBe(200)

  // The owner resets their own password from the roster; the link is used in another browser.
  await owner.goto('/settings')
  await owner.getByRole('button', { name: `Reset the password for ${account.email}` }).click()
  const link = await owner.getByTestId('password-reset-url').innerText()
  // The admin is told before sending the link, not after the integration breaks.
  await expect(owner.getByTestId('password-reset-revokes-tokens')).toContainText('1 API token')

  const other = await browser.newContext()
  const page = await other.newPage()
  await page.goto(new URL(link).pathname)
  await page.getByLabel('New password', { exact: true }).fill('a-brand-new-password')
  await page.getByLabel('Confirm new password').fill('a-brand-new-password')
  await page.getByTestId('password-reset-submit').click()
  await expect(page).toHaveURL(/\/login$/)
  await other.close()

  expect(await lookup(token), 'reset revoked the token').toBe(401)
  expect(await lookup(await mintToken('integration (rotated)')), 'a new token restores it').toBe(200)
})

// A service account (spec 2026-09-24) is the fix for the test above: its token belongs to no person, so
// no one's password reset — not even its manager's — can revoke it.
test('a service-account token survives its manager\'s password reset', async ({
  signedIn: owner,
  account,
  request,
  browser,
}) => {
  const docId = await createDocument(owner, 'Lease Agreement')
  await uploadVersion(owner, docId, 'base.docx')

  await owner.goto('/settings')
  await disclose(owner.getByTestId('new-service-account'))
  await owner.getByPlaceholder('Service account name').fill('integration')
  await owner.getByRole('button', { name: 'Create service account' }).click()
  const row = owner.locator('[data-testid="service-account-row"][data-name="integration"]')
  const email = await row.getByTestId('service-account-email').innerText()
  await row.getByRole('button', { name: 'New token' }).click()
  const token = await owner.getByTestId('service-token-value').innerText()

  // Joined to the document like a person.
  const added = await owner.request.post(`/api/v1/documents/${docId}/members`, { data: { email, role: 'Viewer' } })
  expect(added.ok(), `add failed: ${added.status()} ${await added.text()}`).toBeTruthy()

  const lookup = async () =>
    (await request.get(`/api/v1/documents/${docId}/versions?order=desc&limit=100`, {
      headers: { Authorization: `Bearer ${token}` },
    })).status()
  expect(await lookup(), 'works before the reset').toBe(200)

  await owner.goto('/settings')
  await owner.getByRole('button', { name: `Reset the password for ${account.email}` }).click()
  const link = await owner.getByTestId('password-reset-url').innerText()
  const other = await browser.newContext()
  const page = await other.newPage()
  await page.goto(new URL(link).pathname)
  await page.getByLabel('New password', { exact: true }).fill('a-brand-new-password')
  await page.getByLabel('Confirm new password').fill('a-brand-new-password')
  await page.getByTestId('password-reset-submit').click()
  await expect(page).toHaveURL(/\/login$/)
  await other.close()

  expect(await lookup(), 'the service token is not the manager\'s').toBe(200)
})

// A service account is Owner of what it creates. Its roster row must still say Owner: the picker drops
// the Owner option for service rows (the API caps them at Editor), so dropping it here too would make
// the select fall back to its first option and misstate the role as Editor.
test('a service account keeps showing Owner on a document it created', async ({
  signedIn: owner,
  account,
  request,
}) => {
  const created = await owner.request.post('/api/v1/org/service-accounts', { data: { name: 'ingest' } })
  const svc = (await created.json()) as { userId: string; email: string }
  const minted = await owner.request.post(`/api/v1/org/service-accounts/${svc.userId}/tokens`, {
    data: { name: 'ingest token' },
  })
  const auth = { Authorization: `Bearer ${((await minted.json()) as { token: string }).token}` }

  const doc = await request.post('/api/v1/documents', { headers: auth, data: { name: 'Ingested' } })
  expect(doc.ok(), `create failed: ${doc.status()} ${await doc.text()}`).toBeTruthy()
  const docId = ((await doc.json()) as { id: string }).id
  const added = await request.post(`/api/v1/documents/${docId}/members`, {
    headers: auth,
    data: { email: account.email, role: 'Owner' },
  })
  expect(added.ok(), `add failed: ${added.status()} ${await added.text()}`).toBeTruthy()

  await owner.goto(`/documents/${docId}/members`)
  const row = owner.locator(`[data-testid="member-row"][data-email="${svc.email}"]`)
  await expect(row.getByTestId('member-role')).toHaveValue('Owner')
})
