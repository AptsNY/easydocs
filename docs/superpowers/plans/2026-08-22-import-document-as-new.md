# Import a .docx as a New Document — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One multipart call, and one dashboard control, that turn a `.docx` into a new document named
from its filename.

**Architecture:** A new `POST /api/v1/documents:import` mapped outside the documents route group,
reusing `SaveAsync`'s hostile-body hardening and `Fork`'s create-then-commit shape. A dashboard
disclosure prefills the name from the file and posts once.

**Tech Stack:** .NET 10 minimal APIs, EF Core + Npgsql, xUnit + Testcontainers, React 19 +
react-router, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-22-import-document-as-new-design.md`

---

## Before You Start

Read these four before touching anything:

- `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` — `MapDocumentEndpoints` (the group), `Create`
  (the document-creation shape you are reproducing), and `SaveAsync` (the multipart hardening you are
  reusing). Its comments explain *why* each 400 exists; you are keeping that behaviour, not reinventing it.
- `src/EasyDocs.Api/Copies/CopyEndpoints.cs` — `Fork`. This is the closest existing thing to what you
  are building: it creates a document then commits its first version. Match its shape.
- `web/src/routes/Dashboard.tsx` — the `.docs-tools` toolbar, the "New document" disclosure, and the
  `.filebutton` pattern in the tile actions.
- `CONTRIBUTING.md` and `.github/pull_request_template.md` — every commit needs `-s` (a CI job
  rejects any commit without a `Signed-off-by` line, and it checks all of them).

**Environment:**

- Backend tests need Docker (Testcontainers boots a real PostgreSQL 16).
- E2E needs the API on `:8080`. Bring it up with `docker compose up -d --build` from
  `deploy/compose/` — plain `up -d` silently reuses a cached image and will serve code without your
  changes.
- **Adding a public endpoint changes the OpenAPI document**, and
  `docs-site/docs/api/openapi/v1.json` is a committed snapshot asserted against it. Regenerate with
  `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches` and commit
  the result, or every later task inherits a red suite.

**Conventions the reviewer will hold you to:**

- Comments explain WHY, never WHAT. The existing comments are long and prose-style; match that. A
  comment restating the line below it will be rejected.
- Test names are full sentences with underscores.
- Errors go through `Problem.Of(status, title, detail)` (RFC-7807).
- Every test scopes its documents to a freshly created folder where order or visibility matters — the
  suite shares one database and runs in parallel.
- Uploads use `DocxFixtures.Build(...)` with a unique paragraph per test. Blobs are content-addressed
  and two tests uploading the same first-time sha concurrently can race into a 500.

## File Structure

| File | Change | Responsibility |
| --- | --- | --- |
| `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` | Modify | The `documents:import` route, its handler, and the filename→name helper |
| `tests/EasyDocs.Api.Tests/DocumentImportTests.cs` | Create | Everything about the new endpoint |
| `docs-site/docs/api/openapi/v1.json` | Regenerate | Committed snapshot; also what generates the API reference |
| `web/src/api.ts` | Modify | Type for the import response, if the existing ones do not fit |
| `web/src/routes/Dashboard.tsx` | Modify | The Import disclosure, name prefill, and the single call |
| `web/src/index.css` | Modify only if needed | Reuse `.filebutton` and `.disclose`; add nothing that already exists |
| `web/e2e/dashboard.spec.ts` | Modify | Two cases: prefilled name, and an edited name |
| `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` | Modify | §10.1 is the authoritative endpoint set |
| `docs-site/docs/user-guide.md` | Modify | One bullet in the dashboard section |
| `CHANGELOG.md` | Modify | `[Unreleased]` → `### Added` |

---

## Task 1: Verify the route pattern before building on it

The spec claims `RouteGroupBuilder` joins its prefix to a pattern with `/`, so a collection-level colon
action has to be mapped on `app` rather than on the group. **Verify that rather than trusting it** —
the route is public API surface and appears in the spec and the OpenAPI document, so guessing wrong
means churn through four files.

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (`MapDocumentEndpoints` only)
- Test: `tests/EasyDocs.Api.Tests/DocumentImportTests.cs` (create)

- [ ] **Step 1: Write a test that pins the route's existence**

Create `tests/EasyDocs.Api.Tests/DocumentImportTests.cs`. This first test only asserts the route is
reachable and authenticated — not its behaviour, which arrives in Task 2.

