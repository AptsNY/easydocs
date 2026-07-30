import { test, expect } from './fixtures'

// Every screen spec §9 names has a route from Task 9 onward, most of them still stubs. This asserts the
// route table itself resolves — so when a later task fills a screen in, a routing mistake is already
// ruled out. The ids are fake on purpose: the stubs do not fetch yet.
const id = '00000000-0000-0000-0000-0000000000ab'

const authenticated: [string, string][] = [
  ['/', 'dashboard'],
  [`/folders/${id}`, 'dashboard'],
  ['/trash', 'dashboard'],
  ['/approvals', 'approvals-inbox'],
  ['/settings', 'settings'],
  [`/documents/${id}`, 'history'],
  [`/documents/${id}/major-versions`, 'major-versions'],
  [`/documents/${id}/copies`, 'copies'],
  [`/documents/${id}/approvals`, 'approvals'],
  [`/documents/${id}/audit`, 'audit'],
  [`/documents/${id}/compare`, 'compare'],
  [`/versions/${id}/edit`, 'editor'],
]

for (const [path, testId] of authenticated) {
  test(`${path} renders`, async ({ signedIn }) => {
    await signedIn.goto(path)
    await expect(signedIn.getByTestId(testId)).toBeVisible()
  })
}

test('the document console keeps its chrome on every tab', async ({ signedIn }) => {
  await signedIn.goto(`/documents/${id}/copies`)
  await expect(signedIn.getByTestId('document-console')).toBeVisible()
})

test('the share landing is public: no session, no redirect', async ({ page }) => {
  // Also covers the dev proxy's content negotiation — a browser navigation to /s/{token} must get the
  // SPA shell, not the API's JSON.
  await page.goto('/s/some-token')
  await expect(page.getByTestId('share-landing')).toBeVisible()
})
