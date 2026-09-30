# Collabora Editor Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/versions/{vid}/edit` a full-height editing screen: a slim document bar, Collabora's tabbed ribbon without its sidebar, a collapsible easydocs rail (History, Members, Approvals), save status from Collabora's PostMessage API, and no "What's new" popup.

**Architecture:** Two small server changes (`ui_defaults` on the minted editor URL, `PostMessageOrigin` in CheckFileInfo) and one compose flag. On the web side, `Editor.tsx` composes a new `useCollabora` hook (PostMessage in/out) and a new `EditorRail` component, reading only existing endpoints and refreshing on the existing `useSse` stream. The page never closes the edit session (spec: "Sessions are not closed from the page").

**Tech Stack:** ASP.NET Core minimal APIs + xUnit/Testcontainers (`tests/EasyDocs.Api.Tests`), React 19 + react-router 8, Playwright (`web/e2e`).

**Spec:** `docs/superpowers/specs/2026-09-29-collabora-editor-layout-design.md`

**Deviations from the spec (deliberate):**
- The spec's "Vitest" tests become Playwright tests. The web project has no unit-test runner and adding one is a new dependency for one hook. Playwright drives the hook through a **stub Collabora page** served at the real Collabora origin with `page.route`, so origin checks run for real.
- "Saved as" filters out versions that already existed when the page loaded (a set of ids), instead of comparing `createdAt` with the client's clock. Same intent, and no clock skew between browser and server.

**Repo rules for every commit:** `git commit -s` (DCO), and end the message with:
```
Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Eiu4iAF5Lz2Wkk3qwyupn1
```
Work on branch `feat/editor-layout` (already exists, holds the spec).

**How to run things:**
- Server tests: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~<Name>"` (needs Docker for Testcontainers).
- Web type-check and build: `cd web && npm run build`. Lint: `cd web && npm run lint`.
- E2E locally: start the API on :8080 (`docker compose -f deploy/compose/docker-compose.yml up -d` or your usual dev stack), then `cd web && npx playwright test e2e/editor-layout.spec.ts`. Playwright starts Vite on :5173 itself.

---

## File map

| File | Change | Responsibility |
|---|---|---|
| `src/EasyDocs.Api/Editing/EditingEndpoints.cs` | modify | append `ui_defaults` to `editorUrl` |
| `src/EasyDocs.Api/Editing/WopiEndpoints.cs` | modify | `PostMessageOrigin` in CheckFileInfo |
| `tests/EasyDocs.Api.Tests/WopiHostTests.cs` | modify | tests for both server changes |
| `deploy/compose/docker-compose.yml` | modify | `--o:welcome.enable=false` |
| `web/src/api.ts` | modify | `VersionDetail` type |
| `web/src/useCollabora.ts` | create | PostMessage handshake, status, `save()`, `beforeunload` |
| `web/src/components/EditorRail.tsx` | create | `useRead` helper + the collapsible rail |
| `web/src/routes/Editor.tsx` | rewrite | bar + frame + rail; Done; no session close |
| `web/src/index.css` | modify | full-height editor layout, rail, bar |
| `web/e2e/editor-layout.spec.ts` | create | stub-Collabora tests of bar, rail, Done |
| `web/e2e/actions.spec.ts` | modify | comment only (the editor no longer closes sessions) |

---

### Task 1: Tabbed ribbon via `ui_defaults`

**Files:**
- Modify: `src/EasyDocs.Api/Editing/EditingEndpoints.cs:57-60`
- Test: `tests/EasyDocs.Api.Tests/WopiHostTests.cs` (next to `Editor_url_carries_the_tokens_real_expiry_as_access_token_ttl`, ~line 286)

- [ ] **Step 1: Write the failing test**

Add below `Editor_url_carries_the_tokens_real_expiry_as_access_token_ttl`:

