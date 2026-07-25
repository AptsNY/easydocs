# easydocs M3 — Public API GA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the REST API a first-class, documented, GA product surface — the easydocs differentiator SimulDocs lacks. Add **Personal Access Tokens** (`ed_` Bearer, alongside the existing JWT cookie), an auto-generated **OpenAPI 3.1** document served with rendered docs at `/docs`, **cursor pagination**, confirm **RFC-7807** everywhere, freeze the **authoritative v1 endpoint set** (spec §10.1), and stand up the **E1–E12 conformance suite** as the public acceptance suite that runs the full compose stack in CI. Exit gate: **the API drives the full document flow unattended** (create → upload → edit → publish → approve → share) via a PAT, and the conformance suite is green in CI.

**Architecture:** Still one ASP.NET Core process. New: an API-token auth path that composes with the M0 JWT scheme (a request authenticates via `ed_` Bearer OR `ed_session` cookie OR JWT Bearer); OpenAPI generated from the minimal-API metadata; a `/docs` page (self-contained, no phone-home); pagination helpers. No new tables (M0 migrated `api_tokens`).

**Tech Stack (added on top of M0/M1/M2):** built-in `Microsoft.AspNetCore.OpenApi` (net10 native OpenAPI) → `/openapi/v1.json`; a static docs renderer (Scalar or Swagger UI served as self-contained static assets — pick one that needs no CDN, spec §3 CSP-free/self-host) · `System.Security.Cryptography` for token hashing.

**Spec:** `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` — §3.2-equivalent (tokens/scopes, trimmed to PATs), **§10** (API front door), **§10.1** (authoritative v1 endpoint set — supersedes source §9), **§10.3** (multipart ingest), §11 (auth chokepoint, org-role has no implicit doc access), **§12.1 E1–E12** (v1 conformance profile), **§12.3** (CI runs the compose stack), §13 (M3 row).

**Builds directly on (read first):**
- `src/EasyDocs.Api/Auth/JwtService.cs` + the M0 JWT bearer wiring in `Program.cs` (`OnMessageReceived` cookie fallback, `DefaultMapInboundClaims=false`, `ValidateIssuer/Audience=false`). M3 adds a token scheme that resolves `ed_` Bearer tokens against `api_tokens` and yields the same `sub`/`org` claim principal so `CurrentUser` + `DocumentAuthorization` work unchanged.
- `src/EasyDocs.Api/Domain/ApiToken.cs` — `OrgId`, `UserId?` (null = service account), `ServiceName?`, `TokenHash`, `Scopes[]`, `ExpiresAt?`, `LastUsedAt?`, `RevokedAt?`. Already migrated.
- `src/EasyDocs.Api/Auth/DocumentAuthorization.cs` — a token **never exceeds its owner's document role** (resolve role for the token's `UserId`); org role grants no implicit doc access (spec §11).
- `src/EasyDocs.Api/Common/Problem.cs` — RFC-7807; audit every mutation.
- All M0/M1/M2 endpoint groups — M3 does NOT rewrite them; it (a) ensures they emit OpenAPI metadata (`.WithName`/`.Produces`/tags), (b) adds cursor pagination to the list endpoints (`GET /documents`, `/versions`, `/audit`, `/publications`), (c) adds the token endpoints.

**No new tables.**

---

## File Structure (new/changed in M3)

```
src/EasyDocs.Api/
  Auth/
    ApiTokenService.cs            # mint (ed_ + hash), verify, touch LastUsedAt, scopes
    ApiTokenAuthHandler.cs        # AuthenticationHandler resolving ed_ Bearer -> principal
    TokenEndpoints.cs             # GET/POST/DELETE /api/v1/tokens
  Api/
    Pagination.cs                 # cursor encode/decode + PagedResult<T>
    OpenApiConfig.cs              # document info, security schemes, servers
  wwwroot/docs/                   # self-contained API docs page (no CDN)
tests/EasyDocs.Api.Tests/
  ApiTokenTests.cs  PaginationTests.cs  OpenApiTests.cs
  Conformance/                    # E1–E12 v1-profile end-to-end suite (spec §12.1)
    E01_Folders.cs ... E12_Security.cs  ConformanceFixture.cs
.github/workflows/
  conformance.yml                 # boots compose stack, runs Conformance suite (spec §12.3)
```

---

## Task 1: API tokens (`ed_` PATs)

**Files:** `Auth/ApiTokenService.cs`, `Auth/TokenEndpoints.cs`; test `ApiTokenTests.cs`.

