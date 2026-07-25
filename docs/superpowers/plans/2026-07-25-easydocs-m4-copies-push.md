# easydocs M4 — Copies & Push/Merge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The signature SimulDocs isolation feature. Let a user **fork a specific version into a new isolated "copy" document** (its own members, its own history — internal drafts never leak) so an external reviewer can redline it, then **push edits back** to the master where members **Accept/Reject** and merge them in. Exit gate: acceptance criterion **E9 (Copies)** green, and full **E1–E12** green (the conformance suite from M3 now covers E9).

**Architecture:** Still one ASP.NET Core process. New endpoints for copies and pushes; reuse of M1's `VersioningService`/`IMergeService` and M0's `DocumentAuthorization`. The one deliberate design point: **push-back authorizes on membership of the SOURCE copy, not the target** — the single sanctioned bypass of the target authorization chokepoint (spec §8, §10). No new tables (M0 migrated `push_requests`; `documents.parent_document_id`/`forked_from_version_id` exist).

**Tech Stack:** nothing new — reuses blob store (zero-copy fork references the same immutable blob), the merge engine, SSE.

**Spec:** `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` — **§8 (copies & push)**, §5.3 (merge; cross-document ancestor = the copy's fork point), §10.1 (copies/push endpoints), §11 (the sanctioned chokepoint bypass; copies do NOT inherit master membership), **§12.1 E9**, §13 (M4 row).

**Builds directly on (read first):**
- `src/EasyDocs.Api/Documents/DocumentEndpoints.cs` — `Create` (a copy is a `documents` row with `ParentDocumentId`+`ForkedFromVersionId`), `AuthorizeAsync`.
- `src/EasyDocs.Api/Auth/DocumentAuthorization.cs` — `ResolveAsync`. **Copies do not inherit master membership** — a fork starts with only its creator as Owner. Push-back is authorized against the SOURCE copy's membership.
- **M1's `VersioningService.CommitSaveAsync`** — materializing an accepted push creates a version on a new `incoming_push` branch via the same write path.
- **M1's `IMergeService`** — extend with the **cross-document fork-point ancestor**: when merging an `incoming_push` branch into main, the common ancestor is the stored fork point (see Task 3), not a same-document branch root.
- `src/EasyDocs.Api/Domain/PushRequest.cs` — `SourceDocumentId` (copy), `SourceVersionId`, `TargetDocumentId`, `PushedBy`, `Status` (`pending`/`accepted`/`rejected`/`auto_accepted`), `ReviewedBy?`, `ReviewedAt?`, `MaterializedVersionId?`.
- `src/EasyDocs.Api/Domain/Branch.cs` — `Kind=IncomingPush`, `RootVersionId` (store the fork point here on materialization — see Task 3), rendered distinctly by the UI (no parent edge).
- `src/EasyDocs.Api/Domain/Document.cs` — `ParentDocumentId`, `ForkedFromVersionId`.
- `src/EasyDocs.Api/Storage/IBlobStore.cs` — fork references the same blob sha (zero-copy).
- M3's `ed_` PAT path — a non-member automation can push via a service account (goes to review).

**No new tables.**

---

## File Structure (new/changed in M4)

```
src/EasyDocs.Api/
  Copies/
    CopyEndpoints.cs        # POST /versions/{vid}/copies ; GET /documents/{id}/copies
    PushEndpoints.cs        # POST /documents/{copyId}/pushes ; GET .../push-requests ; accept/reject
    PushService.cs          # authorize on source-copy membership; materialize or queue; fork-point wiring
tests/EasyDocs.Api.Tests/
  CopyTests.cs  PushTests.cs  PushMergeTests.cs
  Conformance/E09_Copies.cs   # un-skip the M3 placeholder
```

---

## Task 1: Push To Copy (fork)

**Files:** `Copies/CopyEndpoints.cs`; test `CopyTests.cs`.

- [ ] Failing tests: `POST /api/v1/versions/{vid}/copies {name}` creates a NEW `documents` row with `ParentDocumentId` = source doc, `ForkedFromVersionId` = the version, a main branch, and a first version referencing the **same blob sha** (zero-copy); the copy's members start with ONLY the creator (Owner); a member of the copy who is NOT a member of the master cannot see master drafts (isolation); `GET /api/v1/documents/{id}/copies` lists copies of a document. Requires `CanEdit` on the source.
- [ ] Implement; commit `-s`.

## Task 2: Push back — authorize on source, member vs non-member

**Files:** `Copies/PushEndpoints.cs`, `Copies/PushService.cs`; test `PushTests.cs`.

- [ ] Failing tests:
  - `POST /api/v1/documents/{copyId}/pushes {target_document_id, version_id}` is authorized on **membership of the source copy** (a copy member with NO target role may still push — the sanctioned bypass); a non-authenticated/non-copy-member → 403.
  - Pusher **also holds a target role** → immediately materialize an `incoming_push` branch on the target + `push_requests{status: auto_accepted, MaterializedVersionId}`; `push.requested`→(auto) event.
  - Pusher **lacks a target role** → `push_requests{status: pending}`; target members see it via `GET /api/v1/documents/{id}/push-requests?status=pending`; `push.requested` SSE to target consoles.
  - `POST /api/v1/push-requests/{id}:accept` (target Editor+) → materialize (as above), status `accepted`; `:reject` → status `rejected`, nothing enters the target's history, pusher notified. Accept/reject requires target `CanEdit`.
- [ ] Implement `PushService` with the authorization-on-source rule clearly commented as the ONE documented chokepoint bypass (spec §8/§11). Materialization creates the version via `VersioningService` on a fresh `Branch{Kind=IncomingPush}`. Commit `-s`.

## Task 3: Fork-point ancestor + push-merge

**Files:** `Copies/PushService.cs` (materialize), extend `IMergeService`; test `PushMergeTests.cs`.

- [ ] Failing tests: merging an `incoming_push` branch into main resolves the common ancestor as the **fork point** (`documents.forked_from_version_id` of the copy, carried onto the materialized branch), NOT a same-document branch root; the merge output is tracked-changes attributed correctly; the incoming branch closes on merge.
- [ ] On materialization, **store the fork point on the incoming branch** (set `Branch.RootVersionId` to the fork-point version id, sourced from `push_requests.source_version_id` + the copy's `ForkedFromVersionId`) so cross-document merge resolves the ancestor without walking into the copy document (spec §8 resolution note). Extend `IMergeService` to use it. Commit `-s`.

## Task 4: Un-skip E9 conformance + full suite + PR

**Files:** `tests/EasyDocs.Api.Tests/Conformance/E09_Copies.cs`.

- [ ] Implement E9 exactly (spec §12.1): isolated members/history; non-member push → pending review; accept → incoming branch (no parent edge, distinct render); reject → hidden + pusher notified; merge into main via fork-point ancestor. Remove the M3 skip.
- [ ] `dotnet test` all green (full E1–E12 now); `dotnet build` 0 warnings; `git push -u origin m4-copies-push && gh pr create --fill --base main`.

---

## M4 Done — Exit Checklist

- [ ] Fork creates an isolated copy (own members/history, zero-copy blob); master drafts never leak to copy-only members.
- [ ] Push-back authorized on **source-copy** membership (the one sanctioned bypass); member push → auto-materialize; non-member push → pending review; accept → `incoming_push` branch; reject → hidden + notify.
- [ ] Cross-document merge uses the **fork-point** ancestor; incoming branch closes on merge.
- [ ] **E9 green**, and the full **E1–E12** conformance suite green in CI.

**Next:** write/execute the **M5** plan (v1.0.0 launch — docs site, security pass, tag).