```csharp
    // Collabora's own defaults open every document with the formatting sidebar and ruler taking a third
    // of the width. The easydocs rail replaces the sidebar, so the editor URL asks for the tabbed ribbon
    // with both off. ui_defaults only sets defaults: a user's own toggles in Collabora still win.
    [Fact]
    public async Task Editor_url_asks_collabora_for_the_tabbed_ribbon_without_sidebar()
    {
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Ui" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var part = new ByteArrayContent(BaseBytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions",
            new MultipartFormDataContent { { part, "file", "l.docx" } });
        var vid = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var mint = (await (await c.PostAsync($"/api/v1/versions/{vid}/sessions", null))
            .Content.ReadFromJsonAsync<EditorMintDto>())!;

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(mint.EditorUrl).Query);
        Assert.Equal("UIMode=tabbed;TextSidebar=false;TextRuler=false", query["ui_defaults"]);
    }
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~Editor_url_asks_collabora_for_the_tabbed_ribbon"`
Expected: FAIL, `Assert.Equal() Failure` with actual `null`.

- [ ] **Step 3: Implement**

In `EditingEndpoints.cs`, replace the `editorUrl` assignment:

```csharp
        // ui_defaults: the tabbed ribbon, with Collabora's formatting sidebar and ruler off. The page's
        // own side rail takes that space. Defaults only: a user's own toggles in Collabora still win.
        var editorUrl = $"{actionUrl}WOPISrc={wopiSrc}&access_token={token}" +
                        $"&access_token_ttl={expiresAt.ToUnixTimeMilliseconds()}" +
                        $"&ui_defaults={Uri.EscapeDataString("UIMode=tabbed;TextSidebar=false;TextRuler=false")}";
```

- [ ] **Step 4: Run the WOPI and edit-session tests**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~WopiHostTests|FullyQualifiedName~EditSessionTests"`
Expected: all PASS. `Editor_mints_session_with_editor_url_and_token` asserts a prefix of the URL, and appending at the end keeps that prefix; if it asserts the whole URL, extend its expected string with the same suffix.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Editing/EditingEndpoints.cs tests/EasyDocs.Api.Tests/WopiHostTests.cs
git commit -s -m "feat(editing): open Collabora with the tabbed ribbon, sidebar and ruler off"
```

---

### Task 2: `PostMessageOrigin` in CheckFileInfo

**Files:**
- Modify: `src/EasyDocs.Api/Editing/WopiEndpoints.cs` (`CheckFileInfo` ~line 29, `CheckFileInfoResponse` ~line 84)
- Test: `tests/EasyDocs.Api.Tests/WopiHostTests.cs`

- [ ] **Step 1: Write the failing tests**

(a) Add `"PostMessageOrigin"` to the `wopiNames` array in `CheckFileInfo_wire_json_is_WOPI_PascalCase_not_the_app_naming_policy`:

```csharp
        string[] wopiNames =
        [
            "BaseFileName", "Size", "OwnerId", "UserId", "UserFriendlyName", "UserCanWrite",
            "Version", "SupportsLocks", "SupportsUpdate", "SupportsGetLock", "PostMessageOrigin",
        ];
```

(b) Add two tests after that one:

```csharp
    // PostMessageOrigin is what lets Collabora talk to the editor page (save status, save requests). It
    // must be an ORIGIN, not PUBLIC_BASE_URL verbatim: Collabora compares it with the page's origin.
    [Fact]
    public async Task CheckFileInfo_names_the_app_origin_for_postmessage()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);

        var raw = await _f.CreateClient().GetStringAsync($"/wopi/files/{sid}?access_token={token}");

        Assert.Contains("\"PostMessageOrigin\":\"http://localhost\"", raw); // ApiFactory: PUBLIC_BASE_URL=http://localhost
    }

    // Unset PUBLIC_BASE_URL must never stop documents opening: the field is simply left out, and the
    // editor page then gets no status messages.
    [Fact]
    public async Task CheckFileInfo_omits_PostMessageOrigin_when_PUBLIC_BASE_URL_is_unset()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);
        using var host = _f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["PUBLIC_BASE_URL"] = "" })));

        var res = await host.CreateClient().GetAsync($"/wopi/files/{sid}?access_token={token}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("PostMessageOrigin", await res.Content.ReadAsStringAsync());
    }
```

Add the usings this needs at the top of the file if missing: `using System.Net;`, `using Microsoft.AspNetCore.Hosting;`, `using Microsoft.Extensions.Configuration;` (the same ones `ForwardedHeadersTests.cs` uses for `WithWebHostBuilder`).

- [ ] **Step 2: Run them and check they fail**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~CheckFileInfo"`
Expected: the PascalCase test and `CheckFileInfo_names_the_app_origin_for_postmessage` FAIL (no such key); the "omits" test passes already.

