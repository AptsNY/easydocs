# easydocs v1 — Design

**Date:** 2026-07-24
**Status:** Approved design (pre-implementation)
**One-line pitch:** Open-source, self-hostable Git-style version control for Word documents — a lean, balanced take on SimulDocs, with a first-class public API.

## Sources

- `simuldocs-rebuild-specification.md` — full SimulDocs feature set + a maximalist 8-service architecture (feature reference).
- `opendocs-open-source-edition-spec.md` — "better than SimulDocs" ambitions: M365 Graph, OAuth server, SDKs, webhooks (v2+ ideas, out of scope for v1).

Both are the *maximalist vision*. This document is the *lean v1 plan*. It keeps SimulDocs' **feature surface** (parity is the goal) and throws out the enterprise **deployment topology** and API-maturity plumbing.

Tags: **[V]** verified SimulDocs behavior reproduced · **[D]** our design decision.

---

## 1. Product positioning

Two co-equal front doors:

1. **A polished web UI** for the general public — click-click-click document versioning, no Git literacy required.
2. **A real, documented REST API** for developers — the differentiator SimulDocs lacks. The UI is just the API's first client.

The soul of the product (from SimulDocs) is **invisible automatic versioning**: you edit in the browser, hit Save, and a numbered, diffable, attributed version *just appears*. The signature trick is **redline diff even when Track Changes was never turned on**.

---

## 2. Scope

### In v1 (SimulDocs parity, built lean)
- Folders (nestable), documents, members, roles (Owner/Editor/Viewer)
- Upload → `0.0.1`; automatic versioning on every save/import
- **In-browser editing** via Collabora Online with save → auto-version
- Branch-on-stale-base (concurrent edits never overwrite) + **one-click merge**
- **Redline diff** (Open-XML-PowerTools `WmlComparer`) — even without Track Changes
- X.Y.Z version numbering (rules R1–R8)
- Publish minor/major → PDF render + Major Versions tab
- Approvals (sign-off on published versions)
- Copies/Push (isolated client-review fork + push-back with accept/reject) — **last feature milestone**
- Name / Revert / Download / Share-link
- REST API at `/api/v1`, OpenAPI 3.1, `Bearer` token auth
- Acceptance criteria E1–E12 as an automated conformance suite

### Out of v1 (deferred / dropped)
- **v1.1**: desktop "Open in Word" (WebDAV + `ms-word:`), OIDC/SSO, cloud export pickers (Dropbox/OneDrive/GDrive), graphical DAG revision graph, `ONLYOFFICE` editor option
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
- **Queue → none.** Diffs are computed on-demand and cached; PDF/merge run in an in-process `BackgroundService`. Diffs are always recomputable, so nothing is lost on restart.
- **Redis → none.** Single instance: WOPI locks live in the DB; live console updates use SSE.
- **MinIO → filesystem.** Content-addressed blobs on a volume; an S3 backend is a future env-configurable swap.
- **Blob GC → none.** Versions are retained forever by design; disk is cheap.

`ponytail: single process. Split a service out only if a profiler demands it.`

---

## 4. Data model (PostgreSQL)

Based on the SimulDocs base spec §7, trimmed for v1.

**Kept:** `organizations`, `users`, `org_members`, `folders`, `documents`, `document_members`, `invitations`, `branches`, `blobs`, `versions`, `approval_requests`, `push_requests`, `edit_sessions`, `share_links`, `version_diffs`, `audit_events`.

**Refinements from review:**
- **Publish state folded into `versions`** (no separate `publications` table): `published_kind`, `published_by`, `published_at`, `publish_name`, `pdf_blob_sha256` columns. (It was 1:1 with `versions` anyway.)
- **No `tasks`/`task_comments` tables.** An approval is a row: `approval_requests` carries `approver_id, decision, decision_comment, decided_at, cancelled_at`. Re-add a general task system only if standalone tasks become a real feature.
- **`blobs.av_status` dropped** (ClamAV out of v1).
- **`edit_sessions.mode` column dropped** — only WOPI exists in v1; re-add when WebDAV lands in v1.1.
- **`api_tokens`** (new, simplified): `id, org_id, user_id?, service_name?, token_hash, scopes[], expires_at, last_used_at, revoked_at`. `ed_` prefix, hashed at rest.

**Dropped entirely:** `cloud_connections`, `export_jobs`, all M365 tables, `oauth_clients`, `webhooks`, `webhook_deliveries`.

Principles retained: versions immutable, blobs content-addressed (written once, referenced many — revert/copy/push are pointer operations), every query filtered by `org_id`.

---

## 5. Versioning engine — the heart

`commit_save(session, blob_sha)` is the single write path:

