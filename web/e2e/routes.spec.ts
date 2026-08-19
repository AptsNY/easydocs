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

// The masthead's "API docs" entry has to escape the SPA: /docs is the server's own Swagger UI, so the
// link must be a plain <a>. Written as a click rather than a goto because the failure mode being
// guarded is somebody "tidying" it into a react-router <Link> — which matches no route, falls through
// to index.html, and silently opens the app again instead of the docs.
test('the header links out to the app-served API docs, not through the router', async ({
  signedIn: page,
}) => {
  const link = page.getByRole('link', { name: 'API docs (opens in a new tab)' })
  await expect(link).toBeVisible()

  const docs = await new Promise<import('@playwright/test').Page>((resolve) => {
    page.context().once('page', resolve)
    void link.click()
  })
  await docs.waitForLoadState('domcontentloaded')

  // /docs 301s to /docs/index.html — assert the prefix, not the exact path, or this fails on a
  // redirect that is Swagger UI working normally.
  expect(new URL(docs.url()).pathname).toMatch(/^\/docs\b/)
  // The rendered document's own title, which proves two things at once: Swagger UI booted, and it
  // loaded THIS app's OpenAPI document rather than an empty shell. (`.swagger-ui` alone is ambiguous —
  // the page has two such elements.)
  await expect(docs.getByRole('heading', { name: /easydocs API/ })).toBeVisible()
  await expect(docs.getByTestId('dashboard')).toHaveCount(0)
})