- [ ] **Step 3: Implement**

In `WopiEndpoints.cs`, add `IConfiguration cfg` as the last parameter of `CheckFileInfo`:

```csharp
    private static async Task<IResult> CheckFileInfo(Guid fileId, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken tokens, IBlobStore blobs, ILoggerFactory logs, IConfiguration cfg)
```

Pass the origin as the new last constructor argument:

```csharp
            auth.Perms == "w",
            baseVersion.Id.ToString(),
            // The origin serving the SPA; Collabora only posts status messages to a page on it. Unset
            // PUBLIC_BASE_URL leaves the field out rather than failing: documents still open, just
            // without save status on the page.
            Uri.TryCreate(cfg["PUBLIC_BASE_URL"], UriKind.Absolute, out var app)
                ? app.GetLeftPart(UriPartial.Authority)
                : null));
```

Add the record parameter after `Version`:

```csharp
        [property: JsonPropertyName("Version")] string Version,
        [property: JsonPropertyName("PostMessageOrigin"),
                   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PostMessageOrigin)
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~WopiHostTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Editing/WopiEndpoints.cs tests/EasyDocs.Api.Tests/WopiHostTests.cs
git commit -s -m "feat(wopi): send PostMessageOrigin so the editor page gets save status"
```

---

### Task 3: No "What's new" popup (DROPPED during execution)

> Dropped: CODE ignores `welcome.enable` unless `home_mode.enable=true` (20 connections / 10 documents cap). The real-Collabora e2e test caught it. See the spec's "Collabora configuration".

**Files:**
- Modify: `deploy/compose/docker-compose.yml:64`

- [ ] **Step 1: Add the flag**

```yaml
      # welcome.enable=false: CODE otherwise opens a "What's new" slideshow over the document on every
      # first visit per browser profile, which for a contract editor is every visit that matters.
      extra_params: "--o:ssl.enable=false --o:ssl.termination=${COLLABORA_SSL_TERMINATION:-false} --o:welcome.enable=false"
```

(Put the new comment line directly above `extra_params`, under the existing ssl comment.)

- [ ] **Step 2: Verify on the local stack**

Run: `docker compose -f deploy/compose/docker-compose.yml up -d collabora && sleep 20 && docker compose -f deploy/compose/docker-compose.yml logs collabora 2>&1 | grep -i "welcome" | head`
Expected: no error about an unknown option. Open any document in the editor in a fresh private window: no welcome slideshow.

- [ ] **Step 3: Commit**

```bash
git add deploy/compose/docker-compose.yml
git commit -s -m "chore(compose): turn off Collabora's welcome slideshow"
```

Production is not covered by this repo: Collabora on Cloud Run is owned by AptsNY/infra-platform (`gcp/easydocs/`). Record in the PR body that the same `--o:welcome.enable=false` must be added there. Do not open that PR without asking the user.

---

### Task 4: E2E harness and failing tests (stub Collabora)

**Files:**
- Create: `web/e2e/editor-layout.spec.ts`

The stub is a tiny HTML page served at the real Collabora origin (taken from the minted `editorUrl`). It speaks the PostMessage protocol and lets each test choose Collabora's answer to `Action_Save`.

- [ ] **Step 1: Write the spec**

```ts
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

test('Done saves (DontSaveIfUnmodified) and lands on the document; no session is closed', async ({
  signedIn: page,
}) => {
  const { documentId, stub, deletes } = await openEditor(page, 'Done Saves')
  await expect(status(page)).toHaveText('Saved')
  const f = await stub()
  await f.evaluate(() => (window as any).send('Doc_ModifiedStatus', { Modified: true }))

  await page.getByRole('button', { name: 'Done', exact: true }).click()

  await expect(page).toHaveURL(new RegExp(`/documents/${documentId}$`))
  const save = await f
    .evaluate(() => (window as any).received.find((m: any) => m.MessageId === 'Action_Save'))
    .catch(() => null)
  // The frame is gone after navigation; if the evaluate raced it, the URL assertion above still proves
  // Done completed. When it did capture the message, check its flags.
  if (save) expect(save.Values).toEqual({ Notify: true, DontSaveIfUnmodified: true })
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
  await (await stub()).evaluate(() => ((window as any).saveReply = null))

  await page.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(status(page)).toHaveText('Saving…')
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
```

