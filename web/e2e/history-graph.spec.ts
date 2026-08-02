import { test, expect, createDocument, uploadVersion } from './fixtures'

// Issue #13: the history tab's Graph toggle renders the revision DAG — one dot per version, the
// list stays the default (the conformance suite asserts the indented list, and merging lives there).
test('the graph toggle renders one dot per version and the list stays default', async ({
  signedIn: page,
}) => {
  const docId = await createDocument(page, 'Graph doc')
  await uploadVersion(page, docId, 'base.docx')
  await uploadVersion(page, docId, 'edited.docx')

  await page.goto(`/documents/${docId}`)

  // Default is the list the rest of the suite depends on.
  await expect(page.getByTestId('branch-spine')).toBeVisible()
  await expect(page.getByTestId('history-graph')).toHaveCount(0)

  await page.getByTestId('graph-toggle').click()
  await expect(page.getByTestId('history-graph')).toBeVisible()
  await expect(page.getByTestId('graph-dot')).toHaveCount(2)
  await expect(page.getByTestId('branch-spine')).toHaveCount(0)

  // And back.
  await page.getByRole('button', { name: 'List' }).click()
  await expect(page.getByTestId('branch-spine')).toBeVisible()
})
