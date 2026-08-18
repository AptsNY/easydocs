# Automation recipes

Everything the web UI does, the API does — it is the same surface, not a subset. This page walks one
document through its whole lifecycle with `curl`, using a personal access token.

## Where the API reference lives

These recipes are worked examples, not a reference. The reference is generated from the running build:

- **[API reference](api/index.md)** on this site — a committed snapshot of the document below,
  guarded by CI so it cannot drift from what the application serves.
- **Interactive docs:** **`/docs`** on your install — e.g. <http://localhost:8080/docs>. Self-contained,
  no external CDN, and "try it out" works there.
- **OpenAPI 3.1 document:** **`/openapi/v1.json`** — feed it to a client generator, Bruno, Insomnia, or
  anything else that speaks OpenAPI.

!!! bug "Known defect: five request bodies share one schema name in the generated document"
    In the current build, the request-body schemas for **`POST /api/v1/documents`**,
    **`POST /api/v1/folders`**, **`POST /api/v1/versions/{vid}/share-links`** and
    **`POST /api/v1/versions/{vid}/copies`** are wrong in `/openapi/v1.json`.

    Five separate C# records are all named `CreateRequest`, and the OpenAPI generator collapses them into
    a single `#/components/schemas/CreateRequest` component. The one that wins is the token one, so all
    five endpoints are documented as taking `{ "name": …, "scopes": [...], "expiresAt": … }`.

    **The bodies on this page are read from the endpoint source and are correct.** Where they disagree
    with `/openapi/v1.json`, trust this page. Response schemas and every other endpoint are unaffected.
    Tracked for a fix; until then treat those four request bodies as documented here.

## Authentication

Two credentials, same surface:

- **`Authorization: Bearer ed_…`** — a personal access token. Use this for automation.
- **the `ed_session` cookie** — what the browser uses. Also what server-sent events require, since
  `EventSource` cannot set headers.

A token **never exceeds the role of the user who minted it**. There is no way to create a token more
powerful than yourself.

### Mint a token

Tokens are minted by an authenticated user, so start with a login to get a session cookie, then use it
once to create the token.

```bash
BASE=http://localhost:8080

# 1. Log in, keeping the session cookie.
curl -sS -c cookies.txt -X POST "$BASE/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"you@example.com","password":"your-password"}'

# 2. Mint a PAT. The raw token is returned EXACTLY ONCE.
curl -sS -b cookies.txt -X POST "$BASE/api/v1/tokens" \
  -H 'Content-Type: application/json' \
  -d '{"name":"release-bot","scopes":[],"expiresAt":null}'
# {"id":"…","token":"ed_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"}

export ED_TOKEN=ed_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
```

Only a SHA-256 hash of the token is stored, so it cannot be recovered. Lost it? Revoke
(`DELETE /api/v1/tokens/{id}`) and mint another.

!!! warning "`scopes` is recorded but not enforced in v1"
    You must send the field, and it is stored and echoed back by `GET /api/v1/tokens` — but **nothing
    checks it.** A token's authority is exactly its owner's per-document role, no more and no less.
    Sending `["read"]` does **not** produce a read-only token. Do not build a permission model on it.
    Send `[]` and treat every token as full-authority-of-its-owner until scope enforcement lands.

`POST /api/v1/tokens` is rate-limited to **20 per 60 seconds per user**.

From here on, every call uses the token:

```bash
AUTH="Authorization: Bearer $ED_TOKEN"
```

## The full lifecycle

### 1. Create a document

```bash
DOC=$(curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/documents" \
  -d '{"name":"Supply Agreement","folderId":null}' | jq -r .id)
```

Body is `{ "name": string?, "folderId": uuid? }`. Pass `folderId` to file it; `null` leaves it at the
root. (Create folders with `POST /api/v1/folders`, body `{ "name": string?, "parentId": uuid? }`.)

### 2. Upload the first version

Multipart, with the file in a field named **`file`**:

```bash
V1=$(curl -sS -H "$AUTH" \
  -X POST "$BASE/api/v1/documents/$DOC/versions" \
  -F 'file=@./supply-agreement.docx' | jq -r .versionId)
```

Response is `{ "versionId": …, "major": 0, "minor": 0, "revision": 1 }` — the first version is always
**`0.0.1`**.

`POST /api/v1/documents/{id}/versions:import` is the same call with a different provenance recorded on
the version.

!!! tip "Retrying an upload is safe"
    A sessionless upload de-duplicates against **the current main-line head's sha256**. So immediately
    re-POSTing the same file — the timeout-and-retry case — returns the existing version rather than
    creating a duplicate, and the response carries the same `versionId`.

    Note the exact scope: it compares against the head only, not the whole history. Upload A, then B,
    then A again and you *do* get a new version, because A is no longer the head. That is intended —
    re-instating earlier content is a real change.

### 3. Upload a second version and read the change summary