```csharp
using System.Net;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// One multipart call that creates a document and its first version (spec: import-document-as-new).
// Separate from DocumentUploadTests, which is about adding versions to a document that already exists.
public class DocumentImportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentImportTests(ApiFactory f) => _f = f;

    // The route carries a colon at the collection level, which is the one place the group prefix cannot
    // build it -- RouteGroupBuilder joins prefix and pattern with a slash, so mapping ":import" on the
    // group would yield /api/v1/documents/:import. Pinning reachability separately from behaviour means a
    // routing regression reads as a routing failure rather than as every import test failing at once.
    [Fact]
    public async Task The_import_route_exists_and_requires_authentication()
    {
        var anon = _f.CreateClient();
        var res = await anon.PostAsync("/api/v1/documents:import", TestAuth.DocxForm());

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
```

- [ ] **Step 2: Run it and watch it fail for the right reason**

```bash
dotnet test --filter "FullyQualifiedName~DocumentImportTests" 2>&1 | tail -20
```

Expected: FAIL with `404 != 401` — the route does not exist yet. **If it fails with 401 already, stop
and investigate**: that would mean something else is matching the path, and you need to know what
before you add a second thing that matches it.

- [ ] **Step 3: Map the route**

In `MapDocumentEndpoints`, after the group's own mappings and before the `/api/v1/versions` group:

```csharp
        // Mapped on `app`, not on `g`: RouteGroupBuilder joins its prefix to a pattern with a slash
        // unless the pattern is empty, so g.MapPost(":import") would route /api/v1/documents/:import.
        // A collection-level colon action therefore has to spell out the whole path and re-apply the
        // three things group membership was giving it.
        app.MapPost("/api/v1/documents:import", ImportNew)
            .RequireAuthorization().WithTags("Documents").DisableAntiforgery();
```

And a stub handler, so this task compiles and proves the route in isolation:

```csharp
    private static Task<IResult> ImportNew(HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs, VersioningService versioning) =>
        throw new NotImplementedException();
```

- [ ] **Step 4: Run it again**

```bash
dotnet test --filter "The_import_route_exists_and_requires_authentication" 2>&1 | tail -10
```

Expected: PASS. Authentication is rejected before the handler runs, so the `NotImplementedException`
is never reached.

**If this returns 404 rather than 401, the spec's routing claim is wrong.** Report that immediately
rather than working around it — the fallback is `/api/v1/documents/import` (no colon), which changes
the spec, §10.1, and the OpenAPI document, and I want to make that call rather than have it made for me.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocumentEndpoints.cs tests/EasyDocs.Api.Tests/DocumentImportTests.cs
git commit -s -m "feat(api): route POST /api/v1/documents:import

Mapped on app rather than the documents group: RouteGroupBuilder joins its
prefix to a pattern with a slash, so a collection-level colon action cannot
be built from the group prefix. Handler lands next."
```

---

## Task 2: The import handler

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs`
- Test: `tests/EasyDocs.Api.Tests/DocumentImportTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `DocumentImportTests.cs`. `TestAuth` gives you `RegisterAsync`, `DocxForm`, `CreateFolderAsync`
and `SeedOrgUserAsync`; read it before writing these.

Cover, one test each:

1. **A file and no name** → `201`, the document is named from the filename minus its extension, and
   the response's `major/minor/revision` are `0/0/1`.
2. **An explicit `name`** wins over the filename.
3. **A Windows-style `file.FileName`** (`C:\docs\lease.docx`) yields `lease`. The comment must say why:
   on Linux `Path.GetFileNameWithoutExtension` does not treat `\` as a separator, so this is the case
   that would silently produce a document called `C:\docs\lease`.
4. **A filename with no usable stem** (`.docx`) → `400`. Not a document called "Untitled".
5. **`name` present but whitespace-only** → `400`, matching `Create`.
6. **A `folderId` from another org** → `400`; **a valid `folderId`** places the document in it
   (assert via `GET /api/v1/documents?folderId=…`).
7. **A non-multipart body** (`StringContent` with `application/json`), **an unparseable multipart body**,
   and **a zero-length file** → each `400`, never `500`. These three are the reason `SaveAsync` is
   being reused rather than reimplemented; a `500` on a public endpoint is the failure mode they exist
   to prevent.
8. **The caller is the Owner**, and a second org member seeded via `SeedOrgUserAsync` who was never
   invited gets `404`/`403` rather than seeing it — membership is per-document (spec §11).
9. **The response's `versionId` resolves** via `GET /api/v1/versions/{vid}` and reports the same
   document id. This is what proves the two halves are actually linked rather than both merely existing.
10. **An audit row for `document.created`** exists for the new document.

Use `DocxFixtures.Build(...)` with a unique paragraph per test.

- [ ] **Step 2: Run them and confirm they fail**

```bash
dotnet test --filter "FullyQualifiedName~DocumentImportTests" 2>&1 | tail -25
```

Expected: every new test fails with `NotImplementedException` surfacing as a 500. The Task 1 route
test still passes.

- [ ] **Step 3: Write the filename→name helper**

```csharp
    // The filename is a courtesy, not a path: a client may send a Windows-style path and on Linux
    // Path.GetFileNameWithoutExtension does not treat `\` as a separator, so `C:\docs\lease.docx` would
    // become a document literally called `C:\docs\lease`. Split on both separators, then strip one
    // trailing extension. The result is stored as a display name and never used to open anything.
    private static string? NameFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var stem = fileName.AsSpan()[(fileName.LastIndexOfAny(['/', '\\']) + 1)..].ToString();
        var dot = stem.LastIndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        return stem.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    }
