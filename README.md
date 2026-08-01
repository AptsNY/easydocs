# easydocs

**Git-style history for `.docx` — without asking anyone to learn Git.**

Open-source, self-hostable version control for Word documents. Every save becomes an immutable,
numbered version. Two people editing at once branch instead of overwriting each other, and merge in one
click. You get a real redline between any two versions even if nobody ever turned Track Changes on.

Two front doors: a web UI for the people who just need to work on the document, and a documented REST
API for developers. One process, `docker compose up`.

## Status

**Milestones M0–M5 are complete: the REST API and the full web UI both work end to end.** The v1
conformance profile (E1–E12) runs green in CI against the real container image, with zero skipped
criteria — nothing in that suite is allowed to skip, because a silently-skipped criterion reads as
coverage that does not exist. Behind it sit 346 API tests and 90 browser tests, the latter driven
against the shipped image rather than a dev server.

Also not built yet, so you do not go looking for them:

- **No OIDC/SSO.** Local email + password only (Argon2id). SSO is v1.1.
- **No MFA.** Password or `ed_` API token. Put an authenticating proxy in front if you need it.
- **No desktop "Open in Word."** Editing is in the browser via Collabora. WebDAV + `ms-word:` is v1.1.
- **No graphical revision graph** (history is an indented list), **no full-text content search**
  (names only), **no cloud export/import pickers**. All v1.1.
- **No antivirus scanning on upload.** v1 trusts `.docx` from authenticated members.

Rate limiting on the anonymous and credential routes **does** now exist, per-endpoint rather than
global — but it is per client IP, so it collapses into one install-wide budget behind a reverse proxy
unless you set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Read
[SECURITY.md](SECURITY.md#known-v1-limitations-not-vulnerabilities) for that and the rest of the known
v1 limitations before you expose easydocs to anyone.

## Run it

```bash
git clone https://github.com/Robertzu43/easydocs && cd easydocs/deploy/compose
cp .env.example .env          # then edit it: Jwt__Secret and POSTGRES_PASSWORD ship as placeholders
docker compose up --build
```

Then open **<http://localhost:8080>** and register. The first account you create also creates your
organization, and you are its owner.

`Jwt__Secret` in `.env.example` is deliberately too short to boot — the app fails fast under 32 bytes.
Generate a real one with `openssl rand -base64 48`. A placeholder that *worked* would mean every
install that took the quickstart literally signed its sessions with a secret published in this
repository.

To bring in a colleague, invite them from **Settings → Organization** (or from a document's Members
panel, which also grants access to that document) and send them the invitation link. They sign in and
open it. Someone who belongs to more than one organization gets a switcher in the header.

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
- **Share links.** Version-scoped, expiring, revocable, and audited — an anonymous view lands on the
  audit trail with a view count. The recipient needs no account and sees a plain download page: no app
  chrome, no sign-up wall.
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

**Everything in this repository today is AGPL-3.0.** [`LICENSE`](LICENSE) is the GNU Affero General
Public License v3.0, and it covers all of it: the ASP.NET Core server, the React SPA, the tests, the
deployment files and the docs.

A future MIT exception is already decided but **not yet applicable**, and the distinction matters if
you are about to embed something:

| Path | License | Exists today? |
|---|---|---|
| Everything in this repo | **AGPL-3.0** | Yes — this is the whole repository right now |
| `packages/*` — future API client libraries and SDKs | **MIT** | **No.** There is no `packages/` directory. |

The reasoning (spec [§14](docs/superpowers/specs/2026-07-24-easydocs-v1-design.md)): AGPL is the right
licence for a self-hostable server, because it keeps modifications to a *hosted* easydocs available to
its users. It is the wrong licence for a thin client library, because copyleft on an SDK would make
easydocs' own API awkward to call from proprietary software — which defeats the point of publishing an
API. So when SDKs are written they will live under `packages/*` and carry their own `LICENSE` file
naming MIT, and the boundary will be a directory you can point at.

**Until that directory exists, assume AGPL-3.0 for anything you take from here.** No file in this
repository is MIT-licensed today, and this table is not a grant — the MIT terms apply to code that has
not been written yet.

Contributions are under the **Developer Certificate of Origin (DCO)** — sign off every commit with
`git commit -s`. No CLA, and **you keep the copyright to your contributions**. See
[CONTRIBUTING.md](CONTRIBUTING.md).

## Project docs

- [CONTRIBUTING.md](CONTRIBUTING.md) — DCO sign-off, dev setup, workflow
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — Contributor Covenant 2.1
- [SECURITY.md](SECURITY.md) — private disclosure, and the **known v1 limitations** an operator should
  read before exposing easydocs
- [GOVERNANCE.md](GOVERNANCE.md) — how decisions get made, the RFC process, release versioning
- [CHANGELOG.md](CHANGELOG.md) — grouped by milestone; nothing released yet
- [Self-hosting guide](docs-site/docs/self-hosting.md) — TLS, `.env`, proxies, backups, upgrades
