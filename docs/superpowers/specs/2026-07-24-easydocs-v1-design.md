# easydocs v1 — Design

**Date:** 2026-07-24
**Status:** Approved design (pre-implementation)
**One-line pitch:** Open-source, self-hostable Git-style version control for Word documents — a lean, balanced take on SimulDocs, with a first-class public API.

## Sources

- `simuldocs-rebuild-specification.md` — full SimulDocs feature set + a maximalist 8-service architecture (feature reference).
- `opendocs-open-source-edition-spec.md` — "better than SimulDocs" ambitions: M365 Graph, OAuth server, SDKs, webhooks (v2+ ideas, out of scope for v1).

Both are the *maximalist vision*. This document is the *lean v1 plan*. It keeps SimulDocs' **feature surface** (parity is the goal, and is now **immutable**) and throws out the enterprise **deployment topology** and API-maturity plumbing. Where the source spec's endpoints, acceptance criteria, or algorithms describe out-of-v1 behavior, **this document is authoritative** — the source is a feature reference, not the contract.

Tags: **[V]** verified SimulDocs behavior reproduced · **[D]** our design decision.

---

## 1. Product positioning

Two co-equal front doors:

1. **A polished web UI** for the general public — click-click-click document versioning, no Git literacy required.
2. **A real, documented REST API** for developers — the differentiator SimulDocs lacks. The UI is just the API's first client.

The soul of the product (from SimulDocs) is **invisible automatic versioning**: you edit in the browser, hit Save, and a numbered, diffable, attributed version *just appears*. The signature trick is **redline diff even when Track Changes was never turned on**.

---

## 2. Scope

### In v1 (SimulDocs parity, built lean — immutable)
- Folders (nestable), documents, members, roles (Owner/Editor/Viewer)
- Upload → `0.0.1`; automatic versioning on every save/import
- **In-browser editing** via Collabora Online with save → auto-version
- Branch-on-stale-base (concurrent edits never overwrite) + **one-click merge**
- **Redline diff** (Open-XML-PowerTools `WmlComparer`) — even without Track Changes
- X.Y.Z version numbering (rules R1–R8)
- Publish minor/major → PDF render + Major Versions tab
- Approvals (sign-off on published versions; single decision + comment, no threaded conversation)
- Copies/Push (isolated client-review fork + push-back with accept/reject)
- Name / Revert / Download / Share-link
- Dashboard search over document/folder/version **names** (Postgres `ILIKE`/`tsvector`)
- REST API at `/api/v1`, OpenAPI 3.1, `Bearer` token auth
- Acceptance criteria E1–E12 (v1 profile, §12) as an automated conformance suite

### Out of v1 (deferred / dropped)
- **v1.1**: desktop "Open in Word" (WebDAV + `ms-word:`), OIDC/SSO, cloud export/import pickers (Dropbox/OneDrive/GDrive), graphical DAG revision graph, `ONLYOFFICE` editor option, full-text **content** search, tile thumbnails, threaded task system
- **v2+ / dropped**: M365 Graph round-trip, OAuth 2.0 authorization server, client SDKs + CLI, webhook-delivery infrastructure, SCIM, MFA/TOTP, Helm chart, RabbitMQ/Redis/MinIO/ClamAV, blob GC

---

## 3. Architecture — one app, two front doors

A single **ASP.NET Core** application serves the REST API, hosts the React SPA as static assets, runs the WOPI host, and runs in-process background work. `.docx` diff/merge requires `WmlComparer` (.NET) — the one hard constraint that dictates the language for the whole app.

```
docker compose up  ->
  easydocs    ASP.NET Core: REST API + React SPA + WOPI host + background work.
              LibreOffice headless bundled in the image (invoked as a child process for PDF).
  collabora   Collabora Online (CODE) — in-browser .docx editing over WOPI.
  postgres    Metadata, ACL, audit.
  (blobs on a mounted filesystem volume, content-addressed by sha256)
```