```bash
V2=$(curl -sS -H "$AUTH" \
  -X POST "$BASE/api/v1/documents/$DOC/versions" \
  -F 'file=@./supply-agreement-rev2.docx' | jq -r .versionId)

curl -sS -H "$AUTH" \
  "$BASE/api/v1/documents/$DOC/compare?from=$V1&to=$V2&format=summary"
# {"insertions":14,"deletions":3,"moves":0,"formatChanges":0}
```

!!! note "No polling needed — and `moves`/`formatChanges` are always 0"
    A background worker computes the summary eagerly after each save, but **`format=summary` computes it
    inline if the cache is not populated yet**. One call always returns real numbers; there is no
    pending state and nothing to poll for.

    `moves` and `formatChanges` are **hard-coded to `0`**. The comparison engine classifies insertions
    and deletions only: a move is reported as a deletion plus an insertion, and formatting-only edits are
    not counted. Do not treat those two fields as measurements.

The same endpoint gives you the redline itself:

```bash
# Redline as HTML (computed on first request, then cached permanently)
curl -sS -H "$AUTH" \
  "$BASE/api/v1/documents/$DOC/compare?from=$V1&to=$V2&format=html" -o redline.html

# Redline as a .docx with tracked changes
curl -sS -H "$AUTH" \
  "$BASE/api/v1/documents/$DOC/compare?from=$V1&to=$V2&format=docx" -o redline.docx
```

`format` defaults to `summary`. If the comparison engine cannot handle the documents, `summary` and
`docx` return `422 Comparison unavailable` and `html` returns a plain "Comparison unavailable." message
(it is meant to be displayed, not parsed) — never a 500.

!!! warning "`0/0` means no changes, and only that"
    A `200` with `{"insertions":0,"deletions":0,…}` means the two versions were compared successfully and
    their text is identical — a common result when a file is re-saved without an edit. A comparison that
    *failed* is the `422` above, never zeros. Check the status code, not the counts.

### 4. Publish

```bash
curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/versions/$V2/publish" \
  -d '{"kind":"minor","name":"Counterparty draft"}'
```

`kind` must be **`minor`** or **`major`** — anything else is a `400`. `name` is an optional publish
label. Minor takes the counter to `X.(Y+1).0`, major to `(X+1).0.0`, and the **version you selected** is
renumbered; it does not have to be the newest one.

Publishing also queues a **PDF render**. It is asynchronous, so poll for it if you need the file:

```bash
until curl -fsS -H "$AUTH" \
    "$BASE/api/v1/versions/$V2/download?format=pdf" -o released.pdf; do
  echo "PDF not ready yet…"; sleep 2
done
```

`?format=pdf` on an **unpublished** version returns `409` — no PDF exists, and easydocs will not render
one on the fly and imply it is a release artifact. `?format=docx` (the default) always works.

List everything published with `GET /api/v1/documents/{id}/publications`.

### 5. Request approval

Approvers must **already be members of the document**, so add them first if needed. Note that
`POST …/members` takes an **`email`**, not a user id, and requires that you are an **Owner** of the
document:

```bash
curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/documents/$DOC/members" \
  -d '{"email":"reviewer@example.com","role":"Viewer"}'
# {"userId":"…","email":"reviewer@example.com","role":"Viewer"}
```

`role` must be `Owner`, `Editor` or `Viewer`. If that email already belongs to a member of your
organization the membership is granted immediately and the response carries their `userId`; if not, an
invitation is created instead. Adding someone who is already a member returns `409`.

The approval request itself takes **user ids**:

```bash
# NB: the members list is a bare JSON array, not a paginated {items:[…]} envelope.
REVIEWER=$(curl -sS -H "$AUTH" "$BASE/api/v1/documents/$DOC/members" \
  | jq -r '.[] | select(.email=="reviewer@example.com") | .userId')

curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/versions/$V2/approvals" \
  -d '{"approverIds":["'"$REVIEWER"'"],"dueAt":"2026-08-15T17:00:00Z"}'
```

Body is `{ "approverIds": uuid[], "dueAt": timestamp? }`. One row is created **per approver**, each
returned with its own `id`. Requirements, all enforced:

- the version must be **published** (`400 Not published` otherwise);
- `approverIds` must be non-empty;
- every id must already be a **member of the document** (`400` otherwise).

The approver then decides, once and immutably:

```bash
curl -sS -H "$AUTH_REVIEWER" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/approvals/$APPROVAL_ID:respond" \
  -d '{"decision":"approved","comment":"Clause 7 is fine as drafted."}'
```

Poll a worklist with `GET /api/v1/approvals?filter=assigned&status=open` (`filter` is `assigned` or
`requested`; `status` is `open` or `closed`). The requester can withdraw an undecided request with
`POST /api/v1/approvals/{id}:cancel`.

### 6. Share a version