- [ ] **Step 2: Run it and check it fails**

Run: `cd web && npx playwright test e2e/editor-layout.spec.ts`
Expected: every test FAILS (no `editor-bar` / `editor-status` / `editor-rail` test ids yet).

- [ ] **Step 3: Commit the failing spec**

```bash
git add web/e2e/editor-layout.spec.ts
git commit -s -m "test(e2e): editor bar, rail and save flow against a stub Collabora"
```

---

### Task 5: `useCollabora` hook

**Files:**
- Create: `web/src/useCollabora.ts`

- [ ] **Step 1: Write the hook**

```ts
import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'

// Collabora's PostMessage API (https://sdk.collaboraonline.com/docs/postmessage_api.html), host side.
// Every message both ways is a JSON string {MessageId, SendTime, Values}. Collabora only talks to a page
// whose origin matches the PostMessageOrigin in CheckFileInfo, and this page only listens to the frame's
// own window at Collabora's origin.
//
// No "upload finished" signal exists: Action_Save_Resp arrives when the core has saved its local copy,
// before coolwsd uploads it to the WOPI host. So nothing here ever closes the edit session; see the
// spec's "Sessions are not closed from the page".
export type EditorStatus = 'loading' | 'saved' | 'unsaved' | 'saving' | 'failed'
export type SaveResult = 'done' | 'failed' | 'unknown'

type Message = { MessageId?: string; Values?: Record<string, unknown> }

const SAVE_TIMEOUT_MS = 15_000
// With PostMessageOrigin unset (or under the Vite dev server, a different origin) no message ever
// arrives while the editor works fine, so silence is not an error: the status is just hidden.
const QUIET_MS = 30_000

export function useCollabora(frame: RefObject<HTMLIFrameElement | null>, origin: string | null) {
  const [status, setStatus] = useState<EditorStatus>('loading')
  const [heard, setHeard] = useState(false)
  const [quiet, setQuiet] = useState(false)
  const loaded = useRef(false)
  const pendingSave = useRef<((ok: boolean) => void) | null>(null)

  const post = useCallback(
    (MessageId: string, Values: Record<string, unknown> = {}) => {
      if (origin) frame.current?.contentWindow?.postMessage(JSON.stringify({ MessageId, SendTime: Date.now(), Values }), origin)
    },
    [frame, origin],
  )

  useEffect(() => {
    if (!origin) return
    const onMessage = (e: MessageEvent) => {
      if (e.origin !== origin || e.source !== frame.current?.contentWindow) return
      let msg: Message
      try {
        msg = (typeof e.data === 'string' ? JSON.parse(e.data) : e.data) as Message
      } catch {
        return
      }
      setHeard(true)
      const v = msg.Values ?? {}
      switch (msg.MessageId) {
        case 'App_LoadingStatus':
          // Frame_Ready is Collabora's "I am listening"; the iframe load event can fire before that.
          if (v.Status === 'Frame_Ready') post('Host_PostmessageReady')
          if (v.Status === 'Document_Loaded') {
            loaded.current = true
            setStatus('saved')
          }
          break
        case 'Doc_ModifiedStatus':
          setStatus(v.Modified ? 'unsaved' : 'saved')
          break
        case 'Action_Save_Resp':
          // "unmodified" is Collabora declining a save there was no need for (DontSaveIfUnmodified).
          pendingSave.current?.(v.success === true || v.result === 'unmodified')
          pendingSave.current = null
          break
      }
    }
    window.addEventListener('message', onMessage)
    const t = window.setTimeout(() => setQuiet(true), QUIET_MS)
    return () => {
      window.removeEventListener('message', onMessage)
      window.clearTimeout(t)
    }
  }, [frame, origin, post])

  // Before Document_Loaded Collabora ignores Action_Save, and without PostMessageOrigin it never answers,
  // so waiting would only stall Done. 'unknown' means "leave anyway": the session stays open, so
  // Collabora's own save on disconnect still reaches the WOPI host.
  const save = useCallback((): Promise<SaveResult> => {
    if (!loaded.current) return Promise.resolve('unknown')
    setStatus('saving')
    return new Promise((resolve) => {
      const t = window.setTimeout(() => {
        pendingSave.current = null
        resolve('unknown')
      }, SAVE_TIMEOUT_MS)
      pendingSave.current = (ok) => {
        window.clearTimeout(t)
        if (!ok) setStatus('failed')
        resolve(ok ? 'done' : 'failed')
      }
      // DontSaveIfUnmodified: an unchanged document re-saved would become a new version on every Done.
      post('Action_Save', { Notify: true, DontSaveIfUnmodified: true })
    })
  }, [post])

  // The browser's own "Leave site?" prompt. Leaving anyway is safe: see save() above.
  useEffect(() => {
    if (status !== 'unsaved' && status !== 'saving') return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [status])

  return { status, hidden: quiet && !heard, save }
}
```

