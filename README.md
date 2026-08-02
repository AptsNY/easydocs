<p align="center">
  <img src="docs/brand/easydocs-logo.svg" alt="easydocs" width="440">
</p>

**Git-style history for `.docx` — without asking anyone to learn Git.**

[![CI](https://github.com/Robertzu43/easydocs/actions/workflows/ci.yml/badge.svg)](https://github.com/Robertzu43/easydocs/actions/workflows/ci.yml)
[![Conformance](https://github.com/Robertzu43/easydocs/actions/workflows/conformance.yml/badge.svg)](https://github.com/Robertzu43/easydocs/actions/workflows/conformance.yml)
[![Release](https://img.shields.io/github/v/release/Robertzu43/easydocs)](https://github.com/Robertzu43/easydocs/releases/latest)
[![License: AGPL-3.0](https://img.shields.io/github/license/Robertzu43/easydocs)](LICENSE)

Open-source, self-hostable version control for Word documents. Every save becomes an immutable,
numbered version. Two people editing at once branch instead of overwriting each other, and merge in one
click. You get a real redline between any two versions even if nobody ever turned Track Changes on.

Two front doors: a web UI for the people who just need to work on the document, and a REST API for
developers — the same surface, not a subset. One stack, `docker compose up`.

easydocs exists because Simuldocs, the hosted service this way of working lived in, shut down.
This project is an independent reimplementation of that workflow — not affiliated with Simuldocs —
built so the people who relied on it don't lose it. Self-hostable and AGPL-3.0 on purpose: those are
exactly the guarantees a discontinued service can't take away.

<!-- TODO(screenshot): document console with a branch + Merge button, and a redline comparison view.
     The single highest-impact addition to this page. -->

## Quickstart

Download the compose bundle from the [latest release](https://github.com/Robertzu43/easydocs/releases/latest)
— no clone, no build, the image is pinned by version and signed with cosign:

```bash
mkdir easydocs && tar xzf easydocs-*-compose.tar.gz -C easydocs && cd easydocs
cp .env.example .env    # then edit it — two secrets, see below
docker compose up -d
```

Then open **<http://localhost:8080>** and register. The first account you create also creates your
organization, and you are its owner. Invite colleagues from **Settings → Organization** (or from a
document's Members panel) and send them the invitation link; someone who belongs to more than one
organization gets a switcher in the header.

Two things to set in `.env`:

- **`Jwt__Secret`** ships deliberately too short to boot — the app fails fast under 32 bytes. Generate
  a real one with `openssl rand -base64 48`. (A placeholder that *worked* would mean every install
  that took the quickstart literally signed its sessions with a secret published in this repository.)
- **`POSTGRES_PASSWORD`** — anything non-placeholder.

The stack is three containers: the app (with LibreOffice bundled for PDF rendering), PostgreSQL 16, and
Collabora Online for in-browser editing.

> **⚠️ Plain HTTP beyond localhost silently breaks login.** The session cookie is `Secure`, so a
> browser only sends it over HTTPS — or to `localhost`, which is why the quickstart works. Serve
> easydocs at `http://192.168.1.50:8080` or `http://easydocs.internal` and login will appear to
> succeed, the browser will discard the cookie, and you land back on the sign-in screen with no error.
> Terminate TLS at a reverse proxy in front of the app — see the
> [self-hosting guide](https://robertzu43.github.io/easydocs/self-hosting/).

Developing or building from source instead:

```bash
git clone https://github.com/Robertzu43/easydocs && cd easydocs/deploy/compose
cp .env.example .env    # same two secrets
docker compose up --build
```

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

The whole lifecycle is doable in a browser without ever touching an HTTP client — which is what the
Playwright suite asserts, against the shipped container image rather than a dev server.

## What's *not* in v1

Listed so you do not go looking for them. All are planned — see [ROADMAP.md](ROADMAP.md) and the
[v1.1 milestone](https://github.com/Robertzu43/easydocs/milestone/1).

- **No OIDC/SSO and no MFA.** Local email + password (Argon2id) or an `ed_` API token. Put an
  authenticating reverse proxy in front if you need either.
- **No desktop "Open in Word."** Editing is in the browser via Collabora. WebDAV + `ms-word:` is v1.1.
- **No graphical revision graph** (history is an indented list), **no full-text content search**
  (names only), **no cloud export/import pickers**.
- **No antivirus scanning on upload.** v1 trusts `.docx` from authenticated members.

Rate limiting on the anonymous and credential routes exists per endpoint — but it is per client IP, so
behind a reverse proxy it collapses into one install-wide budget unless you set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Read
[SECURITY.md](SECURITY.md#known-v1-limitations-not-vulnerabilities) for that and the rest of the known
v1 limitations before you expose easydocs to anyone.

## API

Everything the UI does, the API does — it is the same surface, not a subset.

- **Interactive docs:** `/docs` on your install (self-contained, no external CDN)
- **OpenAPI 3.1:** `/openapi/v1.json`
- **Auth:** `ed_`-prefixed personal access tokens as `Authorization: Bearer ed_…`, or the session cookie
  for the browser. A token can never exceed the role of the user who minted it.
- **Live updates:** server-sent events per document at `/api/v1/documents/{id}/events`.

Worked end-to-end examples: [automation recipes](https://robertzu43.github.io/easydocs/automation-recipes/).

## Tech

ASP.NET Core (.NET 10) minimal APIs · PostgreSQL 16 · EF Core · React 19 + Vite 8 + react-router ·
Collabora Online (WOPI) · LibreOffice for PDF · content-addressed filesystem blobs. One container for
the app; the SPA is built into it and served from `wwwroot`.

## Docs

Guides are published at **<https://robertzu43.github.io/easydocs/>**:

- [Getting started](https://robertzu43.github.io/easydocs/getting-started/) — one `.docx` from upload to a second version
- [Concepts](https://robertzu43.github.io/easydocs/concepts/) — the mental model: numbering, branches, redlines, approvals
- [Self-hosting guide](https://robertzu43.github.io/easydocs/self-hosting/) — TLS, `.env`, proxies, backups, upgrades
- [Automation recipes](https://robertzu43.github.io/easydocs/automation-recipes/) — the full lifecycle over the REST API

And in the repo:

- [ROADMAP.md](ROADMAP.md) — what's planned after v1.0.0, and how to influence it
- [SECURITY.md](SECURITY.md) — private disclosure, and the known v1 limitations
- [GOVERNANCE.md](GOVERNANCE.md) — how decisions get made, the RFC process, release versioning
- [CHANGELOG.md](CHANGELOG.md) — v1.0.0 released 2026-08-01
- Design specs and conformance profile: [`docs/superpowers/specs/`](docs/superpowers/specs/)

## License & contributing

**Everything in this repository today is AGPL-3.0** ([LICENSE](LICENSE)) — server, SPA, tests, deploy
files, docs.

| Path | License |
|---|---|
| Everything in this repo | **AGPL-3.0** — the whole repository right now |
| `packages/*` — future API client SDKs | **MIT**, when written. The directory does not exist yet. |

AGPL is the right licence for a self-hostable server — it keeps modifications to a *hosted* easydocs
available to its users. It is the wrong licence for a thin client library, so future SDKs will live
under `packages/*` with their own MIT `LICENSE`. **Until that directory exists, assume AGPL-3.0 for
anything you take from here.** Full reasoning:
[spec §14](docs/superpowers/specs/2026-07-24-easydocs-v1-design.md).

Contributions are under the **Developer Certificate of Origin** — sign off every commit with
`git commit -s`. No CLA, and **you keep the copyright to your contributions**. Start with
[CONTRIBUTING.md](CONTRIBUTING.md); conduct is governed by the
[Code of Conduct](CODE_OF_CONDUCT.md) (Contributor Covenant 2.1).
