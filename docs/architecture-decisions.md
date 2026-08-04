# easydocs — Architecture Decision Records

How easydocs works, told through the decisions that shaped it. Each record is Context → Decision →
Consequences. Statuses are current as of v1.1.0. The deeper design rationale lives in
[`docs/superpowers/specs/`](superpowers/specs/); this document is the tour.

**The system in one paragraph:** easydocs is a self-hostable version-control service for `.docx`
files. One ASP.NET Core process serves a REST API and a React SPA; PostgreSQL holds all state;
document bytes live in a content-addressed blob store (filesystem or S3); Collabora Online provides
in-browser editing and LibreOffice renders PDFs. Every save — from any editor — becomes an
immutable, numbered version through a single write path.

---

## ADR-1: Every save is an immutable version

**Context.** The product's whole reason to exist is that documents get silently overwritten —
"final_v2_REAL.docx" is version control by filename. Any design where an edit mutates stored bytes
recreates the problem it's meant to kill.

**Decision.** A version, once written, is never modified or deleted. A save creates a *new* version
row pointing at a *new* (or deduplicated) blob. Rename, revert, merge, publish — all of them are
new rows or metadata stamps; none rewrite history. Even "revert" commits the old content as a new
head rather than moving a pointer backwards.

**Consequences.** History is trustworthy by construction and the audit trail can't disagree with
it. Storage grows monotonically (mitigated by content-addressed dedup, ADR-3, and garbage
collection of *unreferenced* blobs only, ADR-8). Every foreign key onto versions/blobs is
`ON DELETE RESTRICT` — the schema itself refuses operations that would dangle history.

## ADR-2: One write path for every editor

**Context.** Versions can be born five ways: HTTP upload, browser edit (Collabora/WOPI), desktop
edit (Word/WebDAV), merge, revert, copy push-back. Five code paths means five slightly different
sets of bugs, and invariants (numbering, dedup, branching, audit) enforced in some places and
forgotten in others.

**Decision.** All of them funnel through one method — `VersioningService.CommitSaveAsync` — which
owns numbering, sha-dedup, branch-on-stale, the audit row, and job enqueueing, inside one
transaction under a per-document lock.

**Consequences.** Word (WebDAV) and Collabora (WOPI) are just clients of the same contract; adding
the desktop editor in v1.1 required no new versioning logic. A save of unchanged bytes is a no-op
everywhere (dedup lives in the one path). The trade: the method carries several concerns, and its
correctness is guarded by the heaviest test coverage in the repo.

## ADR-3: Content-addressed, write-once blob storage

**Context.** Documents are opaque binary blobs, versions are many, and most versions share most of
their bytes with siblings only at the whole-file level (docx is a zip; delta-compression is not
worth its complexity here).

**Decision.** Blobs are stored by the sha256 of their bytes — the hash *is* the key (sharded
directories on the filesystem, the bare hash as the object key on S3). Storage is write-once:
identical content is stored exactly once, whoever uploads it, whenever. The `Blobs` table carries
metadata; MIME is *sniffed from the bytes* at serve time, never trusted from the client.

