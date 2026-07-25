# easydocs M1 — Versioning Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the M0 skeleton into a working versioning engine. Add in-browser `.docx` editing via **Collabora Online over a WOPI host**, generalize M0's upload path into a single `commit_save` write path (dedupe + **branch-on-stale-base** + authoritative counter increment), make the **X.Y.Z numbering rules R1–R8** authoritative on `documents.version_counter_*` (spec §5.1), add **one-click concurrent-branch merge** via Open-XML-PowerTools `WmlComparer` (3-way, tracked-changes output, graceful degradation), **redline diff** (on-demand HTML cached in `version_diffs`) with an **eager numeric summary** computed in an in-process `BackgroundService`, an **Import New Version** endpoint, and **SSE `version.created`**. Exit gate: acceptance criteria **E3 (Edit/version)**, **E4 (Branch/merge)**, **E5 (Numbering)** green.

**Architecture:** Still one ASP.NET Core process (spec §3). New pieces are all in-process: a WOPI host under `/wopi/*`, a `VersioningService` owning the `commit_save` transaction + numbering, an `IDiffService` and `IMergeService` wrapping `WmlComparer`, an in-memory `Channel<T>`-backed `BackgroundService` for the eager numeric summary, and an in-process SSE `IEventBus`. Collabora Online (CODE) joins the compose stack as a sibling container that talks back to the app over the compose network. No queue, no Redis, no editor abstraction (spec §3, §6).

**Tech Stack (added on top of M0):** Collabora Online (CODE) container `collabora/code` · Open-Xml-PowerTools `WmlComparer` (the `Clippit` maintained fork if the original package does not restore on `net10.0` — see Task 1) · `System.Threading.Channels` (BCL, no package) · raw `text/event-stream` writes for SSE · `System.IdentityModel.Tokens.Jwt` (already present) for WOPI access tokens.

**Spec:** `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` — §5 (versioning engine), §5.1 (numbering source of truth), §5.2 (`commit_save`), §5.3 (merge), §6 + §6.1 (Collabora/WOPI + networking), §7 (diff/PDF, no queue), §10.1 (endpoints), §10.2 (SSE), §12.1 E3–E5, §13 (M1 row).

**Builds directly on these M0 files (read them first):**
- `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` — the `Upload` handler (lines 93–135) is the seed of `commit_save`: `IBlobStore.PutAsync` → content-addressed `Blob` upsert → `SELECT … FOR UPDATE` on `"Documents"` (`ExecuteSqlInterpolatedAsync`) → `VersionCounterRev += 1` → insert `DocumentVersion` on the `Ordinal==0` main branch. M1 extracts this into `VersioningService.CommitSaveAsync` and generalizes it. Also the `AuthorizeAsync` helper (lines 140–158) mapping `AccessResult`→404/403.
- `src/EasyDocs.Api/Auth/DocumentAuthorization.cs` — `ResolveAsync(db, orgId, userId, documentId)` → `(AccessResult, DocRole?)` + `CanEdit(role)`. Reuse verbatim; WOPI/merge/diff endpoints route through it.
- `src/EasyDocs.Api/Auth/CurrentUser.cs` — `UserId(ctx.User)`/`OrgId(ctx.User)` from `sub`/`org` claims.
- `src/EasyDocs.Api/Auth/JwtService.cs` — HS256 `Issue(userId, orgId)` pattern reading `Jwt:Secret`; M1 adds a second short-TTL token type (WOPI access token).
- `src/EasyDocs.Api/Storage/IBlobStore.cs` — `PutAsync(stream)`/`ExistsAsync(sha)`/`OpenReadAsync(sha)` + `BlobResult(Sha256, SizeBytes)`. Reused by WOPI `PutFile`, diff, and merge.
- `src/EasyDocs.Api/Domain/*.cs` — `EditSession` (`BaseVersionId`, `BranchId?`, `LockValue`, `LockExpiresAt`, `LastCommittedSha`, `ClosedAt`), `Branch` (`Ordinal`, `Kind`, `RootVersionId`, `MergedIntoVersionId`), `DocumentVersion` (`SeqInBranch`, `ParentVersionId`, `MergeParentVersionId`, `Major/Minor/Revision`, `Source`, `BlobSha256`, `PdfBlobSha256`), `VersionDiff` (`FromSha256`/`ToSha256` composite PK, `Insertions`/`Deletions`/`Moves`/`FormatChanges`, `RedlineBlobSha256`, `HtmlBlobSha256`). **All tables already exist** — M0 migrated the full schema; M1 adds no new tables.
- `src/EasyDocs.Api/Common/Problem.cs` — `Problem.Of(status, title, detail)` RFC-7807 helper.
- `src/EasyDocs.Api/Program.cs` — DI + `app.MapDocumentEndpoints()` etc.; the M0 SPA-fallback block already terminates `/wopi/{**rest}` with a 404 (map WOPI routes **before** it). Add `MapWopiEndpoints`, `MapEditingEndpoints`, `MapEventEndpoints`, `MapMergeEndpoints`, service registrations, and `AddHostedService`.
- `tests/EasyDocs.Api.Tests/ApiFactory.cs` — Testcontainers harness; M1 adds config keys to `ConfigureWebHost`.
- `deploy/compose/docker-compose.yml`, `Dockerfile` — extend with the `collabora` service + base-URL env vars. (LibreOffice is already bundled in the runtime image from M0 — M2 uses it.)

**No new tables.** M0 declared the entire v1 schema. If a numbering/merge rule needs a scalar column that is genuinely missing, add it as a **one-column migration** in the task that needs it and say so; do not add tables.

---

## Prerequisites (do once, before Task 1)

