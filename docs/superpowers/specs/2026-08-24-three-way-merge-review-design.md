# Three-way review before merging — design

**Date:** 2026-08-24
**Status:** Approved, ready for implementation planning
**Touches:** `Merging/MergeEndpoints.cs`, `Merging/MergePreviewService.cs` (new),
`Merging/ThreeWayOverlap.cs` (new), `web/src/routes/MergeReview.tsx` (new),
`web/src/routes/History.tsx`, `web/src/App.tsx`, `web/src/api.ts`, the OpenAPI snapshot,
`CHANGELOG.md`

## Problem

Merging a concurrent branch is a single unconfirmed click. `History.tsx` renders a **Merge** button on
every unmerged concurrent branch group; it posts `{left: mainHead, right: branchHead}` to
`POST /api/v1/documents/{id}/merges` and the version lands immediately. The absence of a confirmation
step was deliberate and is recorded in the component:

> `// Merge takes main's head and this branch's head and lands a tracked-changes version on main.`
> `// Nothing is discarded, so there is no confirmation step to add.`

That reasoning is sound about *safety* — ADR-1 means nothing is destroyed, and the merge is itself just
another version that can be reverted. It is wrong about *informedness*. Before clicking, the user
cannot see:

- what the **other** side did since the fork (the merge shows only the incoming author's edits, as
  tracked changes on top of current main),
- whether both authors touched the same text,
- what the merge will even produce.

The result is a click made blind, whose output then has to be read in an editor to find out what
happened. For the two-people-edited-the-same-lease case the product exists to serve, that is the
moment the user most needs to see all three documents at once.

Note what already exists, because it is adjacent to this and none of it closes the gap:

- `GET /api/v1/documents/{id}/compare?from=&to=` renders a redline between **any** two versions, in
  `summary`, `html`, or `docx` form, cached by `(from_sha, to_sha)`.
- `Compare.tsx` is a full screen for that, with the sandboxed-iframe rendering pattern.
- `Branch.RootVersionId` **already holds the fork point**, written by `CommitSaveAsync` when the
  concurrent branch is created.

So the base is in the database and the comparison engine is in place. What is missing is a screen that
puts all three together, and an endpoint that answers "what would this merge collide with?".

## Scope

In: a read-only preview endpoint, a review screen the Merge button routes through, and a best-effort
paragraph-level overlap hint.

Out: **any change to what a merge commits**. No three-way fuse of both authors over the ancestor, no
conflict resolution UI, no gating of the API, no change to the copy push-back accept/reject flow on
the Copies tab.

That exclusion is the point of the design, not a corner cut from it. `WmlComparerMergeService` carries
the heaviest test coverage in the repo, and its own `ponytail:` comment names the true three-way fuse
as the deferred §5.3 enhancement. A read-only review delivers the informedness without touching that
code at all, and can be built and shipped while the fuse stays deferred.

## Decisions

### `GET /api/v1/documents/{id}/merges/preview?left=&right=`

A `GET`, not a `?dryRun=true` on the existing `POST`. The preview is a pure read — cacheable,
revisitable, linkable — and making a read look like a write to save one route entry is a bad trade.
`/merges/preview` as a sub-resource rather than the house colon-action convention (`{id}:restore`,
`versions:import`) because that convention marks *actions*, which are `POST`s; this is a noun.

Authorization is Editor+, resolved through the same `DocumentAuthorization.ResolveAsync` chokepoint as
`POST /merges`. A preview of a write requires the write's role — a Viewer who cannot merge has no
business enumerating what a merge would collide with.

```jsonc
{
  "available": true,
  "base":     { "id": "…", "number": "0.0.2" },
  "main":     { "id": "…", "number": "0.0.5", "authorName": "Ana",
                "summary": { "insertions": 12, "deletions": 3 } },
  "incoming": { "id": "…", "number": "0.0.4", "authorName": "Ben",
                "summary": { "insertions": 7, "deletions": 9 } },
  "overlaps": [ { "ordinal": 14, "text": "Rent. Tenant shall pay $2,200 per month…" } ]
}
```

**Every field degrades independently**, and this is the part most likely to be got wrong by treating
"available" as one boolean:

| Field | Null / false means | Merge still possible? |
| --- | --- | --- |
| `available` | `main ↔ incoming` could not be compared | **No** — the merge would 409 |
| `base` | fork point unknown | Yes — two-way preview only |
| `main.summary` | `base → main` compare failed | Yes |
| `incoming.summary` | `base → incoming` compare failed | Yes |
| `overlaps` | either base leg failed, so no intersection | Yes |

`available` is the only one that disables the Merge button, because it is the only one that predicts
failure: the merge runs the same `WmlComparer.Compare(mainHead, incoming)` on the same bytes, so if
the preview could not compare that pair, neither will the merge. Letting the user click into a
guaranteed 409 is worse than saying so up front.

