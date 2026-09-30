import { test, expect, createDocument, uploadVersion } from './fixtures'
import type { Page } from '@playwright/test'

// The editor page's own behaviour (spec 2026-09-29 editor layout), with Collabora replaced by a stub
// served at Collabora's REAL origin, so the page's origin check is exercised, not bypassed. The real
// editor round-trip stays in collabora.spec.ts; this file tests only what the page does with messages.
//
// Stub protocol: every message is a JSON string {MessageId, SendTime, Values}. The stub answers
// Host_PostmessageReady with Document_Loaded, and Action_Save with window.saveReply (null = never answer).
const STUB = `<!doctype html><title>stub collabora</title><script>
window.received = []
window.saveReply = { success: true }
window.send = (MessageId, Values = {}) =>
  parent.postMessage(JSON.stringify({ MessageId, SendTime: Date.now(), Values }), '*')
addEventListener('message', (e) => {
  const m = JSON.parse(e.data)
  window.received.push(m)
  if (m.MessageId === 'Host_PostmessageReady' && !window.silent) send('App_LoadingStatus', { Status: 'Document_Loaded' })
  if (m.MessageId === 'Action_Save' && window.saveReply) send('Action_Save_Resp', window.saveReply)
})
window.silent = location.hash === '#silent'
send('App_LoadingStatus', { Status: 'Frame_Ready' })
</script>`

async function openEditor(page: Page, name: string, opts: { silent?: boolean } = {}) {
  const documentId = await createDocument(page, name)
  const versionId = await uploadVersion(page, documentId, 'base.docx')
  const mint = await page.request.post(`/api/v1/versions/${versionId}/sessions`)
  const origin = new URL(((await mint.json()) as { editorUrl: string }).editorUrl).origin
  await page.route(`${origin}/**`, (r) =>
    r.fulfill({ contentType: 'text/html', body: opts.silent ? STUB.replace("location.hash === '#silent'", 'true') : STUB }),
  )
  const deletes: string[] = []
  page.on('request', (r) => {
    if (r.method() === 'DELETE' && r.url().includes('/api/v1/sessions/')) deletes.push(r.url())
  })
  await page.goto(`/versions/${versionId}/edit`)
  await expect(page.getByTestId('editor-frame')).toBeVisible()
  const stub = async () => {
    await expect.poll(() => page.frames().some((f) => f.url().startsWith(origin))).toBe(true)
    return page.frames().find((f) => f.url().startsWith(origin))!
  }
  return { documentId, versionId, origin, stub, deletes }
}

const status = (page: Page) => page.getByTestId('editor-status')

test('bar and rail render on a direct load, and the handshake completes', async ({ signedIn: page }) => {
  const { stub } = await openEditor(page, 'Bar and Rail')

  const bar = page.getByTestId('editor-bar')
  await expect(bar).toContainText('Bar and Rail')
  await expect(bar).toContainText('editing from 0.0.1')
  await expect(status(page)).toHaveText('Saved')

  const rail = page.getByTestId('editor-rail')
  await expect(rail.getByRole('heading', { name: 'History' })).toBeVisible()
  await expect(rail).toContainText('0.0.1')
  await expect(rail.getByRole('heading', { name: 'Members' })).toBeVisible()
  await expect(rail).toContainText('E2E User')
  await expect(rail.getByRole('heading', { name: 'Approvals' })).toBeVisible()

  const f = await stub()
  await expect.poll(() => f.evaluate(() => (window as any).received.map((m: any) => m.MessageId))).toContain(
    'Host_PostmessageReady',
  )
})

test('status follows Doc_ModifiedStatus; messages from another origin are ignored', async ({ signedIn: page }) => {
  const { stub } = await openEditor(page, 'Modified')
  await expect(status(page)).toHaveText('Saved')

  // Same shape, wrong sender: the page itself.
  await page.evaluate(() =>
    window.postMessage(JSON.stringify({ MessageId: 'Doc_ModifiedStatus', SendTime: 0, Values: { Modified: true } }), '*'),
  )
  await expect(status(page)).toHaveText('Saved')

  const f = await stub()
  await f.evaluate(() => (window as any).send('Doc_ModifiedStatus', { Modified: true }))
  await expect(status(page)).toHaveText('Unsaved changes')
  await f.evaluate(() => (window as any).send('Doc_ModifiedStatus', { Modified: false }))
  await expect(status(page)).toHaveText('Saved')
})

