# Reading a version's text — design

**Date:** 2026-09-10
**Status:** Approved, ready for implementation planning
**Touches:** `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (one endpoint);
`src/EasyDocs.Api/Documents/DocxText.cs` (one character); `packages/mcp/easydocs_mcp.py` (one line);
`packages/mcp/test_easydocs_mcp.py`; `packages/mcp/README.md`;
`docs-site/docs/api/openapi/v1.json` (regenerated snapshot);
`docs-site/docs/automation-recipes.md` (tool count); `tests/EasyDocs.Api.Tests/` (new cases);
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
  `compare_versions` tool therefore already accepts `format="html"`. **Verify this against a live
  server before building anything** — it also tells us whether FastMCP surfaces a non-JSON response
  body cleanly. If it works, "recent changes" needs no code.
- **History and activity.** `list_versions` and `list_audit_events` already cover them.

## Scope

**In:** one authenticated endpoint that returns the plain text of one version, and the one line of
MCP configuration that exposes it as a tool.

**Out:** server-side summarization (the model summarizes text it can read); pagination or an
`offset`/`limit` on the text; PDF and legacy `.doc` extraction; a whole-document `/text` that serves
the search index; a `mime` field on `get_version`; any write tool.

## Decisions

### Address it by version, not by document

`GET /api/v1/versions/{vid}/text`, in the existing `/api/v1/versions` group
(`DocumentEndpoints.cs:41`) so it inherits `RequireAuthorization()` and the `Documents` tag.

The tempting cheaper option is `GET /api/v1/documents/{id}/text` serving the `DocumentTexts` row
that `TextIndexWorker` already maintains: no extraction, near-zero cost. Rejected — that row is the
**main-branch head at last indexing time**. It cannot answer for a historical version, cannot answer
for a concurrent branch, and is stale for as long as the poll interval plus the job. A tool that
sometimes returns last week's text without saying so is worse than no tool. Extraction is cheap
enough to do per call (`ZipArchive` + `XmlReader`, stopping at `MaxChars`), and correctness beats a
cache nobody asked for.

### Mirror `Download`'s authorization exactly

The handler is the shape of `Download` (`DocumentEndpoints.cs:76`):

1. load the version → 404 if absent;
2. `AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: false)` — Viewer+, with the same
   404-for-non-member / 403-for-wrong-role mapping every document read uses;
3. open the blob and spool it to a `MemoryStream` — `ZipArchive` needs a seekable stream and the S3
   backend's is not, the same reason `TextIndexWorker:42` spools;
4. `BlobMime.Sniff` the bytes;
5. not a docx → **415**, naming the mime it actually is;
6. docx → `DocxText.Extract`, returning
   `{ versionId, number, mime, text, truncated }`.

`truncated` is `text.Length >= DocxText.MaxChars` (500,000 — the existing constant). A caller that
sees `truncated: true` knows the tail is missing; nobody gets a paginated API until a real document
hits the cap.

Reading text is a read: Viewer suffices, exactly as it does for `Download` and `GetVersion`. Nothing
here is more privileged than downloading the same bytes, which any Viewer can already do.

### 415 rather than an empty string

`DocxText.Extract` answers `""` for a PDF or a legacy `.doc` — by design, silently
(`DocxText.cs:20,24`), because a search index wants "nothing to index" and not an exception. A tool
must not inherit that silence: a model handed `""` concludes the document is blank, and the corpus
genuinely holds PDFs and legacy `.doc` files (which is why `Download` sniffs at all). So the
endpoint sniffs first and refuses with the mime in the problem detail — "this version is
`application/pdf`; text extraction supports `.docx`". The model learns why it got nothing.

PDF extraction is a later decision, not a hidden one. LibreOffice is already in the image for
rendering, so `soffice --convert-to txt` is available if the corpus turns out to need it — a
subprocess, a temp-file dance and a second failure mode, none of it justified before someone hits
the 415.

### One character in `DocxText`: `' '` → `'\n'`

The paragraph/`br`/`tab`/`cr` boundary currently appends a space (`DocxText.cs:45`), so a 40,000
character agreement extracts as one run-on line. That is what a tsvector wants and not what a reader
wants; a newline gives the model paragraph structure for free.

Safe for search, checked rather than assumed:

- The content predicate is a tsvector match, not a `LIKE` (`DocumentEndpoints.cs:187`), and
  `to_tsvector` treats a newline and a space identically as token separators — same tokens, same
  positions, so even a quoted phrase query behaves the same.
- The existing test asserts only that a paragraph boundary "must become whitespace"
  (`ContentSearchTests.cs:19`), which a newline satisfies.
- No backfill. Existing `DocumentTexts` rows keep their spaces and keep matching; the new endpoint
  extracts fresh on every call, so it emits newlines immediately regardless of index state.

Two callers, both accounted for: the worker and the new endpoint.

### One line of MCP configuration

```python
"GET /api/v1/versions/{vid}/text": "get_version_text",
```

No new `RouteMap`. The endpoint carries the `Documents` tag, so the tag map at
`easydocs_mcp.py:50` already promotes it to a tool, and the download exclusion is anchored
`/download$` so it cannot swallow `/text`. The `_rename` `KeyError` guard turns a forgotten `NAMES`
entry into a loud start-up failure instead of an auto-slugged tool name.

Read-only stays a property of the tool set: the new tool is a `GET`, so
`test_every_tool_is_a_get` continues to hold.

## Ripples that fail the build if skipped

- `Openapi_snapshot_in_docs_site_matches_the_served_document` pins
  `docs-site/docs/api/openapi/v1.json` to the served document, and `test_easydocs_mcp.py` builds the
  server from that snapshot. The snapshot is regenerated in the same commit or both suites fail.
- `EXPECTED` in `test_easydocs_mcp.py:17` is an exact-set assertion — add `get_version_text`.
- `packages/mcp/README.md`'s tool table, and "seventeen tools" at
  `docs-site/docs/automation-recipes.md:257` → eighteen.

## Testing

**C#** (`tests/EasyDocs.Api.Tests/`):

- a docx version returns its content — upload a `DocxFixtures.Build` docx carrying a unique marker,
  assert the marker comes back in `text`;
- paragraph structure survives — two paragraphs, assert the newline between them and no
  concatenation;
- a PDF version returns 415 with `application/pdf` in the detail;
- a non-member gets 404 and a wrong-role member 403, mirroring the cases `DownloadTests` already
  pins;
- `truncated` is `false` for an ordinary document.

**`DocxText`**: extend the boundary test to assert `'\n'` specifically.

**Python**: `test_tool_set_is_exactly_the_read_surface` covers the new tool once `EXPECTED` names
it — no new test needed.

## Verification before implementation

Run `compare_versions(id=…, from=…, to=…, format="html")` against the live server. If it returns
redline markup, this spec is the entire remaining gap. If FastMCP mangles a `text/html` body, that
is a second finding to fold in before anyone writes code.