`404` and `403` mirror `POST /merges` exactly. `409` only when the merge could not be *attempted* at
all — no identifiable incoming branch, no main head — carrying the same title and detail the `POST`
would have returned, so the screen shows the real message before the click rather than after.

`base: null` is reachable but rare. `CommitSaveAsync` creates a `Concurrent` branch only when
`BaseVersionId` is present, and sets `RootVersionId` from it — so for the branches this screen is
reached from, the fork point always exists. The null path is a guard for `IncomingPush` branches and
any legacy row, and it is built rather than asserted away because a dead guard is cheaper than a
`NullReferenceException` on the headline feature.

### The overlap hint

The genuinely new logic, and the only part that can be wrong. It lives in `ThreeWayOverlap.cs` as a
**pure function over two compared `WmlDocument`s** — no database, no blob store, no host — so the
fiddly part is unit-testable against fixture `.docx` files directly.

```
TouchedBaseParagraphs(compared) → Set<int>
  ordinal = 0
  for each w:p in word/document.xml:
      if the paragraph MARK is inserted (w:pPr/w:rPr/w:ins):
          continue                       # this paragraph never existed in the base
      if p contains any w:ins or w:del:
          add ordinal
      ordinal += 1
```

Overlap = `Touched(base→main) ∩ Touched(base→incoming)`.

**Why the ordinal is the right anchor.** The obvious approach — align paragraphs by index across the
two redlines — breaks the moment one side inserts a paragraph, because every index after it shifts.
Skipping the paragraphs that did not exist in the base leaves a count that *is* the base document's own
paragraph numbering. Both comparisons were run against the same base, so the two numberings are
identical by construction.

**How "did not exist in the base" is decided, and how it was decided wrongly first.** The shipped test is
the paragraph MARK being inserted (`w:pPr/w:rPr/w:ins`) — direct evidence from WmlComparer, not an
inference. The first implementation inferred it from content instead: *has insertions and no surviving
base text*. Those read as equivalent and are not. An empty base paragraph — a spacer, a blank line under
a heading — that an author types into satisfies the content test while having existed in the base all
along; skipping it desynced every ordinal below. The symptom was the worst available: an overlap named
on a clause **neither** author had touched. Caught in review, pinned by
`Filling_an_empty_base_paragraph_does_not_invent_an_overlap`.

The rejected alternative was matching on reconstructed base paragraph **text**. It fails on documents
this product is built for: real leases repeat `"Intentionally omitted."` and blank numbered clauses a
dozen times, and every repetition would be a false positive. The ordinal has no such failure mode.

Each hit is labelled with the reconstructed base text of that paragraph — `w:t` outside `w:ins`, plus
`w:delText`, in document order — truncated to 60 characters. Real text rather than a parsed clause
number (`§4 Rent`), because a large share of the corpus has no clause numbering to parse and a label
that works for half the documents is worse than one that works for all of them.

**Known ceiling, to be marked with a `ponytail:` comment.** A paragraph *split* on one side — someone
pressing Enter mid-paragraph — shifts that side's ordinals by one from the split down, so overlaps
after it can be missed or attributed to the neighbouring paragraph. This is why the feature ships
worded as a hint and never as a guarantee, and why it does not block the merge. Upgrade path if it
matters: anchor on `w14:paraId` (present in the compared XML) where the attribute exists, falling back
to the ordinal where it does not.

### Wording, and why it is not a gate

The panel reads *"Both authors changed these paragraphs"*, followed by *"A hint, not a guarantee —
body-text paragraphs only. Review the redlines below."* It never blocks the Merge button.

This follows the hedging `Compare.tsx` already does, for the same reason it does it there — the engine
diffs body text only, so headers, footers, and pure formatting changes are invisible to it:

> `// "No changes" would overclaim: the engine diffs body text only, and two versions can`
> `// differ purely in formatting (highlights) or in headers/footers — real leases do.`

Making the hint a gate was considered and rejected: a heuristic with a known false-negative mode
(paragraph splits) cannot be load-bearing. A false positive would block a legitimate merge, and a
false negative would grant false comfort — the acknowledgement checkbox would teach users that an
absent warning means "no conflicts", which is precisely what this cannot promise.

### The review screen

New route `/documents/:id/merge?left=&right=`, a sibling of `/documents/:id/compare` — which is
already a standalone route rather than a console tab, so this follows the established shape.

`History.tsx`'s Merge button becomes a `<Link>` to it. `BranchGroup` loses its `canMerge`/`onMerge`
props and the `merge` callback goes with them; the POST moves to the review screen. Everything else
about the history list is unchanged.

The screen makes **one** call to the preview endpoint for structure and counts, then fetches the two
redlines from the existing `/compare?from={base}&to={X}&format=html` endpoint — no new rendering code.
Both are displayed in iframes reusing `Compare.tsx`'s `sandbox=""` and `REDLINE_STYLE` pattern
verbatim, including the reason that pattern exists: the markup is generated from a user-uploaded
`.docx` and is untrusted, so inlining it would be an XSS path through the headline feature.

