import { test, expect, register, signIn } from './fixtures'

test('a new user registers, lands on the dashboard, and stays signed in across a reload', async ({
  page,
  request,
}) => {
  const account = await register(request)
  await signIn(page, account)
  await expect(page.getByText(account.orgName)).toBeVisible()

  // The session is an httpOnly cookie, so a reload must still be authenticated.
  await page.reload()
  await expect(page.getByTestId('dashboard')).toBeVisible()
})

test('a wrong password shows an error and does not navigate', async ({ page, request }) => {
  const account = await register(request)
  await page.goto('/login')
  await page.getByLabel('Email').fill(account.email)
  await page.getByLabel('Password').fill('definitely-wrong-password')
  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page.getByRole('alert')).toContainText(/incorrect/i)
  await expect(page.getByTestId('dashboard')).toBeHidden()
})

test('an anonymous visitor is redirected to the login screen', async ({ page }) => {
  await page.goto('/')
  await expect(page).toHaveURL(/\/login$/)
})