**Consequences.** Dedup is free and global. Integrity checks are inherent (the key proves the
content). Caches keyed by content (ADR-7's `version_diffs`) are automatically shared across
documents. The backend is swappable (`BlobStore=filesystem|s3`) behind a four-method interface,
because nothing above it knows about paths — only hashes.

## ADR-4: Postgres is the only stateful dependency — including the job queue

**Context.** The system needs background work (diff summaries, PDF renders, search indexing) that
survives restarts. The industry default is a broker (Redis/RabbitMQ/SQS) — a second stateful
service for a self-hoster to operate, back up, and secure.

**Decision.** The queue is a Postgres table. Jobs are enqueued *in the same transaction* as the
domain write that needs them (a job exists iff the work committed), claimed with
`FOR UPDATE SKIP LOCKED`, leased with a `RunAfter` bump so a crashed worker's job retries instead
of running twice, and dropped loudly after five failures. In-memory channels survive only as
latency nudges — losing one costs a poll interval, nothing more. Full-text search follows the same
philosophy: a Postgres `tsvector` + GIN index, not Elasticsearch.

**Consequences.** `docker compose up` needs exactly three containers, and exactly one of them holds
state you must back up. Transactional enqueue eliminates the classic "committed the row, lost the
message" outbox problem for free. Ceiling: Postgres-as-queue is fine for this workload's volume;
a future firehose workload would revisit it.

## ADR-5: Concurrent edits branch; nothing ever overwrites

**Context.** Two people editing "the same document" simultaneously is the normal case, not the
edge case — and the classic outcomes (last-writer-wins, or hard locks) both lose work or block it.

**Decision.** Every save declares the version it was based on. Saving on the current head
fast-forwards the main line; saving on a *stale* base creates a **concurrent branch** — visible in
the history, indented under its fork point (or as its own lane in the graph view). Merging is
one click: Clippit's `WmlComparer` produces a new main-line version with the incoming author's
changes as tracked changes. Merged branches are marked, never deleted.

**Consequences.** No edit is ever lost, and no editor is ever blocked — the cost is that users see
branches, which the UI spends real effort making legible. Merge output is a genuine Word document
with genuine tracked changes, reviewable in any Word-compatible editor.

## ADR-6: Editing happens in real editors, through standard protocols

**Context.** Building a docx editor is a decade of work; shipping a half-editor poisons trust in
the versioning underneath it.

**Decision.** easydocs never edits documents itself. In the browser, Collabora Online talks to an
easydocs **WOPI** host; on the desktop, Microsoft Word talks to a minimal easydocs **WebDAV**
class-2 surface via an `ms-word:` URL. Both editor sessions are capability-scoped: a short-TTL
token that authorizes exactly one edit session and nothing else (a session token never authorizes
the app; an app cookie never authorizes an edit endpoint). Saves land in ADR-2's single write path.

**Consequences.** Fidelity is the real editor's, not ours. The protocols are server-to-server
contracts, so the test suite drives Word's and Collabora's halves of the conversation directly —
no desktop needed in CI. Trade: Collabora is a third container, and the editor UX is theirs to
own, ONLYOFFICE support later is an isolated swap.

## ADR-7: Redlines are computed from the documents, and cached by content

**Context.** The comparison people actually need — "what changed between these two versions?" —
must not depend on anyone having remembered to turn Track Changes on.

**Decision.** Diffs are computed from the two documents' bytes with `WmlComparer`: a numeric
summary (insertions/deletions) eagerly via the job queue on every commit, and the full redline
(HTML + a tracked-changes docx) on demand. Results cache in `version_diffs`, keyed by
`(from_sha, to_sha)` — content hashes, not version ids — so identical comparisons are computed once
per *instance*, not once per document. Comparison failure degrades to "unavailable"; it never
breaks the version history around it.

**Consequences.** Redlines work on any pair of versions, ever. The content-hash key means the
cache is racy by design (the eager worker and an inline compare can both compute the same pair);
both race outcomes are handled — the loser adopts the winner's row. That race produced two real
bugs (a 422, then a never-filling cache), both now pinned by deterministic tests.

## ADR-8: Garbage collection deletes only what nothing references

**Context.** ADR-1 says history is immutable; ADR-3 says storage is write-once. Yet failed
commits, rejected pushes, and manual surgery can strand blobs no row points at — "nothing is ever
deleted" eventually becomes a disk-space complaint.

**Decision.** A daily sweep deletes blobs referenced by **no** `Versions` or `VersionDiffs` column,
skipping anything younger than a 24h grace window (an upload writes its blob before its version row
commits). Deletion is object-then-row so a crash leaves a retryable row, never an unfindable
object; the Restrict FKs make it structurally impossible for the sweep to take anything history
points at.

**Consequences.** Immutability and bounded storage coexist. The residual race (identical bytes
re-uploaded during the sweep of that exact unreferenced hash) is documented, sub-second, and
self-healing on re-upload.

## ADR-9: Sessions are JWTs; API tokens are hashed capabilities; the `org` claim is the perimeter

**Context.** Three kinds of callers: browsers (cookie), scripts (`ed_` personal access tokens),
and half-authenticated states (an MFA challenge). They must not be confusable with each other.

**Decision.** Browser sessions are HS256 JWTs in a `Secure`/`HttpOnly` cookie carrying
`sub` + `org` — a session is always scoped to exactly one organization. `ed_` tokens are stored
only as SHA-256 hashes and resolve through a separate handler; a composite scheme routes by prefix.
The default authorization policy requires the **`org` claim on every endpoint** — which is
precisely what confines the MFA challenge token (deliberately org-less, 5-minute TTL) to the one
endpoint that finishes MFA. SSO (OIDC) converts the IdP handshake into the *same* session JWT, and
provisions accounts only from **verified** emails.

**Consequences.** One enforcement point instead of per-endpoint vigilance. A leaked DB dump yields
no usable tokens. Trade, documented: JWTs aren't revocable before expiry (sign-out clears the
cookie; a revocation list is the named upgrade path).

