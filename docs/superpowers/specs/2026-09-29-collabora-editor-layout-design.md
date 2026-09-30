# Collabora editor layout: design

Date: 2026-09-29 · Status: approved in brainstorming, pending spec review

## Problem

The editing page (`/versions/{vid}/edit`, `web/src/routes/Editor.tsx`) is hard to work in. At 1280×720 about five lines of
the document are visible:

- Double chrome: app header, a "Back" row, Collabora's title bar and ribbon take ~280px before the page starts.
- Collabora's formatting sidebar is open by default and takes ~30% of the width.
- The frame is a fixed `75svh`, leaving dead space below it.
- Collabora's "What's new" popup appears on every open.
- No easydocs context: which version is being edited, whether it is saved, how to finish.
- Leaving by a full page unload (tab close, reload) does not close the edit session.

## Decisions

- Layout: the editor inside the app shell, a slim document bar above it, and a collapsible easydocs side rail on the
  right in place of Collabora's sidebar.
- Toolbar: Collabora's tabbed ribbon, sidebar and ruler off.
- Rail content: version history, members, approvals. Read-only, linking to the full pages.
- Rail behaviour: open by default, collapsible, state remembered per browser; starts collapsed below 1100px wide.
- Save status comes from Collabora's PostMessage API; the existing SSE stream refreshes the rail.

## Components

### Server

- `src/EasyDocs.Api/Editing/EditingEndpoints.cs`: append
  `&ui_defaults=` + URL-encoded `UIMode=tabbed;TextSidebar=false;TextRuler=false` to the `editorUrl` it already builds.
- `src/EasyDocs.Api/Editing/WopiEndpoints.cs`: add `PostMessageOrigin` (`[JsonIgnore(Condition = WhenWritingNull)]`) to `CheckFileInfoResponse`, set to
  the origin (scheme://host[:port], no path) of `PUBLIC_BASE_URL`, which is the origin serving the SPA. When
  `PUBLIC_BASE_URL` is unset the field is omitted (never a 500: that would stop every document opening) and the page
  simply gets no status messages. Add the key to the list in `WopiHostTests.CheckFileInfo_wire_json_is_WOPI_PascalCase…`.
  Under the Vite dev server the SPA origin differs from `PUBLIC_BASE_URL`, so status messages are dropped there; the
  load timeout below must not treat that as a failure (see Error handling).

No new endpoints and no change to the public API spec; the rail uses existing reads.

### Collabora configuration

- Disable the welcome popup: add `--o:welcome.enable=false` to `extra_params` in `deploy/compose/docker-compose.yml`.
- Production Collabora is owned by AptsNY/infra-platform (`gcp/easydocs/`); the same flag goes there as a separate change.

### Web

`Editor.tsx` becomes the full editing screen, composed of:

- **`EditorBar`**: ← document name (link to `/documents/{docId}`), "editing from {version label}", save status, **Done**.
  Below ~700px wide it shows only ← name, status and Done.
- **Collabora iframe**: fills the height left by the app header and the bar (flex layout). `.editor-frame` is shared
  with `MergeReview.tsx` and `Compare.tsx`, so it keeps its `75svh`; the editor page adds its own modifier class.
- **`EditorRail`**: three sections: History (versions list), Members, Approvals for the version. Collapse toggle; open
  state in `localStorage` (wrapped in try/catch, default open ≥1100px, collapsed below). Each section loads and fails
  independently and links out to the existing full page.
- **`useCollabora(frameRef, collaboraOrigin)`**: hook returning
  `{ status: 'loading' | 'saved' | 'unsaved' | 'saving' | 'failed', save(): Promise<boolean> }`.

Data sources, all existing:

- `GET /api/v1/versions/{vid}` → `documentId`, `major`/`minor`/`revision` (label built client-side as
  `major.minor.revision`), then `GET /api/v1/documents/{id}` → document name. Sequential: the second needs the first.
- `GET /api/v1/documents/{id}/versions?order=desc` (default order is ascending, limit 25), `GET /api/v1/documents/{id}/members`, `GET /api/v1/versions/{vid}/approvals`.
- `useSse(documentId, refetch)` refreshes the rail on any event, the pattern `DocumentConsole` already uses.

Out of scope: editing members or answering approvals from the rail; the Word/WebDAV editing path.

## Data flow

Opening:

1. The page mints the session (unchanged) and in parallel fetches the version, then the document.
2. Collabora posts `App_LoadingStatus {Status: "Frame_Ready"}`; the host answers `Host_PostmessageReady` (not on the
   iframe `load` event, which can fire before Collabora listens).
3. Collabora posts `App_LoadingStatus {Status: "Document_Loaded"}` → status `saved`.

All messages both ways are JSON strings of the shape `{MessageId, SendTime, Values}`; test mocks use that shape.

Editing:

- `Doc_ModifiedStatus {Values: {Modified: true}}` → `unsaved`.
- `Doc_ModifiedStatus {Values: {Modified: false}}` → `saved`. This only means the core saved to its local file; the WOPI
  PutFile upload runs after it, asynchronously. So `saved` is a display state, never permission to close the session.
- Collabora's PutFile → `CommitSaveAsync` (unchanged) → `version.created` on SSE → rail refetches.
- The bar shows "Saved as {label}" for the newest version in the refetched list with `source == "EditWopi"`,
  `createdBy == me.id` and `createdAt >=` the session's mint time; with none, just "Saved".

Session closing rule: **the session is closed only after a confirmed upload**: the last relevant message was
`Action_Save_Resp {Values: {success: true}}` with no `Modified: true` after it. Closing sets `ClosedAt`, and
`WopiEndpoints.LiveSessionAsync` then rejects any PutFile still in flight or the one coolwsd sends when the last view
disconnects with changes, so closing early loses edits. Leaving without closing is always safe: the session stays open,
Collabora's disconnect save lands, and the cost is a stale row. So every uncertain case (no messages, `loading`,
`saving`, timeout, dev server, unconfigured `PostMessageOrigin`) navigates without closing. In dev and unconfigured
environments sessions are therefore never closed from the page.