- [ ] Docker running (`docker ps`) — Testcontainers + compose both need it.
- [ ] M0 present/merged: `dotnet build` and `dotnet test` green on the base.
- [ ] `docker pull collabora/code` ahead of time so the first compose boot is not a surprise.
- [ ] Two valid `.docx` fixtures checked in at `tests/EasyDocs.Api.Tests/Fixtures/base.docx` + `edited.docx` (a base + an edited copy). Generate once with the bundled LibreOffice headless. A 5-byte fake (as M0 used) is fine for upload/counter tests but **not** for `WmlComparer` tests — those need real OOXML.

---

## File Structure (new/changed in M1)

```
src/EasyDocs.Api/
  Versioning/
    Numbering.cs                # pure counter transitions R1–R8, DB-free, unit-testable
    VersioningService.cs        # commit_save (the single write path) + numbering wiring
  Editing/
    CollaboraDiscovery.cs       # fetch+cache discovery XML daily -> action URL for docx
    WopiAccessToken.cs          # short-TTL {sid,uid,perms,typ:wopi} JWT issue/validate
    EditingEndpoints.cs         # POST /versions/{vid}/sessions, DELETE /sessions/{sid}
    WopiEndpoints.cs            # /wopi/files/{fileId}(/contents) + LOCK/UNLOCK/REFRESH/GET_LOCK
  Diffing/
    WmlComparerDiffService.cs   # concrete (one impl, no interface); wraps every WmlComparer call, degrades gracefully
    DiffSummaryWorker.cs        # BackgroundService: eager numeric summary on commit (Channel<T>)
  Merging/
    WmlComparerMergeService.cs  # concrete; 3-way merge, common ancestor = branch RootVersionId
    MergeEndpoints.cs           # POST /documents/{id}/merges {left,right}
  Events/
    EventBus.cs                 # concrete in-process SSE fan-out, keyed by documentId
    EventEndpoints.cs           # GET /api/v1/documents/{id}/events (SSE)
  Documents/
    DocumentEndpoints.cs        # MODIFIED: Upload delegates to VersioningService; add :import, download, version-counter PUT, compare
deploy/compose/docker-compose.yml   # MODIFIED: + collabora service, + WOPI_HOST_URL/PUBLIC_BASE_URL/COLLABORA_URL
deploy/compose/.env.example         # MODIFIED: new keys
tests/EasyDocs.Api.Tests/
  ApiFactory.cs               # MODIFIED: inject the three URLs + COLLABORA_ACTION_URL test seam
  Fixtures/base.docx, edited.docx
  NumberingTests.cs           # R1–R8 unit tests (pure)
  CommitSaveTests.cs          # dedupe, fast-forward, branch-on-stale
  EditSessionTests.cs         # mint session, token roundtrip, close
  WopiHostTests.cs            # CheckFileInfo/GetFile/PutFile/lock lifecycle
  DiffTests.cs                # numeric summary eager, redline cached, WmlComparer failure degrades
  MergeTests.cs               # concurrent branches -> tracked-changes merge, branch closed
  SseTests.cs                 # version.created delivered
  DownloadTests.cs            # R8 filename, pdf->409, manual counter override
```

**Decomposition rationale:** each capability (versioning, editing/WOPI, diffing, merging, events) is a folder owning its endpoints + services + DTOs, mirroring M0's group-by-responsibility layout. `Numbering.cs` is a pure, DB-free function set so R1–R8 are unit-tested without a container.

---

## Task 1: M1 branch + config keys + pin the WmlComparer package

**Files:**
- Modify: `deploy/compose/.env.example`, `tests/EasyDocs.Api.Tests/ApiFactory.cs`, `src/EasyDocs.Api/appsettings.json`, `src/EasyDocs.Api/EasyDocs.Api.csproj`

- [ ] **Step 1: Branch off main** — `git checkout main && git checkout -b m1-versioning-core`

- [ ] **Step 2: Add the M1 config keys** (spec §6.1). Three URLs are threaded through because the browser and the Collabora container see the app at different addresses:
  - `PUBLIC_BASE_URL` — where the browser loads the Collabora iframe (e.g. `https://docs.example.com`).
  - `WOPI_HOST_URL` — where Collabora reaches the app internally (e.g. `http://easydocs:8080`), injected into the WOPI action URL.
  - `COLLABORA_URL` — where the app fetches Collabora's discovery XML internally (e.g. `http://collabora:9980`).

  Add them to `deploy/compose/.env.example`. In `ApiFactory.ConfigureWebHost` add deterministic test values plus a discovery test seam:
```csharp
["PUBLIC_BASE_URL"] = "http://localhost",
["WOPI_HOST_URL"]   = "http://localhost",
["COLLABORA_URL"]   = "http://localhost:9980",
["COLLABORA_ACTION_URL"] = "http://localhost:9980/browser/dist/cool.html?", // test seam: skip live discovery
```

- [ ] **Step 3: Pin the WmlComparer package now (fail fast — decides Tasks 8/9).**
```bash
dotnet add src/EasyDocs.Api package Open-Xml-PowerTools
dotnet build
```
  If it does **not** restore/compile on `net10.0`, swap to the maintained fork:
```bash
dotnet remove src/EasyDocs.Api package Open-Xml-PowerTools
dotnet add src/EasyDocs.Api package Clippit   # maintained OpenXmlPowerTools fork; WmlComparer under Clippit.Word
dotnet build
```
  Record the winner in a one-line comment at the top of `WmlComparerDiffService.cs`. `ponytail: pick whichever restores; both expose the same WmlComparer algorithm — no abstraction over it.` Expected: `Build succeeded`.

- [ ] **Step 4: Commit** — `git commit -s -am "chore(m1): Collabora/WOPI config keys + pin WmlComparer package"`