- [ ] **Step 2: Type-check**

Run: `cd web && npx tsc -b`
Expected: no errors (the hook is not used yet; unused exports are fine).

- [ ] **Step 3: Commit**

```bash
git add web/src/useCollabora.ts
git commit -s -m "feat(web): useCollabora, the PostMessage side of the editor page"
```

---

### Task 6: `EditorRail`

**Files:**
- Modify: `web/src/api.ts` (add `VersionDetail` next to `VersionRow`)
- Create: `web/src/components/EditorRail.tsx`

- [ ] **Step 1: Add the version-detail type**

In `web/src/api.ts`, after the `VersionRow` type:

```ts
// GET /api/v1/versions/{vid}: the fields the editor page reads. `name` is the VERSION's optional name,
// not the document's; the document name comes from GET /api/v1/documents/{id}.
export type VersionDetail = { id: string; documentId: string; major: number; minor: number; revision: number }
```

- [ ] **Step 2: Write the component**

```tsx
import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { api, type Approval, type Member, type VersionRow } from '../api'

// One GET, refetched when `tick` changes; a null path waits. Failure is local to the caller: each rail
// section fails on its own and editing is never blocked.
export function useRead<T>(path: string | null, tick = 0) {
  const [state, setState] = useState<{ data?: T; failed?: boolean }>({})
  useEffect(() => {
    if (!path) return
    let live = true
    api.get<T>(path).then(
      (data) => live && setState({ data }),
      () => live && setState({ failed: true }),
    )
    return () => {
      live = false
    }
  }, [path, tick])
  return state
}

const KEY = 'easydocs.editorRail.open'

// Remembered per browser; the first visit starts open unless the window is narrow.
function initialOpen() {
  try {
    const saved = localStorage.getItem(KEY)
    if (saved !== null) return saved === '1'
  } catch {
    // Storage blocked (private mode, policy): fall through to the width default.
  }
  return !window.matchMedia('(max-width: 1099px)').matches
}

type Props = {
  documentId: string
  versionId: string
  versions: { data?: VersionRow[]; failed?: boolean }
  tick: number
}

// Read-only context while editing. Everything that changes something lives on the full pages it links to.
export default function EditorRail({ documentId, versionId, versions, tick }: Props) {
  const [open, setOpen] = useState(initialOpen)
  const members = useRead<Member[]>(`/api/v1/documents/${documentId}/members`, tick)
  const approvals = useRead<Approval[]>(`/api/v1/versions/${versionId}/approvals`, tick)

  const toggle = () => {
    setOpen(!open)
    try {
      localStorage.setItem(KEY, open ? '0' : '1')
    } catch {
      // Not remembered this time; the rail still toggles.
    }
  }

  return (
    <aside className={open ? 'editor-rail' : 'editor-rail editor-rail--closed'} data-testid="editor-rail">
      <button type="button" className="link editor-rail-toggle" aria-expanded={open} aria-controls="editor-rail-body" onClick={toggle}>
        {open ? 'Hide details »' : '« Details'}
      </button>
      <div id="editor-rail-body" hidden={!open}>
        <section>
          <h3>History</h3>
          {versions.failed ? (
            <p className="error">Couldn't load.</p>
          ) : (
            <ul>
              {versions.data?.slice(0, 8).map((v) => (
                <li key={v.id}>
                  <span className="version-number">{v.number}</span> {v.createdByName} ·{' '}
                  {new Date(v.createdAt).toLocaleString()}
                </li>
              ))}
            </ul>
          )}
          <Link to={`/documents/${documentId}`}>Full history</Link>
        </section>

        <section>
          <h3>Members</h3>
          {members.failed ? (
            <p className="error">Couldn't load.</p>
          ) : (
            <ul>
              {members.data?.map((m) => (
                <li key={m.userId}>
                  {m.displayName} ({m.role})
                </li>
              ))}
            </ul>
          )}
        </section>

        <section>
          <h3>Approvals</h3>
          {approvals.failed ? (
            <p className="error">Couldn't load.</p>
          ) : approvals.data?.length ? (
            <ul>
              {approvals.data.map((a) => (
                <li key={a.id}>
                  {a.approverName}: {a.status}
                </li>
              ))}
            </ul>
          ) : (
            <p>None for this version.</p>
          )}
          <Link to={`/documents/${documentId}/approvals`}>All approvals</Link>
        </section>
      </div>
    </aside>
  )
}
```

