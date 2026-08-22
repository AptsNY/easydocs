# Import a .docx as a new document — design

**Date:** 2026-08-22
**Status:** Approved, ready for implementation planning
**Touches:** `Documents/DocumentEndpoints.cs`, `web/src/routes/Dashboard.tsx`, `web/src/api.ts`,
`docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` (§10.1), the OpenAPI snapshot, `CHANGELOG.md`

## Problem

Getting a `.docx` into easydocs as a *new* document takes two calls and a name typed from memory.
`POST /api/v1/documents` accepts JSON — a name and an optional folder, no file. `POST
/api/v1/documents/{id}/versions` (and its `:import` sibling) attaches a version to a document that
must already exist. So the flow is: create an empty document with a hand-typed name, then upload into
it.

Two costs. The obvious one is the typing: the file already carries a perfectly good name and the user
retypes it. The quieter one is that the two calls can half-succeed — a failed upload leaves an empty
document in the library that nobody asked for and someone has to trash by hand.

Note what already exists, because it is most of what "rename on import" could mean and none of it is
the gap:

- `PATCH /api/v1/documents/{id}` `{name}` renames a document.
- `PATCH /api/v1/versions/{vid}` renames a version.
- `POST /api/v1/versions/{vid}/copies` `{name}` names a **child** document at fork time, defaulting to
  `"{master} (copy)"`, and the dashboard's "Push to a new copy" modal already exposes that field.

The missing capability is specifically *file in, new document out, in one step*.

## Scope

In: a new endpoint that creates a document and its first version from one multipart request, and a
dashboard control that uses it, with the name prefilled from the filename and editable before
anything is written.

Out: bulk/multi-file import; importing into an existing folder tree from a zip; naming the *version*
during import (`versions:import` still takes no name); changing `Fork`.

## Decisions

### `POST /api/v1/documents:import`

Multipart, matching the house colon-action convention (`{id}:restore`, `versions:import`,
`push-requests/{id}:accept`).

| Field | Required | Meaning |
| --- | --- | --- |
| `file` | yes | The document bytes. |
| `name` | no | The new document's name. Omitted → derived from the filename. |
| `folderId` | no | Target folder. Must exist in the caller's org, same check `Create` runs. |

Returns `201 Created` with `Location: /api/v1/documents/{id}` and a body carrying both halves of what
was created — `{ id, name, folderId, versionId, major, minor, revision }` — so a client needs no
follow-up read. The version is `0.0.1`, because the document's counter starts at zero like any other
new document's.

**Mapped on `app`, not on the `/api/v1/documents` group.** `RouteGroupBuilder` joins its prefix to a
pattern with a `/` unless the pattern is empty, so `g.MapPost(":import", …)` would produce
`/api/v1/documents/:import`. The collection-level action therefore gets `app.MapPost(
"/api/v1/documents:import", …)` and re-applies the two things group membership was providing,
`.RequireAuthorization()` and `.WithTags("Documents")`. `.DisableAntiforgery()` is *not* one of them —
the group's own multipart routes each opt into it per-route as well.

### Name derivation

`name` present → trimmed; empty after trimming is a `400`, exactly as `Create` does today.