```

`>= 0`, so a filename that is *only* an extension (`.docx`) has its dot stripped down to an empty
string and falls out of the final check as null — which is what makes test 4 a `400` instead of a
document called `.docx`. An earlier draft of this plan wrote `> 0` here and claimed the same effect; it
does not, and the test caught it returning `201`.

- [ ] **Step 4: Write the handler**

Follow `Fork`'s shape. The body handling is `SaveAsync`'s, so **read that method and reuse its
structure** — the `HasFormContentType` guard, the `try`/`catch (InvalidDataException)` around
`ReadFormAsync`, the empty-file check, `blobs.PutAsync`, and `BlobMime.SniffAsync` on the stored bytes.
Do not trust `file.ContentType`.

Order matters: validate the body and resolve the name **before** writing anything, so a bad request
never creates a document. Then create document + branch + owner membership + audit in one
`SaveChangesAsync`, then `CommitSaveAsync`.

Include this comment on the two-step write:

```csharp
        // Two steps, not one transaction, and deliberately so: CommitSaveAsync opens its own
        // transaction and takes SELECT ... FOR UPDATE on the document row for the counter increment
        // (spec §5.1), so it cannot enlist in an outer one. Fork has the same shape for the same
        // reason. What this buys over the two-call client flow is that a CLIENT can no longer strand an
        // empty document -- no browser-closed-between-calls orphan -- and what it does not buy is
        // immunity to a server-side failure between these two steps.
        //
        // ponytail: that residual window is accepted, matching Fork. Closing it means changing
        // CommitSaveAsync's locking for every write path in the product, which is out of proportion to
        // one convenience endpoint.
```

Return `Results.Created($"/api/v1/documents/{doc.Id}", new { id, name, folderId, versionId, major, minor, revision })`.

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~DocumentImportTests" 2>&1 | tail -25
```

Expected: PASS. Then the full suite, because you touched a file every document test loads:

```bash
dotnet test 2>&1 | tail -3
```

Expected: PASS, with only the two `soffice`-guarded PDF tests skipping.

- [ ] **Step 6: Regenerate the OpenAPI snapshot**

```bash
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches
git diff --stat docs-site/docs/api/openapi/v1.json
```

Confirm the diff mentions `documents:import` and nothing unrelated.

- [ ] **Step 7: Commit**

```bash
git add src/EasyDocs.Api/Documents/DocumentEndpoints.cs tests/EasyDocs.Api.Tests/DocumentImportTests.cs docs-site/docs/api/openapi/v1.json
git commit -s -m "feat(api): import a .docx as a new document in one call

POST /api/v1/documents:import takes a file, an optional name and an optional
folderId, and returns the new document with its first version. The name
falls back to the filename minus its extension; a filename with no usable
stem is a 400 rather than a document nobody named.

Body handling is SaveAsync's, so a hostile or malformed multipart body is a
400 rather than a 500, and the blob's mime is sniffed from the stored bytes
rather than read from the attacker-controlled filename."
```

---

## Task 3: The dashboard control

**Files:**
- Modify: `web/src/routes/Dashboard.tsx`, `web/src/api.ts`
- Modify only if genuinely needed: `web/src/index.css`

- [ ] **Step 1: Read the patterns you are reusing**

In `Dashboard.tsx`: the "New document" `<details className="disclose">` block, and the `.filebutton`
label-wrapping-a-clipped-input inside `.tile-actions`. You are combining those two. Read their
comments — they explain why the file input is clipped rather than hidden (`.visually-hidden` clips, it
does not `display: none`, so the input stays label-associated and settable) and why writes live behind
disclosures.

- [ ] **Step 2: Add the disclosure**

Beside the "New document" disclosure in `.docs-tools`. It needs: a `.filebutton` label wrapping a
clipped `<input type="file">`, a text input for the name, and a submit button. Give the `<details>` a
`data-testid="import-document"` and the file input a `data-testid="import-input"`, matching how the
other controls on this screen are addressed from the e2e suite.

State: the picked `File` and the name string.