No RabbitMQ, Redis, MinIO, or separate worker services. Justification for each collapse:
- **Queue → none.** Redline/HTML diffs are computed on-demand and cached; the cheap numeric diff summary, PDF, and merge run in an in-process `BackgroundService`. Diffs are always recomputable, so nothing is lost on restart.
- **Redis → none.** Single instance: WOPI locks live in the DB; live console updates use SSE.
- **MinIO → filesystem.** Content-addressed blobs on a volume; an S3 backend is a future env-configurable swap.
- **Blob GC → none.** Versions are retained forever by design; disk is cheap.

**Frontend build & serving:** Vite builds the React SPA into `wwwroot`; ASP.NET serves static files with SPA fallback to `index.html` for any route not under `/api`, `/wopi`, or `/s`. A multi-stage Dockerfile (node build stage → dotnet runtime stage, with LibreOffice installed in the runtime image) produces the `easydocs` image.

**Mail:** transactional email is sent directly from the in-process `BackgroundService` over SMTP (env: host/port/creds/from), with retry. No queue. v1 emails: invitation, approval request, approval response, share-link notify.

`ponytail: single process. Split a service out only if a profiler demands it.`

---

## 4. Data model (PostgreSQL)

Based on the SimulDocs base spec §7, trimmed for v1. **Migrations** are EF Core migrations applied automatically on application startup.

**Kept:** `organizations`, `users`, `org_members`, `folders`, `documents`, `document_members`, `invitations`, `branches`, `blobs`, `versions`, `approval_requests`, `push_requests`, `edit_sessions`, `share_links`, `version_diffs`, `audit_events`.

**Refinements from review:**
- **Publish state folded into `versions`** (no separate `publications` table): `published_kind`, `published_by`, `published_at`, `publish_name`, `pdf_blob_sha256` columns. The console "publications" payload and the (retained) `GET /publications` route query `versions WHERE published_kind IS NOT NULL`.
- **No `tasks`/`task_comments` tables.** An approval is a row: `approval_requests` carries `approver_id, decision, decision_comment, decided_at, cancelled_at, due_at`. No threaded conversation in v1. Re-add a general task system only if standalone tasks become a real feature.
- **`versions.source` CHECK** = `('upload','edit_wopi','import','merge','revert','copy_push')` — `edit_webdav` removed (WebDAV is v1.1).
- **`blobs.av_status` dropped** (ClamAV out of v1).
- **`edit_sessions.mode` column dropped** — only WOPI exists in v1; re-add when WebDAV lands in v1.1.
- **`documents.forked_from_version_id`** gets `REFERENCES versions(id)`.
- **`api_tokens`** (new, simplified): `id, org_id, user_id?, service_name?, token_hash, scopes[], expires_at, last_used_at, revoked_at`. `ed_` prefix, hashed at rest.

**Dropped entirely:** `cloud_connections`, `export_jobs`, all M365 tables, `oauth_clients`, `webhooks`, `webhook_deliveries`.

**First-run seed:** `POST /auth/register` creates the user **and**, if they have no org, a new `organizations` row + `org_members(role=owner)`. This prevents an orphaned first user (every query is `org_id`-filtered).

Principles retained: versions immutable, blobs content-addressed (written once, referenced many — revert/copy/push are pointer operations), every query filtered by `org_id`.

---

## 5. Versioning engine — the heart

### 5.1 Numbering source of truth (resolves the R1–R8 ambiguity)

**`documents.version_counter_{major,minor,rev}` is the single source of truth** — *not* the branch head's number. This is what makes R5 (manual override) and R6 (publish a non-head version) work.

