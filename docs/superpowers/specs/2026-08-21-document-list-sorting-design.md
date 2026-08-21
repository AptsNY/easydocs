# Document list sorting — design

**Date:** 2026-08-21
**Status:** Approved, ready for implementation planning
**Touches:** `GET /api/v1/documents`, `Api/Pagination.cs`, `Documents/DocumentEndpoints.cs`,
`Documents/DocumentListProjection.cs`, `web/src/routes/Dashboard.tsx`, `web/src/index.css`

## Problem

The dashboard lists document tiles in the only order the endpoint can produce: ascending by
`(CreatedAt, Id)`, because `ListDocuments` calls `Pagination.PageAsync(..., descending: false)` with
no way to ask for anything else. There is no sort control in the UI and no `sort` or `order`
parameter on the API. A library therefore shows its oldest documents first and its most recently
worked-on documents last — the exact inverse of what someone opening the dashboard is looking for.

The three fields a reader would sort by are also the three the current query cannot sort by:
`UpdatedAt`, `CurrentNumber`, and `VersionCount` are all computed by `DocumentListProjection.BuildAsync`
*after* the page has already been selected, so they exist only for the 25 rows that keyset
pagination happened to return.

## Scope

In: sorting the document tiles on the dashboard and folder views by **last updated**, **name**, and
**date created**, each in both directions.

Out: sorting the version list inside a document (`History` already sends `order=desc` and the
endpoint already accepts asc/desc); sorting by version count; a sort control on the trash view.

## Decisions

### API surface

`GET /api/v1/documents?folderId=&q=&trashed=&sort=&order=&cursor=&limit=`

| Param | Values | Default |
| --- | --- | --- |
| `sort` | `created`, `updated`, `name` | `created` |
| `order` | `asc`, `desc` | `asc` |

The defaults preserve today's behavior exactly, so no existing API client and no conformance test
changes. `order` keeps `Pagination.Descending`'s existing leniency — anything that is not `desc`
reads as ascending.

An unrecognized `sort` returns **400 problem+json**, not a silent fallback. Unlike `order`, `sort`
changes *which key the cursor encodes*; a typo silently paging against the wrong column is a worse
outcome than an error.

### Cursor

Current format is `base64url(8B UtcTicks ‖ 16B Guid)` — a fixed 24 bytes whose key is always
`CreatedAt`. New format:

```
base64url( 1B sortTag ‖ 2B keyLen ‖ keyBytes ‖ 16B Guid )
```

`keyBytes` is 8 bytes of ticks for `created` and `updated`, and `utf8(lower(name))` for `name`.

`Decode` continues to recognise `length == 24` as the legacy `created` form, so a cursor already in
a client's hand keeps working. `/api/v1` is a published surface; three lines buys not breaking it.

The `sortTag` exists so that a cursor minted under one sort and replayed under a different `sort` is
**detected rather than misinterpreted** → 400. The web UI resets the cursor whenever the sort
changes, so this guard is for API clients.

### Pagination helpers

`Pagination.PageAsync` gains **two concrete overloads** — one keyed on `DateTimeOffset`, one keyed on
`string` — rather than one version generic over `IComparable`.

Rationale: a generic keyset row-value comparison cannot be written with C# operators inside an
expression tree. It needs hand-built `Expression.GreaterThan` for the value types plus an entirely
separate `string.Compare(a, b) > 0` path for strings (which is what Npgsql translates to `>`). The
"general" implementation is therefore both longer than two typed overloads and considerably harder
to read.

`IKeyed` and the existing three-argument `PageAsync` are left untouched — eight other endpoints page
through them and none of them need a sort key.

### Query changes in `ListDocuments`

- **`updated`** moves out of `DocumentListProjection` and into the query as a correlated subquery,
  `db.Versions.Where(v => v.DocumentId == d.Id).Max(v => v.CreatedAt)`, **coalesced to `d.CreatedAt`**
  when the document has no versions. Two reasons: a `NULL` cannot participate in a keyset row-value
  comparison (comparisons against it evaluate to unknown, so those rows would silently vanish from
  every page), and "a document with no versions was last touched when it was created" is true rather
  than a sentinel. The tile still *renders* `updatedAt: null` as "No versions yet" — only the sort
  key coalesces, and `DocumentListProjection`'s output shape is unchanged.
