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