```bash
curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/versions/$V2/share-links" \
  -d '{"expiresAt":"2026-09-01T00:00:00Z"}'
# {"token":"kQ8…","url":"/s/kQ8…"}
```

Body is just `{ "expiresAt": timestamp? }` — `null` for no expiry. The **raw token is returned once**;
only its hash is stored, so it cannot be recovered later. Prefix `url` with your `PUBLIC_BASE_URL` to get
something sendable.

The recipient needs no account. `GET /s/{token}` returns JSON to an API client and the SPA landing page
to a browser, and `GET /s/{token}/download` streams the `.docx`. Both are rate-limited per client IP
(120/min and 30/min respectively). Every view increments a counter and writes an audit row.

To revoke, you need the link's **id**, which the create call does not return — list them:

```bash
curl -sS -H "$AUTH" "$BASE/api/v1/documents/$DOC/share-links"
# {"items":[{"id":"…","versionId":"…","versionNumber":"0.1.0","createdByName":"…",
#            "createdAt":"…","expiresAt":"…","revokedAt":null,"viewCount":3}],"nextCursor":null}

curl -sS -H "$AUTH" -X DELETE "$BASE/api/v1/share-links/$LINK_ID"
```

The list includes revoked and expired links, flagged by `revokedAt` / `expiresAt` — "did I revoke that?"
is one of the questions it exists to answer. It never returns the token or its hash. Either the creator
or an Editor of the document may revoke.

## Other useful calls

```bash
# Who am I, and which org is this session bound to?
curl -sS -H "$AUTH" "$BASE/api/v1/me"

# Version history, newest first, cursor-paginated. Rows carry branch identity
# (branchId, branchKind, branchOrdinal, branchMergedIntoVersionId).
curl -sS -H "$AUTH" "$BASE/api/v1/documents/$DOC/versions?order=desc&limit=50"

# Search documents by name
curl -sS -H "$AUTH" "$BASE/api/v1/documents?q=supply"

# Revert: appends a new version equal to an older one; history is untouched
curl -sS -H "$AUTH" -X POST "$BASE/api/v1/versions/$V1/revert"

# Merge a concurrent branch into main
curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/documents/$DOC/merges" \
  -d '{"left":"'"$MAIN_HEAD"'","right":"'"$BRANCH_HEAD"'"}'

# Fork a version into an isolated copy for external review
curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X POST "$BASE/api/v1/versions/$V2/copies" \
  -d '{"name":"Counterparty review copy"}'

# Manual version-counter override (R5) — note the field is `rev`, not `revision`
curl -sS -H "$AUTH" -H 'Content-Type: application/json' \
  -X PUT "$BASE/api/v1/documents/$DOC/version-counter" \
  -d '{"major":2,"minor":4,"rev":0}'

# The audit trail
curl -sS -H "$AUTH" "$BASE/api/v1/documents/$DOC/audit"
```

## Live updates

```bash
curl -N -b cookies.txt "$BASE/api/v1/documents/$DOC/events"
```

Server-sent events. Authorized by the **session cookie** or a short-lived `?token=` capability
parameter — native `EventSource` cannot send an `Authorization` header, which is why a `Bearer ed_…` is
not the credential here.

Events: `version.created`, `version.published`, `merge.completed`, `diff.ready`, `member.added`,
`push.requested`, `push.reviewed`, `approval.responded`, `pdf.ready`, `version.named`,
`version.reverted`.

If you proxy easydocs, make sure response buffering is off or this stream will stall — see
[Self-hosting](self-hosting.md#nginx).

## Conventions

- **Pagination** is cursor-based: list endpoints return `{ "items": [...], "nextCursor": … }`. Pass
  `?cursor=` to continue, `?limit=` to size the page. A `null` `nextCursor` means the end.
- **Errors** are RFC 7807 `application/problem+json`, everywhere, including rate-limit rejections.
- **Rate limits** return `429` with a `Retry-After` header. Honour it.
- **No `Idempotency-Key` support.** Version upload is naturally idempotent via sha256 de-duplication;
  other mutations are low-frequency. Do not blind-retry a publish or an approval request.
- **Timestamps** are ISO 8601, UTC. Send UTC — a non-UTC offset is normalized, but do not rely on it.

## Error responses worth handling

| Status | When |
|---|---|
| `400 Not published` | Requesting approval on an unpublished version. |
| `400` | `kind` not `minor`/`major`; empty `approverIds`; an approver who is not a document member; a negative version counter; a non-multipart or empty upload body. |
| `403` | You are not a member of the document. Organization role grants **no** document access. |
| `404` | Also returned instead of `403` where existence itself is sensitive, and for an unknown, revoked, or expired share token. |
| `409` | PDF requested for an unpublished version; a merge the comparison engine could not produce. |
| `422` | A redline `.docx` could not be produced. |
| `429` | Rate limited. Check `Retry-After`. |