- **`name`** sorts on `d.Name.ToLower()`, which Npgsql emits as `lower(name)`, so `apple` sorts
  before `Zebra` instead of after every capital letter in the library. The cursor key uses the
  identical expression; if the two ever diverge, paging skips or repeats rows.
- **`created`** is the existing key and needs no query change beyond threading `descending` through.
- `q`, `folderId` and `trashed` filtering is untouched. They filter; this sorts the result.

A `ponytail:` comment records the deliberate ceiling: there is no index supporting either
`lower(name)` or the per-document max-version aggregate, so both sorts are a sequential scan plus a
sort. The upgrade path — a denormalised `Document.UpdatedAt` maintained on the version write path,
plus `(OrgId, UpdatedAt, Id)` and `(OrgId, lower(Name), Id)` indexes — is deliberately **not** built,
because an organization's library is hundreds of rows and the migration would add a permanent
write-path invariant to maintain.

### Web UI

One native `<select>` in `.docs-tools`, immediately after the search form, with a
`.visually-hidden` label following the pattern the search input already uses. One control and one
state value, no JavaScript beyond the change handler, focusable and operable from the keyboard for
free.

| Label | Value |
| --- | --- |
| Last updated | `updated:desc` |
| Oldest updated | `updated:asc` |
| Name A–Z | `name:asc` |
| Name Z–A | `name:desc` |
| Newest first | `created:desc` |
| Oldest first | `created:asc` |

State lives in the URL through `useSearchParams` as `?sort=updated&order=desc`. That survives a
reload, works with back/forward, makes the view shareable as a link, carries across folder
navigation, and makes the e2e assertion trivial.

Absent params read as `updated:desc`, and the UI always sends the pair explicitly — the same
arrangement `History` already uses, where the web client opts into `order=desc` while the API's own
default stays ascending for the conformance suite. **The default dashboard view therefore becomes
last-updated-first.**

`sort` and `order` join `load`'s `useCallback` dependency list, so changing the select refetches from
`cursor: null` and *replaces* the tile array instead of appending to it. The existing `q` path
already behaves exactly this way; no new mechanism.

The **trash view gets no sort control**. Its toolbar branch renders explanatory prose rather than
`.docs-tools`, and it is a recovery view reached by people looking for one specific document. The
API supports sorting it; the UI simply does not ask.

CSS: one rule sizing the select alongside `.docs-tools .search input`, plus the corresponding entry
in the existing mobile breakpoint.

## Tests

**`tests/EasyDocs.Api.Tests/DocumentListTests.cs`**

- each of the three sorts, in both directions, returns the expected order
- a sorted list stays correctly ordered *across cursor pages* — five documents read at `limit=2`
- `sort=bogus` returns 400
- a cursor minted under `sort=name` and replayed under `sort=updated` returns 400
- a legacy 24-byte cursor still pages correctly
- a document with no versions sorts by its own `CreatedAt` under `sort=updated`, and is not dropped
- `apple` precedes `Zebra` under `sort=name&order=asc`

**`tests/EasyDocs.Api.Tests/PaginationTests.cs`**

- the two new overloads directly, including an encode/decode round trip for a name containing
  multibyte characters

**`web/e2e/dashboard.spec.ts`**

- choosing a sort reorders the tiles, asserted off `[data-testid="document-tile"]`'s `data-name`
- the URL carries `sort` and `order`
- a reload preserves the chosen order

## Rejected alternatives

**Offset pagination for sorted views** (`?sort=name&offset=25`, keyset left as the default path).
Roughly fifteen lines instead of the cursor rework. Rejected because a concurrent upload makes
"Load more" duplicate or skip a tile, and because it would leave one endpoint with two pagination
modes in a spec (§10) that commits to cursor pagination as a convention. The smaller diff is bought
by letting the list occasionally misreport its own contents.

**Denormalising `Document.UpdatedAt` with supporting indexes.** The right answer at a scale this
product is not at. Deferred, recorded as the `ponytail:` upgrade path above.

**Client-side sorting of the loaded pages.** Wrong for the same reason `Dashboard.tsx` already gives
for doing search server-side: it would only ever reorder the rows that happen to have been fetched.

**Clickable column headers.** The most familiar sorting affordance, but it means replacing the tile
grid with a table — a much larger change than adding sorting.