---

## Task 2: Numbering engine (R1–R8) — pure, unit-tested

> The heart of E5 and spec §5.1 ("`documents.version_counter_*` is the single source of truth"). Build it DB-free first so every rule is nailed by a fast unit test, then wire it into `commit_save` (Task 3) and the manual-override endpoint (Task 10). The publish transitions (R3/R4/R6) are exercised here at the engine level; the publish **endpoint** that calls them ships in M2 — E5's publish-path assertions are green now via these unit tests, and re-validated end-to-end in M2.

**Files:**
- Create: `src/EasyDocs.Api/Versioning/Numbering.cs`
- Test: `tests/EasyDocs.Api.Tests/NumberingTests.cs`

- [ ] **Step 1: Write the failing tests.** A counter is `(int Major, int Minor, int Rev)`.
```csharp
public class NumberingTests
{
    [Fact] public void R1_first_draft_is_0_0_1() => Assert.Equal((0,0,1), Numbering.NextDraft((0,0,0)));
    [Fact] public void R2_draft_increments_rev() => Assert.Equal((0,0,8), Numbering.NextDraft((0,0,7)));
    [Fact] public void R3_publish_minor_0_0_7_to_0_1_0() => Assert.Equal((0,1,0), Numbering.PublishMinor((0,0,7)));
    [Fact] public void R4_publish_major_0_0_7_to_1_0_0() => Assert.Equal((1,0,0), Numbering.PublishMajor((0,0,7)));
    [Fact] public void R5_manual_allows_zeroes() => Assert.Equal((0,0,0), Numbering.Manual(0,0,0));
    [Fact] public void R5_manual_rejects_negatives() => Assert.Throws<ArgumentOutOfRangeException>(() => Numbering.Manual(-1,0,0));
    [Fact] public void R6_draft_after_minor_publish_continues_from_counter() => Assert.Equal((0,1,1), Numbering.NextDraft(Numbering.PublishMinor((0,0,7))));
    [Fact] public void R8_download_filename() => Assert.Equal("aces__Master_Lease-v0.1.0.docx", Numbering.DownloadFileName("aces","Master Lease",(0,1,0),"docx"));
}
```
  (R7 = revert numbering — a revert is a new draft equal to the target content, i.e. `NextDraft` on the current counter; asserted end-to-end when Revert ships in M2.)

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter NumberingTests` → FAIL (compile: `Numbering` missing).

- [ ] **Step 3: Implement `Numbering.cs`** — a static class of pure functions over `(int Major, int Minor, int Rev)`:
  - `NextDraft(c)` → `(c.Major, c.Minor, c.Rev+1)` (R1/R2/R7).
  - `PublishMinor(c)` → `(c.Major, c.Minor+1, 0)` (R3).
  - `PublishMajor(c)` → `(c.Major+1, 0, 0)` (R4).
  - `Manual(M,m,rev)` → all ≥ 0 or `throw new ArgumentOutOfRangeException`; return the triple (R5).
  - `DownloadFileName(orgSlug, docName, c, ext)` → sanitize `docName` (spaces→`_`, strip filesystem-hostile chars), format `{slug}__{name}-v{M}.{m}.{rev}.{ext}` (R8).

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter NumberingTests` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): pure numbering engine R1-R8 (counter-authoritative)"`

---

## Task 3: `VersioningService.CommitSaveAsync` — the single write path

> Extract M0's `DocumentEndpoints.Upload` transaction into a reusable service (spec §5.2). HTTP upload/import and WOPI `PutFile` both call it. Branch-on-stale-base is added in Task 6; this task delivers the **fast-forward** (main-branch) path plus **sha dedupe**, keeping M0's behavior identical but relocated.

**Files:**
- Create: `src/EasyDocs.Api/Versioning/VersioningService.cs`
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (delegate `Upload`; add `:import`), `Program.cs` (register scoped `VersioningService`)
- Test: `tests/EasyDocs.Api.Tests/CommitSaveTests.cs`; M0 `DocumentUploadTests` must stay green

- [ ] **Step 1: Write the failing tests** (spec §5.2, E3 "unchanged re-save creates none")
```csharp
[Fact] public async Task Second_save_of_same_sha_creates_no_new_version() {
    // create doc, upload bytes X -> 0.0.1; upload bytes X again -> NO new version; one version; counter still 1
}
[Fact] public async Task Import_creates_version_with_source_import() {
    // upload X (0.0.1); POST /versions:import bytes Y -> 0.0.2, source=Import, parent=previous head
}
[Fact] public async Task Fast_forward_save_advances_main_branch_seq() {
    // two distinct uploads -> seq_in_branch 1 then 2 on main; parent chain intact
}
```
  > **Dedupe scope note:** M0's `Upload` deduped the **blob** (content-addressed) but always inserted a new *version* row. Spec §5.2 step 2 dedupe is per **session** (`session.last_committed_sha`) — Collabora re-PUTs unchanged files. For the sessionless HTTP upload/import path, the dedupe key is "incoming sha == current branch head sha": if equal, no-op (no new version). The session path (Tasks 5/6) uses `EditSession.LastCommittedSha`. *(M0's `DocumentUploadTests` has no second-identical-upload assertion, so this dedupe adds behavior without breaking an M0 test; if you find a test expecting a duplicate upload to yield a new version, update it to match E3 "unchanged re-save creates none".)*

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter CommitSaveTests` → FAIL.

- [ ] **Step 3: Implement `VersioningService.CommitSaveAsync`.** Records:
```csharp
public sealed record CommitInput(
    Guid DocumentId, string BlobSha256, long SizeBytes, VersionSource Source, Guid ActorUserId,
    Guid? SessionId = null, Guid? BaseVersionId = null, Guid? ExplicitBranchId = null,
    Guid? MergeParentVersionId = null);   // second parent for Source=Merge (Task 9)