## ADR-10: The API is the product; the UI is its first client

**Context.** Admin UIs drift from their APIs the moment the UI gets a private endpoint "just for
now."

**Decision.** Everything the UI does, the API does — same surface, not a subset. The OpenAPI 3.1
document is generated from the running code and served by the app itself (`/docs`,
`/openapi/v1.json`), so it cannot go stale. Live updates are plain server-sent events per document
— the same stream the SPA uses is the one integrators get. The SPA keeps no state library; SSE *is*
the cache invalidation.

**Consequences.** Automation is never second-class (the conformance suite drives the full document
lifecycle over raw HTTP). The SPA stays small — three runtime dependencies. Trade: every UI
feature costs an honest, documented endpoint.

## ADR-11: One container, three processes' worth of restraint

**Context.** Self-hosters are the audience. Every additional service multiplies their operational
burden more than ours.

**Decision.** The deployable unit is one image: ASP.NET Core minimal APIs serving the built SPA
from `wwwroot`, with LibreOffice bundled for PDF rendering. The compose stack is exactly app +
PostgreSQL + Collabora. Migrations run at boot. Misconfiguration **fails fast at startup** with a
message naming the problem — a too-short JWT secret, an unparseable trusted proxy, an unknown blob
backend — rather than surfacing as a runtime mystery. Config that would be silently ignored is a
boot *error* by policy.

**Consequences.** The quickstart is `cp .env.example .env`, two secrets, `docker compose up`.
Releases ship a compose bundle generated from the dev compose file (they cannot drift), pinned to a
cosign-signed multi-arch image.

## ADR-12: Tests prove the shipped artifact, and are forbidden to skip

**Context.** Test suites rot in two quiet ways: they test a dev server that differs from the
shipped image, and they skip when a dependency is missing — reading as coverage that doesn't exist.
Both happened here (two PDF tests skipped silently for five milestones and nearly shipped a broken
release gate).

**Decision.** The browser suite (Playwright) runs against the **built container image**, not a dev
server. CI installs every dependency and **fails if any test skips**. A release tag re-runs the
full verification before anything publishes — a release is the one build nobody re-runs before
trusting. Conformance criteria (E1–E12) encode the spec's promises as executable checks, including
*negative* ones (endpoints that must not exist).

**Consequences.** Green means the artifact users pull works, on both architectures. Trade:
negative guards age — two of them had to learn that v1.1 legitimately ships WebDAV.

## ADR-13: AGPL server, MIT SDKs (when they exist)

**Context.** A self-hostable server wants copyleft (improvements to *hosted* instances must flow
back); client libraries want the opposite (copyleft on an SDK punishes the API's own users).

**Decision.** Everything in the repository is AGPL-3.0. Future client SDKs will live under
`packages/*` with their own MIT license — a directory boundary you can point at. Until that
directory exists, nothing here is MIT. Contributions are DCO (`git commit -s`), no CLA;
contributors keep their copyright.

**Consequences.** A company can run, modify, and even sell hosted easydocs — as long as its users
get the source of what's actually serving them. Integrating with the API from proprietary code is
explicitly intended and will be MIT-smooth once SDKs exist.

---

*Questions or challenges to any of these? That's what they're for — open an issue or a Discussion
on the repo. The per-decision fine print (and what each plan got wrong before contact with
reality) lives in `docs/superpowers/specs/` and `docs/superpowers/plans/`.*