- **Draft save (R2):** under the `FOR UPDATE` lock, read-and-increment `version_counter_rev`; the new version takes the current counter. Concurrent branches serialize on the lock, so each save gets a distinct `Z`.
- **Publish (R3/R4):** minor → `version_counter_minor += 1, rev = 0`; major → `version_counter_major += 1, minor = 0, rev = 0`. The published version is renumbered to the new counter; future drafts continue from it — regardless of which branch head exists, and even if the published version was not the head (R6).
- **Manual override (R5):** write all three counter columns to any non-negative ints (incl. `0.0.0`); governs all future revisions.

The source spec's head-based `next_draft_number` pseudocode is **superseded** by the counter-based rule above.

### 5.2 `commit_save(session, blob_sha)` — the single write path

```
1. tx begin; SELECT ... FOR UPDATE on the documents row   -- per-doc mutual exclusion
2. if blob_sha == session.last_committed_sha: no-op        -- idempotent (Word re-PUTs unchanged)
3. head = current head version of branch(main)
4. if session.branch_id set:            target = that branch
   elif session.base_version_id == head.id:   target = main            -- fast-forward
   else:                                       target = new concurrent branch  -- STALE BASE
5. number = read-and-increment document.version_counter_rev   -- R2 (see 5.1)
6. insert version(source=edit_wopi|import|..., parent=head|base, blob=blob_sha, number)
7. tx commit
8. enqueue numeric-summary diff (parent->new) on the BackgroundService; SSE broadcast 'version.created'
```

`ponytail: SELECT ... FOR UPDATE, not SERIALIZABLE — same correctness at document granularity, no 40001 retry loops.`

**Download filename (R8):** `{org_slug}__{Doc_Name}-v{X}.{Y}.{Z}.docx`.

### 5.3 Merge (ships in M1 for concurrent branches)