**The prefill rule matters and is the one thing to get right.** On file pick, set the name from the
filename minus its extension — but only if the field is empty *or* still holds the name derived from a
previously picked file. A name the user typed must never be silently overwritten by picking a second
file. Keep the last derived value in state to make that comparison possible, and say why in a comment;
without it, someone will "simplify" this to an unconditional assignment.

- [ ] **Step 3: Submit with one call**

`FormData` with `file`, `name` (trimmed) and `folderId` when the current route has one, posted to
`/api/v1/documents:import`. `api.post` already handles a `FormData` body — check how the tile's
"Upload version" does it and match.

On success, navigate to `/documents/{id}` using the returned id (`useNavigate`), and reset the form
state. Landing on the document you just created is what the action implies; the dashboard reloads
behind you anyway.

Errors go through the existing `act()`/`problemText` path so a failure says why — the file's comment
on that is explicit that a silent no-op is the worst outcome available.

- [ ] **Step 4: Typecheck, lint, look at it**

```bash
npm --prefix web run build && npm --prefix web run lint
docker compose up -d --build   # from deploy/compose/ -- plain up -d serves a stale image
npm --prefix web run dev
```

Check by hand: picking a file fills the name; editing the name then picking a *different* file does
not clobber your edit; submitting lands on the new document's console showing `0.0.1`; and the file
button is reachable and operable from the keyboard.

- [ ] **Step 5: Commit**

```bash
git add web/src/routes/Dashboard.tsx web/src/api.ts
git commit -s -m "feat(web): import a document from the dashboard toolbar

One disclosure, one call. The name is prefilled from the filename but never
overwrites a name the user typed -- picking a second file only re-derives it
if the field still holds the first file's derived value."
```

---

## Task 4: End-to-end coverage

**Files:**
- Modify: `web/e2e/dashboard.spec.ts`

- [ ] **Step 1: Write two specs**

Read the existing specs first for the fixture idioms (`register`, `signIn`, `disclose`, the file-local
`tile()`), and reuse the committed `e2e/fixtures/*.docx` files — Playwright cannot build a `.docx`, and
the note in that file explains why there are three of them and why an uploading test must own its bytes.

1. Importing a fixture prefills the name from the filename and, on submit, lands on the new document's
   console with `0.0.1` visible.
2. Editing the prefilled name before submitting uses the edited name.

For the second one: assert on what the resulting **document** is called, not on the input's value. An
input holding the right string proves nothing about what was sent — the same trap that made an earlier
sort spec pass with its feature reverted.

- [ ] **Step 2: Run the whole file, then the whole suite**

```bash
npm --prefix web run e2e -- dashboard.spec.ts
npm --prefix web run e2e
```

Expected: pass. `session.spec.ts:50` is a known pre-existing flake under parallel load — if it fails,
re-run it alone to confirm that is what you hit, and say so rather than treating it as yours.

- [ ] **Step 3: Commit**

```bash
git add web/e2e/dashboard.spec.ts
git commit -s -m "test(e2e): cover importing a document and naming it"
```

---

## Task 5: Docs

**Files:**
- Modify: `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` (§10.1),
  `docs-site/docs/user-guide.md`, `CHANGELOG.md`

- [ ] **Step 1: Spec §10.1**

Line 214 is the authoritative endpoint set. Add `POST /documents:import` (multipart) to the Documents
line, beside the existing `POST /documents/{id}/versions:import`. The PR template requires this move in
the same PR as the code.

- [ ] **Step 2: User guide**

One bullet in the dashboard section (`docs-site/docs/user-guide.md`, near "Create document"), in the
voice of the surrounding page. Say that importing a file creates the document in one step and takes its
name from the file unless you change it. The API reference needs no edit — it is generated from the
OpenAPI snapshot Task 2 regenerated.

- [ ] **Step 3: CHANGELOG**

Under `[Unreleased]` → `### Added`. Mention both the endpoint and the dashboard control.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-07-24-easydocs-v1-design.md docs-site/docs/user-guide.md CHANGELOG.md
git commit -s -m "docs: document the one-step import"
```

---

## Done When

- [ ] `dotnet build easydocs.slnx` produces zero warnings (`TreatWarningsAsErrors` is on)
- [ ] `dotnet test` passes; only the two `soffice`-guarded PDF tests skip
- [ ] `npm --prefix web run build` and `run lint` are clean
- [ ] `npm --prefix web run e2e` passes (full suite, against `up -d --build`)
- [ ] Every commit is signed off (`git log main..HEAD --format=%h --invert-grep --grep="Signed-off-by"`
      returns nothing)
- [ ] A malformed multipart body returns `400`, verified by an actual request, not by inspection
