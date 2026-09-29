# easydocs

**Git-style history for `.docx` — without asking anyone to learn Git.**

Open-source, self-hostable version control for Word documents. Every save becomes an immutable,
numbered version. Two people editing at once branch instead of overwriting each other, and merge in
one click. You get a real redline between any two versions even if nobody ever turned Track Changes
on.

Two front doors: a web UI for the people who just need to work on the document, and a documented REST
API for developers. One process, `docker compose up`.

## Where to go

<div class="grid cards" markdown>

- **[Getting started](getting-started.md)** — install with `docker compose up` and take one `.docx`
  from upload to a second version, in about ten minutes.

- **[User guide](user-guide.md)** — task-by-task instructions for every screen of the web UI, plus
  an orientation to the REST API.

- **[Concepts](concepts.md)** — the mental model. `X.Y.Z` numbering, branches and merge, redline diff,
  publishing, approvals, copies, share links. Read this before you roll easydocs out to anyone else.

- **[Self-hosting](self-hosting.md)** — the operator's guide. Every `.env` variable, why TLS is
  mandatory in practice, reverse-proxy configs, rate-limit tuning, and backup/restore.

- **[Automation recipes](automation-recipes.md)** — drive the whole lifecycle over the REST API with
  an `ed_` personal access token.

- **[API reference](api/index.md)** — every endpoint, rendered from the OpenAPI 3.1 document the
  application itself serves.

</div>

## Status

Install from the compose bundle attached to the
[latest release](https://github.com/AptsNY/easydocs/releases/latest) — currently **v1.1.0** —
rather than building `main` from source; `main` is where development happens.

## New in v1.1

OIDC/SSO sign-in, per-account TOTP MFA, desktop "Open in Word" over WebDAV, full-text content
search, a graphical revision graph, S3-compatible blob storage, blob garbage collection, a durable
job queue, and configurable trusted proxies. Details: the
[self-hosting guide](self-hosting.md) and the changelog.

## Not built yet

Listed so you do not go looking for them — the full list lives in the repository's `ROADMAP.md`.

- **No cloud export/import pickers** (Dropbox/OneDrive/GDrive) and **no ONLYOFFICE editor option**.
- **No antivirus scanning on upload.** easydocs trusts `.docx` files from authenticated organization
  members.
- **No org-wide MFA enforcement** — MFA is per-account opt-in.

## API reference

The reference is generated from the running build, so it can never drift:

- **[API reference](api/index.md)** on this site — the full document rendered with Swagger UI, from
  a snapshot that CI verifies against what the application actually serves.
- **Interactive docs:** `/docs` on your install (self-contained, no external CDN) — "try it out"
  works there.
- **OpenAPI 3.1 document:** `/openapi/v1.json` on your install.

See [Automation recipes](automation-recipes.md) for worked end-to-end examples.

## License

The server is **AGPL-3.0**. Future API clients and SDKs under `packages/*` will be **MIT** — that
directory does not exist yet, so assume AGPL-3.0 for anything you take today; the full reasoning is in
the repository README's licensing section. Contributions are under the **Developer Certificate of
Origin** (`git commit -s`), with no CLA.