Done (and the ← link, same logic):

1. Post `Action_Save {Values: {Notify: true}}` whatever the status, and show "Saving…".
2. `Action_Save_Resp {success: true}` → close the session and navigate to `/documents/{docId}` (replaces
   `navigate(-1)`, so a direct load works).
3. `Action_Save_Resp {success: false}` → stay; the bar says the save failed and the changes are still in the editor.
4. No response within 15s (including when no messages ever arrive) → navigate without closing.

Leaving otherwise:

- In-app navigation (effect cleanup) and tab close or reload (`pagehide`): close only if the closing rule holds, using
  `fetch(DELETE /api/v1/sessions/{id}, {keepalive: true, credentials: 'same-origin'})` for `pagehide` (what `api.del`
  sends; no antiforgery token is needed). Otherwise leave the session open.
- Both handlers read state from a ref, not captured render state, or they would see the first render's status.
- While status is `unsaved` or `saving`, `beforeunload` triggers the browser's "Leave site?" prompt. Leaving anyway
  keeps the session open, so the disconnect save still goes through.

## Error handling

- Collabora never loads: there is no reliable signal (an iframe fires `load`, not `error`, on a failed or error page,
  and "no messages" also happens when `PostMessageOrigin` is unconfigured). So there is no error banner: after 30s with
  no Collabora message the status indicator is hidden, and Collabora's own error page shows in the frame. The rail still
  works.
- Save fails: `Action_Save_Resp {success: false}` keeps the user on the page with the message above; nothing is closed.
- Messages whose `origin` is not the Collabora origin (parsed from `editorUrl`) are ignored; messages the host sends
  target that origin, never `*`.
- A Viewer opening the URL directly: the existing 403 message, no bar or rail.
- A rail section fails or is forbidden: that section shows "Couldn't load"; editing is never blocked.
- Two people editing the same version: each session has its own WOPI file id (`/wopi/files/{session.Id}`), so they are
  separate Collabora documents with no live co-editing; each save commits on its own and the rail refreshes through SSE.

## Testing

- Vitest: `useCollabora` driven by fake `message` events in the `{MessageId, SendTime, Values}` shape: handshake on
  `Frame_Ready`, status changes, origin filter, save success, save timeout and failure, "no close while unsaved", and "no close between `Modified: false` and a
  successful `Action_Save_Resp`". `EditorRail` open-state memory and the narrow-screen default.
- Server tests: the minted `editorUrl` contains `ui_defaults`; CheckFileInfo returns `PostMessageOrigin` as an origin
  when `PUBLIC_BASE_URL` is set (the test factory sets `http://localhost`) and omits it when unset (needs a factory
  override).
- Playwright (web-e2e): bar and rail render on a direct load; Done lands on `/documents/{id}`. CI has no Collabora,
  so Collabora-driven states are covered by posting fake messages into the page.
- Manual on the local compose stack before merge, in a clean browser profile (Collabora keeps the user's own UI
  toggles in its localStorage, and they override `ui_defaults`): screenshots at 1280×720 and 1440×900 compared with today's.