Redline fetches are **not** free, and an earlier draft of this section claimed they were. `version_diffs`
is keyed by `(from_sha, to_sha)`, but the cache-first read lives in `DocumentEndpoints.Compare`, not in
`WmlComparerDiffService.SummaryAsync` — and the eager `DiffSummaryWorker` never fills the html/redline
pointers at all, as that service's own comment says. So the first review of a branch costs several full
comparisons plus blob writes. The `ponytail:` comment in `MergePreviewService` carries the accepted
ceiling and the ordered upgrade path.

On success the merge navigates back to the document console, where the merged branch now renders as
*"Merged into the main history."* — the existing behaviour, reached by an existing SSE tick.

### What the API keeps

`POST /api/v1/documents/{id}/merges` is **unchanged** — same shape, same auth, same semantics, no new
required parameter, no preview token. Scripts that merge on purpose keep merging on purpose, and the
E04 conformance test does not move.

This is a deliberate reading of ADR-10. "Everything the UI does, the API does" means the API gains the
*preview* capability; it does not mean the API inherits a UI's confirmation flow. A gate on the POST
would break every existing client to protect automation that chose to automate.

## Tests

**`tests/EasyDocs.Api.Tests/ThreeWayOverlapTests.cs`** (new — pure, no host, no database)

- a paragraph touched in both comparisons is reported
- a paragraph touched in only one is not
- a paragraph inserted by one side does not shift the other side's ordinals (the regression the
  ordinal anchor exists to prevent)
- wholly-inserted paragraphs are skipped rather than numbered
- a document with repeated identical paragraph text produces no false positive — the case that
  rejected text matching
- the label is the reconstructed base text, so a paragraph whose text was deleted still labels
  from its base content, not from an empty string

**`tests/EasyDocs.Api.Tests/MergePreviewTests.cs`** (new)

- Editor gets a preview; Viewer gets `403`; a same-org non-member gets `403`; a cross-org document
  is `404` (no existence leak) — mirroring `DocumentAuthorization` and `POST /merges` exactly
- an unknown document is `404`; a version id belonging to **another** document is `409`, because it
  fails at side-resolution rather than at authorization
- the fork point returned is the branch's `RootVersionId`, not the parent of the branch's oldest row
- `base: null` when `RootVersionId` is null, with `available` still true and the Merge button live
- each leg degrades independently: an uncomparable `base→main` nulls `main.summary` and `overlaps`
  while leaving `available: true`
- an uncomparable `main ↔ incoming` yields `available: false`
- the preview writes **no** version and **no** audit row — previewing is not an action on the document

**`web/e2e/merge-review.spec.ts`** (new)

- the Merge button navigates to the review rather than committing; the branch is still unmerged after
  the click
- the review shows both sides' counts and both redlines
- Merge commits and returns to the console with the branch marked merged
- Cancel returns to the console with nothing committed

**Unchanged, and asserted to be unchanged:** `MergeTests.cs`, `PushMergeTests.cs`, and conformance
`E04_BranchMerge.cs`. If any of them needs an edit, the scope boundary has been crossed.

## Rejected alternatives

**Preview the merge output only** (`main → incoming` redline, confirm/cancel). The smallest possible
change, and it is what "preview before merge" usually means. Rejected because it is not three-way: it
shows the incoming author's edits and says nothing about what main did since the fork, which is the
half the user cannot currently see and the half that determines whether the merge is safe.

**A true three-way merge** — fuse both authors over the ancestor with conflict resolution. The
deferred §5.3 enhancement, and the eventual right answer. Rejected *for now* because it changes what
merge commits, in the most heavily-tested code in the repo, to solve a problem that a read-only screen
solves without risk. This design deliberately leaves that door open: it introduces the base into the
UI and the API without committing to a merge semantics change.

**Extending `/compare` with a `base` parameter** instead of a new endpoint. Saves a route. Rejected
because `/compare` is a clean two-way primitive that is conformance-tested, documented, and consumed
by a screen that has earned its current shape; adding a mode that changes the response body is how a
good endpoint becomes a Swiss army knife. Adding a noun is cheaper than overloading a verb.

**Computing the overlap in the browser** from the two redline HTMLs. Genuinely the least backend code.
Rejected because it does not work: `WmlComparerDiffService.RenderHtml` appends every `w:delText` to
the **end** of its paragraph, after all `w:t` content, so base paragraph text cannot be reconstructed
in document order from the HTML. It would also make the hint invisible to API consumers, which is the
outcome ADR-10 exists to prevent.

**Routing copy push-back acceptance through the same review.** More consistent on its face. Rejected
because push acceptance is already a two-person review with an explicit reject path (`E09`), so
gating it a second time adds ceremony rather than information — and it would put a second endpoint and
a second conformance area in scope for no gain.