- [ ] Failing tests: `POST /api/v1/tokens {name, scopes[], expires_at?}` returns the raw `ed_…` token ONCE (store only `TokenHash`); `GET /api/v1/tokens` lists (no secret); `DELETE` revokes (`RevokedAt`); the raw token is a valid `Authorization: Bearer ed_…` credential on a protected endpoint; expired/revoked → 401; `LastUsedAt` updates on use.
- [ ] Implement `ApiTokenService` (random 256-bit, `ed_` prefix, SHA-256 hash at rest, constant-time compare) and the endpoints (require a logged-in user; token inherits the creating user's identity/org). Commit `-s`.

## Task 2: `ed_` Bearer authentication handler

**Files:** `Auth/ApiTokenAuthHandler.cs`; modify `Program.cs`; extend `ApiTokenTests.cs`.

- [ ] Failing test: a request with `Authorization: Bearer ed_…` authenticates and `CurrentUser.UserId/OrgId` resolve from the token's owner; `DocumentAuthorization` enforces the owner's role (a Viewer's token cannot mutate).
- [ ] Implement a custom `AuthenticationHandler` (scheme `"ApiToken"`) added to the auth pipeline via a **policy scheme** that dispatches: `ed_` Bearer → ApiToken handler; otherwise → the existing JWT/cookie scheme. Build the same claims principal (`sub`, `org`). Commit `-s`.

## Task 3: Cursor pagination

**Files:** `Api/Pagination.cs`; modify the list endpoints; test `PaginationTests.cs`.

- [ ] Failing tests: `GET /api/v1/documents?limit=2` returns 2 items + a `next_cursor`; passing `?cursor=` returns the next page; stable ordering; opaque base64 cursor (encodes the sort key, e.g. `(created_at, id)`).
- [ ] Implement `PagedResult<T>` + cursor encode/decode; apply to `/documents`, `/documents/{id}/versions`, `/documents/{id}/publications`, `/documents/{id}/audit`. Commit `-s`.

## Task 4: OpenAPI 3.1 + `/docs`

**Files:** `Api/OpenApiConfig.cs`, `wwwroot/docs/*`; modify endpoints to add metadata; test `OpenApiTests.cs`.

- [ ] Failing tests: `GET /openapi/v1.json` returns a valid OpenAPI 3.1 document that includes the core endpoints (documents, versions, publish, approvals, share-links, tokens) and declares the security schemes (`ed_` Bearer + cookie); `GET /docs` returns a self-contained HTML page (no external CDN requests — CSP-safe, spec §3) that renders the spec.
- [ ] Add `builder.Services.AddOpenApi()` + `app.MapOpenApi()`; annotate endpoint groups with `.WithTags`/`.Produces<T>`/`.WithName`; serve a bundled docs UI (Scalar or Swagger UI static assets copied into `wwwroot/docs`, referenced locally). Ensure the v1 endpoint set matches spec §10.1 exactly (no dead endpoints — no exports/tasks/sso/webdav). Commit `-s`.

## Task 5: E1–E12 conformance suite (v1 profile)

**Files:** `tests/EasyDocs.Api.Tests/Conformance/*`; test the suite itself.

- [ ] Implement the **v1 conformance profile** exactly as spec §12.1 restates it (E2 local-upload-only; E3 Collabora-save; E8 the 8-action set; E10 share+download no cloud export; E7 approvals single-decision-no-thread; etc.). Each `E##_*.cs` drives the API (via a PAT where possible) end-to-end against the `ApiFactory`/compose stack. Pure-API criteria hit the API directly; criteria needing the Collabora browser round-trip (E3, E4, parts of E8) use a headless-browser driver or a WOPI-level simulation of `PutFile` (document which). E9 (copies) is marked **pending until M4** — the suite should skip-with-reason, not fail.
- [ ] Prove the **reference automation flow** unattended: a single test script using only a PAT does create → upload → (edit/commit) → publish → request approval → respond → share, asserting each step (spec §3.5-equivalent). Commit `-s`.

## Task 6: Conformance CI job + full suite + PR

**Files:** `.github/workflows/conformance.yml`; modify `ci.yml` if needed.

- [ ] Add a CI job that boots the docker-compose stack (Postgres + Collabora + bundled LibreOffice) and runs the `Conformance` suite (spec §12.3); mark which criteria are pure-API vs. browser-round-trip. Keep the fast `build-test` job (unit + Testcontainers) as-is.
- [ ] `dotnet test` all green; `dotnet build` 0 warnings; `git push -u origin m3-public-api && gh pr create --fill --base main`.

---

## M3 Done — Exit Checklist

- [ ] `ed_` PATs mint/verify/revoke; Bearer auth composes with the JWT cookie; a token never exceeds its owner's document role; org role grants no implicit doc access.
- [ ] `GET /openapi/v1.json` is valid OpenAPI 3.1 matching the §10.1 endpoint set (no dead endpoints); `/docs` renders self-contained (no phone-home).
- [ ] Cursor pagination on all list endpoints; RFC-7807 errors; every mutation audited.
- [ ] The reference automation flow runs **unattended via a PAT** end-to-end.
- [ ] E1–E12 conformance suite runs in CI on the compose stack (E9 pending M4, skip-with-reason).

**Assumed interfaces introduced here (referenced later):** the `ed_` Bearer auth path (M4 push automation can use a service-account PAT), the conformance harness (M4 fills in E9; M5 makes it the public conformance badge).

**Note on ordering:** M3 (API GA) intentionally ships **before** M4 (copies/push) per spec §13 — the product is fully usable via the bundled editor from M1, and the API is the differentiator, so it is de-risked first.

**Next:** write/execute the **M4** plan (copies & push/merge — closes E9).