`POST /documents/{id}/merges {left, right}`: **merge-into-main model** (revised — see note). The base is the **current main-branch head** (which already carries the first author's accepted edits); a single `WmlComparer.Compare(mainHead, incomingBranchHead)` renders the incoming concurrent branch's changes as Word tracked-changes revisions **attributed to the incoming author**, ready to accept/reject on top of current main. Committed as `source=merge` with two parent pointers (parent = main head, merge-parent = incoming). The merged concurrent branch closes (`merged_into_version_id`). Overlapping edits are **not** auto-resolved — the editor's accept-reject UI is the resolver. Every `WmlComparer` call is guarded; failure → `409` "merge unavailable", never a partial commit. Auto-branching *requires* merge, so the **base merge engine ships in M1**; the copy/push cross-document ancestor path is M4.

> **[D] Merge-model decision (M1).** The original "run `WmlComparer` on `base→left` and `base→right` and consolidate both authors' revisions over the common ancestor" was found **not implementable** with the OSS comparer (Clippit/OpenXmlPowerTools): `WmlComparer.Compare` flattens any pre-existing revisions and stamps exactly one `AuthorForRevisions` per call, so dual-author tracked changes over a shared ancestor cannot be produced by chaining, and chaining makes the first author's edits appear as the second author's *deletions* (misleading). We therefore adopt **merge-into-main**: the first author's edits are the accepted base, the incoming branch comes in as a clean single-author redline. Nothing is lost (both branch versions persist in history). The "both-authors-over-common-ancestor" redline (a manual XML fuse of two `Compare(base, side)` revision sets) is a possible **future enhancement**, not v1.

---

## 6. Editing — Collabora via a WOPI host

1. Click **Edit** → mint an `edit_session` pinned to a `base_version_id`.
2. Hand Collabora a WOPI `file_id = session_id` + short-lived access token (JWT `{session_id, user_id, perms}`).
3. Collabora edits `.docx` in the browser; on save it calls our WOPI host `PutFile` → `commit_save` → new version + SSE push to open consoles.
4. **Save-coalescing (v1, minimal):** sha-dedupe (already in `commit_save`) + commit-on-session-close. `ponytail: no N-second timer yet — Collabora saves on explicit checkpoints, not per keystroke. Add a timer only if version spam is observed.`

WOPI host endpoints: `CheckFileInfo`, `GetFile`, `PutFile`, and `LOCK/UNLOCK/REFRESH_LOCK/GET_LOCK` (locks stored on the session row, 30-min TTL). Collabora-only for v1 — **no `EDITOR_PROVIDER` abstraction** (`ponytail: no config for a value that never changes; adding ONLYOFFICE later is an isolated change`).

### 6.1 Concrete Collabora ↔ WOPI networking (the real setup work)

Two different base URLs must be threaded through, because the browser and the Collabora container see the WOPI host at different addresses:

- **`PUBLIC_BASE_URL`** (e.g. `https://docs.example.com`) — where the browser loads the Collabora editor iframe from.
- **`WOPI_HOST_URL`** (e.g. `http://easydocs:8080` on the compose network) — injected into the WOPI action URL and discovery so Collabora fetches `CheckFileInfo`/`GetFile`/`PutFile` from the app *internally*.
- Collabora config: add the host to its `aliasgroup`/domain **allowlist**; the app fetches and caches Collabora's **discovery XML** daily to build action URLs.

This wiring ships in **M1** — the entire editing story depends on it, so it is built and tested first, not deferred.

---

## 7. Diff & PDF — no queue

- **Numeric summary** (`{insertions, deletions, moves, format_changes}`): computed **eagerly** for each `(parent → child)` in the `commit_save` `BackgroundService`, so the version-list rows show "14 insertions, 3 deletions" without opening a comparison.
- **Redline `.docx` + HTML render:** computed **on-demand** on first compare view, cached by `(from_sha, to_sha)` in `version_diffs`, permanent after.
- **PDF:** rendered on publish by the `BackgroundService` (LibreOffice headless), with retry. `download?format=pdf` on an **unpublished** version returns `409` (no PDF exists).

**Robustness (must, not laziness):**
- Every `WmlComparer` call is wrapped. On failure (complex tables, content controls, numbering edge cases) the diff/merge degrades to **"comparison unavailable — download both versions"** — never a 500.
- LibreOffice runs as an **out-of-process child with a hard timeout + retry**. "In-process" means the *scheduler*, not the renderer — a hung render must never take down request threads.

---

## 8. Copies & push

- **Push To Copy** (`POST /versions/{vid}/copies {name}`): fork a specific version into a new `documents` row (`parent_document_id`, `forked_from_version_id`), referencing the same immutable blob (zero-copy). The copy has its own members and version history — internal drafts never leak. Invite external reviewers to the *copy only*.
- **Push back** (`POST /documents/{copy_id}/pushes {target, version_id}`): **authorized on membership of the source copy**, not the target — this is the one sanctioned bypass of the target authorization chokepoint (§10). Any member of the copy may push:
  - Pusher also holds a target role → materialize an `incoming_push` branch immediately; write `push_requests {status: auto_accepted, materialized_version_id}`.
  - Otherwise → `push_requests {status: pending}`; target members **Accept/Reject** (`POST /push-requests/{id}:accept|reject`). Accepted → materialize; rejected → hidden, pusher notified.
- **Merge base for pushes:** when materializing, the fork point is copied onto the new incoming branch (via `push_requests.source_version_id` + the copy's `forked_from_version_id`) so cross-document merge can resolve the common ancestor without walking into the copy document.

Copies/push is the last feature milestone (M4).

---

## 9. Web UI (React SPA)

Served as static assets by the app (Vite → `wwwroot`, SPA fallback — §3). Screens: dashboard (folder tree + document tiles + name search) · **document console** (version list with per-row change summary, per-version Actions menu, members panel) · **comparison/redline view** · **Major Versions tab** · copies management · approvals · public share landing · settings. Live updates via **SSE** (see §10.2).

**M4.5 deviation:** the version-list row (§10.1) now carries **branch identity** — `branchId`, `branchKind`, `branchOrdinal`, `branchMergedIntoVersionId` — superseding the earlier decision to keep branch topology off the v1 surface. The console's grouped concurrent-branch history and its Merge button cannot be built without knowing which rows share a branch and which branch merged into which version.

`ponytail: revision history renders as an indented list — main branch with grouped "concurrent branch" entries + a Merge button. The graphical DAG renderer is v1.1; most documents are linear until a concurrent edit happens.`

The public share landing (`GET /s/{token}`, §10.1) content-negotiates: a browser (`Accept: text/html`) gets the SPA shell, which re-requests the same URL as JSON; any other client gets JSON directly.

Dashboard search is name-based (`?q=` over document/folder/version names via Postgres). Content-text search and tile thumbnails are v1.1.

Design quality is handled with the frontend-design skill at build time.

---

## 10. API (developer front door)

- Versioned at `/api/v1`; **OpenAPI 3.1 auto-generated**, rendered docs at `/docs` (self-contained, no phone-home).
- Auth: session JWT via `httpOnly` cookie (web) or `Bearer ed_…` token (API).
- Conventions: RFC-7807 problem+json errors, cursor pagination.
- `ponytail: no Idempotency-Key infrastructure in v1 — commit_save is already idempotent via sha-dedupe; other mutations are low-frequency human clicks. Add the middleware when the SDK/retry story exists.`
- **Authorization chokepoint:** one middleware `resolve_role(user, document)` backed by `document_members`. A token never exceeds its owner's document role. **Org role grants no implicit document access** — membership is strictly per-document (matching copies isolation); org owner/admin is for org/member management only. The sole documented bypass is push-back (§8), authorized on source-copy membership.

### 10.1 v1 endpoint set (authoritative — supersedes source §9)

Auth: `POST /auth/register` (creates user + org), `POST /auth/login`, `POST /invitations/{token}:accept`.
Folders: `GET/POST /folders`, `PATCH/DELETE /folders/{id}`.
Documents: `GET /documents?folder_id=&q=&trashed=true&sort=created|updated|name&order=asc|desc`, `POST /documents`, `GET /documents/{id}`, `PATCH /documents/{id}`, `PUT /documents/{id}/version-counter`, `DELETE /documents/{id}`, `POST /documents/{id}:restore`.
Versions: `POST /documents/{id}/versions` (multipart upload — §10.3), `POST /documents/{id}/versions:import` (multipart), `GET /documents/{id}/versions?order=desc`, `GET /versions/{vid}`, `GET /versions/{vid}/download?format=docx|pdf`, `PATCH /versions/{vid}` (name), `POST /versions/{vid}/revert`, `GET /documents/{id}/compare?from=&to=&format=html|docx|summary`.
Editing: `POST /versions/{vid}/sessions` (wopi only), `DELETE /sessions/{sid}`; WOPI host under `/wopi/*`.
Publish/approvals: `POST /versions/{vid}/publish`, `GET /documents/{id}/publications`, `POST /versions/{vid}/approvals`, `POST /approvals/{id}:respond`, `POST /approvals/{id}:cancel`.
Approvals (read): `GET /approvals?filter=assigned|requested&status=`, `GET /versions/{vid}/approvals`.
Sharing: `POST /versions/{vid}/share-links`, `GET /documents/{id}/share-links`, `GET /s/{token}` (public), `DELETE /share-links/{id}`.
Copies/push: `POST /versions/{vid}/copies`, `GET /documents/{id}/copies`, `POST /documents/{id}/pushes`, `GET /documents/{id}/push-requests`, `POST /push-requests/{id}:accept|reject`.
Members/merge: `GET/POST /documents/{id}/members`, `PATCH/DELETE /documents/{id}/members/{uid}`, `POST /documents/{id}/merges`.
Tokens: `GET/POST/DELETE /tokens`.
Org: `GET/PATCH /org`, `GET/POST /org/members`, `PATCH/DELETE /org/members/{uid}`.
Audit: `GET /documents/{id}/audit`.

**Removed vs source §9:** exports, cloud-connections, tasks, `auth/sso`, `sessions {mode:webdav}`, `versions:initiate`/`:commit`, `WS /realtime`.

### 10.2 Live updates (SSE)

`GET /api/v1/documents/{id}/events` — Server-Sent Events, authorized via the `httpOnly` session cookie (native `EventSource` cannot send a `Bearer` header) or a short-lived `?token=` capability param, then `resolve_role` on the document. v1 events: `version.created`, `version.published`, `merge.completed`, `diff.ready`, `member.added`, `push.requested`, `push.reviewed`, `approval.responded`, `pdf.ready`, `version.named`, `version.reverted`.

### 10.3 Ingest (filesystem, no pre-signed URLs)

Upload and import are **direct multipart uploads to the app**: `POST /documents/{id}/versions` (multipart body) → app streams to a temp file → computes sha256 → moves into the content-addressed volume path → inserts `blobs` row + version. Reuses the same blob-write helper as WOPI `PutFile`. The source spec's `:initiate`/`:commit`/`upload_url` flow does not apply (no object store in the default install).

---

## 11. Auth & security

- **AuthN:** local email/password (**Argon2id**) + JWT sessions (`httpOnly` cookie for the web, `Bearer` tokens for the API). OIDC/SSO deferred to v1.1.
- **AuthZ:** single middleware (§10); copies do **not** inherit master membership; org role grants no implicit document access; WOPI/share tokens are capability tokens — hashed, scoped, short-TTL.
- **Audit:** append-only `audit_events` on **mutations + public share-link reads** (`ponytail: not every GET — auditing reads is write-amplification for little value`). Exportable per document.
- **At rest / transport:** TLS at the proxy; token/secret columns hashed or envelope-encrypted, never plaintext.
- **Deferred:** ClamAV AV-scan (`ponytail: v1 trusts member .docx uploads — known ceiling, add ClamAV when untrusted upload paths exist`), MFA.

---

## 12. Testing

### 12.1 v1 conformance profile (E1–E12 restated against the lean surface)

The source E-criteria assume dropped features; the v1 suite tests the built surface. Milestone rows (§13) map to these.

- **E1 Folders** — nest ≥ 3 levels; move doc preserves history/members; delete prompts promote-vs-trash.
- **E2 Ingest** — **local upload only**; first version exactly `0.0.1`.
- **E3 Edit/version** — **Collabora save** produces a new version; unchanged re-save creates none; list shows author/time/summary.
- **E4 Branch/merge** — two sessions from one head → two branches, zero lost edits; merge (merge-into-main, §5.3) output opens with the **incoming branch's** changes as tracked changes attributed to their author on top of current main; both branch versions persist in history; merged branch closes. *(Ships M1.)*
- **E5 Numbering** — R1–R6 exactly, incl. `0.0.7→0.1.0`, `0.0.7→1.0.0`, manual `0.0.0`; downloads named per R8.
- **E6 Publish** — published version renumbers + PDF + Major Versions entry; applies to the *selected* version.
- **E7 Approvals** — only on published versions; one approval row per approver with due date; **single decision + comment (no thread)**; decisions immutable; cancel closes the request.
- **E8 Actions menu** — the **v1 action set** present and functional: Open in Collabora, Import, Share, Download, Name, Publish, Revert, Push To Copy (**8 actions**; desktop "Open in Word" and Export are v1.1).
- **E9 Copies** — isolated members/history; non-member push → pending review; accept → incoming branch; reject → hidden + pusher notified; merge into main via fork-point ancestor. *(Ships M4.)*
- **E10 Share/download** — share link scoped to one version, revocable, audited; DOCX + PDF download (no cloud export).
- **E11 Revert** — new head equal to target content; history untouched.
- **E12 Security** — §10/§11 role matrix enforced per endpoint × role; copies never leak master drafts.

### 12.2 Unit & integration

- **Unit:** numbering (R1–R8, counter-authoritative — §5.1), branch-on-stale-base, merge ancestry resolution, WOPI `commit_save` (dedupe, fast-forward vs branch).
- **Integration:** every API endpoint × role for the permission matrix.
- **Robustness:** `WmlComparer` failure degrades gracefully; LibreOffice timeout/kill.

### 12.3 CI

The E2E conformance job boots the docker-compose stack (Postgres + Collabora + bundled LibreOffice). Pure-API criteria run against the API directly; criteria requiring the Collabora browser round-trip (E3, E4, part of E8/E9) use a headless-browser driver.

---

## 13. Milestones

| M | Contents | Exit |
|---|---|---|
| **M0** | Monorepo, CI, compose boots, migrations-on-startup, register→org+owner, auth, folders, multipart upload → `0.0.1` | E1–E2 green |
| **M1** | Collabora WOPI editing (incl. §6.1 networking), `commit_save`, branch-on-stale, **concurrent-branch merge**, redline diff, eager summary, numbering | E3–E5 green |
| **M2** | Publish minor/major, PDF, approvals, name/revert/download/share, SSE console | E6–E8, E10–E11 green |
| **M3** | Public API GA: OpenAPI, tokens, `/docs`; conformance suite public (E9 pending until M4) | API drives full flow unattended |
| **M4** | Copies & push/merge (fork-point ancestor, accept/reject review) | E9 green; full E1–E12 green |
| **M5** | v1.0.0: docs site, security pass, license/DCO | Tagged `v1.0.0` |

API GA (M3) ships before copies/push (M4) — the product is fully usable via the bundled editor from M1, and the API is the differentiator.

---

## 14. Decisions & remaining open items

1. **License — DECIDED.** **AGPL-3.0 for the server**, **MIT for API clients/examples/SDKs**. **DCO** (Developer Certificate of Origin, `Signed-off-by` line) from commit #1 — contributors retain copyright; the project stays AGPL. No CLA. Repo is MIT today; the split (server → AGPL, `/packages/*` clients → MIT) is applied during M0 repo setup, and `LICENSE`/`DCO`/`CONTRIBUTING.md` are finalized by M5. **Monetization:** donations (GitHub Sponsors / Buy Me a Coffee / Open Collective) + optionally an official paid hosted instance later — both fully compatible with AGPL. Dual-license/commercial-license sales are intentionally *not* pursued (would have required a CLA).
2. **Coalescing default** *(open)* — start with dedupe + on-close; tune only if Collabora produces version spam in practice.

---

## Appendix: ponytail decisions (deliberate cuts, with ceilings)

| Cut | Ceiling / upgrade path |
|---|---|
| Single process, no queue | Split a service / add a durable queue if a profiler demands it |
| `SELECT ... FOR UPDATE` not SERIALIZABLE | Fine at per-document granularity; revisit if cross-document txns appear |
| Redline/HTML diffs on-demand (summary eager) | Precompute redline on commit if compare-view latency bites |
| No `Idempotency-Key` infra | Add middleware when the public SDK/retry story exists |
| Collabora-only, no editor abstraction | Add ONLYOFFICE as an isolated change in v1.1 |
| No save-timer, dedupe + on-close only | Add N-second coalescing if version spam observed |
| Indented-list history, no DAG | Build the graphical revision graph in v1.1 |
| Name-only search, no thumbnails | Content-text search + thumbnails in v1.1 |
| Filesystem blobs, no S3, no GC | S3 backend via env; GC when retention policy demands it |
| No ClamAV | Add AV gate when untrusted upload paths exist |
| Audit mutations + share reads only | Add read-audit if a compliance customer needs "who viewed X" |
| Direct multipart upload, no pre-signed URLs | Pre-signed direct-to-S3 when the S3 backend lands |