```
1. tx begin; SELECT ... FOR UPDATE on the documents row   -- per-doc mutual exclusion
2. if blob_sha == session.last_committed_sha: no-op        -- idempotent (Word re-PUTs unchanged)
3. head = current head version of branch(main)
4. if session.branch_id set:            target = that branch
   elif session.base_version_id == head.id:   target = main            -- fast-forward
   else:                                       target = new concurrent branch  -- STALE BASE
5. number = next_draft_number(document)   -- R2: Z+1 off the doc counter
6. insert version(source=edit_wopi|import|..., parent=head|base, blob=blob_sha)
7. bump document.version_counter_rev
8. tx commit
9. SSE broadcast 'version.created'   (diff computed lazily on first compare view)
```

`ponytail: SELECT ... FOR UPDATE, not SERIALIZABLE — same correctness at document granularity, no 40001 retry loops.`

**Numbering (R1–R8):** start `0.0.1`; every draft `Z+1`; minor publish `Y+1, Z=0`; major publish `X+1, Y=0, Z=0`; manual override to any non-negative `X.Y.Z`; publish renumbers the *selected* version; side-branch versions display a branch discriminator, canonical identity `(branch_id, seq)`; downloads named `{org}__{Doc}-v{X}.{Y}.{Z}.docx`.

**Merge** (`POST /documents/{id}/merges {left, right}`): resolve common ancestor (concurrent branch root, or copy fork point), run `WmlComparer` on `base→left` and `base→right`, consolidate into one `.docx` where each side's edits are Word tracked-changes revisions attributed to their authors. Committed as `source=merge` with two parent pointers. Overlapping edits are **not** auto-resolved — both revisions are present and Word/Collabora's accept-reject UI is the conflict resolver. Auto-branching *requires* merge to exist; it is load-bearing and ships in v1.

---

## 6. Editing — Collabora via a WOPI host

1. Click **Edit** → mint an `edit_session` pinned to a `base_version_id`.
2. Hand Collabora a WOPI `file_id = session_id` + short-lived access token (JWT `{session_id, user_id, perms}`).
3. Collabora edits `.docx` in the browser; on save it calls our WOPI host `PutFile` → `commit_save` → new version + SSE push to open consoles.
4. **Save-coalescing (v1, minimal):** sha-dedupe (already in `commit_save`) + commit-on-session-close. `ponytail: no N-second timer yet — Collabora saves on explicit checkpoints, not per keystroke. Add a timer only if version spam is observed.`

WOPI host endpoints: `CheckFileInfo`, `GetFile`, `PutFile`, and `LOCK/UNLOCK/REFRESH_LOCK/GET_LOCK` (locks stored on the session row, 30-min TTL). Collabora-only for v1 — **no `EDITOR_PROVIDER` abstraction** (`ponytail: no config for a value that never changes; adding ONLYOFFICE later is an isolated change`).

**Setup risk flagged:** Collabora is the hardest part of "compose-up just works" — it needs its own hostname, a WOPI host URL reachable *from* the Collabora container, discovery XML, token round-trip, and domain allowlisting. Budget real time here; it's where the lean-self-host promise gets tested.

---

## 7. Diff & PDF — no queue

- **Diffs:** computed **on-demand** and cached by `(from_sha, to_sha)` in `version_diffs`. First compare view triggers `WmlComparer` → redline `.docx` + HTML render + `{insertions, deletions, ...}` summary; cached permanently after.
- **PDF:** rendered on publish by an in-process `BackgroundService` (LibreOffice headless), with retry.

**Robustness (must, not laziness):**
- Every `WmlComparer` call is wrapped. On failure (complex tables, content controls, numbering edge cases) the diff/merge degrades to **"comparison unavailable — download both versions"** — never a 500.
- LibreOffice runs as an **out-of-process child with a hard timeout + retry**. "In-process" means the *scheduler*, not the renderer — a hung render must never take down request threads.

---

## 8. Copies & push

- **Push To Copy** (`POST /versions/{vid}/copies {name}`): fork a specific version into a new `documents` row (`parent_document_id`, `forked_from_version_id`), referencing the same immutable blob (zero-copy). The copy has its own members and version history — internal drafts never leak. Invite external reviewers to the *copy only*.
- **Push back** (`POST /documents/{copy_id}/pushes {target, version_id}`): if the pusher is a member of the target, materialize an `incoming_push` branch immediately; else create a `push_requests {status: pending}` row and target members **Accept/Reject**. Accepted → regular version on an incoming branch (rendered distinctly); rejected → hidden, pusher notified. Merge into main uses standard merge machinery, ancestor = `forked_from_version_id`.

This is the last feature milestone (M4) — droppable if v1 runs long, but the intent is to ship it for SimulDocs parity.

---

## 9. Web UI (React SPA)

