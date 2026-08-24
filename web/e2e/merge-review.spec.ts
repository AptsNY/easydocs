import type { Page } from '@playwright/test'
import { test, expect, raceConcurrentBranch } from './fixtures'

// The three-way review at /documents/:id/merge?left=&right=, opened from the console's
// "Review & merge" link (spec: 2026-08-24-three-way-merge-review-design.md). Both fixtures behind
// raceConcurrentBranch edit the same "Bravo" paragraph, so this is also where the overlap hint gets
// proven against a real response rather than only the unit test.

const concurrentGroup = (page: Page) =>
  page.locator('[data-testid="branch-group"][data-kind="Concurrent"]')
const row = (page: Page, number: string) =>
  page.locator(`[data-testid="version-row"][data-number="${number}"]`)

async function versionCount(page: Page, documentId: string): Promise<number> {
  const res = await page.request.get(`/api/v1/documents/${documentId}/versions`)
  const body = (await res.json()) as { items: unknown[] }
  return body.items.length
}

// Opening a review is just that — a look. The heart of the two-step merge is that reaching the
// screen never commits anything on its own; only the Merge button's own POST may add a version.
test('opening the review from the console commits nothing', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Look, Don’t Touch')
  await page.goto(`/documents/${documentId}`)

  await concurrentGroup(page).getByRole('link', { name: 'Review & merge' }).click()
  await expect(page.getByTestId('merge-review')).toBeVisible()

  expect(await versionCount(page, documentId)).toBe(3)
})

// The review has to say what it is reviewing: the common start both sides forked from, and which
// version is which side, or a reader has no way to tell whether the merge is the one they intended.
test('the review names the fork point and both sides', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Name Everything')
  await page.goto(`/documents/${documentId}`)
  await concurrentGroup(page).getByRole('link', { name: 'Review & merge' }).click()

  await expect(page.getByTestId('merge-base')).toContainText('0.0.1')
  await expect(page.getByTestId('merge-side-main')).toContainText('0.0.2')
  await expect(page.getByTestId('merge-side-incoming')).toContainText('0.0.3')
  await expect(page.getByTestId('merge-side-main').getByTestId('merge-side-summary')).toBeVisible()
  await expect(page.getByTestId('merge-side-incoming').getByTestId('merge-side-summary')).toBeVisible()
})

// The feature's headline behaviour: when both authors touched the same paragraph, that has to surface
// BEFORE the merge commits anything, not as a surprise inside the redline afterward. It is explicitly a
// hint rather than a conflict marker, and the screen has to say so.
test('the overlap hint names the shared paragraph both sides edited', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Shared Paragraph')
  await page.goto(`/documents/${documentId}`)
  await concurrentGroup(page).getByRole('link', { name: 'Review & merge' }).click()

  const overlaps = page.getByTestId('merge-overlaps')
  await expect(overlaps).toBeVisible()
  await expect(overlaps).toContainText('Bravo')
  await expect(overlaps).toContainText('A hint, not a guarantee')
})

// Cancel has to be a real cancel: back at the console, the branch is still unmerged and still offers
// the same review link, not a screen that quietly did something on the way out.
test('cancelling the review leaves the branch unmerged', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Change My Mind')
  await page.goto(`/documents/${documentId}`)
  await concurrentGroup(page).getByRole('link', { name: 'Review & merge' }).click()
  await expect(page.getByTestId('merge-review')).toBeVisible()

  await page.getByRole('button', { name: 'Cancel' }).click()

  await expect(page.getByTestId('history')).toBeVisible()
  await expect(concurrentGroup(page).getByRole('link', { name: 'Review & merge' })).toBeVisible()
  expect(await versionCount(page, documentId)).toBe(3)
})

// The other end of the same two-step: committing from the review screen has to actually land the
// merge, the same E4 guarantee console.spec.ts already proves, reached through the review this time.
test('merging from the review commits and returns to the console', async ({ signedIn: page }) => {
  const documentId = await raceConcurrentBranch(page, 'Commit From Review')
  await page.goto(`/documents/${documentId}`)
  await concurrentGroup(page).getByRole('link', { name: 'Review & merge' }).click()
  await expect(page.getByTestId('merge-review')).toBeVisible()

  await page.getByRole('button', { name: 'Merge' }).click()

  await expect(page.getByTestId('history')).toBeVisible()
  await expect(row(page, '0.0.4')).toBeVisible()
})