public sealed record CommitResult(Guid VersionId, int Major, int Minor, int Revision, Guid BranchId, bool Deduped);
public async Task<CommitResult> CommitSaveAsync(CommitInput input, CancellationToken ct);
```
  Body (generalizes M0 `Upload` lines 106–131):
  1. Upsert the `Blob` row if `!await db.Blobs.AnyAsync(sha)` (blob already on disk — caller ran `IBlobStore.PutAsync`).
  2. `BeginTransactionAsync`; `ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {id} FOR UPDATE")` — per-doc mutual exclusion (M0 pattern).
  3. Resolve main branch (`Ordinal==0`) + head version (max `SeqInBranch`).
  4. **Dedupe:** if `input.BlobSha256 == head?.BlobSha256` (or, when `SessionId` set, `== session.LastCommittedSha`) → commit tx, return `Deduped=true` with head's number, **no insert**.
  5. **Target branch** (Task 6 fills the stale-base arm): for now `ExplicitBranchId ?? main.Id`, fast-forward. `NextDraft` from `doc.VersionCounter*` via `Numbering.NextDraft`; write the three counter columns back.
  6. Insert `DocumentVersion{ Source, BlobSha256, ParentVersionId=head?.Id, MergeParentVersionId=input.MergeParentVersionId, SeqInBranch=maxSeq+1, Major/Minor/Revision from counter, CreatedBy=ActorUserId }`.
  7. If `SessionId` set, update `EditSession.LastCommittedSha`.
  8. Commit tx.
  9. **After commit (non-deduped):** `IEventBus.Publish(docId,"version.created",…)` (Task 7) and enqueue `DiffJob(parentSha,newSha,docId)` on the channel (Task 8). Wire these no-op-safe now.

  Refactor `DocumentEndpoints.Upload` to `PutAsync` → `CommitSaveAsync(CommitInput{Source=Upload, BaseVersionId=head})`. Add `POST /api/v1/documents/{id}/versions:import` (multipart, `Source=Import`). Register `VersioningService` scoped.

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter CommitSaveTests` then `dotnet test --filter DocumentUploadTests` (M0 regression) → both PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): VersioningService.CommitSaveAsync single write path + import endpoint"`

---

## Task 4: Collabora discovery + WOPI access token + edit-session mint

> Spec §6, §6.1. Minting a session hands Collabora `file_id = session_id` + a short-TTL access token; the editor iframe URL is built from Collabora's cached discovery XML.

**Files:**
- Create: `src/EasyDocs.Api/Editing/CollaboraDiscovery.cs`, `WopiAccessToken.cs`, `EditingEndpoints.cs`
- Modify: `Program.cs` (register `CollaboraDiscovery` singleton + `HttpClient`, `WopiAccessToken`; `app.MapEditingEndpoints()`)
- Test: `tests/EasyDocs.Api.Tests/EditSessionTests.cs`

- [ ] **Step 1: Write the failing tests**
```csharp
[Fact] public async Task Editor_mints_session_with_editor_url_and_token() {
    // Editor member: POST /api/v1/versions/{vid}/sessions -> 201 { sessionId, editorUrl, accessToken, accessTokenTtlSeconds }
    // editorUrl contains WOPISrc={WOPI_HOST_URL}/wopi/files/{sessionId}; EditSession row pinned to BaseVersionId==vid
}
[Fact] public async Task Viewer_cannot_mint_session_403() { }
[Fact] public void Access_token_roundtrips_sid_uid_perms() {
    // WopiAccessToken.Issue(sid,uid,"w") then Validate(token) -> (sid,uid,"w")
}
[Fact] public async Task Login_jwt_is_rejected_as_wopi_token() { /* typ check: a normal session JWT fails Validate */ }
[Fact] public async Task Close_session_sets_closed_at() { /* DELETE /sessions/{sid} -> 204, ClosedAt set */ }
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter EditSessionTests` → FAIL.