- [ ] **Step 3: Type-check**

Run: `cd web && npx tsc -b`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add web/src/api.ts web/src/components/EditorRail.tsx
git commit -s -m "feat(web): EditorRail, history, members and approvals beside the editor"
```

---

### Task 7: The editor page

**Files:**
- Rewrite: `web/src/routes/Editor.tsx`

- [ ] **Step 1: Replace the file**

```tsx
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { api, problemText, type DocumentDetail, type Paged, type VersionDetail, type VersionRow } from '../api'
import { useSession } from '../auth'
import EditorRail, { useRead } from '../components/EditorRail'
import { useCollabora } from '../useCollabora'
import { useSse } from '../useSse'

type Session = { sessionId: string; editorUrl: string; accessToken: string; accessTokenTtlSeconds: number }

// The Collabora host page (spec §6; layout spec 2026-09-29): a slim document bar, the editor filling the
// rest of the window, and a collapsible easydocs rail.
//
// Minting happens here rather than in the Actions menu because every arrival at this URL needs a session:
// a bookmark and a reload as much as a menu click.
//
// The page never closes the session. Closing revokes the WOPI token at once, and Collabora uploads its
// last save AFTER telling the page it saved, so any close from here can race that upload and lose the
// edit. An open session costs a row and a token that expires on its own (WopiAccessToken.TtlSeconds).
export default function Editor() {
  const { vid } = useParams()
  const navigate = useNavigate()
  const { me } = useSession()
  const [session, setSession] = useState<Session | null>(null)
  const [error, setError] = useState('')
  const [tick, setTick] = useState(0)
  const frame = useRef<HTMLIFrameElement>(null)

  useEffect(() => {
    if (!vid) return
    let live = true
    api.post<Session>(`/api/v1/versions/${vid}/sessions`).then(
      (s) => live && setSession(s),
      // A Viewer who follows this URL directly gets 403 "Editor role required." Say so; a blank frame
      // would look like Collabora is broken.
      (e: unknown) => live && setError(problemText(e, 'Could not open the editor.')),
    )
    return () => {
      live = false
    }
  }, [vid])

  const version = useRead<VersionDetail>(vid ? `/api/v1/versions/${vid}` : null)
  const documentId = version.data?.documentId
  const doc = useRead<DocumentDetail>(documentId ? `/api/v1/documents/${documentId}` : null)
  const bump = useCallback(() => setTick((t) => t + 1), [])
  useSse(documentId, bump)
  const page = useRead<Paged<VersionRow>>(documentId ? `/api/v1/documents/${documentId}/versions?order=desc` : null, tick)
  const versions = useMemo(() => ({ data: page.data?.items, failed: page.failed }), [page])

  // "Saved as" names only a version this page's editing produced: an editor save by me that did not
  // exist when the page first loaded the list. Not a co-editor's, not one from another tab earlier.
  const before = useRef<Set<string> | null>(null)
  if (versions.data && !before.current) before.current = new Set(versions.data.map((v) => v.id))
  const mine = versions.data?.find(
    (v) => v.source === 'EditWopi' && v.createdBy === me?.id && !before.current?.has(v.id),
  )

  const origin = useMemo(() => (session ? new URL(session.editorUrl).origin : null), [session])
  const { status, hidden, save } = useCollabora(frame, origin)

  const done = async () => {
    if ((await save()) === 'failed') return
    void navigate(documentId ? `/documents/${documentId}` : '/')
  }

  if (error)
    return (
      <section data-testid="editor" className="editor">
        <p role="alert" className="error">
          {error}
        </p>
      </section>
    )

  const label = version.data ? `${version.data.major}.${version.data.minor}.${version.data.revision}` : ''
  const statusText = {
    loading: 'Opening…',
    saved: mine ? `Saved as ${mine.number}` : 'Saved',
    unsaved: 'Unsaved changes',
    saving: 'Saving…',
    failed: "Couldn't save. Your changes are still in the editor.",
  }[status]

  return (
    <section data-testid="editor" className="editor editor-page">
      <div className="editor-bar" data-testid="editor-bar">
        {/* Same path as Done: save first, then leave. */}
        <button type="button" className="link" onClick={() => void done()}>
          ← {doc.data?.name ?? 'Document'}
        </button>
        {label && <span className="editor-bar-from">editing from {label}</span>}
        {!hidden && (
          <span role="status" data-testid="editor-status" className={`editor-status editor-status--${status}`}>
            {statusText}
          </span>
        )}
        <button type="button" onClick={() => void done()} disabled={status === 'saving'}>
          Done
        </button>
      </div>

      {session ? (
        // The src attribute is the whole contract on this side. Collabora is a separate product and may be
        // absent in a dev or CI environment, in which case this frame simply fails to load.
        <iframe
          ref={frame}
          data-testid="editor-frame"
          className="editor-frame editor-frame--fill"
          title="Document editor"
          src={session.editorUrl}
          allow="fullscreen"
        />
      ) : (
        <p className="editor-opening">Opening the editor…</p>
      )}

      {documentId && vid && <EditorRail documentId={documentId} versionId={vid} versions={versions} tick={tick} />}
    </section>
  )
}
```

Note the "Saved as" status: the test in Task 4 expects plain `Saved` because the stub never creates a version. Real saves are covered by the manual check in Task 9.

- [ ] **Step 2: Type-check and lint**

Run: `cd web && npm run build && npm run lint`
Expected: build succeeds, lint clean.

- [ ] **Step 3: Commit**

```bash
git add web/src/routes/Editor.tsx
git commit -s -m "feat(web): editor page with document bar, rail and save-then-leave Done"
```

---

### Task 8: Layout CSS

**Files:**
- Modify: `web/src/index.css` (after the `.editor-frame` block, ~line 1522)

- [ ] **Step 1: Add the rules**

After the existing `.editor-frame { … }` block (which stays unchanged: `MergeReview.tsx` and `Compare.tsx` use it):

```css
/* The editing screen fills the window under the masthead: no page scroll, no dead space. The shell is
   pinned to the viewport only while it holds this page, so every other screen scrolls as before. */
