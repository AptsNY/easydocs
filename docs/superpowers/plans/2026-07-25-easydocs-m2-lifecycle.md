# easydocs M2 — Lifecycle (publish, PDF, approvals, share, revert) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn versioned drafts into a document *lifecycle*: publish a selected version as Minor/Major (R3/R4) which renumbers it, renders a PDF, and lists it on the "Major Versions" view; request approvals (single decision + comment) on published versions; name, revert, download (docx + pdf, filename R8), and share a version by public link; and push all of it live to open consoles over SSE. Exit gate: acceptance criteria **E6 (Publish)**, **E7 (Approvals)**, **E8 (Actions menu)**, **E10 (Share/download)**, **E11 (Revert)** green.

**Architecture:** Still one ASP.NET Core process. New pieces are all in-process: a `PublishService` (numbering R3/R4 via the same authoritative `documents.version_counter_*` + row lock as M1's `VersioningService`), an `IPdfRenderer` that shells out to **LibreOffice headless as an out-of-process child with a hard timeout + retry** (LibreOffice is already bundled in the M0 runtime image), driven by the existing in-process `BackgroundService`; approval, share-link, name, revert, and download endpoints; and reuse of M1's `IEventBus`/`/events` SSE with new event types. No queue, no new tables (M0 migrated the full v1 schema).

**Tech Stack (added on top of M0/M1):** LibreOffice headless (`soffice --headless --convert-to pdf`, bundled) · `System.Diagnostics.Process` with a `CancellationTokenSource` timeout · nothing else new.

**Spec:** `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` — §5.1 (numbering R3/R4/R5/R6), §7 (PDF out-of-process, no queue), §8-independent, §10.1 (publish/approvals/share/download endpoints), §10.2 (SSE events), §12.1 **E6, E7, E8, E10, E11**, §13 (M2 row).

**Builds directly on (read first):**
- `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` — endpoint + `AuthorizeAsync` (404/403/role) pattern; `ListVersions`.
- **M1's `VersioningService`** (`CommitSaveAsync`, the `SELECT … FOR UPDATE` counter transaction) — `PublishService` reuses the same row-lock + counter-write discipline; **revert** creates a new draft via `CommitSaveAsync` with `Source=Revert` and the target version's blob (content-addressed, zero re-upload).
- **M1's `IEventBus` + `/api/v1/documents/{id}/events`** — extend with `version.published`, `approval.responded`, `version.named`, `version.reverted`.
- **M1's `IDiffService`** — mirror its WmlComparer guard pattern for the PDF renderer (never 500).
- `src/EasyDocs.Api/Domain/DocumentVersion.cs` — publish columns already exist: `PublishedKind?`, `PublishedBy?`, `PublishedAt?`, `PublishName?`, `PdfBlobSha256?` (spec §4 folded publications into versions — there is **no** `publications` table).
- `src/EasyDocs.Api/Domain/ApprovalRequest.cs` — `VersionId`, `ApproverId`, `Decision?`, `DecisionComment?`, `DueAt?`, `DecidedAt?`, `CancelledAt?`, `RequestedBy` (single decision + comment; **no threaded conversation**, no tasks table — spec §4).
- `src/EasyDocs.Api/Domain/ShareLink.cs` — `VersionId`, `TokenHash` (store hash not token), `CreatedBy`, `ExpiresAt?`, `RevokedAt?`, `ViewCount`.
- `src/EasyDocs.Api/Storage/IBlobStore.cs` — blobs for the rendered PDF; `Blob` rows for dedupe.
- `src/EasyDocs.Api/Auth/DocumentAuthorization.cs` (`ResolveAsync`/`CanEdit`), `Auth/CurrentUser.cs`, `Common/Problem.cs`.
- `Dockerfile` — LibreOffice already installed in the runtime stage (M0 Task 10); confirm `soffice` is on PATH in the image.

**No new tables.** If a scalar column is genuinely missing for R8 download naming or share auditing, add a one-column migration in the task that needs it and say so.

---

## File Structure (new/changed in M2)

```
src/EasyDocs.Api/
  Publishing/
    PublishService.cs             # R3/R4 renumber the SELECTED version under the doc row lock
    PublishEndpoints.cs           # POST /versions/{vid}/publish ; GET /documents/{id}/publications
    IPdfRenderer.cs  LibreOfficePdfRenderer.cs   # out-of-process soffice, timeout + retry, guarded
    PdfRenderBackgroundService.cs # channel-fed; renders on publish, links PdfBlobSha256
  Approvals/
    ApprovalEndpoints.cs          # request / respond / cancel (published versions only)
  Sharing/
    ShareEndpoints.cs             # POST share-links ; GET /s/{token} (public) ; DELETE
  Versions/
    VersionActionsEndpoints.cs    # PATCH name ; POST revert ; GET download?format=docx|pdf (R8)
tests/EasyDocs.Api.Tests/
  PublishTests.cs  PdfRenderTests.cs  ApprovalTests.cs  ShareLinkTests.cs
  RevertTests.cs  DownloadTests.cs
```

---

## Task 1: `PublishService` + publish endpoint (R3/R4, E6)

**Files:** create `Publishing/PublishService.cs`, `Publishing/PublishEndpoints.cs`; test `PublishTests.cs`.

- [ ] Failing tests: publishing a *selected* draft (not necessarily head) as **minor** sets it to `(major, minor+1, 0)` and writes the doc counter so future drafts continue from it (R3, R6); as **major** → `(major+1, 0, 0)` (R4); publish stamps `PublishedKind/PublishedBy/PublishedAt/PublishName`; only Owner/Editor (`CanEdit`) may publish; a `version.published` SSE event fires; `GET /documents/{id}/publications` returns only versions where `PublishedKind IS NOT NULL`, newest first.
- [ ] Implement `PublishService.PublishAsync(documentId, versionId, kind, name?)` using the **same `SELECT … FOR UPDATE` on `"Documents"`** as M1: load doc, compute new number per R3/R4, write `documents.version_counter_*` AND the selected version's `Major/Minor/Revision` + publish columns in one transaction. Enqueue PDF render (Task 2). Route through `AuthorizeAsync(requireEdit)`.
- [ ] `POST /api/v1/versions/{vid}/publish {kind, name?}` and `GET /api/v1/documents/{id}/publications`. Commit `-s`.

## Task 2: PDF renderer — out-of-process LibreOffice (part of E6)

**Files:** `Publishing/IPdfRenderer.cs`, `LibreOfficePdfRenderer.cs`, `PdfRenderBackgroundService.cs`; test `PdfRenderTests.cs`.

- [ ] Failing test: after publish, within a bounded wait the published version gets a non-null `PdfBlobSha256`; the blob is a valid PDF (`%PDF` header); a deliberately malformed docx does NOT crash the service (renderer returns failure, logged, version stays published without PDF — never a 500).
- [ ] Implement `IPdfRenderer.RenderAsync(docxStream, ct)`: write to a temp file, run `soffice --headless --convert-to pdf --outdir <tmp> <file>` via `System.Diagnostics.Process` with a **hard timeout** (kill the process tree on timeout) and **one retry**; read the PDF, `IBlobStore.PutAsync`, return sha. Guard every path — `ponytail:` out-of-process renderer so a hung/crashing soffice never takes down request threads.
- [ ] `PdfRenderBackgroundService` (mirror M1's summary `BackgroundService` + `Channel<T>`): consume publish events, render, set `PdfBlobSha256`, emit nothing new (publish already SSE'd). Register `AddHostedService`. Commit `-s`.

## Task 3: Approvals (E7)

**Files:** `Approvals/ApprovalEndpoints.cs`; test `ApprovalTests.cs`.

- [ ] Failing tests: approvals can be requested **only on a published version** (unpublished → 400/409); one `ApprovalRequest` row per approver with optional `DueAt`; a `respond` records `Decision` (`approved`/`rejected`) + `DecisionComment` + `DecidedAt` **immutably** (no second respond overwrites — a superseding request is a new row); `cancel` sets `CancelledAt`; decisions are permanently readable on the version; `approval.responded` SSE fires. No threaded conversation (single comment field).
- [ ] Implement `POST /api/v1/versions/{vid}/approvals {approver_ids[], due_at?}`, `POST /api/v1/approvals/{id}:respond {decision, comment?}`, `POST /api/v1/approvals/{id}:cancel`. Authorize via the version's document role (`CanEdit` to request/cancel; the named approver may respond). Commit `-s`.

## Task 4: Name + Revert (E11, part of E8)

**Files:** `Versions/VersionActionsEndpoints.cs`; tests `RevertTests.cs` (+ name case).

- [ ] Failing tests: `PATCH /api/v1/versions/{vid} {name}` sets `DocumentVersion.Name` (metadata only), `version.named` SSE; **revert** creates a NEW head draft whose content equals the target version (same `BlobSha256`, `Source=Revert`, `ParentVersionId` = prior head, next Z via the counter) — history untouched (E11); `version.reverted` SSE.
- [ ] Implement name (Editor+) and `POST /api/v1/versions/{vid}/revert` calling **M1's `VersioningService.CommitSaveAsync`** with the target blob and `Source=Revert` (zero re-upload — content-addressed). Commit `-s`.

## Task 5: Share links + public viewer (E10)

**Files:** `Sharing/ShareEndpoints.cs`; test `ShareLinkTests.cs`.

- [ ] Failing tests: `POST /api/v1/versions/{vid}/share-links {expires_at?}` returns a token ONCE (store only `TokenHash`); `GET /s/{token}` (PUBLIC, no auth) serves version metadata + a download link, increments `ViewCount`, and writes an **audit event** (share-link read is one of the two audited reads, spec §11); `DELETE /api/v1/share-links/{id}` revokes (sets `RevokedAt`); a revoked/expired token → 404; the link is scoped to exactly one version.
- [ ] Implement with a 128-bit random token, hashed at rest; the public route is anonymous and rate-limitable later. Commit `-s`.

## Task 6: Download (docx + pdf, R8) (E10, part of E8)

**Files:** `Versions/VersionActionsEndpoints.cs` (download handler); test `DownloadTests.cs`.

- [ ] Failing tests: `GET /api/v1/versions/{vid}/download?format=docx` streams the version's blob with filename `{org_slug}__{Doc_Name}-v{X}.{Y}.{Z}.docx` (R8); `?format=pdf` on a **published** version streams `PdfBlobSha256`; `?format=pdf` on an **unpublished** version → **409** (no PDF exists, spec §7). Viewer+ may download.
- [ ] Implement via `IBlobStore.OpenReadAsync`; set `Content-Disposition` with the R8 filename (slugify doc name safely). Commit `-s`.

## Task 7: Actions-menu wiring check (E8) + full suite + PR

- [ ] Confirm the **v1 action set** is all present and functional (spec §12.1 E8, 8 actions): Open in Collabora (M1), Import (M1), Share, Download, Name, Publish, Revert, Push-To-Copy (M4 — stub the button/endpoint contract only if trivial, else leave for M4). For M2, assert the M2-owned actions end-to-end.
- [ ] `dotnet test` → all green (M0+M1 regression + M2 suites); `dotnet build` → 0 warnings.
- [ ] `git push -u origin m2-lifecycle && gh pr create --fill --base main`.

---

## M2 Done — Exit Checklist

- [ ] **E6 Publish:** publish applies to the *selected* version; renumbers per R3/R4; writes doc counter (future drafts continue); PDF rendered and linked; appears on `/publications`.
- [ ] **E7 Approvals:** only on published versions; one row per approver + due date; decisions immutable + permanently displayed; cancel closes the request; single comment (no thread).
- [ ] **E8 Actions:** the M2 actions (Share, Download, Name, Publish, Revert) present and functional; full v1 action set audited (Push-To-Copy lands in M4).
- [ ] **E10 Share/download:** share link scoped to one version, revocable, audited; DOCX + PDF download; R8 filename; PDF-on-unpublished → 409.
- [ ] **E11 Revert:** new head equals target content; history untouched.
- [ ] PDF renderer out-of-process with timeout+retry; every render/diff guarded (no 500).

**Assumed interfaces introduced here (referenced later):** `PublishService.PublishAsync` (M4 push-merge may publish), `IPdfRenderer` (M3 conformance suite exercises it), the public `/s/{token}` route + audit-on-read (M3 API docs list it).

**Next:** write/execute the **M3** plan (public API GA: OpenAPI, tokens, `/docs`, conformance suite).
