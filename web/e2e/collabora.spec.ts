import { test, expect, createDocument, uploadVersion } from './fixtures'

// Spec §12.3's headless-browser driver for E3, finally for real.
//
// M1 substituted this: the C# suite plays Collabora's side of the WOPI conversation itself. That
// substitution hid a TOTAL failure of the flagship feature for four milestones — CheckFileInfo went out
// as camelCase (the app-wide JSON naming policy rewriting an anonymous object), Collabora answered
// "Unauthorized WOPI host", and no document could be opened in a browser. Every C# test stayed green
// because HttpClient's JSON deserialisation is case-insensitive.
//
// So this spec drives the actual product: sign in -> open the editor route -> the REAL Collabora SPA
// loads in the iframe, fetches CheckFileInfo/GetFile back off the WOPI host, renders the document ->
// type -> save -> assert easydocs recorded a new version with source EditWopi.
//
// The hostname problem, and why this spec no longer papers over it.
//
// There are two audiences for a Collabora URL. WOPISrc=http://easydocs:8080 is fetched by the coolwsd
// process INSIDE the compose network, so it must be the internal name. The editor page, by contrast, is
// loaded by a person's browser, which cannot resolve `collabora` at all.
//
// This spec used to rewrite the editor origin before navigating — and that intercept hid a total failure
// of the flagship feature for exactly the same reason the camelCase bug above survived four milestones:
// the test repaired the product's output and then asserted the repaired version worked. The API really
// was handing browsers `http://collabora:9980/...`, and the editor pane really did fail to load, while
// this test stayed green. Twice now, the substitution WAS the bug.
//
// So the rewrite is gone. The app resolves the browser-facing origin itself from COLLABORA_PUBLIC_URL,
// and the first assertion below is that it did — because a URL the browser cannot fetch is the failure.
const COLLABORA_ORIGIN = process.env.E2E_COLLABORA_URL ?? 'http://localhost:9980'

// Collabora CODE cold-starts a document process per file and streams a large SPA before it draws
// anything. Warm it is a second or two; cold on a CI runner it is not, so the budgets are generous.
const EDITOR_TIMEOUT = 120_000

test.describe('Collabora editing (spec §6, §12.3 E3)', () => {
  test.setTimeout(4 * 60_000)

  test('the real editor opens the document and a save becomes a version', async ({
    signedIn: page,
  }) => {
    const documentId = await createDocument(page, 'Collabora Round Trip')
    const versionId = await uploadVersion(page, documentId, 'base.docx')

    // The API must hand the browser an origin the browser can actually reach. Asserted before the
    // navigation so the failure names the cause — an unreachable editor host — rather than surfacing
    // two minutes later as "the canvas never appeared".
    const minted = await page.request.post(`/api/v1/versions/${versionId}/sessions`)
    expect(minted.ok(), `mint failed: ${minted.status()}`).toBeTruthy()
    const { editorUrl } = (await minted.json()) as { editorUrl: string }
    expect(new URL(editorUrl).origin, 'editorUrl must be browser-reachable, not the compose-internal host')
      .toBe(COLLABORA_ORIGIN)
    // WOPISrc stays internal: coolwsd fetches it from inside the network, so that is correct as-is.
    expect(decodeURIComponent(new URL(editorUrl).searchParams.get('WOPISrc') ?? '')).toContain('easydocs:8080')

    await page.goto(`/versions/${versionId}/edit`)
    await expect(page.getByTestId('editor-frame')).toBeVisible()

    // The document canvas only appears once coolwsd has completed CheckFileInfo + GetFile against the
    // WOPI host. A non-conforming CheckFileInfo stops the load here with "Unauthorized WOPI host", which
    // is precisely the regression this spec exists to catch.
    const editor = page.frameLocator('[data-testid="editor-frame"]')
    const canvas = editor.locator('#document-container canvas').first()
    await expect(canvas).toBeVisible({ timeout: EDITOR_TIMEOUT })

    // CODE greets a first-time browser profile with a three-slide "what's new" modal that covers the
    // document — every Playwright run is a fresh profile, so it is always there. Click through it the
    // way a person does.
    //
    // The wait is the point. CODE injects this modal a beat AFTER the canvas paints, so probing for it
    // the instant the canvas appeared raced it: the probe reported "not there", the modal arrived, and
    // it swallowed the canvas click below — a flake that fails with "<iframe title='Welcome Dialog'>
    // intercepts pointer events". Wait for it to show, dismiss it, then wait for it to go away.
    // Every wait is bounded and swallowed, so a CODE build that drops the modal is still a no-op.
    const welcomeFrame = editor.locator('iframe.iframe-welcome-modal')
    await welcomeFrame.waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {})
    if (await welcomeFrame.isVisible().catch(() => false)) {
      const welcome = editor.frameLocator('iframe.iframe-welcome-modal')
      await welcome.locator('#slide-3-indicator').click()
      await welcome.locator('#slide-3-button').click()
      await welcomeFrame.waitFor({ state: 'hidden', timeout: 15_000 }).catch(() => {})
    }

    // Type into the real editor, then press its own Save button -> WOPI PutFile against this host.
    // The button, not a keyboard shortcut: Collabora binds the platform accelerator (⌘+S where the
    // browser reports a Mac), so a hard-coded Control+S silently does nothing and the only save that
    // ever arrives is the one coolwsd flushes when the client disconnects.
    const marker = `wopi-e2e-${Date.now()}`
    await canvas.click() // centre of the page: the corners carry Collabora's floating controls
    await page.keyboard.type(marker, { delay: 20 })
    await editor.getByRole('button', { name: 'Save', exact: true }).first().click()

    // The version is the assertion: easydocs recorded the edit, attributed and numbered (spec §5). It
    // also proves the typing landed — CommitSaveAsync sha-dedupes, so an unchanged re-save produces no
    // second row at all.
    await expect
      .poll(
        async () => {
          const res = await page.request.get(`/api/v1/documents/${documentId}/versions`)
          if (!res.ok()) return []
          const { items } = (await res.json()) as { items: { source: string }[] }
          return items.map((v) => v.source)
        },
        { timeout: EDITOR_TIMEOUT, message: 'no EditWopi version was recorded by the WOPI host' },
      )
      .toContain('EditWopi')
  })
})
