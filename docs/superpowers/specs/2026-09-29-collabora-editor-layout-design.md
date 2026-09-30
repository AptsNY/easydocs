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
- `src/EasyDocs.Api/Editing/WopiEndpoints.cs`: add `PostMessageOrigin` to `CheckFileInfoResponse`, set to
  `PUBLIC_BASE_URL` (trailing slash trimmed, as `WebdavEndpoints` does). Without it Collabora sends no host messages.

No new endpoints and no change to the public API spec; the rail uses existing reads.

### Collabora configuration

- Disable the welcome popup: add `--o:welcome.enable=false` to `extra_params` in `deploy/compose/docker-compose.yml`.
- Production Collabora is owned by AptsNY/infra-platform (`gcp/easydocs/`); the same flag goes there as a separate change.

### Web

`Editor.tsx` becomes the full editing screen, composed of:

- **`EditorBar`**: ← document name (link to `/documents/{docId}`), "editing from {version label}", save status, **Done**.
  Below ~700px wide it shows only ← name, status and Done.
- **Collabora iframe**: fills the height left by the app header and the bar (flex layout; the fixed `75svh` in
  `.editor-frame` goes).
- **`EditorRail`**: three sections: History (versions list), Members, Approvals for the version. Collapse toggle; open
  state in `localStorage` (wrapped in try/catch, default open ≥1100px, collapsed below). Each section loads and fails
  independently and links out to the existing full page.
- **`useCollabora(frameRef, collaboraOrigin)`**: hook returning
  `{ status: 'loading' | 'saved' | 'unsaved' | 'saving' | 'failed', save(): Promise<boolean> }`.

Data sources, all existing:

- `GET /api/v1/versions/{vid}` → `documentId`, name, version label.
- `GET /api/v1/documents/{id}/versions`, `GET /api/v1/documents/{id}/members`, `GET /api/v1/versions/{vid}/approvals`.
- `useSse(documentId, refetch)` refreshes the rail on any event, the pattern `DocumentConsole` already uses.

Out of scope: editing members or answering approvals from the rail; the Word/WebDAV editing path.

## Data flow

Opening:

1. The page mints the session (unchanged) and in parallel fetches `GET /versions/{vid}`.
2. On iframe load, the host posts `Host_PostmessageReady` to Collabora.
3. Collabora posts `App_LoadingStatus` with `Status: "Document_Loaded"` → status `saved`.

Editing:

- `Doc_ModifiedStatus {Modified: true}` → `unsaved`.
- Collabora autosaves via WOPI PutFile → `CommitSaveAsync` (unchanged) → `version.created` on SSE → rail refetches.
- `Doc_ModifiedStatus {Modified: false}` → `saved`. The bar shows "Saved as {label}" using the newest version in the
  refetched list.

Done:

- Status `saved`: close the session, navigate to `/documents/{docId}` (replaces `navigate(-1)`, so a direct load works).
- Status `unsaved`: post `Action_Save {Notify: true}`, show "Saving…", wait for `Action_Save_Resp`, then close and leave.

Leaving otherwise:

- In-app navigation: the existing effect cleanup closes the session (unchanged).
- Tab close or reload: a `pagehide` handler sends `fetch(DELETE /api/v1/sessions/{id}, {keepalive: true})` with the same
  credentials and headers as `api.del`.
- While status is `unsaved`, `beforeunload` triggers the browser's "Leave site?" prompt.

## Error handling

- Collabora never loads: after 30s without `Document_Loaded`, the bar says the editor did not load, with reload and
  download options. The rail still works.
- Save does not answer: if `Action_Save_Resp` is missing after 15s or has `success: false`, Done stays on the page and
  says the changes are still in the editor. The session is never closed with unsaved edits.
- Messages whose `origin` is not the Collabora origin (parsed from `editorUrl`) are ignored; messages the host sends
  target that origin, never `*`.
- A Viewer opening the URL directly: the existing 403 message, no bar or rail.
- A rail section fails or is forbidden: that section shows "Couldn't load"; editing is never blocked.
- Co-editing: handled by Collabora; the rail refreshes on every save through SSE.

## Testing

- Vitest: `useCollabora` driven by fake `message` events: status changes, origin filter, save success, save
  timeout and failure. `EditorRail` open-state memory and the narrow-screen default.
- Server tests: the minted `editorUrl` contains `ui_defaults`; CheckFileInfo returns `PostMessageOrigin`.
- Playwright (web-e2e): bar and rail render on a direct load; Done lands on `/documents/{id}`. CI has no Collabora,
  so Collabora-driven states are covered by posting fake messages into the page.
- Manual on the local compose stack before merge: screenshots at 1280×720 and 1440×900 compared with today's.