test('Done saves and lands on the document; no session is closed', async ({
  signedIn: page,
}) => {
  const { documentId, stub, deletes } = await openEditor(page, 'Done Saves')
  await expect(status(page)).toHaveText('Saved')
  const f = await stub()
  await f.evaluate(() => (window as any).send('Doc_ModifiedStatus', { Modified: true }))

  await page.getByRole('button', { name: 'Done', exact: true }).click()

  await expect(page).toHaveURL(new RegExp(`/documents/${documentId}$`))
  expect(deletes).toEqual([])
})

test('Done with nothing to save ("unmodified") still leaves', async ({ signedIn: page }) => {
  const { documentId, stub } = await openEditor(page, 'Unmodified')
  await expect(status(page)).toHaveText('Saved')
  await (await stub()).evaluate(() => ((window as any).saveReply = { success: false, result: 'unmodified' }))

  await page.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/documents/${documentId}$`))
})

test('a failed save keeps the user on the page and says so', async ({ signedIn: page }) => {
  const { stub } = await openEditor(page, 'Save Fails')
  await expect(status(page)).toHaveText('Saved')
  const f = await stub()
  await f.evaluate(() => ((window as any).saveReply = { success: false, result: 'error' }))

  await page.getByRole('button', { name: 'Done', exact: true }).click()

  await expect(status(page)).toContainText("Couldn't save")
  await expect(page).toHaveURL(/\/edit$/)
  // The next modified-status message clears the failure.
  await f.evaluate(() => (window as any).send('Doc_ModifiedStatus', { Modified: true }))
  await expect(status(page)).toHaveText('Unsaved changes')
})

test('no reply to Action_Save within 15s: Done leaves anyway', async ({ signedIn: page }) => {
  test.setTimeout(60_000)
  const { documentId, stub } = await openEditor(page, 'No Reply')
  await expect(status(page)).toHaveText('Saved')
  const f = await stub()
  await f.evaluate(() => ((window as any).saveReply = null))

  await page.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(status(page)).toHaveText('Saving…')
  // The frame is still up while Done waits, so the request itself can be checked. DontSaveIfUnmodified
  // matters: without it Collabora re-saves an unchanged document and every Done makes a new version.
  // Polled: postMessage delivery is async, so the stub may not have it the instant "Saving…" renders.
  await expect
    .poll(() => f.evaluate(() => (window as any).received.find((m: any) => m.MessageId === 'Action_Save')?.Values))
    .toEqual({ Notify: true, DontSaveIfUnmodified: true })
  await expect(page).toHaveURL(new RegExp(`/documents/${documentId}$`), { timeout: 25_000 })
})

test('Done before the document has loaded leaves at once', async ({ signedIn: page }) => {
  const { documentId } = await openEditor(page, 'Not Loaded', { silent: true })
  await expect(status(page)).toHaveText('Opening…')

  await page.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/documents/${documentId}$`), { timeout: 3_000 })
})

test('the rail remembers being collapsed, and starts collapsed on a narrow screen', async ({ signedIn: page }) => {
  await openEditor(page, 'Rail Memory')
  const toggle = page.getByRole('button', { name: /details/i })
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await page.reload()
  await expect(page.getByRole('button', { name: /details/i })).toHaveAttribute('aria-expanded', 'false')

  await page.evaluate(() => localStorage.clear())
  await page.setViewportSize({ width: 1000, height: 720 })
  await page.reload()
  await expect(page.getByRole('button', { name: /details/i })).toHaveAttribute('aria-expanded', 'false')
})

test('leaving during a save: the pending save never navigates from the next page', async ({ signedIn: page }) => {
  test.setTimeout(60_000)
  const { stub } = await openEditor(page, 'Leave Mid Save')
  await expect(status(page)).toHaveText('Saved')
  await (await stub()).evaluate(() => ((window as any).saveReply = null))

  await page.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(status(page)).toHaveText('Saving…')
  await page.getByRole('link', { name: 'Settings' }).click()
  await expect(page).toHaveURL(/\/settings$/)
  // Past the 15s save timeout: a timer left armed would now push /documents/… on top of Settings.
  await page.waitForTimeout(17_000)
  await expect(page).toHaveURL(/\/settings$/)
})
