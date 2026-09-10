# Reading a version's text — design

**Date:** 2026-09-10
**Status:** Approved; passed the pre-implementation ponytail gate (findings folded in below)
**Touches:** `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (one endpoint);
`src/EasyDocs.Api/Documents/DocxText.cs` (return shape + one character);
`src/EasyDocs.Api/Documents/TextIndexWorker.cs` (one call site);
`packages/mcp/easydocs_mcp.py` (one line + a stale comment);
`packages/mcp/test_easydocs_mcp.py`; `packages/mcp/README.md`;
`docs-site/docs/api/openapi/v1.json` (regenerated snapshot);
`docs-site/docs/automation-recipes.md` (tool count);
`tests/EasyDocs.Api.Tests/Conformance/E12_Security.cs` and `ContentSearchTests.cs`;
`CHANGELOG.md`. **No new ADR** — this adds an endpoint to an existing surface, it does not decide
anything ADR-13 didn't already decide.

## Problem

The MCP server exposes seventeen read tools, and not one of them returns a word of the document.
`get_document` and `get_version` return metadata; `compare_versions` in its default `summary` form
returns counts; the server publishes no MCP resources. So an agent can be told that
`Clickpay_2026-07-28` has one version, imported, unpublished — and still cannot answer "what does it
say?"

This is the follow-up the original MCP spec named and deferred: its **Out** list reads "a plain-text
'read this document' tool (the API has no such endpoint — a candidate follow-up)". The API still has
no such endpoint. That is the whole gap.

Two things are *not* the gap, and finding that out shrinks the work:

- **Changes between two versions.** `format` is already a declared query parameter on
  `GET /api/v1/documents/{id}/compare` in the OpenAPI document, and the endpoint's `html` case
  returns a rendered redline as `text/html` (`DocumentEndpoints.cs:633`). The generated
  `compare_versions` tool therefore already accepts `format="html"`, and `packages/mcp/README.md:65`
  already documents it. **Verify against a live server before building** — it also tells us whether
  FastMCP surfaces a non-JSON body cleanly. This does not shrink the scope below to nothing:
  comparing needs *two* versions, and the document that prompted this has one.
- **History and activity.** `list_versions` and `list_audit_events` already cover them.

## Scope

**In:** one authenticated endpoint that returns the plain text of one version, the change to
`DocxText` that lets it tell "not a docx" from "a docx with no text", and the one line of MCP
configuration that exposes it as a tool.

**Out:** server-side summarization (the model summarizes text it can read); pagination or an
`offset`/`limit`; PDF and legacy `.doc` extraction; a whole-document `/text` serving the search
index; a `mime` field on `get_version`; any write tool; a rate limit (see Decisions).

## Decisions

### Address it by version, not by document

`GET /api/v1/versions/{vid}/text`, in the existing `/api/v1/versions` group
(`DocumentEndpoints.cs:41`) so it inherits `RequireAuthorization()` and the `Documents` tag.

The tempting cheaper option is `GET /api/v1/documents/{id}/text` serving the `DocumentTexts` row
that `TextIndexWorker` already maintains: no extraction, near-zero cost. Rejected — that row is the
**main-branch head at last indexing time** (`TextIndexWorker.cs:27-33`). It cannot answer for a
historical version, cannot answer for a concurrent branch, and is stale for as long as the poll
interval plus the job. A tool that sometimes returns last week's text without saying so is worse
than no tool. Extraction is cheap enough per call, and correctness beats a cache nobody asked for.

A third option, rejected on the record: `GET /api/v1/versions/{vid}?include=text` on the existing
`GetVersion` — no new path, no `NAMES` entry, no tool-count churn. It loses on discoverability.
Minimal APIs emit no parameter descriptions, so the OpenAPI document would carry a bare
`include: string` that a model has no reason to guess, where a named `get_version_text` tool is
self-describing. Keep the separate endpoint.

### `DocxText.Extract` reports why it produced nothing

Today it returns `""` for three different situations: the bytes are not a zip (`DocxText.cs:20`),
they are a zip without `word/document.xml` (`:24`), or they are a real docx with no text nodes. For
a search index those are all "nothing to index", correctly. For a tool they are not the same
answer at all — the first two mean "wrong kind of file", the third means "this document really is
blank", and a model handed `""` cannot tell which.

Sniffing first does **not** fix this, and this is the gate's one correctness finding. `BlobMime.Sniff`
(`Storage/BlobMime.cs:52-56`) recognizes only `%PDF-` and the OLE2 signature and **defaults to
docx**, so arbitrary bytes sniff as docx, reach the extractor, and come back as an empty string —
exactly the silent blank a 415 was meant to prevent. `DownloadTests.cs:66` already stores such a
blob.

So `Extract` returns the answer instead of a bare string:

```csharp
public readonly record struct Extraction(string? Text, bool Truncated);
```

`Text is null` means "these bytes are not a docx" — the two early returns. `Text is ""` means a
docx with nothing in it. `Truncated` is set by the only code that can know: the loop that stops at
`MaxChars`. Computing truncation in the caller as `text.Length >= MaxChars` is wrong, because
`:50` truncates and then `.Trim()`s, and the character at the cap is very often the boundary
whitespace this spec is about to turn into a newline — a genuinely truncated document would report
`truncated: false`.

`TextIndexWorker.cs:45` becomes `DocxText.Extract(seekable).Text ?? ""`, which is exactly its
current behaviour.

### One character in the same file: `' '` → `'\n'`

The paragraph/`br`/`tab`/`cr` boundary appends a space (`DocxText.cs:45`), so a 40,000 character
agreement extracts as one run-on line. That is what a tsvector wants and not what a reader wants; a
newline gives the model paragraph structure for free.

Safe for search, checked rather than assumed:

- The content predicate is a tsvector match, not a `LIKE` (`DocumentEndpoints.cs:188-189`), and
  `SearchVector` is a **stored generated column** `to_tsvector('simple', "Text")`
  (`EasyDocsDbContext.cs:198-199`, `Migrations/20260802012457_ContentSearch.cs:20`). Newline and
  space are both `blank` to the default parser: same tokens, same positions, so even a quoted
  phrase query is unaffected.
- The existing test asserts only that a paragraph boundary "must become whitespace"
  (`ContentSearchTests.cs:19`), which a newline satisfies.
- Every caller of `Extract` is accounted for: `TextIndexWorker.cs:45` and the tests at
  `ContentSearchTests.cs:15,26`. Nothing else reads `DocumentText.Text`.
- No backfill. Existing rows keep their spaces and keep matching; the endpoint extracts fresh on
  every call.

### The handler

The shape of `Download` (`DocumentEndpoints.cs:76`):

1. load the version → 404 if absent;
2. `AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: false)` — Viewer suffices, and
   `Need.Read` is satisfied by every role (`DocumentAuthorization.cs:36`), so the only failures are
   **404** for a cross-org caller or missing document and **403** for a same-org non-member
   (`DocumentAuthorization.cs:24-28`). There is no wrong-role case for a read.
3. open the blob and spool it to a `MemoryStream` — `ZipArchive` needs a seekable stream and the S3
   backend's is not, the same reason `TextIndexWorker.cs:42` spools;
4. `DocxText.Extract`;
5. `Text is null` → **415**, naming what `BlobMime.Sniff` says the bytes actually are: "this version
   is `application/pdf`; text extraction supports `.docx`";
6. otherwise **200** `{ versionId, major, minor, revision, text, truncated }` — the same three
   number fields `GetVersion` already returns (`DocumentEndpoints.cs:59-61`) rather than a second
   shape, and no `mime`, which at this point is always the docx constant.

Nothing here is more privileged than downloading the same bytes, which any Viewer can already do.

PDF extraction stays a later decision, not a hidden one. LibreOffice is already in the image for
rendering, so `soffice --convert-to txt` is available if the corpus needs it — a subprocess, a
temp-file dance and a second failure mode, none of it justified before someone hits the 415.

### No rate limit

Per-call extraction is unmetered on purpose. Blob size is bounded at upload by Kestrel, the
extractor stops at `MaxChars`, and this is strictly cheaper than `preview_merge`, which already
ships unrated (`Merging/MergePreviewService.cs:93`).

### One line of MCP configuration

```python
"GET /api/v1/versions/{vid}/text": "get_version_text",
```

No new `RouteMap`. The endpoint carries the `Documents` tag, so the tag map at `easydocs_mcp.py:50`
already promotes it; the download exclusion at `:49` is anchored `/download$` and cannot match
`/text`. The `_rename` `KeyError` guard at `:63-64` is **not** the safety net this spec first
claimed: verified against fastmcp 3.4.7, `component_fn` catches it, logs a warning, and registers
the tool under the slug `GET_apiv1versionsvidtext` anyway — so, exactly as the comment at `:62`
says, the exact-set test is the real guard. Read-only stays a property of the tool set — the new
tool is a `GET`, so `test_every_tool_is_a_get` continues to hold.

## Ripples that fail the build if skipped

- `Openapi_snapshot_in_docs_site_matches_the_served_document` pins
  `docs-site/docs/api/openapi/v1.json` to the served document, and `test_easydocs_mcp.py` builds the
  server from that snapshot. Regenerate it in the same commit or both suites fail. Note the new
  `200` will carry no output schema, exactly like every other response in that document.
- `EXPECTED` in `test_easydocs_mcp.py:17` is an exact-set assertion — add `get_version_text`.
- `easydocs_mcp.py:20`'s "The seventeen tools." comment goes stale. (`:90`'s banner uses
  `len(NAMES)` and needs nothing.)
- `packages/mcp/README.md`'s tool table, and "seventeen tools" at
  `docs-site/docs/automation-recipes.md:257` → eighteen.
- **Leave `CHANGELOG.md:37` alone** — that "seventeen tools" is a shipped-release note, not a live
  count.

## Testing

**Authorization** — no new file. Add `/api/v1/versions/{vid}/text` to the three loops that already
pin every document read (`Conformance/E12_Security.cs:60` member-allowed, `:73`/`:80` stranger 403,
`:88` outsider 404). The fixture uploads `DocxFixtures.Base()`, a real docx, so members get 200.
`DownloadTests` pins no auth cases and is not the place for these.

**`DocxText`** (`ContentSearchTests.cs`, unit, no server):

- a paragraph boundary is `'\n'` specifically, not merely whitespace;
- non-docx bytes give `Text is null`, not `""` — the existing `[Theory]` at `:22` becomes this
  assertion;
- a docx with no text nodes gives `Text is ""` — the case that must not 415;
- `Truncated` is `false` for an ordinary fixture.

**Endpoint** (integration):

- a docx version's marker text comes back in `text`;
- a version whose blob is not a docx returns 415 with the sniffed mime in the detail — reuse the
  raw-bytes upload from `DownloadTests.cs:66`.

## Verification before implementation

Run `compare_versions(id=…, from=…, to=…, format="html")` against a live server on a document with
two versions. If it returns redline markup, this spec is the entire remaining gap.

One pre-existing wart to note in passing, not to fix here: `DocumentEndpoints.cs:636` answers **200**
with `<p>Comparison unavailable.</p>` when a comparison cannot be rendered, so a model cannot
distinguish "no changes" from "compare failed".
