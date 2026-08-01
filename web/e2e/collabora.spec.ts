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
// The hostname problem. The editorUrl the API issues is correct FOR THE DEPLOYMENT and must stay that
// way: WOPISrc=http://easydocs:8080 is fetched by the coolwsd process inside the collabora container, so
// it has to be the compose-internal name, and Collabora's own /hosting/discovery advertises itself under
// the host it was asked on. A browser running on the host cannot resolve either. Rather than weaken the
// deployment config, this spec rewrites only the ONE URL the browser itself must load — the iframe src —
// swapping the editor origin for the published localhost:9980 port. WOPISrc is left untouched, so the
// WOPI conversation this test is actually about runs exactly as it does in production.
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

    // Point the iframe at the published Collabora port; leave WOPISrc alone (see note above).
    await page.route('**/api/v1/versions/*/sessions', async (route) => {
      const response = await route.fetch()
      const body = (await response.json()) as { editorUrl: string }
      body.editorUrl = body.editorUrl.replace(/^https?:\/\/[^/]+/, COLLABORA_ORIGIN)
      await route.fulfill({ response, json: body })
    })

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
    // way a person does. (`.first()` and the visibility probe keep this a no-op if a future CODE build
    // or a configured deployment does not show it.)
    const welcome = editor.frameLocator('iframe.iframe-welcome-modal')
    const lastSlide = welcome.locator('#slide-3-indicator')
    if (await lastSlide.isVisible().catch(() => false)) {
      await lastSlide.click()
      await welcome.locator('#slide-3-button').click()
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
