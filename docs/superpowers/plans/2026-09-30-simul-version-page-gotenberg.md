# Simul-style version page on Gotenberg: implementation plan

> For agentic workers: use superpowers:executing-plans. Steps are checkboxes. Ponytail ultra: smallest diff that meets
> the spec (`docs/superpowers/specs/2026-09-30-simul-version-page-gotenberg-design.md`); anything not listed is out.

**Goal:** the History tab shows the selected version's redline as real pages, rendered by Gotenberg, and Gotenberg
replaces the in-app LibreOffice for published PDFs.

**Architecture:** `GotenbergPdfRenderer` (HTTP) replaces `LibreOfficePdfRenderer` with the same contract, plus a
bytes-only `RenderAsync`. `compare?format=pdf` renders the cached redline docx (or the version itself when
`from == to`). The web keeps the History list as the timeline (each row already is the card) and adds a preview
column: an `<iframe>` on `format=pdf`.

**Deviation from the spec (ultra):** no separate card component; the selected row *is* the card, so the card is
the existing `VersionRow` marked selected. The published-only Approvals link sits above the frame.

Commits: `git commit -s`, trailers `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>` and
`Claude-Session: https://claude.ai/code/session_01Eiu4iAF5Lz2Wkk3qwyupn1`. Branch `feat/version-page-gotenberg`.

## Task 1: renderer

- [ ] `src/EasyDocs.Api/Publishing/GotenbergPdfRenderer.cs`: `HttpClient` + `IBlobStore` + logger. `RenderAsync(Stream,
  ct) -> byte[]?` POSTs multipart `files` (`in.docx`) to `{GOTENBERG_URL}/forms/libreoffice/convert`, 30s timeout,
  one retry, null on any failure or when `GOTENBERG_URL` is unset, never throws (except caller cancellation).
  `RenderToBlobAsync` = `RenderAsync` + `blobs.PutAsync`.
- [ ] Delete `LibreOfficePdfRenderer.cs`; `Program.cs`: `AddHttpClient<GotenbergPdfRenderer>()`;
  `PdfRenderBackgroundService` resolves the new type; `DurableJobWorker` lease comment updated.
- [ ] Tests: `tests/EasyDocs.Api.Tests/Soffice.cs` holds `ResolveSoffice()` (moved verbatim) for the merge tests;
  `ThreeWayMergeLibreOfficeTests` uses it. `ApiFactory`: `static Lazy<Task<string>> Gotenberg` starting
  `gotenberg/gotenberg:8` via Testcontainers `ContainerBuilder` (port 3000, wait on `/health`), and
  `GOTENBERG_URL` in the in-memory config. `PdfRenderTests`/`E06_Publish`: drop the soffice skips.
  `Malformed_docx_does_not_crash_renderer` builds the renderer from the factory's services.
  New: renderer returns null on an unset URL and on a 500 (stub `HttpMessageHandler`).
- [ ] Run `dotnet test --filter "PdfRenderTests|E06_Publish|ThreeWayMergeLibreOffice"`; commit.

## Task 2: `compare?format=pdf`

- [ ] `DocumentEndpoints.Compare`: `case "pdf"`. `from == to`: sniff the blob; PDF streams as is, else
  `RenderAsync` of the blob. Otherwise ensure the redline (as `docx` does) and `RenderAsync` it. Null → 422
  "Comparison unavailable". 200: `application/pdf`, `Content-Disposition: inline`,
  `Cache-Control: private, max-age=31536000, immutable`. OpenAPI/doc comment lists the new format.
- [ ] Test (`CompareTests` or nearest): two versions → 200 `%PDF-`; same version → 200 `%PDF-`; Viewer allowed;
  other org 404. Commit.

## Task 3: deploy surfaces

- [ ] `deploy/compose/docker-compose.yml`: `gotenberg` service (image pinned by digest, no host port) and
  `GOTENBERG_URL: http://gotenberg:3000` on the app; `.env.example` note.
- [ ] `Dockerfile`: drop the `libreoffice` install and its comment. `deploy-gcp.yml`/`release.yml` comments:
  no "bundles LibreOffice". `conformance.yml` and `release.yml`: drop the LibreOffice install steps (E06 now uses
  the Testcontainers Gotenberg); `ci.yml` keeps its install (merge tests). `conformance-smoke.sh` wording.
- [ ] Rebuild the compose stack; `conformance-smoke.sh`-equivalent publish renders a PDF. Commit.

## Task 4: version page (web)

- [ ] `VersionRow`: optional `selected` and `onSelect`; with `onSelect` the number is a button
  (`aria-pressed`). Summary text pluralises ("1 insertion"); update any e2e regex that assumed "insertions".
- [ ] `History.tsx`: `useSearchParams` `v`; selected = row with that id, else the newest. List view becomes a
  two-column `.history-split`: the spine left, and right `<section data-testid="version-preview">` with a heading
  "Changes in {number}" (or "{number}" for a parentless version), the Approvals link when published, and
  `<iframe data-testid="version-pdf" title=… src="/api/v1/documents/{id}/compare?from={parent ?? v}&to={v}&format=pdf">`.
  CSS in `index.css`: grid, stacks below 1100px, frame `height: 80svh`.
- [ ] e2e `web/e2e/console.spec.ts` (or new `version-page.spec.ts`): selecting a row sets `?v`, the frame `src`
  targets that pair, and fetching it returns `%PDF-`. `copies.spec.ts` test 2 rewritten to use a version whose
  render fails (a docx Gotenberg rejects), not a lost race. Commit.

## Task 5: ship

- [ ] Full C# suite, full e2e against the rebuilt compose stack, screenshots vs Simul.
- [ ] PR, fresh review, merge. **Before deploying:** infra-platform PR adding the Gotenberg sidecar and
  `GOTENBERG_URL=http://localhost:3000` to `gcp/easydocs/service.yaml`, reviewed and applied. Then deploy easydocs,
  live test publish + version page in production.
