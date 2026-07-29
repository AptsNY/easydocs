import { test as base, expect, type Page, type APIRequestContext } from '@playwright/test'

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

// `signedIn` gives a test a page that is already inside a fresh, empty org.
export const test = base.extend<{ account: Account; signedIn: Page }>({
  account: async ({ request }, use) => use(await register(request)),
  signedIn: async ({ page, account }, use) => {
    await signIn(page, account)
    await use(page)
  },
})

export { expect }