- [ ] **Step 3: Implement.**
  - `WopiAccessToken`: HS256 JWT (reuse `Jwt:Secret` via `IConfiguration`, same key as `JwtService`) with claims `sid`, `sub`, `perms` (`"w"`/`"r"`), `typ="wopi"`, 30-min expiry. `Issue(sid,uid,perms)` and `Validate(token) -> (Guid Sid, Guid Uid, string Perms)?` (reject if `typ != "wopi"` — a login cookie must not authorize WOPI, and vice-versa).
  - `CollaboraDiscovery`: `Task<string> ActionUrlForDocxAsync(ct)` — if config `COLLABORA_ACTION_URL` is set (test/dev seam) return it; else fetch `{COLLABORA_URL}/hosting/discovery`, parse `<action ext="docx" name="edit">`'s `urlsrc`, cache 24h in a field. `ponytail: daily refresh via a timestamp field, no cron.`
  - `EditingEndpoints`: `POST /api/v1/versions/{vid}/sessions` (`.RequireAuthorization()`; load the version's document; `DocumentAuthorization.ResolveAsync` + `CanEdit` else 403; create `EditSession{ BaseVersionId=vid, UserId, LastCommittedSha=null }`; build `editorUrl = {actionUrl}WOPISrc={WOPI_HOST_URL}/wopi/files/{sessionId}&access_token={token}`; return 201). `DELETE /api/v1/sessions/{sid}` (owner-only; set `ClosedAt`; 204).

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter EditSessionTests` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): edit-session mint + WOPI access token + Collabora discovery"`

---

## Task 5: WOPI host endpoints (CheckFileInfo / GetFile / PutFile / locks)

> Spec §6. Collabora calls these; `PutFile` funnels into `commit_save`. Locks live on the `EditSession` row (`LockValue`, `LockExpiresAt`, 30-min TTL). No NuGet — plain HTTP handlers, authorized by the WOPI access token (query param), **not** the cookie/JWT pipeline.

**Files:**
- Create: `src/EasyDocs.Api/Editing/WopiEndpoints.cs`
- Modify: `Program.cs` (`app.MapWopiEndpoints()` mapped **before** the M0 `/wopi/{**rest}` 404 catch-all)
- Test: `tests/EasyDocs.Api.Tests/WopiHostTests.cs`

- [ ] **Step 1: Write the failing tests** — drive the WOPI protocol with the test client, `access_token` as a query param (as Collabora does).
```csharp
[Fact] public async Task CheckFileInfo_returns_name_size_write_perm() {
    // GET /wopi/files/{sid}?access_token=... -> 200 { BaseFileName, Size, OwnerId, UserId, UserCanWrite=true, Version }
}
[Fact] public async Task GetFile_streams_base_version_bytes() { /* GET .../contents -> body == base blob bytes */ }
[Fact] public async Task PutFile_creates_new_version_via_commit_save() {
    // LOCK then POST .../contents (edited docx) -> 200; new DocumentVersion exists; EditSession.LastCommittedSha updated
}
[Fact] public async Task Lock_unlock_lifecycle_and_conflict() {
    // LOCK sets LockValue; GET_LOCK echoes it; PUT/UNLOCK with mismatched X-WOPI-Lock -> 409 + X-WOPI-Lock header
}
[Fact] public async Task Invalid_access_token_401() { }
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter WopiHostTests` → FAIL.

- [ ] **Step 3: Implement `WopiEndpoints`** under `/wopi/files/{fileId}` (`fileId == sessionId`; auth via `WopiAccessToken.Validate(Request.Query["access_token"])`; do **not** `.RequireAuthorization()`):
  - `GET /wopi/files/{fileId}` → **CheckFileInfo** JSON: `BaseFileName="{Doc_Name}.docx"`, `Size`=base blob size, `OwnerId`, `UserId`, `UserCanWrite = perms=="w"`, `Version`=base version id/sha, `SupportsLocks=true`, `SupportsUpdate=true`.
  - `GET /wopi/files/{fileId}/contents` → **GetFile**: stream `IBlobStore.OpenReadAsync(baseVersion.BlobSha256)`.
  - `POST /wopi/files/{fileId}/contents` → **PutFile**: body → `IBlobStore.PutAsync` → `VersioningService.CommitSaveAsync(CommitInput{Source=EditWopi, SessionId, BaseVersionId=session.BaseVersionId})`. Lock check: if locked and `X-WOPI-Lock` ≠ stored `LockValue` → `409` + `X-WOPI-Lock` response header. On `CommitResult.Deduped` return `200` (unchanged re-PUT).
  - `POST /wopi/files/{fileId}` with `X-WOPI-Override` ∈ {`LOCK`,`UNLOCK`,`REFRESH_LOCK`,`GET_LOCK`}: mutate `EditSession.LockValue`/`LockExpiresAt` (30-min TTL); `GET_LOCK` echoes current lock; mismatch → `409` + `X-WOPI-Lock`. `ponytail: locks on the session row, DB-backed, no Redis (spec §3).`

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter WopiHostTests` → PASS. Closes the API side of **E3** (a save produces a new version; the browser round-trip is validated by the M3 CI headless-browser driver, spec §12.3).

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): WOPI host (CheckFileInfo/GetFile/PutFile + lock lifecycle)"`

---

## Task 6: Branch-on-stale-base (auto concurrent branch)

> Spec §5.2 step 4 + §5.3. When a session's `base_version_id` is no longer the main head, `commit_save` opens a **concurrent branch** instead of overwriting — "zero lost edits" (E4).

**Files:**
- Modify: `src/EasyDocs.Api/Versioning/VersioningService.cs`
- Test: `tests/EasyDocs.Api.Tests/CommitSaveTests.cs` (add cases)

- [ ] **Step 1: Write the failing tests** (E4)
```csharp
[Fact] public async Task Two_sessions_from_same_head_produce_two_branches() {
    // upload -> head H (0.0.1); mint session A base=H; mint session B base=H
    // A commits X: base==head -> fast-forward on main (0.0.2)
    // B commits Y: base H now stale -> NEW concurrent branch (Ordinal=1, Kind=Concurrent, RootVersionId=H), 0.0.3
    // both A's and B's content retrievable; nothing overwritten
}
[Fact] public async Task Session_pins_to_its_branch_after_first_stale_commit() {
    // B's next commit fast-forwards on its concurrent branch, not a third branch
}
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter CommitSaveTests` → new cases FAIL.

- [ ] **Step 3: Implement the stale-base arm** in `CommitSaveAsync` step 5 (spec §5.2 step 4 exactly):
```
if input.ExplicitBranchId set:            target = that branch
elif session.BranchId set:                target = session's branch (already diverged)
elif input.BaseVersionId == mainHead.Id:  target = main            (fast-forward)
else:                                      target = new concurrent branch   (STALE BASE)
```
  Stale-base: create `Branch{ Ordinal=max(ordinal)+1, Kind=Concurrent, RootVersionId=input.BaseVersionId }`; new version `ParentVersionId=input.BaseVersionId`, `SeqInBranch=1`. Still increment the **document** counter via `Numbering.NextDraft` under the same `FOR UPDATE` lock (each save gets a distinct `Z` — spec §5.1). If a session drove it, pin `EditSession.BranchId` so its later saves fast-forward on that branch.

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter CommitSaveTests` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): branch-on-stale-base auto concurrent branch"`

---

## Task 7: SSE event bus + `/events` endpoint + `version.created`

> Spec §10.2. M1 needs `version.created` delivered to open consoles for E3/E4 observability. M1 introduces the SSE plumbing (`IEventBus`, `GET /api/v1/documents/{id}/events`) and the first events; **M2 extends** it with the remaining event types and the short-lived `?token=` capability param. Cookie auth already works — M0's `Program.cs` `OnMessageReceived` falls back to the `ed_session` cookie.

**Files:**
- Create: `src/EasyDocs.Api/Events/EventBus.cs`, `EventEndpoints.cs`  *(concrete class — one implementation, no interface. `ponytail:` add an `IEventBus` only when a second impl or a genuine mock appears; the spec cut `EDITOR_PROVIDER` for exactly this reason and registers `VersioningService` as a bare concrete class.)*
- Modify: `Program.cs` (register `EventBus` singleton; `app.MapEventEndpoints()`), `VersioningService.cs` (publish after commit)
- Test: `tests/EasyDocs.Api.Tests/SseTests.cs`

- [ ] **Step 1: Write the failing tests**
```csharp
[Fact] public async Task Version_created_delivered_over_sse() {
    // open GET /events with HttpCompletionOption.ResponseHeadersRead (cookie auth)
    // in parallel upload a version; read stream until 'event: version.created' with the new versionId (timeout guard)
}
[Fact] public async Task Events_403_for_non_member() { }
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter SseTests` → FAIL.

- [ ] **Step 3: Implement.**
  - `EventBus` (concrete class, injected directly): `void Publish(Guid documentId, string type, object payload)` + `IAsyncEnumerable<(string Type,string Json)> Subscribe(Guid documentId, CancellationToken ct)`. Impl: `ConcurrentDictionary<Guid, List<Channel<...>>>` fan-out; subscribers get a bounded/dropping `Channel`; publish serializes payload once and writes to each. `ponytail: in-process fan-out, no Redis pub/sub — single instance (spec §3).`
  - `EventEndpoints`: `GET /api/v1/documents/{id}/events` (`.RequireAuthorization()`; `ResolveAsync` → 403/404 before streaming); `Content-Type: text/event-stream`; loop `await foreach` over `Subscribe(id, ctx.RequestAborted)`, write `event: {type}\ndata: {json}\n\n`, flush; periodic `: keep-alive` comment.
  - `VersioningService.CommitSaveAsync` (after commit, non-deduped): `_bus.Publish(docId, "version.created", new { versionId, major, minor, revision, branchId })`.

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter SseTests` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): in-process SSE event bus + /events + version.created"`

---

## Task 8: Diff — eager numeric summary (BackgroundService) + on-demand redline HTML

> Spec §7. Numeric summary `{insertions, deletions, moves, format_changes}` computed **eagerly** per `(parent→child)` on commit by an in-process `BackgroundService`; redline `.docx` + HTML render computed **on-demand** on first compare and cached by `(from_sha,to_sha)` in `version_diffs`. Every `WmlComparer` call is wrapped — failure degrades to "comparison unavailable", never 500.

**Files:**
- Create: `src/EasyDocs.Api/Diffing/WmlComparerDiffService.cs`, `DiffSummaryWorker.cs`  *(concrete `WmlComparerDiffService` — one impl, no interface; the failure test uses a real malformed `.docx`, not a mock, so nothing needs swapping)*
- Modify: `Program.cs` (register `WmlComparerDiffService`, `AddHostedService<DiffSummaryWorker>()`, a singleton `Channel<DiffJob>`), `VersioningService.cs` (enqueue), `DocumentEndpoints.cs` (`GET /compare`)
- Test: `tests/EasyDocs.Api.Tests/DiffTests.cs` (real `.docx` fixtures)

- [ ] **Step 1: Write the failing tests**
```csharp
[Fact] public async Task Numeric_summary_computed_eagerly_after_commit() {
    // upload base.docx (0.0.1); import edited.docx (0.0.2)
    // poll version_diffs for (baseSha,editedSha) until Insertions/Deletions populated (timeout guard)
    // GET /compare?from=..&to=..&format=summary -> the numbers
}
[Fact] public async Task Redline_html_on_demand_and_cached() {
    // GET compare?...&format=html -> 200 html; second call reuses HtmlBlobSha256 (no recompute)
}
[Fact] public void WmlComparer_failure_degrades_not_throws() {
    // two malformed docx blobs -> DiffSummary/DiffRender with Available=false, no exception
}
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter DiffTests` → FAIL.

- [ ] **Step 3: Implement.**
  - `WmlComparerDiffService` (concrete, injected directly): `Task<DiffSummary> SummaryAsync(fromSha,toSha,ct)`, `Task<DiffRender> RedlineHtmlAsync(fromSha,toSha,ct)`; `DiffRender` carries HTML + `Available`.
  - `WmlComparerDiffService`: open both blobs via `IBlobStore`; run `WmlComparer.Compare(from,to,settings)` **inside try/catch** — on any exception log + return `Available=false` ("comparison unavailable — download both versions"). Summary = count `w:ins`/`w:del`/moves/format-changes from the result. Redline = compared `WmlDocument` saved as docx (store via `IBlobStore`, record `RedlineBlobSha256`) + HTML render (PowerTools `WmlToHtmlConverter`, store `HtmlBlobSha256`). Upsert the `VersionDiff` row.
  - `DiffSummaryWorker : BackgroundService`: read `DiffJob(fromSha,toSha,documentId)` off the singleton `Channel<DiffJob>.Reader`; `SummaryAsync`; upsert `VersionDiff` numeric columns; `EventBus.Publish(documentId,"diff.ready",…)`. `ponytail: Channel<T> is the queue — in-memory, recomputable on restart (spec §3/§7); no durable broker.` *(Optional micro-win: since the eager `Compare(parent,child)` already produced a comparison, persist its `RedlineBlobSha256` on the `VersionDiff` row so the on-demand path only renders HTML for that pair — arbitrary compare pairs still compute on demand.)*
  - `VersioningService`: after non-deduped commit `channel.Writer.TryWrite(new DiffJob(parentSha,newSha,docId))` (skip if no parent).
  - `DocumentEndpoints`: `GET /api/v1/documents/{id}/compare?from=&to=&format=summary|html|docx` — resolve the two versions' shas, authorize, dispatch to `WmlComparerDiffService`; `summary` returns cached numbers (compute inline if the worker hasn't yet), `html` returns cached/on-demand redline, `docx` streams `RedlineBlobSha256`.

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter DiffTests` → PASS. Closes the "list shows author/time/**summary**" part of **E3**.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): eager numeric-summary worker + on-demand cached redline (WmlComparer-guarded)"`