`name` absent → derived from `file.FileName`: take the segment after the last `/` or `\`, then strip a
single trailing extension. The manual split rather than `Path.GetFileNameWithoutExtension` is
deliberate: a client may send a Windows-style path, and on Linux `Path` does not treat `\` as a
separator, so `C:\docs\lease.docx` would become the name `C:\docs\lease`. This is a courtesy for
display, not a security measure — the value is stored as a name and never used as a path.

If derivation yields nothing usable (no filename, or a filename that is all extension), that is a
`400` naming the field, not a silent placeholder like "Untitled". A document whose name nobody chose
is worse than an error that says so.

**No length cap.** `Document.Name` is unbounded `text` and neither `Create` nor `Update` caps it, so
capping only this path would be an inconsistency dressed up as hardening.

### Body handling

Reuses `SaveAsync`'s hardening rather than re-deriving it — that method is already the one place in
the product that has to survive a hostile multipart body, and its comment says so:

- `HasFormContentType` false → `400`, because `ReadFormAsync` throws on a non-multipart body and that
  would surface as a `500` on a public endpoint.
- `InvalidDataException` from `ReadFormAsync` → `400`.
- Missing or zero-length `file` → `400`.
- The stored blob's mime is **sniffed from the bytes** via `BlobMime.SniffAsync`, never read from
  `file.ContentType` or `file.FileName`; both are attacker-controlled. This matters more here than on
  upload, because the filename is now also the source of the document's name.

**Amended during implementation, in both ingest routes.** `catch (InvalidDataException)` alone was not
enough, and the shared path had shipped with the same gap:

- A body that never reaches its closing boundary raises `IOException` from the body reader, not
  `InvalidDataException`, so an unterminated body was a **500** on a public endpoint.
- `BadHttpRequestException` **derives from** `IOException`, so widening the catch swallowed Kestrel's own
  statuses — a `413` became a `400` that never mentioned the limit. It is rethrown ahead of the broad
  clause; `Program.cs`'s handler already renders it as problem+json from the exception's own status.
  Pinned in `.github/scripts/conformance-smoke.sh` rather than the xUnit suite, because `TestServer` does
  not enforce `MaxRequestBodySize`.
- `DirectoryNotFoundException` is **excluded** from the broad clause. Form buffering spills parts over
  64 KB to a temp file, so an unusable temp dir raises it — the server's misconfiguration, which must stay
  a `500` rather than become a `400` blaming the uploader. What remains genuinely ambiguous (a truncated
  body versus a full disk) answers `400` and is logged at Warning.

**Amended: the second write is not cancellable.** `CommitSaveAsync` takes `CancellationToken.None`, not
`ctx.RequestAborted`. Passing the request token stranded an empty document whenever a client hung up
between the two writes — measured at 23 orphans in ~191 aborted imports, and independently at 31 in 380.
Once the first write lands, finishing the second is no longer the caller's business. `Fork` still passes
`ctx.RequestAborted` and now carries a `ponytail:` marker recording the identical exposure.

The endpoint accepts whatever `Upload` accepts. It does not gate on `.docx`: the corpus already holds
legacy `.doc` and PDFs, `Download` sniffs and labels them accordingly, and rejecting them only here
would make import stricter than upload for no stated reason.

### Not a single transaction

`VersioningService.CommitSaveAsync` opens its own transaction and takes a `SELECT … FOR UPDATE` on the
document row for the version-counter increment (spec §5.1), so it cannot enlist in an outer
transaction without surgery on the core write path. This endpoint therefore has the same shape as
`Fork`, which has the same constraint: document + branch + owner membership + audit in one
`SaveChanges`, then `CommitSaveAsync` for the first version.

So the honest guarantee is narrower than "atomic": a **client** cannot strand an empty document any
more — no browser-closed-between-two-calls orphan — and the remaining window is a server-side failure
between the two steps, which is exactly the window `Fork` has today. Buying more than that means
changing `CommitSaveAsync`'s locking for every write path in the product, which is out of proportion to
one convenience endpoint.

`ponytail:` comment records this, naming `Fork` as the precedent so the next reader sees it is a
consistent choice rather than an oversight.

### Dashboard

An "Import a document" disclosure in `.docs-tools`, beside the existing "New document" one. Native
`<details>`, so the disclosure costs no JavaScript and its summary is focusable and Enter-operable —
the same pattern every other write on this screen already uses.

Inside: the existing `.filebutton` pattern (file input clipped to a screen-reader-only box with its
own `<label>` as the visible button, because the OS "Choose File" control is the one thing in the
product nobody designed), plus a name field and a submit button.

Picking a file prefills the name field from the filename, extension stripped, **only when the field is
empty or still holds a previous file's derived name** — so a name the user typed is never silently
overwritten by a second file pick.

Submitting posts once to `documents:import` and, on success, navigates straight to the new document's
console. The dashboard reloads behind it anyway; landing on the thing you just created is what the
action implies.

## Tests

**`tests/EasyDocs.Api.Tests/DocumentImportTests.cs`** (new file — `DocumentUploadTests` is about
versions on an existing document)

- a `.docx` and no `name` creates a document named from the filename, with version `0.0.1`
- an explicit `name` wins over the filename
- a Windows-style `file.FileName` (`C:\docs\lease.docx`) yields `lease`, not `C:\docs\lease`
- a filename with no usable stem (`.docx`) is a `400`, not a document called "Untitled"
- `name` present but whitespace-only is a `400`
- a `folderId` that does not exist in the caller's org is a `400`; one that does places the document
  in it
- a non-multipart body, an unparseable multipart body, and a zero-length file are each a `400`, not a
  `500`
- the caller becomes the document's Owner, and a second org member who was not invited cannot see it
  (membership is per-document, spec §11)
- the response's `versionId` resolves via `GET /api/v1/versions/{vid}`, so the two halves really are
  linked
- an audit row exists for `document.created`

**`web/e2e/dashboard.spec.ts`**

- importing a fixture `.docx` prefills the name from the filename, and submitting lands on the new
  document's console with version `0.0.1` showing
- editing the prefilled name before submitting uses the edited name

## Rejected alternatives

**Two existing calls from the frontend, no API change.** The smallest possible diff, and the API
already permits it. Rejected because a failed upload leaves an orphan empty document, and because the
README stakes out "two front doors… the same surface, not a subset" — an API consumer would still have
to know the two-step dance.

**Two calls plus a compensating `DELETE` on failure.** Removes the orphan in the common failure but
the rollback is best-effort: a browser closed mid-failure strands it anyway. More moving parts than the
endpoint, for a weaker guarantee.

**Extending `POST /api/v1/documents` to accept multipart as well as JSON.** One endpoint doing two
things, with a content-type branch deciding which, and an OpenAPI entry that describes both. The colon
convention exists precisely so actions get their own route.
