# Simul-style version page, rendered by Gotenberg: design

Date: 2026-09-30 · Status: approved in brainstorming (ponytail ultra), pending spec review

## Why

Simul (shutting down 2026-09-30; reference captures in `.superpowers/simul-reference/`, untracked) felt friendlier
for one reason: **click a version, see that version's changes drawn on the real pages**, with a card saying who,
when and how much. easydocs has the same data, split across a History list, an Actions menu and a Compare page
that renders the redline as unpaginated HTML.

Daniel asked for Gotenberg (a Docker HTTP API over LibreOffice/Chromium). A spike on 2026-09-30 confirmed that
our WmlComparer redline `.docx` sent to `POST /forms/libreoffice/convert` (Gotenberg 8.37) comes back in ~1.4s as
a paginated PDF with deletions struck, insertions underlined and change bars in the margin.

## Decisions

- One conversion path: Gotenberg renders both redline pages and published PDFs. LibreOffice leaves the app image.
- The browser's native PDF viewer (an `<iframe>`) shows the pages: thumbnails, zoom, find and print for free.
- No redline-PDF cache, no migration.

Skipped (add when asked): per-version comments (needs a migration), tasks, "only pages with changes", next/previous
change buttons, a PDF cache (add when a real lease view measurably exceeds a couple of seconds).

## Components

### Gotenberg client (server)

`src/EasyDocs.Api/Publishing/GotenbergPdfRenderer.cs` replaces `LibreOfficePdfRenderer.cs` (deleted), keeping its
contract so callers don't change: `RenderToBlobAsync(Stream docx, CancellationToken) -> BlobResult?`, null on any
failure, never throws, one retry. It POSTs the docx as multipart `files` to `{GOTENBERG_URL}/forms/libreoffice/convert`
with a 60s timeout, and stores the PDF through `IBlobStore` as today. A second method,
`RenderAsync(Stream docx, CancellationToken) -> byte[]?`, returns the PDF bytes without storing them (redline view).

Configuration: `GOTENBERG_URL` (e.g. `http://gotenberg:3000`). Unset: every render returns null, which the publish
job already treats as "no PDF" and the redline view answers with 422 (see below). Registered in `Program.cs` via
`AddHttpClient<GotenbergPdfRenderer>` in place of `AddScoped<LibreOfficePdfRenderer>`.

### Compare endpoint

`GET /api/v1/documents/{id}/compare?from=&to=&format=pdf` (additive public API change, needs Daniel's agreement):
same authorization and from/to validation as the existing formats; takes the cached redline `.docx` exactly as
`format=docx` does, sends it to `RenderAsync`, returns `application/pdf` with
`Content-Disposition: inline` and `Cache-Control: private, max-age=31536000, immutable` (the pair of blob SHAs never
changes meaning). Redline unavailable or Gotenberg failure: 422 "Comparison unavailable", same as `format=docx`.
Documented in the OpenAPI description next to the other formats.

### Version page (web)

The document console's History tab (`web/src/routes/History.tsx`) becomes the version page:

- Left: the existing version rows, restyled as a timeline (number, author). Selecting one sets `?v=<versionId>`;
  default is the newest.
- Centre, top: a card with author, date, "N insertions · M deletions" (fixing the "1 insertions" plural),
  version name, published state, and Request approvals (existing flow).
- Centre, below: `<iframe title="Changes in {number}" src=".../compare?from={parentVersionId}&to={v}&format=pdf">`.
  A version with no parent (0.0.1) shows the version's own PDF instead: `.../compare?from={v}&to={v}&format=pdf`
  renders the document unmarked.
- Right: the existing Actions menu for the selected version, shown open as a rail on wide screens.
- The Graph view and the Compare page (any two versions) stay reachable as they are.

### Infrastructure

- `deploy/compose/docker-compose.yml`: a `gotenberg` service (`gotenberg/gotenberg:8`, pinned by digest, internal
  only, no host port) and `GOTENBERG_URL: http://gotenberg:3000` on the app. CI's compose-based jobs get it for free.
- `Dockerfile`: drop the `libreoffice` apt install.
- Production (AptsNY/infra-platform, `gcp/easydocs/`): one Cloud Run service for Gotenberg, internal ingress, and
  `GOTENBERG_URL` on `easydocs-web`. Separate PR in that repo, needs the user's go-ahead.

## Error handling

- Gotenberg down or slow: publish leaves `PdfBlobSha256` null (existing behaviour for a failed render; the durable
  job retries as today); the version page frame shows the 422 problem text via a fallback message and a link to
  download the redline `.docx`.
- A viewer without a PDF plugin (some mobile browsers): the frame falls back to a "Download PDF" link.

## Testing

- C#: `GotenbergPdfRenderer` against a stub HTTP handler (success, 500, timeout → null). `format=pdf`: 200
  `application/pdf` with a `%PDF-` prefix when the renderer succeeds, 422 when it fails, 404/403 as the other formats.
- Existing `PdfRenderTests`/`E06_Publish` switch from "skip when soffice is absent" to running against the compose
  Gotenberg in CI (skip when `GOTENBERG_URL` is unset locally).
- Playwright: select a version, the card shows its counts, the frame's `src` targets `format=pdf` for that version
  against its parent, and the response is a PDF. Against the real stack in CI.
- Manual: the lease spike document in a real browser, 1280×720 and 1440×900.
