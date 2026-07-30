# easydocs

**Git-style history for `.docx` — without asking anyone to learn Git.**

Open-source, self-hostable version control for Word documents. Every save becomes an immutable,
numbered version. Two people editing at once branch instead of overwriting each other, and merge in one
click. You get a real redline between any two versions even if nobody ever turned Track Changes on.

Two front doors: a web UI for the people who just need to work on the document, and a documented REST
API for developers. One process, `docker compose up`.

## Status

**Milestones M0–M4.5 are complete: the REST API and the full web UI both work end to end.** The v1
conformance profile (E1–E12) runs green in CI against the real container image, with zero skipped
criteria — nothing in that suite is allowed to skip, because a silently-skipped criterion reads as
coverage that does not exist.

**Not released yet.** M5 is the release milestone — docs site, security pass, signed `v1.0.0` tag — and
it has not happened. Treat `main` as pre-release: usable, self-hostable, not yet versioned.

Also not built yet, so you do not go looking for them:

- **No OIDC/SSO.** Local email + password only (Argon2id). SSO is v1.1.
- **No desktop "Open in Word."** Editing is in the browser via Collabora. WebDAV + `ms-word:` is v1.1.
- **No API rate limiting.** Put a reverse proxy in front of anything public.
- **You cannot revoke a share link early.** `DELETE /api/v1/share-links/{id}` exists, but creating a link
  returns only its token and URL — never its id — and nothing lists them, so no client can call it. Set
  an expiry when you create the link; that path works. A listing endpoint is the fix, and it is not here
  yet.
- **No graphical revision graph** (history is an indented list), **no full-text content search**
  (names only), **no cloud export/import pickers**. All v1.1.

## Run it

```bash
git clone https://github.com/Robertzu43/easydocs && cd easydocs/deploy/compose
cp .env.example .env          # then edit it: Jwt__Secret and POSTGRES_PASSWORD ship as placeholders
docker compose up --build
```

Then open **<http://localhost:8080>** and register. The first account you create also creates your
organization, and you are its owner.

The stack is three containers: the app (with LibreOffice bundled for PDF rendering), PostgreSQL 16, and
Collabora Online for in-browser editing.

### If you self-host on plain HTTP, read this

The session cookie is `Secure`, so **a browser will only send it over HTTPS — or to `localhost`.**
Browsers treat `http://localhost` as a secure context, which is why the quickstart above works. But
serve easydocs over plain HTTP on a LAN IP or an internal hostname — `http://192.168.1.50:8080`,
`http://easydocs.internal` — and **login will appear to succeed and then silently fail**: the API sets
the cookie, the browser discards it, and you land back on the sign-in screen with no error.

Terminate TLS at a reverse proxy in front of the app. That is the supported answer, and dropping
`Secure` is not — it would hand every session cookie to anyone on the network.

## What it does

- **Every save is a version.** Immutable, numbered `X.Y.Z`, attributed to an author, with a change
  summary. Nothing is ever silently overwritten.
- **Edit in the browser.** Collabora Online via a WOPI host — no upload/download dance, no Word install.
- **Branch on stale, merge in one click.** Two people editing the same version produce two branches
  rather than a lost edit. The console shows the branch indented under the main line with a Merge
  button, and the merge attributes the incoming author's changes.
- **Real redlines, Track Changes or not.** Compare any two versions and get insertions and deletions
  computed from the documents themselves.
- **Publish, PDF, approve.** Publish minor or major, get renumbering plus a rendered PDF, then request
  approval from named document members — one immutable decision each, cancellable while open.
- **Client copies with push-back review.** Fork a version into an isolated copy with its own members and
  its own history. When the copy pushes work back, a member of the original reviews it: accept and it
  lands as a clearly-labelled incoming branch, reject and it never enters the history.
- **Share links.** Version-scoped, expiring, audited — every anonymous read lands on the audit trail.
  The recipient needs no account and sees a plain download page: no app chrome, no sign-up wall.
  (Revoking one early is not reachable yet — see below.)
- **Folders, members, revert, trash, and an audit trail** for every one of the above.

The web UI covers all of it across eight screens: dashboard, document console, comparison view, Major
Versions, copies, approvals, settings, and the public share landing. The full lifecycle is doable in a
browser without ever touching an HTTP client — which is what the Playwright suite asserts, against the
shipped container image rather than a dev server.

## API

Everything the UI does, the API does — it is the same surface, not a subset.

- **Interactive docs:** `/docs` (self-contained, no external CDN)
- **OpenAPI 3.1:** `/openapi/v1.json`
- **Auth:** `ed_`-prefixed personal access tokens as `Authorization: Bearer ed_…`, or the session cookie
  for the browser. A token can never exceed the role of the user who minted it.
- **Live updates:** server-sent events per document at `/api/v1/documents/{id}/events`.

Design and conformance profile: [`docs/superpowers/specs/`](docs/superpowers/specs/). Build plans and
their post-hoc deviation records: [`docs/superpowers/plans/`](docs/superpowers/plans/).

## Tech

ASP.NET Core (.NET 10) minimal APIs · PostgreSQL 16 · EF Core · React 19 + Vite 8 + react-router ·
Collabora Online (WOPI) · LibreOffice for PDF · content-addressed filesystem blobs. One container for
the app; the SPA is built into it and served from `wwwroot`.

## License & contributing

- **Server:** GNU **AGPL-3.0** (see [LICENSE](LICENSE)).
- **Future API clients / SDKs** (`packages/*`): **MIT** (zero-friction embedding).
- Contributions are under the **Developer Certificate of Origin (DCO)** — sign off every commit with
  `git commit -s`. No CLA. See [CONTRIBUTING.md](CONTRIBUTING.md).
