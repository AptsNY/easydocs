# easydocs M5 — v1.0.0 Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take the feature-complete product (M0–M4, all of E1–E12 green) to a public **v1.0.0** open-source release: a documentation site (user + API + self-host guide), a security pass, finalized licensing/governance, signed release artifacts, and a tag. Exit gate: **`v1.0.0` tagged**, images published, conformance suite public.

**Architecture:** No new product code. This milestone is documentation, hardening, packaging, and release hygiene. Prefer deletion/consolidation over addition (`ponytail`): document what exists, don't build features to document.

**Spec:** `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` — §4.1/§14 (AGPL server + MIT clients, DCO — already applied in M0), §4.3-equivalent (governance), §5 (packaging/self-host), §11 (security controls), §12 (conformance as public proof), §13 (M5 row).

**Builds on:** the whole repo (M0–M4). `LICENSE` (AGPL-3.0) and `CONTRIBUTING.md` (DCO) already exist from M0 — M5 finalizes governance docs around them. The CI DCO check + conformance job already exist (M0 Task 12 + M3 Task 6).

---

## File Structure (new in M5)

```
docs-site/                 # MkDocs Material (pip + one mkdocs.yml) — user + self-host guides
  docs/                    #   NOT Docusaurus: lighter for a handful of pages, no React/node build
    getting-started.md  concepts.md (versions/branches/publish/copies)
    self-hosting.md (docker compose, env, TLS via Caddy/Traefik, OIDC note)
    automation-recipes.md    # API reference LINKS to /docs (M3) — not re-rendered here
SECURITY.md                # private disclosure channel + supported versions
GOVERNANCE.md              # roadmap, RFC process, semver-for-the-product, maintainers
CHANGELOG.md               # generated (conventional commits)
.github/
  ISSUE_TEMPLATE/  PULL_REQUEST_TEMPLATE.md
  workflows/release.yml    # build + push signed container images (GHCR) on tag
```

## Task 1: Security pass (hardening + E12 depth)

- [ ] Re-run/extend the authorization matrix tests **per endpoint × role** (spec §12.1 E12): confirm copies never leak master drafts, org role grants no implicit document access, capability tokens (WOPI/share) are hashed + scoped + short-TTL, cross-org → 404.
- [ ] Review the deferred-with-ceiling items recorded across M0–M4 (`ponytail:` comments): the folder root-name uniqueness race (M0 Task 6), no ClamAV on upload, no rate limiting on the public `/s/{token}` and API. Decide per item: fix now vs. document as a known v1 limitation in `SECURITY.md`. At minimum add **basic rate limiting** to the public share route and the token/login endpoints (ASP.NET rate limiter middleware — native, no package).
- [ ] Confirm secrets are never logged; `Jwt:Secret` fail-fast (M0) present; TLS termination documented (reverse-proxy examples). Commit `-s`.

## Task 2: Documentation site

- [ ] Scaffold `docs-site/` with **MkDocs Material** (`pip install mkdocs-material` + one `mkdocs.yml` — lighter than Docusaurus's React/node build for a few guide pages). Author: getting-started, core concepts (X.Y.Z numbering, branches/merge, publish/approvals, copies/push), **self-hosting guide** (`docker compose up`, `.env` reference, S3 backend note, OIDC/SSO note as v1.1, backup/restore), and automation recipes (the reference flow from M3). For the **API reference, link to the self-contained docs already served at `/docs`** (M3 Task 4) — do NOT re-render `/openapi/v1.json` in a second doc system. No phone-home. Commit `-s`.
- [ ] Wire a docs deploy (GitHub Pages workflow) — optional if a hosting target isn't chosen; at minimum the site builds in CI.

## Task 3: Governance, community, release hygiene

- [ ] `SECURITY.md` (private disclosure email/GH advisory, supported-versions policy), `GOVERNANCE.md` (public roadmap via GitHub Projects, RFC process for schema/API changes, **product semver independent of document version numbers**, maintainer list, code of conduct link), issue/PR templates.
- [ ] Confirm `LICENSE` = AGPL-3.0 (server) and add the MIT note for future `packages/*` clients; `CONTRIBUTING.md` DCO flow intact; `CHANGELOG.md` generated from conventional commits. Commit `-s`.

## Task 4: Release pipeline + tag v1.0.0

- [ ] `.github/workflows/release.yml`: on tag `v*`, build and push **signed** container images to **GHCR** (the `easydocs` app image; the compose file references a pinned tag), attach the compose bundle + `.env.example` as release assets, and mark the **E1–E12 conformance suite public** (the acceptance criteria are the public conformance proof, spec §4.3/§12).
- [ ] Final gate: full `dotnet test` + conformance suite green; `docker compose up` clean from scratch on a fresh checkout; `dotnet build` 0 warnings.
- [ ] Tag: `git tag -s v1.0.0 -m "easydocs v1.0.0"` (signed tag) and push; verify the release workflow publishes images. Open the launch PR / release notes.

---

## M5 Done — Exit Checklist

- [ ] Security pass complete; E12 matrix green; basic rate limiting on public + auth routes; known limitations documented in `SECURITY.md`.
- [ ] Docs site (user + API + self-host) builds; automation recipes published; no phone-home.
- [ ] `SECURITY.md`, `GOVERNANCE.md`, templates, `CHANGELOG.md` in place; AGPL/MIT split + DCO confirmed.
- [ ] Release workflow publishes signed GHCR images on tag; conformance suite public.
- [ ] **`v1.0.0` tagged and released.** 🎉

**This is the last milestone.** After v1.0.0, the deferred v1.1 items (desktop Word/WebDAV, OIDC/SSO, cloud export pickers, graphical DAG revision graph, ONLYOFFICE option, full-text content search, thumbnails) and v2+ items (M365 Graph, OAuth server, SDKs/CLI, webhooks, SCIM/MFA, Helm) get their own specs → plans → execution cycles.
