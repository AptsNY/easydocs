# Changelog

All notable changes to easydocs are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and easydocs aims to
follow [Semantic Versioning](https://semver.org/) from `v1.0.0` onward.

**Nothing has been released yet.** There is no `v1.0.0` tag. Everything below is grouped by the
development milestone that produced it (M0–M5, spec §13) rather than by release, and it is all
`[Unreleased]`. Milestones are summaries — the substantive per-milestone record, including what each
plan got *wrong* about the code, is in [`docs/superpowers/plans/`](docs/superpowers/plans/).

A note on the two numbering schemes, because this project is about version numbers: the versions in
this file are **easydocs releases**. The `0.0.1` / `0.1.0` / `1.0.0` numbers that appear in feature
descriptions are **document** versions, produced by the versioning engine. They are unrelated. See
[GOVERNANCE.md](GOVERNANCE.md#versioning-the-products-version-is-not-a-documents-version).

## [Unreleased]

Everything in easydocs. Milestones M0 through M4.5 are merged; M5 is in progress and ends in the
signed `v1.0.0` tag that does not exist yet.

### M5 — release hygiene *(in progress)*

#### Security

- **Rate limiting on the anonymous and credential surfaces** (spec §11). Four named policies applied
  per endpoint, never globally, so static assets and the SPA shell stay unmetered: the public share
  viewer and the public download are limited per client IP and separately from each other (a page view
  costs a row read, a download streams a multi-MB `.docx`); `auth/login` and `auth/register` share a
  token-bucket policy but get independent buckets so a registration flood cannot 429 every legitimate
  login; PAT creation is limited per authenticated user. Rejections are `429` with `Retry-After` and an
  RFC 7807 body. Every value is tunable under `RateLimit:<Policy>:*`.
- **Argon2id hashes are stored in PHC string format** (`$argon2id$v=19$m=…,t=…,p=…$salt$hash`).
  Verification re-derives with the parameters read out of the stored digest rather than with the
  compile-time constants, so the cost can be raised later without invalidating existing hashes — under
  the previous `{salt}.{hash}` encoding the same edit would have locked out every user. M0-era rows
  still verify against the frozen original parameters. Parsing is bounded and fails closed, so a
  corrupt column cannot OOM the process or `500` the login route.
- **Unique index on `ApiTokens.TokenHash`**, which backs every `ed_` PAT authentication.
- **Share links are listable, so revocation is actually reachable.** `DELETE /share-links/{id}` existed
  but creating a link returned only its token and URL, never its id, and nothing enumerated them — so
  no client could call the delete. Added `GET /documents/{id}/share-links`, plus view counts.
- **The token list is scoped to the caller.** A member could enumerate colleagues' token names, scopes
  and last-used times. One predicate now governs both the list and the delete; org-level service-account
  tokens (no owning user) remain visible to Owner/Admin.

#### Documentation

- **MkDocs Material documentation site** under `docs-site/`: getting started, concepts, a user guide,
  automation recipes, and a self-hosting/operator guide covering TLS, the `.env` contract, forwarded
  headers behind a proxy, rate-limit tuning, backup and restore, upgrading, and the log-level footgun.
- **Governance and community files**: `SECURITY.md` (private disclosure, supported versions, what is
  hardened, and the known v1 limitations split by whether they change an operator's risk),
  `GOVERNANCE.md`, `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1), this changelog, GitHub issue forms
  and a pull-request template, and an unambiguous statement of the AGPL-3.0 / MIT split.

### M4.5 — the web UI

The milestone that turned a complete API into a usable product. All eight screens from spec §9:
dashboard, document console, comparison view, Major Versions, copies, approvals, settings, and the
public share landing. The full document lifecycle is doable in a browser without ever touching an HTTP
client, which is what the Playwright suite asserts — **against the shipped container image, not a dev
server.**

#### Added

- **SPA foundation**: React 19 + Vite + react-router, a dev proxy, session handling, and the Playwright
  harness.
- **Dashboard**: folder tree, document tiles carrying version number, modified time and author, search,
  and a listable trash.
- **Document console**: version history with concurrent branches grouped and indented under the main
  line, a Merge button, the members panel, the audit tab, and live updates over SSE.
- **Comparison / redline view**, rendered in a sandboxed iframe.
- **Major Versions tab, copies management, approvals screen, and settings** (profile, API tokens, org
  members).
- **The 8-action Actions menu** — E8's "present and functional": Open in Collabora, Import, Share,
  Download, Name, Publish, Revert, Push To Copy.
- **Public share landing page** at `/s/{token}`: a plain download page for a recipient with no account,
  no app chrome and no sign-up wall. The same URL still returns JSON to API clients.
- **A design and accessibility pass** across all eight screens.
- **The read surface the UI needed**, added beyond spec §10.1 and then reconciled *into* §10.1 in the
  same milestone so spec and code stopped disagreeing: `GET /approvals`,
  `GET /versions/{vid}/approvals`, `GET /documents?trashed=true`, `GET/PATCH /api/v1/org`,
  `GET/POST /api/v1/org/members`, `PATCH/DELETE /api/v1/org/members/{uid}`, actor display names on the
  audit list, and branch topology (`branchId`, `branchKind`, `branchOrdinal`,
  `branchMergedIntoVersionId`) on the version row.

#### Fixed

Six unplanned fixes, four of them defects in already-shipped code, each found because it blocked or
falsified a §9 screen.

- **Approvals privilege escalation.** `POST /versions/{vid}/approvals` never checked that the named
  approvers were document members, and `:respond` authorized on `ApproverId` alone — so **a same-org
  non-member could record an approval decision on a document they could not read.** An E12 role-matrix
  hole. Both ends are fixed and rows written by the vulnerable build cannot be decided either, with a
  regression test.
- **Concurrent identical uploads returned `500`.** The single write path did check-then-insert against
  `Blobs`, whose primary key is the content SHA-256, with no unique-violation guard. Two people
  uploading the same attachment at once is ordinary. Fixed at the one write path, so upload, import,
  WOPI save, merge, revert and copy-push are all covered.
- **`member.added` was never published.** Spec §10.2 listed it as a v1 SSE event and nothing emitted
  it, so an open console never learned about a roster change. The reconciliation ran both ways: the
  code was also publishing four events §10.2 never listed (`pdf.ready`, `push.reviewed`,
  `version.named`, `version.reverted`).
- **Body-binding failures were not RFC 7807.** A malformed JSON body already returned `400`, but with
  an empty body in Production and a leaked exception dump in Development. Handled once ahead of auth so
  it covers every endpoint, narrowed to `BadHttpRequestException` so a genuine fault still `500`s.
- **Any non-UTC `DateTimeOffset` returned `500`.** Npgsql's `timestamptz` mapping accepts only offset 0,
  so a conforming client sending `+02:00` on `dueAt` or `expiresAt` — valid RFC 3339 and advertised as
  `date-time` in the OpenAPI document — got a bare `500`. One converter normalizes at deserialization.
  A bare date reads as midnight UTC, not the server's local midnight.
- **`/s/{token}` was cacheable and the SPA shell replayed over the JSON.** One URL with two
  representations: the HTML branch is a static file carrying `Last-Modified` and no `Cache-Control`, so
  Chromium heuristically cached it and then served the cached shell to the SPA's
  `Accept: application/json` fetch of the same URL — **every live share link rendered "This link is no
  longer available."** Now `Vary: Accept` and `no-store`, the latter correct independently because that
  GET counts a view and writes an audit row. Only reproducible against the shipped image; 23 commits of
  green Vite runs never saw it.

#### Changed

- The Playwright suite runs against the shipped container image in CI, not against a dev server.

### M4 — copies and push-back review

Conformance criterion E9 green, completing E1–E12.

#### Added

- **Push To Copy**: fork a version into an isolated copy with its own members and its own history.
  Copies do **not** inherit the original's membership (spec §8, §11).
- **Push back from a copy with accept/reject review.** A member of the original reviews the incoming
  work: accept and it lands as a clearly-labelled incoming branch, reject and it never enters the
  history and the pusher is notified.
- **Merging an accepted push into main via the fork-point branch root**, so the merge resolves against
  the correct common ancestor.
- E9 conformance coverage, and a rule that **no conformance criterion may skip** — a silently skipped
  criterion reads as coverage that does not exist.

### M3 — public API GA

#### Added

- **`ed_` personal access tokens**: mint, list, revoke. 256-bit CSPRNG values, stored only as a
  SHA-256 hash, returned raw exactly once. A composite authentication scheme routes
  `Authorization: Bearer ed_…` to the PAT handler and everything else to the JWT/cookie scheme, so one
  `RequireAuthorization()` accepts either credential. **A token can never exceed the role of the user
  who minted it.**
- **OpenAPI 3.1 document** at `/openapi/v1.json`, and a **self-contained** interactive `/docs` — assets
  served same-origin as embedded resources, no external CDN.
- **Cursor pagination** on the list endpoints.
- **Append-only audit on every mutation** (spec §11), plus a cursor-paginated
  `GET /documents/{id}/audit`.
- Endpoints that closed acknowledged gaps against the §10.1 authoritative set: version detail,
  per-document members CRUD, `POST /invitations/{token}:accept`, and document trash + restore.
- **The E1–E12 conformance suite**, plus an unattended end-to-end flow driven entirely by a PAT, and a
  CI workflow that runs it against the docker-compose stack (spec §12.3).

#### Fixed

- The rendered PDF is registered as a blob before being linked to a version.

### M2 — the document lifecycle

#### Added

- **Publish minor and major** (R3/R4), applied to the *selected* version, with renumbering and a Major
  Versions list.
- **Rendered PDF on publish** via out-of-process LibreOffice, with a timeout and a retry.
- **Approvals** on published versions: one immutable decision plus a comment per approver, with a due
  date, cancellable while open. No comment threads.
- **Name this version** and **revert to version** — the revert creates a new head with the target's
  content and leaves history intact.
- **Share links**: scoped to one version, expiring, revocable, and audited — an anonymous view writes
  an audit row and increments a view count.
- **SSE-driven console updates.**

### M1 — the versioning core

#### Added

- **The numbering engine, R1–R8**, counter-authoritative (spec §5.1), including `0.0.7 → 0.1.0`,
  `0.0.7 → 1.0.0`, a manual counter override, and R8 download naming.
- **`commit_save` as the single write path** (`VersioningService.CommitSaveAsync`), with an import
  endpoint on top of it.
- **Collabora Online editing over a WOPI host**: edit-session mint, short-TTL WOPI access tokens,
  discovery, `CheckFileInfo`/`GetFile`/`PutFile` and locks, all routing into `commit_save`. Collabora
  ships as a compose service with the §6.1 networking.
- **Branch on a stale base.** Two people editing the same head produce two concurrent branches instead
  of a lost edit, with session-aware dedupe so an unchanged re-save creates no version.
- **Concurrent-branch merge** via `WmlComparer`: merge-into-main applies the incoming branch's changes
  as tracked changes attributed to their author on top of current main. Both branch versions persist;
  the merged branch closes.
- **Redline diff and change summaries**: an eager in-process numeric-summary worker plus an on-demand
  cached redline, both degrading gracefully when `WmlComparer` fails.
- **In-process event bus and per-document SSE** at `/documents/{id}/events`.

### M0 — the skeleton

#### Added

- Solution scaffold on **.NET 10**, the full v1 domain model and `DbContext`, the initial migration,
  and **migrate-on-startup**.
- **Argon2id password hashing.**
- **Register creates a user, an organization and an owner membership** in one call, and issues a
  session JWT. Login issues a `httpOnly` / `Secure` / `SameSite=Lax` cookie or a bearer JWT; `GET /me`
  reads it back.
- **Folders**: CRUD, nesting, and a delete that prompts promote-vs-trash.
- **Content-addressed filesystem blob store.**
- **Document create and multipart upload → version `0.0.1` exactly**, and moving a document between
  folders while preserving history and members.
- **The document authorization chokepoint**: one `resolve_role` over `document_members`, with **no
  org-role fallback** — org membership grants no implicit document access, and a cross-org document
  returns `404` rather than leaking its existence.
- **`docker compose up`**: the app, PostgreSQL 16, a multi-stage Dockerfile, and the SPA built in and
  served from `wwwroot` with an API-safe fallback.
- **CI**, the **AGPL-3.0** licence, and the **DCO** contributing guide with a CI job that rejects any
  commit lacking a `Signed-off-by` line.