Served as static assets by the app. Screens: dashboard (folder tree + document tiles) · **document console** (version list, per-version Actions menu, members panel) · **comparison/redline view** · **Major Versions tab** · copies management · approvals · public share landing · settings. Live updates via **SSE** (native `EventSource`, no client library).

`ponytail: revision history renders as an indented list — main branch with grouped "concurrent branch" entries + a Merge button. The graphical DAG renderer is v1.1; most documents are linear until a concurrent edit happens.`

Design quality is handled with the frontend-design skill at build time.

---

## 10. API (developer front door)

- Surface: SimulDocs base spec §9, versioned at `/api/v1`; **OpenAPI 3.1 auto-generated**, rendered docs at `/docs` (self-contained, no phone-home).
- Auth: session JWT (web) or `Bearer ed_…` token (API).
- Conventions: RFC-7807 problem+json errors, cursor pagination.
- `ponytail: no Idempotency-Key infrastructure in v1 — commit_save is already idempotent via sha-dedupe; other mutations are low-frequency human clicks. Add the middleware when the SDK/retry story exists.`
- Role matrix (Owner/Editor/Viewer) enforced at **one authorization chokepoint** (`resolve_role(user, document)`); a token never exceeds its owner's document role.

---

## 11. Auth & security

- **AuthN:** local email/password (**Argon2id**) + JWT sessions. OIDC/SSO deferred to v1.1.
- **AuthZ:** single middleware; copies do **not** inherit master membership (isolation is the whole point); WOPI/share tokens are capability tokens — hashed, scoped, short-TTL.
- **Audit:** append-only `audit_events` on **mutations + public share-link reads** (`ponytail: not every GET — auditing reads is write-amplification for little value`). Exportable per document.
- **At rest / transport:** TLS at the proxy; token/secret columns hashed or envelope-encrypted, never plaintext.
- **Deferred:** ClamAV AV-scan (`ponytail: v1 trusts member .docx uploads — known ceiling, add ClamAV when untrusted upload paths exist`), MFA.

---

## 12. Testing

- **Conformance suite:** SimulDocs acceptance criteria **E1–E12** as automated end-to-end tests — public proof of behavior, a genuine differentiator.
- **Unit:** versioning engine (numbering R1–R8, branch-on-stale-base, merge ancestry resolution), WOPI `commit_save` path (dedupe, fast-forward vs branch).
- **Integration:** every API endpoint × role for the permission matrix; copies never leak master drafts.
- **Robustness:** `WmlComparer` failure path degrades gracefully; LibreOffice timeout/kill.

---

## 13. Milestones

| M | Contents | Exit |
|---|---|---|
| **M0** | Monorepo, CI, compose boots, auth, folders, upload → `0.0.1` | E1–E2 green |
| **M1** | Collabora WOPI editing, `commit_save`, branch-on-stale, redline diff, numbering | E3–E5 green |
| **M2** | Publish minor/major, PDF, approvals, name/revert/download/share | E6–E8, E10–E11 green |
| **M3** | Public API GA: OpenAPI, tokens, `/docs`; E1–E12 conformance suite public | API drives full flow unattended |
| **M4** | Copies & push/merge (accept/reject review) | E9 green |
| **M5** | v1.0.0: docs site, security pass, license/DCO | Tagged `v1.0.0` |

API GA (M3) ships before copies/push (M4) — the product is fully usable via the bundled editor from M1, and the API is the differentiator.

---

## 14. Open decisions

1. **License.** Repo is MIT today. Recommendation: **AGPL-3.0 for the server** (a competitor can't run a closed hosted fork without contributing back), **MIT for API clients/examples**. Decide before first public commit; add a **DCO** from commit #1.
2. **Coalescing default** — start with dedupe + on-close; tune only if Collabora produces version spam in practice.

---

## Appendix: ponytail decisions (deliberate cuts, with ceilings)

| Cut | Ceiling / upgrade path |
|---|---|
| Single process, no queue | Split a service / add a durable queue if a profiler demands it |
| `SELECT ... FOR UPDATE` not SERIALIZABLE | Fine at per-document granularity; revisit if cross-document txns appear |
| Diffs on-demand, no precompute | Precompute on commit if compare-view latency bites |
| No `Idempotency-Key` infra | Add middleware when the public SDK/retry story exists |
| Collabora-only, no editor abstraction | Add ONLYOFFICE as an isolated change in v1.1 |
| No save-timer, dedupe + on-close only | Add N-second coalescing if version spam observed |
| Indented-list history, no DAG | Build the graphical revision graph in v1.1 |
| Filesystem blobs, no S3, no GC | S3 backend via env; GC when retention policy demands it |
| No ClamAV | Add AV gate when untrusted upload paths exist |
| Audit mutations + share reads only | Add read-audit if a compliance customer needs "who viewed X" |