---

## Task 9: Merge — 3-way concurrent-branch merge (`WmlComparer`)

> Spec §5.3. `POST /documents/{id}/merges {left,right}`: common ancestor = the concurrent branch's `RootVersionId`; run `WmlComparer` on `base→left` and `base→right`; consolidate into one `.docx` where each side's edits are Word **tracked-changes** revisions attributed to their authors; commit `source=Merge` with two parents; close the merged concurrent branch (`Branch.MergedIntoVersionId`). Overlapping edits are NOT auto-resolved — both revisions are present (the editor's accept/reject UI is the resolver). Guard every `WmlComparer` call.

**Files:**
- Create: `src/EasyDocs.Api/Merging/WmlComparerMergeService.cs`, `MergeEndpoints.cs`  *(concrete — one impl, no interface)*
- Modify: `Program.cs` (register `WmlComparerMergeService`; `app.MapMergeEndpoints()`)
- Test: `tests/EasyDocs.Api.Tests/MergeTests.cs` (real fixtures + a second author)

- [ ] **Step 1: Write the failing tests** (E4)
```csharp
[Fact] public async Task Merge_of_two_concurrent_branches_has_both_authors_tracked_changes() {
    // base H; author A -> main fast-forward (left); author B -> concurrent branch (right)
    // POST /merges { left: A's version, right: B's version } -> 201 source=Merge, two parent pointers
    // merged docx contains tracked-change revisions attributed to A and B; right branch closed (MergedIntoVersionId set)
}
[Fact] public async Task Merge_degrades_when_comparer_fails() {
    // force a comparer failure -> "merge unavailable — download both" (not 500); branch left open; no version created
}
[Fact] public async Task Merge_requires_editor_role() { /* Viewer -> 403 */ }
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter MergeTests` → FAIL.

- [ ] **Step 3: Implement.**
  - `WmlComparerMergeService.MergeAsync(documentId, leftVersionId, rightVersionId, actor, ct) -> MergeResult` (concrete — one impl, no interface). Resolve `base` = the concurrent branch's `RootVersionId` (M1: both sides in the same document; the copy/push **fork-point** ancestor path is M4).
    **Consolidation algorithm — design this before coding; it is the hardest step in M1.** `WmlComparer.Compare` produces a *pairwise* comparison, NOT a 3-way merge. Do **not** try to fuse two independent `Compare(base,left)` / `Compare(base,right)` result docs — their `w:ins`/`w:del` revision-ids collide and authorship gets mangled. Use **sequential application** instead:
      1. `mergedLeft = WmlComparer.Compare(base, left, settings)` with `settings.AuthorForRevisions = leftAuthorName` → a docx carrying left's edits as tracked-change revisions attributed to left's author.
      2. `merged = WmlComparer.Compare(mergedLeft, right, settings)` with `settings.AuthorForRevisions = rightAuthorName` → right's edits land as a second, cleanly-regenerated revision layer on top; the second `Compare` produces one internally-consistent revision set, so no id collisions.
      Result: a single docx with **both** authors' changes as tracked changes. Overlapping edits to the same run are NOT auto-resolved — both revisions coexist and the editor's accept/reject UI is the resolver (spec §5.3). Each `WmlComparer.Compare` call is individually try/catch-guarded (Task 8 pattern). Save the consolidated docx via `IBlobStore`.
  - On success: `VersioningService.CommitSaveAsync(CommitInput{ Source=Merge, ExplicitBranchId=main.Id, BaseVersionId=leftHead, MergeParentVersionId=right })`; set `Branch(right).MergedIntoVersionId = mergeVersionId`; `IEventBus.Publish(docId,"merge.completed",…)`.
  - On comparer failure: `MergeResult{ Available=false }` → endpoint responds "merge unavailable — download both versions", branch left open, no version created. Never 500.
  - `MergeEndpoints`: `POST /api/v1/documents/{id}/merges {left,right}` (`.RequireAuthorization()`, `CanEdit` else 403).

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter MergeTests` → PASS. Closes **E4**.

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): concurrent-branch 3-way merge via WmlComparer (tracked changes, guarded)"`

---

## Task 10: R8 download + manual version-counter override (R5)

> Closes the remaining **E5** surface. PDF download returns `409` until publish exists (M2).

**Files:**
- Modify: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` (add `GET /versions/{vid}/download`, `PUT /documents/{id}/version-counter`)
- Test: `tests/EasyDocs.Api.Tests/DownloadTests.cs`

- [ ] **Step 1: Write the failing tests**
```csharp
[Fact] public async Task Download_docx_has_R8_filename() {
    // GET /api/v1/versions/{vid}/download?format=docx -> 200, Content-Disposition filename == {slug}__{Doc_Name}-v0.0.1.docx, body==blob
}
[Fact] public async Task Download_pdf_unpublished_returns_409() { /* format=pdf, no PdfBlobSha256 -> 409 */ }
[Fact] public async Task Manual_counter_override_then_next_draft_follows() {
    // PUT /version-counter {0,0,0} -> next upload 0.0.1 ; PUT {2,5,9} -> next upload 2.5.10
}
[Fact] public async Task Manual_counter_negative_400() { /* rev:-1 -> 400 */ }
```

- [ ] **Step 2: Run — verify fail.** `dotnet test --filter DownloadTests` → FAIL.

- [ ] **Step 3: Implement.**
  - `GET /api/v1/versions/{vid}/download?format=docx|pdf`: authorize via the doc; `docx` → stream `BlobSha256` with `Content-Disposition: attachment; filename="{Numbering.DownloadFileName(orgSlug, docName, (M,m,rev), "docx")}"` (org slug from the doc's org); `pdf` → `PdfBlobSha256` null ⇒ `Problem.Of(409,…)` else stream it.
  - `PUT /api/v1/documents/{id}/version-counter {major,minor,rev}`: `CanEdit`; validate via `Numbering.Manual` (400 on negative); write the three counter columns under the `FOR UPDATE` lock; next `NextDraft` continues from it (R5/R6).

- [ ] **Step 4: Run — verify pass.** `dotnet test --filter DownloadTests` → PASS. Closes **E5** (R1–R6 exact incl. `0.0.7→0.1.0`/`0.0.7→1.0.0` via `NumberingTests`; manual `0.0.0`; downloads per R8).

- [ ] **Step 5: Commit** — `git commit -s -am "feat(m1): R8 download naming + manual version-counter override (R5)"`

---

## Task 11: Collabora compose service + full-stack smoke

**Files:**
- Modify: `deploy/compose/docker-compose.yml`, `deploy/compose/.env.example`

- [ ] **Step 1: Add the `collabora` service** (spec §3, §6.1):
```yaml
  collabora:
    image: collabora/code
    environment:
      aliasgroup1: ${PUBLIC_BASE_URL}   # allowlist the app host (spec §6.1)
      extra_params: "--o:ssl.enable=false --o:ssl.termination=true"
    ports: [ "9980:9980" ]
    cap_add: [ MKNOD ]
```
  Add `WOPI_HOST_URL`, `PUBLIC_BASE_URL`, `COLLABORA_URL` to the `easydocs` service env + `.env.example`; `easydocs` `depends_on` collabora. `ponytail: no TLS app↔Collabora on the compose net — the proxy terminates TLS at the edge (spec §11).`

- [ ] **Step 2: Boot the full stack + smoke** (manual E3/E4 browser-round-trip gate):
```bash
cp deploy/compose/.env.example deploy/compose/.env   # fill secrets + the three URLs
docker compose -f deploy/compose/docker-compose.yml up --build -d
curl -fsS http://localhost:8080/health
curl -fsS http://localhost:9980/hosting/discovery | head   # discovery reachable
docker compose -f deploy/compose/docker-compose.yml down
```
  Expected: app healthy, discovery XML served. (Full in-browser edit→save is exercised by the M3 CI headless-browser driver, spec §12.3.)

- [ ] **Step 3: Commit** — `git commit -s -am "feat(m1): Collabora Online compose service + WOPI networking"`

---

## Task 12: Full suite + open PR

- [ ] **Step 1:** `dotnet test` → all green (M0 regression + all M1 suites); `dotnet build` → 0 warnings (TreatWarningsAsErrors).
- [ ] **Step 2:** `git push -u origin m1-versioning-core && gh pr create --fill --base main`

---

## M1 Done — Exit Checklist

- [ ] **E3 (Edit/version):** minting a session + WOPI `PutFile` produces a new version; an unchanged re-PUT (same sha) creates none; the version list shows author/time + a numeric change summary. (`WopiHostTests`, `CommitSaveTests`, `DiffTests`.)
- [ ] **E4 (Branch/merge):** two sessions from one head → two branches, zero lost edits; `POST /merges` output opens with both authors' tracked changes attributed; merged branch closed. (`CommitSaveTests`, `MergeTests`.)
- [ ] **E5 (Numbering):** R1–R6 exact incl. `0.0.7→0.1.0`, `0.0.7→1.0.0`, manual `0.0.0`; downloads named per R8. (`NumberingTests`, `DownloadTests`; publish-driven R3/R4 re-validated E2E in M2.)
- [ ] Collabora joins the compose stack; discovery reachable; `WOPI_HOST_URL` vs `PUBLIC_BASE_URL` threaded correctly.
- [ ] Every `WmlComparer`/merge call guarded — malformed input degrades to "comparison/merge unavailable", never 500.
- [ ] Numeric summary eager (BackgroundService); redline HTML on-demand + cached in `version_diffs`; `version.created` + `diff.ready` + `merge.completed` on SSE.

**Assumed building blocks introduced here (referenced by later milestones):** `VersioningService.CommitSaveAsync` / `CommitInput` / `CommitResult` (M2 publish + M4 push materialization call it), `EventBus` + `/events` (M2 extends with more event types + `?token=`), `WmlComparerDiffService` (M2 PDF worker mirrors its guard pattern), `WmlComparerMergeService` (M4 adds the cross-document fork-point ancestor path). All are **concrete classes injected directly** — no interfaces until a second implementation genuinely exists (spec §3 precedent).

**Next:** write/execute the **M2** plan (publish minor/major + PDF, approvals, name/revert/download/share, SSE console).