.shell:has(.editor-page) {
  flex: none;
  height: 100svh;
}

.shell > main:has(> .editor-page) {
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: 0;
}

.editor-page {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  grid-template-rows: auto minmax(0, 1fr);
}

.editor-bar {
  grid-column: 1 / -1;
  display: flex;
  align-items: center;
  gap: var(--s4);
  padding: var(--s2) var(--s4);
  border-block-end: 1px solid var(--rule-strong);
  background: var(--paper-raised);
}

.editor-bar > button:last-child {
  margin-inline-start: auto;
}

.editor-bar-from,
.editor-status {
  font-size: 13px;
  color: var(--text);
}

.editor-status--unsaved,
.editor-status--failed {
  color: var(--ink);
  font-weight: 600;
}

.editor-frame--fill {
  height: 100%;
  margin: 0;
  border: 0;
  border-radius: 0;
}

.editor-opening {
  padding: var(--s5);
}

.editor-rail {
  width: 280px;
  overflow-y: auto;
  padding: var(--s3) var(--s4);
  border-inline-start: 1px solid var(--rule-strong);
  background: var(--paper-raised);
  font-size: 14px;
}

.editor-rail--closed {
  width: auto;
  padding-inline: var(--s2);
}

.editor-rail h3 {
  margin-block: var(--s4) var(--s2);
  font-size: 13px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.editor-rail ul {
  margin: 0 0 var(--s2);
  padding: 0;
  list-style: none;
  line-height: 1.7;
}

@media (max-width: 700px) {
  .editor-bar-from {
    display: none;
  }
}
```

- [ ] **Step 2: Run the new e2e spec**

Run: `cd web && npx playwright test e2e/editor-layout.spec.ts`
Expected: all 8 tests PASS. If a test fails, fix the page, not the test, unless the test contradicts the spec.

- [ ] **Step 3: Run the whole e2e suite**

Run: `cd web && npx playwright test`
Expected: all PASS. `actions.spec.ts` test 1 asserts the frame `src` with `/^http.+WOPISrc=.+&access_token=.+$/`, which still matches with `&ui_defaults=…` at the end. `routes.spec.ts` still finds `data-testid="editor"`. `a11y.spec.ts` must stay clean; if it flags the rail, fix the markup.

- [ ] **Step 4: Commit**

```bash
git add web/src/index.css
git commit -s -m "feat(web): full-height editor layout with a collapsible side rail"
```

---

### Task 9: Tidy, real-Collabora check, PR

**Files:**
- Modify: `web/e2e/actions.spec.ts:64-67` (comment only)

- [ ] **Step 1: Fix the stale comment**

Replace the StrictMode comment in test 1 with:

```ts
  // Every mint is collected rather than just the first: React StrictMode double-invokes effects in
  // development, so the dev server legitimately mints twice (the extra session is simply never used; the
  // editor page closes no sessions, see Editor.tsx). Asserting the src is ONE OF the minted URLs proves
  // the frame shows a real minted session without pinning the test to a dev-only render count.
```

- [ ] **Step 2: Manual check against the real Collabora (local compose stack)**

Rebuild and start the stack with this branch (`docker compose -f deploy/compose/docker-compose.yml up -d --build`), then in a **fresh browser profile** (Collabora keeps a user's own UI toggles in its localStorage, and they override `ui_defaults`):
1. Import a .docx, open it in the editor at 1280×720: tabbed ribbon, no Collabora sidebar, no welcome slideshow, the page filling the window, the rail on the right.
2. Type something: the status says "Unsaved changes", then after Collabora's autosave "Saved as 0.0.2" (the rail's History shows 0.0.2).
3. Click Done with no further edits: you land on the document page and no new version appears.
4. Type again, click Done straight away: "Saving…", then the document page, and the new version is in History.
5. Screenshot 1280×720 and 1440×900 for the PR.

Also run the real round trip: `cd web && E2E_BASE_URL=http://localhost:8080 npx playwright test e2e/collabora.spec.ts`. Expected: PASS.

- [ ] **Step 3: Commit and open the PR**

```bash
git add web/e2e/actions.spec.ts
git commit -s -m "test(e2e): the editor no longer closes sessions; say so where the double mint is explained"
git push -u origin feat/editor-layout
gh pr create --title "Editor layout: full-height Collabora with a document bar and side rail" --body "$(cat <<'EOF'
Implements docs/superpowers/specs/2026-09-29-collabora-editor-layout-design.md.

- Collabora opens with the tabbed ribbon, its sidebar and ruler off (`ui_defaults`).
- CheckFileInfo sends `PostMessageOrigin`, so the page shows Opening… / Unsaved changes / Saving… / Saved as x.y.z.
- The editor fills the window; a slim bar (← document, editing from, status, Done) and a collapsible rail (History, Members, Approvals).
- Done asks Collabora to save (only if modified) and then goes to the document page; a failed save keeps you on the page.
- The page no longer closes edit sessions: Collabora uploads after it reports "saved", so a close could lose the last edit. Sessions expire with their token.
- Compose turns off Collabora's welcome slideshow. **Production needs the same `--o:welcome.enable=false` in AptsNY/infra-platform (gcp/easydocs/).**

Screenshots: (attach 1280×720 and 1440×900)

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01Eiu4iAF5Lz2Wkk3qwyupn1
EOF
)"
```
